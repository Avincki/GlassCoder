using System.ComponentModel;
using System.Globalization;
using GlassCoder.Tools.Changes;
using GlassCoder.Tools.Guardrails;
using GlassCoder.Tools.Processes;
using GlassCoder.Tools.Registry;
using Microsoft.Extensions.Logging;

namespace GlassCoder.Tools.Build;

/// <summary>Result payload of <c>launch_app</c>.</summary>
/// <param name="Path">The project that was launched.</param>
/// <param name="Started">Whether the application got as far as running.</param>
/// <param name="StayedUp">Whether it was still running when the timeout arrived.</param>
/// <param name="ExitCode">Its exit code, when it exited on its own.</param>
/// <param name="Error">Tail of anything it wrote to standard error.</param>
/// <param name="ElapsedMs">How long it ran.</param>
public sealed record LaunchAppResult(
    [property: Description("The project that was launched.")] string Path,
    [property: Description("True when the application ran rather than failing to start.")] bool Started,
    [property: Description("True when it was still running at the timeout - for a desktop app, the good outcome.")] bool StayedUp,
    [property: Description("Exit code, when it exited on its own.")] int ExitCode,
    [property: Description("Tail of standard error.")] string Error,
    [property: Description("How long it ran, in milliseconds.")] double ElapsedMs);

/// <summary>
/// <c>launch_app</c> - the runtime evidence a completion critique keeps asking for (workplan
/// task 71).
/// <para>
/// Twice a panel has refused finished work for want of proof that the application runs, and twice
/// the model has had no way to produce any: run <c>008007e1</c> on 2026-08-07, and run
/// <c>d5edbc59</c> at step 22, refuted 3/3. What was fixed in between was the gate's wording and
/// its concession behaviour, not the missing capability - so the refutation stayed correct and
/// stayed unanswerable, and the only available response was churn. Task 65 built the launch
/// machinery and gave it to the operator; the loop still had no way to start anything.
/// </para>
/// <para>
/// <strong>Deliberately not a screenshot.</strong> Pixels are a separate and much larger question.
/// "The application started, stayed up, and did not crash" is already a strictly better evidence
/// base than compile-and-unit-tests, and it is the part available now.
/// </para>
/// <para>
/// <strong>It runs on the host, and that is a real decision.</strong> The sandbox has no display,
/// so a desktop application cannot start in it - the same reason <c>GitTool</c> and the headless
/// reviewers run on the host. What bounds it is the timeout: the process tree is killed when the
/// clock runs out, every time, so a hung application costs one step rather than a run.
/// </para>
/// </summary>
public sealed class LaunchAppTool : IToolSet
{
    private const string ToolName = "launch_app";
    private const int MaxErrorCharacters = 2000;

    /// <summary>Longest a launch may take before the process tree is killed.</summary>
    private const int MaxTimeoutSeconds = 120;

    private readonly IProcessRunner _processes;
    private readonly IPathGuard _guard;
    private readonly RuntimeEvidence _evidence;
    private readonly ILogger<LaunchAppTool> _logger;

    /// <summary>Creates the tool.</summary>
    public LaunchAppTool(
        IProcessRunner processes,
        IPathGuard guard,
        RuntimeEvidence evidence,
        ILogger<LaunchAppTool>? logger = null)
    {
        _processes = processes;
        _guard = guard;
        _evidence = evidence;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<LaunchAppTool>.Instance;
    }

    /// <summary>Starts the application and reports whether it ran.</summary>
    [GlassCoderTool(ToolName, Order = 65)]
    [Description("Run the app to prove it starts. Build first. For a desktop app, still running at the "
        + "timeout is success. This is the runtime evidence to offer, not tests that parse XAML.")]
    public async Task<ToolObservation<LaunchAppResult>> LaunchAsync(
        [Description("Repo-relative project to run.")]
        string projectPath,
        [Description("Seconds to run before stopping it.")]
        int timeoutSeconds = 10,
        CancellationToken cancellationToken = default)
    {
        PathGuardResult verdict = _guard.Resolve(projectPath ?? string.Empty, PathAccess.Read);
        if (!verdict.Allowed || verdict.FullPath is null)
        {
            return Observation.Fail<LaunchAppResult>(ToolName, ToolErrorCodes.PathNotAllowed, verdict.Reason!);
        }

        if (Directory.Exists(verdict.FullPath))
        {
            return Observation.Fail<LaunchAppResult>(
                ToolName,
                ToolErrorCodes.InvalidArgument,
                $"'{verdict.RelativePath}' is a directory, and this needs one project to run.",
                "Pass the .csproj of the executable project - list_projects names them.");
        }

        int seconds = Math.Clamp(timeoutSeconds, 1, MaxTimeoutSeconds);

        ProcessRunResult result;
        try
        {
            result = await _processes.RunAsync(
                new ProcessRunRequest("dotnet", ["run", "--project", verdict.FullPath, "--no-build"])
                {
                    WorkingDirectory = Path.GetDirectoryName(verdict.FullPath),
                    Timeout = TimeSpan.FromSeconds(seconds),
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Observation.Fail<LaunchAppResult>(
                ToolName,
                ToolErrorCodes.SandboxUnavailable,
                $"The application could not be started: {ex.Message}");
        }

        // A desktop application that is still up when the clock runs out is the success case: it
        // started, drew its window and did not fall over. An application that exits immediately
        // with a non-zero code is the failure this is looking for.
        bool stayedUp = result.TimedOut;
        bool started = stayedUp || result.ExitCode == 0;

        LaunchAppResult payload = new(
            verdict.RelativePath!,
            started,
            stayedUp,
            result.TimedOut ? 0 : result.ExitCode,
            Cap(result.StandardError),
            result.Duration.TotalMilliseconds);

        string summary = (stayedUp, result.ExitCode) switch
        {
            (true, _) => string.Create(
                CultureInfo.InvariantCulture,
                $"'{verdict.RelativePath}' started and was still running after {seconds}s, then was " +
                $"stopped. It launches without crashing; whether the window is right is the " +
                $"operator's Run app to say."),
            (false, 0) => string.Create(
                CultureInfo.InvariantCulture,
                $"'{verdict.RelativePath}' ran and exited 0 after {result.Duration.TotalSeconds:F1}s."),
            _ => string.Create(
                CultureInfo.InvariantCulture,
                $"'{verdict.RelativePath}' exited {result.ExitCode} after {result.Duration.TotalSeconds:F1}s " +
                $"without staying up.{Describe(result.StandardError)}"),
        };

        // Kept for the completion critique, which is the panel that asked for this in the first
        // place and cannot see a tool observation on its own.
        _evidence.Record(summary, started);

        _logger.LogInformation(
            "launch_app on {Path}: started={Started}, stayedUp={StayedUp}, exit={ExitCode}",
            verdict.RelativePath, started, stayedUp, result.ExitCode);

        return Observation.Ok(ToolName, payload, summary, outcomeOk: started);
    }

    private static string Describe(string? standardError)
    {
        string error = Cap(standardError);
        return error.Length == 0 ? string.Empty : $" It wrote: {error}";
    }

    private static string Cap(string? text)
    {
        string value = (text ?? string.Empty).Trim();
        return value.Length <= MaxErrorCharacters ? value : value[..MaxErrorCharacters] + "…";
    }
}

/// <summary>
/// The last thing a run learned by actually running its application (workplan task 71).
/// <para>
/// A tool observation reaches the model and the transcript, and stops there. The completion
/// critique reads the verification summary, so runtime evidence that lived only in an observation
/// would answer the refutation everywhere except where the refutation is made. This carries it
/// the last step, keyed by run on the <c>VerificationRefusalTracker</c> pattern.
/// </para>
/// </summary>
public sealed class RuntimeEvidence
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _latest =
        new(StringComparer.Ordinal);

    /// <summary>Records what the last launch in this run showed.</summary>
    /// <param name="summary">What the tool told the model.</param>
    /// <param name="started">Whether the application ran.</param>
    public void Record(string summary, bool started)
    {
        ArgumentNullException.ThrowIfNull(summary);
        _latest[RunContext.Current.RunId] = $"Runtime: {(started ? "ok" : "FAILED")} - {summary}";
    }

    /// <summary>What this run has shown about running its application, or null if it never did.</summary>
    public string? Latest => _latest.GetValueOrDefault(RunContext.Current.RunId);
}
