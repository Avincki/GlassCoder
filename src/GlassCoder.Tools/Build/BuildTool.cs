using System.ComponentModel;
using GlassCoder.Tools.Execution;
using GlassCoder.Tools.Guardrails;
using GlassCoder.Tools.Registry;
using GlassCoder.Tools.Verification;
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

    private readonly ICommandExecutor _executor;
    private readonly IPathGuard _guard;
    private readonly DiagnosticSummarizer _summarizer;
    private readonly SandboxOptions _sandbox;

    /// <summary>Creates the tool.</summary>
    public BuildTool(
        ICommandExecutor executor,
        IPathGuard guard,
        DiagnosticSummarizer summarizer,
        IOptions<SandboxOptions> sandbox)
    {
        ArgumentNullException.ThrowIfNull(sandbox);

        _executor = executor;
        _guard = guard;
        _summarizer = summarizer;
        _sandbox = sandbox.Value;
    }

    /// <summary>Builds a project, solution or directory.</summary>
    [GlassCoderTool(ToolName, Order = 50)]
    [Description("Build the project, solution or directory with dotnet build. This is the authoritative check "
        + "that the code compiles - run it after editing and before running tests. Returns a summary of the "
        + "first errors, not the whole build log.")]
    public async Task<ToolObservation<BuildResult>> BuildAsync(
        [Description("Project, solution or directory to build, relative to the repository root. Use '.' for everything.")]
        string path = ".",
        [Description("Whether the build may restore NuGet packages, which needs network access.")]
        bool allowRestore = true,
        CancellationToken cancellationToken = default)
    {
        PathGuardResult verdict = _guard.Resolve(path, PathAccess.Read);
        if (!verdict.Allowed || verdict.FullPath is null)
        {
            return Observation.Fail<BuildResult>(ToolName, ToolErrorCodes.PathNotAllowed, verdict.Reason!);
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

        CommandResult result = await _executor.ExecuteAsync(
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
        DiagnosticSummary summary = _summarizer.Summarise(diagnostics, $"Build of {verdict.RelativePath}");

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
            return Observation.Ok(ToolName, payload, $"Build succeeded ({summary.TotalWarnings} warnings).");
        }

        // A failed build is a handled outcome, not a tool failure: this is the single most
        // useful observation the agent receives, and it must arrive as information to act on.
        return Observation.Ok(ToolName, payload, $"Build failed with {summary.TotalErrors} error(s).");
    }
}
