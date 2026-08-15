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
    // "The authoritative check that the code compiles" was written for a reader of this source.
    // The model learns the same thing from being told when to call it, in half the characters.
    [Description("Build with dotnet build. Run it after editing and before running tests.")]
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
                    + "Edit something before building again.",
                reused: true);
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

        // Held past the loop: whether anything parsed *said* anything decides which failure
        // message the model gets, and a count of diagnostics cannot answer that.
        IReadOnlyList<CodeDiagnostic> diagnostics = [];

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

            diagnostics = MsBuildOutputParser.Parse(result.CombinedOutput, _guard.ToRelativePath);
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

        // The SDK's markup pass leaves a scratch project behind when it fails, and nothing else
        // removes it. Run dbaa0580's next build over that directory answered MSB1011 - "more than
        // one project here" - about a second project the harness had created itself; the run spent
        // three steps reading source for a compile error that was in no file, and the 31 KB of
        // machine-specific paths shipped in the deliverable.
        SweepScratchProjects(verdict);

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
                $"'{verdict.RelativePath}' is not a project or solution and contains none at its top level. {guidance}",
                outcomeOk: false);
        }

        // MSB1011 is the same class of fact one door along: the target is a directory holding
        // several projects, so MSBuild will not guess. It got the raw diagnostic and nothing else,
        // and run dbaa0580 spent steps 5, 6 and 7 reading scaffold files looking for the compile
        // error it sounded like. Named here for the reason MSB1003 is: the answer has to be in the
        // message the model is already reading.
        if (summary.Text.Contains("MSB1011", StringComparison.Ordinal))
        {
            string directory = Directory.Exists(verdict.FullPath)
                ? verdict.FullPath
                : System.IO.Path.GetDirectoryName(verdict.FullPath) ?? verdict.FullPath;
            List<string> held = [.. ProjectLocator.FindAllProjects(directory).Take(7)];

            string guidance = held.Count == 0
                ? "Name one project or solution rather than the folder."
                : "It holds " +
                  string.Join(", ", held.Take(6).Select(p => _guard.ToRelativePath(p))) +
                  (held.Count > 6 ? " and more" : string.Empty) +
                  " - build one of those by name.";

            return Observation.Ok(
                ToolName,
                payload,
                $"'{verdict.RelativePath}' is a folder with more than one project or solution in it, " +
                $"so the build had no single target. {guidance}",
                outcomeOk: false);
        }

        // "Build failed with 0 error(s)" is what the model reads when the parser recognised
        // nothing - a restore or SDK error in a format the regexes miss. Seven of those in the
        // 2026-08-06 runs, each answered with a blind identical retry. When there is nothing
        // parsed, the raw tail is the only information there is, so it goes in the message.
        //
        // Or when what was parsed says nothing: run dbaa0580's Compile rung reported, in full,
        // "error MSB4018:" - a code, a colon, and no cause, because MSB4018 puts the failing
        // exception on the lines after it in a format the parser drops. One parsed diagnostic was
        // enough to skip the fallback, so the emptiest possible message won. The test is whether
        // anything parsed actually says something, not how many things parsed.
        if (summary.TotalErrors == 0 || !diagnostics.Any(d => !string.IsNullOrWhiteSpace(d.Message)))
        {
            string tail = Tail(result.CombinedOutput);
            return Observation.Ok(
                ToolName,
                payload with { Diagnostics = tail },
                $"Build failed with exit code {result.ExitCode}, but no compiler diagnostics could be parsed " +
                $"from its output. The output ends:\n{tail}",
                outcomeOk: false);
        }

        // A failed build is a handled outcome, not a tool failure: this is the single most
        // useful observation the agent receives, and it must arrive as information to act on -
        // and as a failed *outcome*, or the progress machinery counts it as work that went well.
        // Run dbaa0580 failed three builds and the sentry and the intent ledger saw three
        // successes, because this was the one tool relaying an exit code that never said so.
        return Observation.Ok(
            ToolName, payload, $"Build failed with {summary.TotalErrors} error(s).", outcomeOk: false);
    }

    /// <summary>
    /// Removes the <c>*_wpftmp</c> projects the SDK's markup pass leaves behind when it fails.
    /// <para>
    /// Scoped to the directory that was built and to the scratch spelling
    /// <see cref="ProjectLocator.IsScratch"/> already knows, and it will not remove the target it
    /// was asked to build. A file that will not delete is left alone: this is housekeeping, and
    /// housekeeping never fails a build.
    /// </para>
    /// </summary>
    private void SweepScratchProjects(PathGuardResult verdict)
    {
        if (verdict.FullPath is null)
        {
            return;
        }

        string directory = Directory.Exists(verdict.FullPath)
            ? verdict.FullPath
            : System.IO.Path.GetDirectoryName(verdict.FullPath) ?? verdict.FullPath;

        try
        {
            foreach (string file in Directory.EnumerateFiles(directory)
                .Where(ProjectLocator.IsScratch)
                .Where(f => !string.Equals(f, verdict.FullPath, StringComparison.OrdinalIgnoreCase)))
            {
                File.Delete(file);
                _logger.LogInformation(
                    "Removed the SDK scratch project {File} left by a failed markup pass",
                    _guard.ToRelativePath(file));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Scratch projects under {Directory} could not be swept", directory);
        }
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
