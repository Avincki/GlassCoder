using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using GlassCoder.Core.Diagnostics;
using GlassCoder.Tools.Processes;
using Microsoft.Extensions.Logging;

namespace GlassCoder.Core.Verification;

/// <summary>
/// The fixed half of a headless Claude Code call: which executable, which model, and what it is
/// allowed to do.
/// <para>
/// Containment lives here rather than on the request, because it is a property of the seam and
/// not of the caller. A request can say what to review; it cannot say "and give yourself Bash".
/// </para>
/// </summary>
/// <param name="CliPath">The executable. Empty means whatever is on PATH.</param>
/// <param name="Model">Model alias passed through to the CLI.</param>
/// <param name="PermissionMode">Permission mode for the session; <c>plan</c> is the non-writing one.</param>
/// <param name="AllowedTools">The tools the subprocess may use. Read-only by construction.</param>
/// <param name="Bare">Whether to run without hooks, plugins or skills, so a call is the same call everywhere.</param>
/// <param name="ApiKeyEnvironmentVariable">
/// Environment variable holding the key to hand the CLI. Null leaves the CLI's own credentials
/// alone, which is the right default: injecting a key over a subscription login silently moves
/// where the call is billed.
/// </param>
public sealed record ClaudeCliProfile(
    string CliPath,
    string Model,
    string PermissionMode,
    IReadOnlyList<string> AllowedTools,
    bool Bare,
    string? ApiKeyEnvironmentVariable)
{
    /// <summary>The executable to launch, defaulted.</summary>
    public string Executable => string.IsNullOrWhiteSpace(CliPath) ? "claude" : CliPath;
}

/// <summary>One headless call: what to ask, what shape to answer in, and how long it may take.</summary>
/// <param name="Directive">The prompt. Goes on stdin, never in the argument list.</param>
public sealed record ClaudeCliRequest(string Directive)
{
    /// <summary>Where the CLI runs. This is the root it reads from.</summary>
    public string? WorkingDirectory { get; init; }

    /// <summary>Extra read-only roots, for material outside the working directory.</summary>
    public IReadOnlyList<string> AddDirectories { get; init; } = [];

    /// <summary>Appended to the CLI's system prompt.</summary>
    public string? SystemPrompt { get; init; }

    /// <summary>JSON Schema the answer is held to. Null asks for prose.</summary>
    public string? ResponseSchema { get; init; }

    /// <summary>Spend ceiling. The CLI stops itself rather than being killed mid-thought.</summary>
    public decimal MaxBudgetUsd { get; init; }

    /// <summary>How long to wait before the process tree is killed.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Where to send the CLI's work as it happens. Null asks for the buffered call, which is
    /// what a caller with nothing to show it to should use.
    /// </summary>
    public Action<ClaudeCliEvent>? OnEvent { get; init; }
}

/// <summary>What kind of thing the CLI just did.</summary>
public enum ClaudeCliEventKind
{
    /// <summary>The session opened; the text is the model and tool count it reported.</summary>
    Started,

    /// <summary>The model said something. The text is what it said.</summary>
    Text,

    /// <summary>The model called a tool. The text is the tool and its most telling argument.</summary>
    ToolCall,

    /// <summary>Something about the call itself worth saying once - a fallback, a skipped line.</summary>
    Note,
}

/// <summary>One thing the CLI did, as it did it.</summary>
/// <param name="Kind">What kind of thing it was.</param>
/// <param name="Text">The line to show, already scrubbed.</param>
public sealed record ClaudeCliEvent(ClaudeCliEventKind Kind, string Text);

/// <summary>The CLI's answer, whether it succeeded or not.</summary>
public sealed record ClaudeCliResult
{
    /// <summary>Whether the call produced an answer.</summary>
    public required bool Succeeded { get; init; }

    /// <summary>The final text answer, when there was one.</summary>
    public string? Result { get; init; }

    /// <summary>The schema-validated payload, when the CLI honoured the schema.</summary>
    public string? StructuredOutput { get; init; }

    /// <summary>The CLI's own session id, so a call can be found in its transcript later.</summary>
    public string? SessionId { get; init; }

    /// <summary>What the call cost, as the CLI reported it.</summary>
    public decimal CostUsd { get; init; }

    /// <summary>Wall-clock for the whole subprocess.</summary>
    public double DurationMs { get; init; }

    /// <summary>Why the call is not usable, when it is not.</summary>
    public string? Failure { get; init; }

    /// <summary>Whether the answer came back as a stream rather than one buffered envelope.</summary>
    public bool Streamed { get; init; }

    /// <summary>A call that never produced an answer, and why.</summary>
    public static ClaudeCliResult Failed(string reason, double durationMs = 0) =>
        new() { Succeeded = false, Failure = reason, DurationMs = durationMs };
}

/// <summary>
/// Headless Claude Code as a seam (workplan tasks 43 and 67).
/// <para>
/// This is the mechanic <see cref="ClaudeCodeFileReviewer"/> was written around, lifted out so
/// the retrospective can use the same one: the launch assembly, the <c>--output-format json</c>
/// envelope, the cached availability probe, and the streaming variant that narrates a long call
/// while it runs. What it deliberately does not know is what any of it is <em>for</em> - the
/// directives, the schemas and the meaning of the answers belong to the callers.
/// </para>
/// <para>
/// It runs on the host rather than in the sandbox, for the same reason <c>GitTool</c> does: the
/// sandbox has no network and no credentials. What makes that defensible is
/// <see cref="ClaudeCliProfile.AllowedTools"/> - the subprocess gets read-only tools and a
/// non-writing permission mode, and nothing that can change a file or run a command.
/// </para>
/// </summary>
public sealed class ClaudeCliSession
{
    private readonly IProcessRunner _processes;
    private readonly ClaudeCliProfile _profile;
    private readonly ILogger _logger;
    private readonly Lock _probeGate = new();
    private Task<ReviewerAvailability>? _probe;

    /// <summary>Creates a session over one profile.</summary>
    /// <param name="processes">The process seam.</param>
    /// <param name="profile">Which executable, which model, and what it may do.</param>
    /// <param name="logger">Where the launch and its outcome are logged.</param>
    public ClaudeCliSession(IProcessRunner processes, ClaudeCliProfile profile, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(profile);

        _processes = processes;
        _profile = profile;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
    }

    /// <summary>The profile this session runs under.</summary>
    public ClaudeCliProfile Profile => _profile;

    /// <summary>
    /// Whether the CLI can be reached at all, probed once and remembered. Asked before a button
    /// is offered, so a missing CLI greys it out with a reason rather than failing on press.
    /// </summary>
    public Task<ReviewerAvailability> ProbeAsync(CancellationToken cancellationToken = default)
    {
        lock (_probeGate)
        {
            // The task is cached rather than its result, so two callers arriving at once share
            // one subprocess. Deliberately started with no cancellation token: a cached task that
            // one caller cancelled would answer "cancelled" to every later caller, and this is a
            // bounded --version call anyway.
            _probe ??= RunProbeAsync();
            return _probe;
        }
    }

    private async Task<ReviewerAvailability> RunProbeAsync()
    {
        try
        {
            ProcessRunResult result = await _processes.RunAsync(
                new ProcessRunRequest(_profile.Executable, ["--version"]) { Timeout = TimeSpan.FromSeconds(20) },
                CancellationToken.None).ConfigureAwait(false);

            if (result.TimedOut)
            {
                return ReviewerAvailability.Unavailable($"'{_profile.Executable} --version' did not answer in time.");
            }

            if (result.ExitCode != 0)
            {
                return ReviewerAvailability.Unavailable(
                    $"'{_profile.Executable}' returned exit {result.ExitCode}: {Scrub(result.StandardError)}");
            }

            return ReviewerAvailability.Available(result.StandardOutput.Trim());
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Almost always "not on PATH". Said in those terms rather than as a Win32 code.
            return ReviewerAvailability.Unavailable(
                $"Could not launch '{_profile.Executable}'. Install Claude Code, or set the CliPath " +
                $"setting to its full path. ({ex.Message})");
        }
    }

    /// <summary>
    /// Runs one call. Every failure comes back as a <see cref="ClaudeCliResult"/> carrying its own
    /// explanation: callers reach this from buttons, and a button that throws past its handler
    /// takes the application with it (CLAUDE.md §7 - errors are observations).
    /// </summary>
    public async Task<ClaudeCliResult> RunAsync(ClaudeCliRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        ClaudeCliResult result = await RunOnceAsync(request, streaming: request.OnEvent is not null, cancellationToken)
            .ConfigureAwait(false);

        // A CLI too old for stream-json refuses the flag and exits without ever starting a
        // session, which is exactly the case this can retry without paying twice: nothing
        // streamed means no work was done. A session that failed halfway is never re-run.
        if (!result.Succeeded && request.OnEvent is not null && !result.Streamed)
        {
            request.OnEvent(new ClaudeCliEvent(
                ClaudeCliEventKind.Note,
                "This Claude Code cannot narrate its work (no stream-json), so the rest of this " +
                "stage runs unwatched. Update the CLI to see it live."));

            _logger.LogInformation("Falling back to the buffered launch: {Failure}", result.Failure);
            return await RunOnceAsync(request, streaming: false, cancellationToken).ConfigureAwait(false);
        }

        return result;
    }

    private async Task<ClaudeCliResult> RunOnceAsync(
        ClaudeCliRequest request, bool streaming, CancellationToken cancellationToken)
    {
        StreamWatcher? watcher = streaming ? new StreamWatcher(request.OnEvent!, _logger) : null;

        ProcessRunRequest launch = BuildLaunch(request, streaming, watcher);

        _logger.LogInformation(
            "Running {Cli} on model {Model} (tools {Tools}, mode {Mode}, streaming {Streaming})",
            _profile.Executable, _profile.Model, string.Join(",", _profile.AllowedTools),
            _profile.PermissionMode, streaming);

        long start = Stopwatch.GetTimestamp();
        ProcessRunResult result;
        try
        {
            result = await _processes.RunAsync(launch, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return ClaudeCliResult.Failed(
                $"The session could not be started: {Scrub(ex.Message)}",
                Stopwatch.GetElapsedTime(start).TotalMilliseconds);
        }

        double elapsed = Stopwatch.GetElapsedTime(start).TotalMilliseconds;

        if (result.TimedOut)
        {
            return ClaudeCliResult.Failed(
                $"The session was still running after {request.Timeout.TotalSeconds:F0}s and was stopped. " +
                "Raise its TimeoutSeconds setting, or ask for less at a time.",
                elapsed) with { Streamed = watcher is { SawStreamEvent: true } };
        }

        // Streaming gives the envelope a line at a time; the buffered form gives it whole. Either
        // way the same fields come out, so everything downstream reads one shape.
        Envelope envelope = watcher?.Envelope ?? ParseEnvelope(result.StandardOutput);

        if (result.ExitCode != 0 || envelope.IsError)
        {
            string detail = Scrub(result.StandardError).Trim();
            if (detail.Length == 0)
            {
                detail = envelope.Result?.Trim() ?? string.Empty;
            }

            _logger.LogWarning("The CLI failed with exit {ExitCode}: {Detail}", result.ExitCode, detail);
            return ClaudeCliResult.Failed(
                $"The session failed (exit {result.ExitCode})." +
                (detail.Length > 0 ? $" {Truncate(detail, 500)}" : string.Empty),
                elapsed) with { Streamed = watcher is { SawStreamEvent: true } };
        }

        return new ClaudeCliResult
        {
            Succeeded = true,
            Result = envelope.Result,
            StructuredOutput = envelope.StructuredOutput,
            SessionId = envelope.SessionId,
            CostUsd = envelope.CostUsd,
            DurationMs = elapsed,
            Streamed = watcher is { SawStreamEvent: true },
        };
    }

    /// <summary>
    /// Assembles the command line.
    /// <para>
    /// Two orderings matter. <c>--add-dir</c> is variadic, so it goes last or it swallows whatever
    /// flag follows it. And the directive goes on stdin rather than in the arguments, which keeps
    /// a long prompt - and anything the operator typed into it - out of the process table.
    /// </para>
    /// </summary>
    private ProcessRunRequest BuildLaunch(ClaudeCliRequest request, bool streaming, StreamWatcher? watcher)
    {
        List<string> arguments = ["-p"];

        if (streaming)
        {
            // --verbose is not optional here: stream-json in print mode is refused without it.
            arguments.Add("--output-format");
            arguments.Add("stream-json");
            arguments.Add("--verbose");
        }
        else
        {
            arguments.Add("--output-format");
            arguments.Add("json");
        }

        arguments.Add("--permission-mode");
        arguments.Add(_profile.PermissionMode);
        arguments.Add("--allowedTools");
        arguments.Add(string.Join(",", _profile.AllowedTools));
        arguments.Add("--model");
        arguments.Add(_profile.Model);

        if (!string.IsNullOrWhiteSpace(request.ResponseSchema))
        {
            arguments.Add("--json-schema");
            arguments.Add(request.ResponseSchema);
        }

        if (!string.IsNullOrWhiteSpace(request.SystemPrompt))
        {
            arguments.Add("--append-system-prompt");
            arguments.Add(request.SystemPrompt);
        }

        if (request.MaxBudgetUsd > 0m)
        {
            arguments.Add("--max-budget-usd");
            arguments.Add(request.MaxBudgetUsd.ToString("0.####", CultureInfo.InvariantCulture));
        }

        if (_profile.Bare)
        {
            arguments.Add("--bare");
        }

        List<string> extraRoots = [.. request.AddDirectories.Where(d => !string.IsNullOrWhiteSpace(d))];
        if (extraRoots.Count > 0)
        {
            arguments.Add("--add-dir");
            arguments.AddRange(extraRoots);
        }

        Dictionary<string, string?>? environment = null;
        if (!string.IsNullOrWhiteSpace(_profile.ApiKeyEnvironmentVariable))
        {
            string? key = Environment.GetEnvironmentVariable(_profile.ApiKeyEnvironmentVariable);
            if (!string.IsNullOrWhiteSpace(key))
            {
                // Through the environment, never the argument list: arguments are visible to
                // anything that can list processes.
                environment = new Dictionary<string, string?> { ["ANTHROPIC_API_KEY"] = key };
            }
        }

        return new ProcessRunRequest(_profile.Executable, arguments)
        {
            WorkingDirectory = request.WorkingDirectory,
            Timeout = request.Timeout,
            Environment = environment,
            StandardInput = request.Directive,
            OnOutputLine = watcher is null ? null : watcher.Read,
        };
    }

    /// <summary>Redacts secrets from anything on its way to a person.</summary>
    public static string Scrub(string? value) => SecretRedactor.Scrub(value) ?? string.Empty;

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max] + "…";

    /// <summary>Finds the JSON object in a text answer, fences and all.</summary>
    public static string? ExtractJson(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        int start = text.IndexOf('{', StringComparison.Ordinal);
        int end = text.LastIndexOf('}');
        return start >= 0 && end > start ? text[start..(end + 1)] : null;
    }

    private static Envelope ParseEnvelope(string stdout)
    {
        string trimmed = stdout.Trim();
        if (trimmed.Length == 0)
        {
            return new Envelope();
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(trimmed);
            return ReadEnvelope(document.RootElement) ?? new Envelope { Result = trimmed };
        }
        catch (JsonException)
        {
            // Not the envelope. Hand the text on rather than losing it - a CLI that printed a
            // plain error is more useful read than discarded.
            return new Envelope { Result = trimmed };
        }
    }

    private static Envelope? ReadEnvelope(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return new Envelope
        {
            Result = Text(root, "result"),
            SessionId = Text(root, "session_id"),
            StructuredOutput = root.TryGetProperty("structured_output", out JsonElement structured)
                && structured.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined
                    ? structured.GetRawText()
                    : null,
            CostUsd = root.TryGetProperty("total_cost_usd", out JsonElement cost)
                && cost.ValueKind == JsonValueKind.Number
                    ? cost.GetDecimal()
                    : 0m,
            IsError = root.TryGetProperty("is_error", out JsonElement error)
                && error.ValueKind == JsonValueKind.True,
        };
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>The CLI's JSON envelope, reduced to what callers use.</summary>
    private sealed record Envelope
    {
        public string? Result { get; init; }

        public string? StructuredOutput { get; init; }

        public string? SessionId { get; init; }

        public decimal CostUsd { get; init; }

        public bool IsError { get; init; }
    }

    /// <summary>
    /// Turns <c>--output-format stream-json</c> into events as they arrive, and keeps the final
    /// <c>result</c> line as the envelope.
    /// <para>
    /// Nothing here may throw or refuse. A line it cannot read is skipped, because the failure
    /// mode of a strict parser on this seam is a retrospective that dies over a narration line
    /// it did not need - and the CLI's event vocabulary is version-dependent in exactly the way
    /// <c>--json-schema</c> is.
    /// </para>
    /// </summary>
    private sealed class StreamWatcher
    {
        /// <summary>
        /// The argument worth showing beside a tool's name, in the order a reader would want it.
        /// Every read-only tool names what it is looking at in one of these.
        /// </summary>
        private static readonly string[] TellingArguments =
            ["file_path", "path", "pattern", "query", "command", "url", "prompt", "description"];

        private readonly Action<ClaudeCliEvent> _sink;
        private readonly ILogger _logger;

        public StreamWatcher(Action<ClaudeCliEvent> sink, ILogger logger)
        {
            _sink = sink;
            _logger = logger;
        }

        /// <summary>The final result line, once one has arrived.</summary>
        public Envelope? Envelope { get; private set; }

        /// <summary>Whether anything at all parsed as a stream event.</summary>
        public bool SawStreamEvent { get; private set; }

        public void Read(string line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                return;
            }

            try
            {
                using JsonDocument document = JsonDocument.Parse(line);
                JsonElement root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                {
                    return;
                }

                SawStreamEvent = true;
                switch (Text(root, "type"))
                {
                    case "system":
                        Emit(ClaudeCliEventKind.Started, DescribeInit(root));
                        break;

                    case "assistant":
                        ReadAssistant(root);
                        break;

                    case "result":
                        Envelope = ReadEnvelope(root);
                        break;

                    default:
                        // user/tool_result and anything a newer CLI invents. The tool call was
                        // already shown; its result is the reviewer's business, not the watcher's.
                        break;
                }
            }
            catch (JsonException)
            {
                // A CLI that is not streaming prints its whole envelope as one line, which lands
                // here as valid JSON and is handled above. Anything else is narration we do not
                // understand, and it is not worth a word.
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "A stream line could not be read");
            }
        }

        private void ReadAssistant(JsonElement root)
        {
            if (!root.TryGetProperty("message", out JsonElement message) ||
                !message.TryGetProperty("content", out JsonElement content) ||
                content.ValueKind != JsonValueKind.Array)
            {
                return;
            }

            foreach (JsonElement block in content.EnumerateArray())
            {
                switch (Text(block, "type"))
                {
                    case "text":
                        string said = (Text(block, "text") ?? string.Empty).Trim();
                        if (said.Length > 0)
                        {
                            Emit(ClaudeCliEventKind.Text, said);
                        }

                        break;

                    case "tool_use":
                        Emit(ClaudeCliEventKind.ToolCall, DescribeToolUse(block));
                        break;

                    default:
                        break;
                }
            }
        }

        private static string DescribeInit(JsonElement root)
        {
            string? model = Text(root, "model");
            int tools = root.TryGetProperty("tools", out JsonElement list) && list.ValueKind == JsonValueKind.Array
                ? list.GetArrayLength()
                : 0;

            return model is null
                ? "Session started."
                : string.Create(CultureInfo.InvariantCulture, $"Session started · {model} · {tools} tool(s).");
        }

        /// <summary>
        /// A tool call as one line. The name alone would say "Read" fourteen times in a row, so
        /// the most telling argument comes with it - which for every read-only tool is the thing
        /// being looked at.
        /// </summary>
        private static string DescribeToolUse(JsonElement block)
        {
            string name = Text(block, "name") ?? "tool";
            if (!block.TryGetProperty("input", out JsonElement input) || input.ValueKind != JsonValueKind.Object)
            {
                return name;
            }

            foreach (string key in TellingArguments)
            {
                if (input.TryGetProperty(key, out JsonElement value) && value.ValueKind == JsonValueKind.String)
                {
                    string text = (value.GetString() ?? string.Empty).ReplaceLineEndings(" ").Trim();
                    if (text.Length > 0)
                    {
                        return $"{name} {Truncate(text, 120)}";
                    }
                }
            }

            return name;
        }

        private void Emit(ClaudeCliEventKind kind, string text)
        {
            // Scrubbed here rather than at the display, so no watcher can forget to. Everything
            // on this path came out of a subprocess reading a repository.
            _sink(new ClaudeCliEvent(kind, Truncate(Scrub(text), 2000)));
        }
    }
}
