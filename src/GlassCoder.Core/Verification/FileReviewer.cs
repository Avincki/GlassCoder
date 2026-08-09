using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using GlassCoder.Core.Diagnostics;
using GlassCoder.Tools.Guardrails;
using GlassCoder.Tools.Processes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GlassCoder.Core.Verification;

/// <summary>
/// How much a recommended action matters, highest first. The order of the members is the order
/// the UI sorts by, so it is part of the contract rather than an accident of declaration.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<ReviewActionPriority>))]
public enum ReviewActionPriority
{
    /// <summary>A defect: wrong behaviour, or a claim the code does not honour.</summary>
    High,

    /// <summary>A real risk that has not bitten yet.</summary>
    Medium,

    /// <summary>Maintainability - correct today, harder to keep correct.</summary>
    Low,

    /// <summary>Taste. Worth reading, safe to decline.</summary>
    Optional,
}

/// <summary>One change the reviewer recommends, small enough to be accepted on its own.</summary>
/// <param name="Id">Short kebab-case slug, stable enough to name the action in a file.</param>
/// <param name="Title">What to do, in a few words.</param>
/// <param name="Detail">Why, and where.</param>
/// <param name="Priority">How much it matters.</param>
public sealed record ReviewAction(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("detail")] string Detail,
    [property: JsonPropertyName("priority")] ReviewActionPriority Priority);

/// <summary>What one file to review, and anything the operator wants emphasised.</summary>
/// <param name="DisplayPath">Repo-relative path, which is what the reviewer is told to open.</param>
public sealed record FileReviewRequest(string DisplayPath)
{
    /// <summary>Extra direction from the operator, when they typed some. Empty is the norm.</summary>
    public string? Instructions { get; init; }
}

/// <summary>What a file review concluded, or why it did not happen.</summary>
public sealed record FileReview
{
    /// <summary>Whether a reviewer actually judged the file.</summary>
    public required bool Reviewed { get; init; }

    /// <summary>The review itself, as Markdown. Carries the failure text when there was one.</summary>
    public required string Report { get; init; }

    /// <summary>The recommended actions, already sorted and capped.</summary>
    public IReadOnlyList<ReviewAction> Actions { get; init; } = [];

    /// <summary>The model that answered, for the record.</summary>
    public string Model { get; init; } = string.Empty;

    /// <summary>The CLI's own session id, so a run can be found in its transcript later.</summary>
    public string? SessionId { get; init; }

    /// <summary>Wall-clock for the whole subprocess.</summary>
    public double DurationMs { get; init; }

    /// <summary>What the run cost, as the CLI reported it.</summary>
    public decimal EstimatedCostUsd { get; init; }

    /// <summary>
    /// Why the review is not usable, when it is not. Distinct from an empty action list, which
    /// is a finding - "nothing to change here" is a review, not a failure.
    /// </summary>
    public string? Failure { get; init; }

    /// <summary>A review that did not happen, and why.</summary>
    public static FileReview NotReviewed(string reason) =>
        new() { Reviewed = false, Report = reason, Failure = reason };
}

/// <summary>Whether the reviewer can be reached at all, and what answered.</summary>
/// <param name="IsAvailable">Whether a review would get as far as the model.</param>
/// <param name="Version">What <c>--version</c> printed, when it printed something.</param>
/// <param name="Reason">Why not, in the operator's terms, when it is not available.</param>
public sealed record ReviewerAvailability(bool IsAvailable, string? Version, string? Reason)
{
    /// <summary>The reviewer answered its version probe.</summary>
    public static ReviewerAvailability Available(string version) => new(true, version, null);

    /// <summary>The reviewer cannot be used, and this is what to tell the operator.</summary>
    public static ReviewerAvailability Unavailable(string reason) => new(false, null, reason);
}

/// <summary>Settings for the on-demand file review (workplan task 43).</summary>
public sealed class FileReviewOptions
{
    /// <summary>Configuration section these options bind from.</summary>
    public const string SectionName = "GlassCoder:FileReview";

    /// <summary>
    /// Whether the viewer offers the review at all.
    /// <para>
    /// Deliberately independent of <see cref="CritiqueOptions.Enabled"/>. This is a human
    /// pressing a button on one file, not a rung of the verification ladder, and the ladder
    /// ships switched off - sharing that switch would grey this out on every fresh install.
    /// </para>
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>The Claude Code executable. Empty means "whatever is on PATH".</summary>
    public string CliPath { get; set; } = "claude";

    /// <summary>Model alias passed through to the CLI.</summary>
    public string Model { get; set; } = "claude-opus-5";

    /// <summary>
    /// Permission mode for the session. <c>plan</c> is the non-writing one, and the second half
    /// of the containment - <see cref="AllowedTools"/> is the first.
    /// </summary>
    public string PermissionMode { get; set; } = "plan";

    /// <summary>
    /// The tools the reviewer is allowed to use. Read-only by construction: no Bash, no Write,
    /// no Edit. This is what makes running the CLI on the host defensible - the subprocess can
    /// read and search the workspace and can do nothing else to it.
    /// </summary>
    /// <remarks>
    /// Settable rather than get-only, so a configured list <em>replaces</em> these rather than
    /// appending to them. With a get-only collection the binder can only add, and "restrict the
    /// reviewer to Read" would silently leave Grep and Glob switched on.
    /// </remarks>
    public IList<string> AllowedTools { get; set; } = new List<string> { "Read", "Grep", "Glob" };

    /// <summary>
    /// Spend ceiling for one review. The CLI stops itself rather than being killed mid-thought,
    /// which is the difference between a capped review and a lost one.
    /// </summary>
    public decimal MaxBudgetUsd { get; set; } = 1.00m;

    /// <summary>How long to wait before killing the subprocess.</summary>
    public int TimeoutSeconds { get; set; } = 600;

    /// <summary>
    /// Whether to run the CLI in its minimal mode - no hooks, no plugins, no skills.
    /// <para>
    /// Off by default, which is not what it looks like it should be. <c>--bare</c> skips the
    /// user's configuration, and the subscription login lives there, so a bare session answers
    /// <c>"Not logged in · Please run /login"</c> and every review fails. Measured, not guessed:
    /// the same call succeeded the moment the flag came off.
    /// </para>
    /// <para>
    /// Worth switching on only alongside <see cref="ApiKeyEnvironmentVariable"/>, where the
    /// credential arrives through the environment instead and the isolation is free.
    /// </para>
    /// </summary>
    public bool Bare { get; set; }

    /// <summary>Extra read-only roots, for a file whose callers live in a sibling repository.</summary>
    public IList<string> AddDirectories { get; set; } = new List<string>();

    /// <summary>Most actions to keep. The reviewer is asked for this many and held to it here.</summary>
    public int MaxActions { get; set; } = 12;

    /// <summary>
    /// Where accepted actions are written, relative to the workspace root. Inside the workspace
    /// on purpose: the file shows up in the tree, opens in this same viewer, and is readable by
    /// the agent's own <c>read_file</c> when something comes to consume it.
    /// </summary>
    public string OutputDirectory { get; set; } = ".glasscoder/reviews";

    /// <summary>
    /// Environment variable holding the API key to hand the CLI. Null or empty means the CLI
    /// uses whatever credentials it already has, which is the right default: injecting a key
    /// over an existing subscription login silently moves where the run is billed.
    /// </summary>
    public string? ApiKeyEnvironmentVariable { get; set; }
}

/// <summary>
/// A second opinion on one file, asked for by a human (workplan task 43).
/// <para>
/// The sibling of <see cref="IRunReviewer"/>, which judges a finished run. This judges a file
/// nobody has run anything against - the case where you are reading code and want to know what
/// is wrong with it before deciding what to ask the agent for.
/// </para>
/// <para>
/// It never edits. The reviewer is handed read-only tools and a non-writing permission mode, and
/// what comes back is a report plus a list of proposals a human ticks. That asymmetry is the
/// point: the expensive oracle finds the work, and a person still decides which of it happens.
/// </para>
/// </summary>
public interface IFileReviewer
{
    /// <summary>Whether the feature is switched on. Configuration only - no subprocess.</summary>
    bool Enabled { get; }

    /// <summary>
    /// Whether the reviewer can actually be reached, probed once and remembered. Asked before
    /// the button is offered, so a missing CLI greys it out with a reason rather than failing
    /// on press.
    /// </summary>
    Task<ReviewerAvailability> ProbeAsync(CancellationToken cancellationToken = default);

    /// <summary>Reviews one file. Failures come back as a <see cref="FileReview"/>, never as an exception.</summary>
    Task<FileReview> ReviewAsync(FileReviewRequest request, CancellationToken cancellationToken = default);
}

/// <summary>
/// <see cref="IFileReviewer"/> over headless Claude Code (<c>claude -p</c>).
/// <para>
/// A subprocess rather than the model seam, and the reason is the one thing the seam cannot do:
/// a review of <c>WorkspaceViewModel.cs</c> that cannot open <c>WorkspacePane.xaml</c> cannot
/// tell whether the command it is reading is bound to anything. The CLI brings its own agent
/// loop and its own file tools, so the reviewer reads the neighbours, the callers and the tests
/// before it says anything.
/// </para>
/// <para>
/// It runs on the host rather than in the sandbox, for the same reason <c>GitTool</c> does
/// (workplan task 40): the sandbox has no network and no credentials, and this needs both. What
/// makes that safe here is <see cref="FileReviewOptions.AllowedTools"/> - the subprocess gets
/// Read, Grep and Glob and nothing that can change a file or run a command.
/// </para>
/// <para>
/// The response shape is enforced by the CLI's own <c>--json-schema</c> rather than asked for in
/// the prompt, so a well-formed report and action list is a validated fact rather than a hope
/// with a regex behind it.
/// </para>
/// </summary>
public sealed class ClaudeCodeFileReviewer : IFileReviewer
{
    /// <summary>
    /// The shape the CLI is told to return. Two fields, because that is what the viewer shows:
    /// prose to read, and proposals to tick.
    /// </summary>
    private const string ResponseSchema =
        """{"type":"object","additionalProperties":false,"required":["report","actions"],"properties":{"report":{"type":"string","description":"The code review, as Markdown."},"actions":{"type":"array","description":"Recommended changes, most important first.","items":{"type":"object","additionalProperties":false,"required":["id","title","detail","priority"],"properties":{"id":{"type":"string","description":"Short kebab-case slug."},"title":{"type":"string","description":"What to do, in a few words."},"detail":{"type":"string","description":"Why, and where."},"priority":{"enum":["High","Medium","Low","Optional"]}}}}}}""";


    private static readonly JsonSerializerOptions PayloadOptions = new(JsonSerializerDefaults.Web);

    private readonly ClaudeCliSession _cli;
    private readonly IPathGuard _guard;
    private readonly FileReviewOptions _options;
    private readonly IStepLogger? _transcript;
    private readonly TimeProvider _time;
    private readonly ILogger<ClaudeCodeFileReviewer> _logger;

    /// <summary>Creates the reviewer.</summary>
    public ClaudeCodeFileReviewer(
        IProcessRunner processes,
        IPathGuard guard,
        IOptions<FileReviewOptions> options,
        ILogger<ClaudeCodeFileReviewer>? logger = null,
        IStepLogger? transcript = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        _guard = guard;
        _options = options.Value;
        _transcript = transcript;
        _time = timeProvider ?? TimeProvider.System;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<ClaudeCodeFileReviewer>.Instance;
        _cli = new ClaudeCliSession(
            processes,
            new ClaudeCliProfile(
                _options.CliPath,
                _options.Model,
                _options.PermissionMode,
                [.. _options.AllowedTools],
                _options.Bare,
                _options.ApiKeyEnvironmentVariable),
            _logger);
    }

    /// <inheritdoc />
    public bool Enabled => _options.Enabled;

    /// <inheritdoc />
    public Task<ReviewerAvailability> ProbeAsync(CancellationToken cancellationToken = default) =>
        _options.Enabled
            ? _cli.ProbeAsync(cancellationToken)
            : Task.FromResult(ReviewerAvailability.Unavailable("File review is switched off in settings."));

    /// <inheritdoc />
    public async Task<FileReview> ReviewAsync(FileReviewRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!_options.Enabled)
        {
            return FileReview.NotReviewed("File review is switched off in settings.");
        }

        ReviewerAvailability availability = await ProbeAsync(cancellationToken).ConfigureAwait(false);
        if (!availability.IsAvailable)
        {
            return FileReview.NotReviewed(availability.Reason ?? "The reviewer is not available.");
        }

        ClaudeCliResult answer = await _cli.RunAsync(
            new ClaudeCliRequest(Directive(request))
            {
                WorkingDirectory = _guard.RepoRoot,
                AddDirectories = [.. _options.AddDirectories],
                SystemPrompt = SystemPrompt(),
                ResponseSchema = ResponseSchema,
                MaxBudgetUsd = _options.MaxBudgetUsd,
                Timeout = TimeSpan.FromSeconds(_options.TimeoutSeconds),
            },
            cancellationToken).ConfigureAwait(false);

        FileReview review = Interpret(answer);
        Record(request, review);
        return review;
    }

    private static string SystemPrompt() =>
        "You are reviewing one file in a codebase, for an engineer who is reading it and deciding " +
        "what to change next. You have read-only tools. Never propose a change you have not read " +
        "the surrounding code for: open the file's callers, the types it uses, and the tests that " +
        "cover it before you judge it. Prefer one specific finding with a line number over three " +
        "general observations. If the file is sound, say so plainly rather than inventing work.";

    private string Directive(FileReviewRequest request)
    {
        string extra = string.IsNullOrWhiteSpace(request.Instructions)
            ? string.Empty
            : $"\n\nThe engineer asked specifically about this:\n{request.Instructions}\n";

        return $"""
            Review the file `{request.DisplayPath}` in this repository.

            Read it, then read enough around it to judge it in context - its callers, the types it
            depends on, the tests that cover it, and anything its comments or documentation claim
            about it.{extra}

            Return two things.

            `report` is the review, as Markdown: correctness, misuse of the APIs it calls, error
            handling, threading, and anything the code claims but does not do. Cite locations as
            `path:line`. Say plainly where it is fine.

            `actions` are concrete changes, each small enough to accept on its own and be done
            independently of the others. Use priority High for defects, Medium for real risks that
            have not bitten yet, Low for maintainability, Optional for taste. Order them High
            first, and return at most {_options.MaxActions}.

            Do not change anything. Report; do not fix.
            """;
    }

    /// <summary>
    /// Turns the finished subprocess into a review.
    /// <para>
    /// Every failure becomes a <see cref="FileReview"/> carrying its own explanation. This is
    /// reached from a button on a viewer window, and a button that throws past its handler takes
    /// the application with it (CLAUDE.md §7: errors are observations).
    /// </para>
    /// </summary>
    private FileReview Interpret(ClaudeCliResult answer)
    {
        if (!answer.Succeeded)
        {
            // The session says what went wrong in the operator's terms - a missing CLI, a
            // timeout, a refused launch - and that sentence is the whole of the failure.
            return FileReview.NotReviewed(answer.Failure ?? "The review failed.");
        }

        // The schema is enforced by the CLI, so structured output is the expected path. The
        // fallback exists because --json-schema is version-dependent, and an older CLI that
        // ignored it would otherwise turn a perfectly good review into a hard failure.
        ReviewPayload? payload =
            Deserialise(answer.StructuredOutput) ?? Deserialise(ClaudeCliSession.ExtractJson(answer.Result));

        if (payload is null)
        {
            string text = answer.Result?.Trim() ?? string.Empty;
            return new FileReview
            {
                Reviewed = text.Length > 0,
                Report = text.Length > 0 ? text : "The reviewer returned nothing.",
                Model = _options.Model,
                SessionId = answer.SessionId,
                DurationMs = answer.DurationMs,
                EstimatedCostUsd = answer.CostUsd,
                Failure = text.Length > 0
                    ? "The reviewer answered in prose rather than the requested shape, so there are no " +
                      "actions to tick - the report below is what it said."
                    : "The reviewer returned nothing.",
            };
        }

        List<ReviewAction> actions =
        [
            .. payload.Actions
                .Where(a => a is not null && !string.IsNullOrWhiteSpace(a.Title))
                .OrderBy(a => a.Priority)
                .Take(Math.Max(1, _options.MaxActions))
        ];

        _logger.LogInformation(
            "File review completed in {Duration:F0} ms with {Count} action(s), cost {Cost:C4}",
            answer.DurationMs, actions.Count, answer.CostUsd);

        return new FileReview
        {
            Reviewed = true,
            Report = payload.Report ?? string.Empty,
            Actions = actions,
            Model = _options.Model,
            SessionId = answer.SessionId,
            DurationMs = answer.DurationMs,
            EstimatedCostUsd = answer.CostUsd,

            // A review that ran out of budget mid-thought is still a review; the caveat rides the
            // status line so nobody reads a partial answer as a complete one.
            Failure = answer.Caveat,
        };
    }

    /// <summary>
    /// Puts the review in the transcript as a human-initiated step, the way the git buttons put
    /// theirs there (workplan task 42). A paid model call that left no trace would be the one
    /// thing on this surface that cannot be reconstructed afterwards (CLAUDE.md §9).
    /// </summary>
    private void Record(FileReviewRequest request, FileReview review)
    {
        if (_transcript is null)
        {
            return;
        }

        try
        {
            _transcript.LogStep(new StepRecord
            {
                RunId = review.SessionId ?? $"review-{Guid.NewGuid():N}",
                TaskId = "file-review",
                StepIndex = 0,
                Role = "human",
                ModelId = _options.Model,
                StartedAt = _time.GetUtcNow(),
                Prompt = [new TranscriptMessage("user", $"Review {request.DisplayPath}")],
                ResponseText = SecretRedactor.Truncate(review.Report, 4000),
                ToolCalls = [],
                ModelLatencyMs = review.DurationMs,
                StepLatencyMs = review.DurationMs,
                Outcome = review.Reviewed ? "reviewed" : "not_reviewed",
                Error = review.Failure,
            });
        }
        catch (Exception ex)
        {
            // Failing to write the transcript must not lose the review the operator just paid for.
            _logger.LogWarning(ex, "Could not record the file review in the transcript");
        }
    }

    private static ReviewPayload? Deserialise(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            ReviewPayload? payload = JsonSerializer.Deserialize<ReviewPayload>(json, PayloadOptions);
            return payload?.Report is null && payload?.Actions is null or [] ? null : payload;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The schema-validated answer.</summary>
    private sealed record ReviewPayload
    {
        [JsonPropertyName("report")]
        public string? Report { get; init; }

        [JsonPropertyName("actions")]
        public IReadOnlyList<ReviewAction> Actions { get; init; } = [];
    }
}
