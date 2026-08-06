using System.ComponentModel;
using GlassCoder.Tools.Execution;
using GlassCoder.Tools.Guardrails;
using GlassCoder.Tools.Registry;
using GlassCoder.Tools.Verification;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GlassCoder.Tools.Build;

/// <summary>Result payload of <c>build</c>.</summary>
/// <param name="Path">What was built.</param>
/// <param name="Succeeded">Whether the build was clean.</param>
/// <param name="ExitCode">Exit code from the build.</param>
/// <param name="TotalErrors">True total of errors, however many are listed.</param>
/// <param name="TotalWarnings">True total of warnings.</param>
/// <param name="Diagnostics">The summarised diagnostics - never the raw build log.</param>
/// <param name="DurationMs">Wall-clock for the build.</param>
/// <param name="Sandbox">Where it ran: <c>docker</c> or <c>host</c>.</param>
public sealed record BuildResult(
    [property: Description("The project or directory that was built.")] string Path,
    [property: Description("True when the build produced no errors.")] bool Succeeded,
    [property: Description("Exit code from the build.")] int ExitCode,
    [property: Description("Total number of errors, including any not listed.")] int TotalErrors,
    [property: Description("Total number of warnings, including any not listed.")] int TotalWarnings,
    [property: Description("Summarised diagnostics: first error per file, deduplicated, capped, earliest first.")] string Diagnostics,
    [property: Description("Wall-clock milliseconds the build took.")] double DurationMs,
    [property: Description("Where the build ran: docker or host.")] string Sandbox);

/// <summary>
/// <c>build</c> - the authoritative compile gate (CLAUDE.md §7, §8.1; workplan task 17).
/// <para>
/// It is ordered before <c>run_tests</c> in the tool list because it is the cheaper, higher
/// value oracle: a build failure is always a real defect, arrives in seconds rather than
/// minutes, and makes any test result meaningless anyway.
/// </para>
/// <para>
/// Output never reaches the model raw. It goes through the summariser first (task 15), because
/// one bad edit can produce hundreds of errors that are all one error.
/// </para>
/// </summary>
public sealed class BuildTool : IToolSet
{
    private const string ToolName = "build";

    /// <summary>How much of the raw output a blind failure carries. Enough to hold the real
    /// error; small enough not to crowd the window.</summary>
    private const int MaxTailCharacters = 2000;

    /// <summary>
    /// Pause before the one retry of a failure that produced no diagnostics. Long enough for a
    /// sync client or antivirus to release a file lock, which is what that signature usually is.
    /// </summary>
    private static readonly TimeSpan TransientRetryDelay = TimeSpan.FromSeconds(1);

    private readonly ICommandExecutor _executor;
    private readonly IPathGuard _guard;
    private readonly DiagnosticSummarizer _summarizer;
    private readonly SandboxOptions _sandbox;
    private readonly BuildCache? _cache;
    private readonly ILogger<BuildTool> _logger;

    /// <summary>Creates the tool.</summary>
    public BuildTool(
        ICommandExecutor executor,
        IPathGuard guard,
        DiagnosticSummarizer summarizer,
        IOptions<SandboxOptions> sandbox,
        BuildCache? cache = null,
        ILogger<BuildTool>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(sandbox);

        _executor = executor;
        _guard = guard;
        _summarizer = summarizer;
        _sandbox = sandbox.Value;
        _cache = cache;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<BuildTool>.Instance;
    }

    /// <summary>Builds a project, solution or directory.</summary>
    [GlassCoderTool(ToolName, Order = 50)]
    [Description("Build with dotnet build - the authoritative check that the code compiles. Run it after "
        + "editing and before running tests.")]
    public async Task<ToolObservation<BuildResult>> BuildAsync(
        [Description("Repo-relative project, solution or directory. '.' is everything.")]
        string path = ".",
        [Description("Allow a NuGet restore, which needs network.")]
        bool allowRestore = true,
        CancellationToken cancellationToken = default)
    {
        PathGuardResult verdict = _guard.Resolve(path, PathAccess.Read);
        if (!verdict.Allowed || verdict.FullPath is null)
        {
            return Observation.Fail<BuildResult>(ToolName, ToolErrorCodes.PathNotAllowed, verdict.Reason!);
        }

        // Nothing has changed since this target last built clean, so the answer cannot have
        // changed either. Returned in milliseconds and said out loud, so a run that would have
        // spent three steps rebuilding an untouched tree spends one.
        if (_cache is not null && _cache.TryGet(verdict.RelativePath!, allowRestore, out BuildResult? cached))
        {
            return Observation.Ok(
                ToolName,
                cached!,
                "Build succeeded (unchanged since the last build, so this result was reused). "
                    + "Edit something before building again.");
        }

        bool isDirectory = Directory.Exists(verdict.FullPath);
        string workingDirectory = isDirectory ? verdict.FullPath : System.IO.Path.GetDirectoryName(verdict.FullPath)!;

        List<string> arguments = ["build", "--nologo", "-v", "q", "-consoleloggerparameters:NoSummary"];
        if (!isDirectory)
        {
            arguments.Insert(1, System.IO.Path.GetFileName(verdict.FullPath));
        }

        if (!allowRestore)
        {
            arguments.Add("--no-restore");
        }

        CommandResult result;
        DiagnosticSummary summary;

        for (int attempt = 0; ; attempt++)
        {
            result = await _executor.ExecuteAsync(
                new CommandRequest("dotnet", arguments)
                {
                    WorkingDirectory = workingDirectory,
                    RequiresNetwork = allowRestore,
                    Timeout = TimeSpan.FromSeconds(_sandbox.CommandTimeoutSeconds),
                },
                cancellationToken).ConfigureAwait(false);

            if (result.FailureReason is not null)
            {
                return Observation.Fail<BuildResult>(
                    ToolName,
                    ToolErrorCodes.SandboxUnavailable,
                    result.FailureReason,
                    "A build executes arbitrary repository code, so it will not be run outside the sandbox.");
            }

            if (result.TimedOut)
            {
                return Observation.Fail<BuildResult>(
                    ToolName,
                    ToolErrorCodes.Timeout,
                    $"The build exceeded {_sandbox.CommandTimeoutSeconds} seconds and was stopped.",
                    "Build a single project rather than the whole solution.");
            }

            IReadOnlyList<CodeDiagnostic> diagnostics =
                MsBuildOutputParser.Parse(result.CombinedOutput, _guard.ToRelativePath);
            summary = _summarizer.Summarise(diagnostics, $"Build of {verdict.RelativePath}");

            if (attempt > 0 || result.ExitCode == 0 || summary.TotalErrors > 0)
            {
                break;
            }

            // A non-zero exit with nothing parseable is the transient signature: a file lock
            // from a sync client or scanner, a restore race. Two runs on 2026-08-06 each burned
            // three steps re-running exactly this build until the lock cleared; one bounded
            // retry absorbs it without hiding a real failure, which parses and skips this.
            _logger.LogWarning(
                "Build of {Path} exited {ExitCode} with no parseable diagnostics; retrying once in case it was transient",
                verdict.RelativePath, result.ExitCode);
            await Task.Delay(TransientRetryDelay, cancellationToken).ConfigureAwait(false);
        }

        BuildResult payload = new(
            verdict.RelativePath!,
            summary.Ok && result.ExitCode == 0,
            result.ExitCode,
            summary.TotalErrors,
            summary.TotalWarnings,
            summary.Text,
            result.Duration.TotalMilliseconds,
            result.Sandbox);

        if (payload.Succeeded)
        {
            _cache?.Set(verdict.RelativePath!, allowRestore, payload);
            return Observation.Ok(ToolName, payload, $"Build succeeded ({summary.TotalWarnings} warnings).");
        }

        // MSB1003 means the target is not a project or solution, which is a fact about the
        // repository rather than about the code. Say so, because "specify a project or solution
        // file" reads like a compile error to anything that only counts errors. And name the
        // projects here rather than pointing at list_projects: run e8f9186a was pointed there
        // once, never called it, and went back to editing - the answer has to be in the message
        // the model is already reading.
        if (summary.Text.Contains("MSB1003", StringComparison.Ordinal))
        {
            string directory = Directory.Exists(verdict.FullPath)
                ? verdict.FullPath
                : System.IO.Path.GetDirectoryName(verdict.FullPath) ?? verdict.FullPath;
            List<string> held = [.. ProjectLocator.FindAllProjects(directory).Take(7)];

            string guidance = held.Count == 0
                ? "Build a specific project, or use list_projects to see what this repository holds."
                : "Its projects are " +
                  string.Join(", ", held.Take(6).Select(p => _guard.ToRelativePath(p))) +
                  (held.Count > 6 ? " and more (list_projects shows the rest)" : string.Empty) +
                  " - build one of those.";

            return Observation.Ok(
                ToolName,
                payload,
                $"'{verdict.RelativePath}' is not a project or solution and contains none at its top level. {guidance}");
        }

        // "Build failed with 0 error(s)" is what the model reads when the parser recognised
        // nothing - a restore or SDK error in a format the regexes miss. Seven of those in the
        // 2026-08-06 runs, each answered with a blind identical retry. When there is nothing
        // parsed, the raw tail is the only information there is, so it goes in the message.
        if (summary.TotalErrors == 0)
        {
            string tail = Tail(result.CombinedOutput);
            return Observation.Ok(
                ToolName,
                payload with { Diagnostics = tail },
                $"Build failed with exit code {result.ExitCode}, but no compiler diagnostics could be parsed " +
                $"from its output. The output ends:\n{tail}");
        }

        // A failed build is a handled outcome, not a tool failure: this is the single most
        // useful observation the agent receives, and it must arrive as information to act on.
        return Observation.Ok(ToolName, payload, $"Build failed with {summary.TotalErrors} error(s).");
    }

    /// <summary>The last lines of the raw output, for the failures the parser cannot type.</summary>
    private static string Tail(string output)
    {
        string trimmed = output.Trim();
        return trimmed.Length <= MaxTailCharacters
            ? trimmed
            : string.Concat("… ", trimmed.AsSpan(trimmed.Length - MaxTailCharacters));
    }
}
