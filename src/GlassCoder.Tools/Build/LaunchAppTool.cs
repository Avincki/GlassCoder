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
    [property: Description("How long it ran, in milliseconds.")] double ElapsedMs)
{
    /// <summary>Whether a window actually appeared, which is the strongest evidence here.</summary>
    [Description("True when the application drew a window - stronger evidence than merely staying up.")]
    public bool ShowedWindow { get; init; }
}

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
    private readonly IWindowPresence? _windows;
    private readonly IUiProbe? _probe;
    private readonly IChangeLog? _changes;
    private readonly ILogger<LaunchAppTool> _logger;

    /// <summary>Creates the tool.</summary>
    public LaunchAppTool(
        IProcessRunner processes,
        IPathGuard guard,
        RuntimeEvidence evidence,
        IWindowPresence? windows = null,
        IUiProbe? probe = null,
        IChangeLog? changes = null,
        ILogger<LaunchAppTool>? logger = null)
    {
        _processes = processes;
        _guard = guard;
        _evidence = evidence;
        _windows = windows;
        _probe = probe;
        _changes = changes;
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
        [Description("Optional window checks by x:Name, ';'-separated: 'Box=12' types, 'Btn!' clicks, "
            + "'Out?' reads.")]
        string? probe = null,
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

        // The application's own executable when it can be found, because the window belongs to
        // whatever process draws it: under `dotnet run` that is a grandchild, and the handle this
        // polls for would stay zero for the whole timeout. Falling back to `dotnet run` costs
        // nothing that was ever there - it is what this tool always did.
        string? executable = FindExecutable(verdict.FullPath);
        bool watched = executable is not null && _windows is not null;

        ProcessRunRequest request = executable is null
            ? new ProcessRunRequest("dotnet", ["run", "--project", verdict.FullPath, "--no-build"])
                { WorkingDirectory = Path.GetDirectoryName(verdict.FullPath) }
            : new ProcessRunRequest(executable, [])
                { WorkingDirectory = Path.GetDirectoryName(executable) };

        // What the model asked to be read off the window, if anything. Parsed before the launch so
        // a script that makes no sense is answered without starting anything.
        UiProbeScript script = UiProbeScript.Parse(probe);

        // The same launch twice over an unchanged tree cannot show anything new, and run ae72c5ad
        // spent a step proving it: the identical call, the identical string back, and nothing in
        // either saying it was a repeat. Same shape as build's and run_tests' reuse, so the sentry
        // that already counts the flag sees this one too. The probe is part of the key - a
        // different question of the same window is a different launch.
        string memo = string.Create(
            CultureInfo.InvariantCulture,
            $"{verdict.RelativePath}|{AppliedChanges()}|{probe ?? string.Empty}");

        if (_evidence.TryReuse(memo, out LaunchAppResult reused, out string reusedSummary))
        {
            return Observation.Ok(
                ToolName,
                reused,
                $"{reusedSummary} (Nothing has been applied since this ran, so the previous result " +
                "was reused. A launch of an unchanged tree cannot show anything new - change " +
                "something, or ask the probe a different question.)",
                outcomeOk: reused.Started,
                reused: true);
        }
        List<UiProbeReading> readings = [];
        bool canProbe = script.Steps.Count > 0 && _probe is not null && watched;

        request = request with
        {
            Timeout = TimeSpan.FromSeconds(seconds),
            ReadyWhen = watched ? _windows!.HasVisibleWindow : null,

            // The probe runs in the one gap where the application is both up and about to be
            // killed. It cannot extend the launch: the timeout still owns the clock. Its own
            // failure is recorded as a reading rather than allowed out - the runner would swallow
            // it, and a silent probe is the one outcome this must never report.
            OnReady = canProbe
                ? async (processId, token) =>
                {
                    try
                    {
                        readings.AddRange(
                            await _probe!.RunAsync(processId, script.Steps, token).ConfigureAwait(false));
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "The UI probe failed on {Path}", verdict.RelativePath);
                        readings.Add(new UiProbeReading(
                            "probe", Ok: false, Saw: null, Problem: $"it could not run: {ex.Message}"));
                    }
                }
                : null,
        };

        ProcessRunResult result;
        try
        {
            result = await _processes.RunAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Observation.Fail<LaunchAppResult>(
                ToolName,
                ToolErrorCodes.SandboxUnavailable,
                $"The application could not be started: {ex.Message}");
        }

        // Three ways to still be alive, and they are not equally good evidence. A window on the
        // screen is the thing the critique panels kept asking for; surviving the clock is the
        // weaker claim this tool used to make; and an immediate non-zero exit is the failure it
        // is looking for.
        bool showedWindow = result.ReadySignalled;
        bool stayedUp = result.TimedOut || showedWindow;
        bool started = stayedUp || result.ExitCode == 0;

        LaunchAppResult payload = new(
            verdict.RelativePath!,
            started,
            stayedUp,
            stayedUp ? 0 : result.ExitCode,
            Cap(result.StandardError),
            result.Duration.TotalMilliseconds)
        {
            ShowedWindow = showedWindow,
        };

        // Anything read out of the window replaces the hedge rather than joining it. "Whether the
        // window is right still needs eyes on it" is the honest thing to say about a launch that
        // only watched; said over a readback of the field it was asked about, it understates the
        // evidence the tool is holding - and it was the sentence three critics quoted back at run
        // ae72c5ad while the harness had nothing better to offer.
        string hedge = readings.Any(r => r.Saw is not null)
            ? "."
            : "; whether the window is *right* still needs eyes on it.";

        string summary = (showedWindow, stayedUp, result.ExitCode) switch
        {
            (true, _, _) => string.Create(
                CultureInfo.InvariantCulture,
                $"'{verdict.RelativePath}' started and drew a window after " +
                $"{result.Duration.TotalSeconds:F1}s, then was stopped. It runs and renders{hedge}"),

            // Watched and saw nothing is a different fact from never having looked, and saying
            // the first when the second is true would invent evidence against the change.
            (false, true, _) when watched => string.Create(
                CultureInfo.InvariantCulture,
                $"'{verdict.RelativePath}' was still running after {seconds}s but never drew a " +
                $"window. It launches without crashing; something is keeping the UI from " +
                $"appearing.{Describe(result.StandardError)}"),
            (false, true, _) => string.Create(
                CultureInfo.InvariantCulture,
                $"'{verdict.RelativePath}' started and was still running after {seconds}s, then was " +
                $"stopped. It launches without crashing; whether the window is right is the " +
                $"operator's Run app to say."),
            (false, false, 0) => string.Create(
                CultureInfo.InvariantCulture,
                $"'{verdict.RelativePath}' ran and exited 0 after {result.Duration.TotalSeconds:F1}s."),
            _ => string.Create(
                CultureInfo.InvariantCulture,
                $"'{verdict.RelativePath}' exited {result.ExitCode} after {result.Duration.TotalSeconds:F1}s " +
                $"without staying up.{Describe(result.StandardError)}"),
        };

        summary += ProbeReport(script, readings, showedWindow, watched);

        // Kept for the completion critique, which is the panel that asked for this in the first
        // place and cannot see a tool observation on its own - and keyed, so the next identical
        // call is answered rather than re-run.
        _evidence.Record(summary, started, memo, payload);

        _logger.LogInformation(
            "launch_app on {Path}: started={Started}, stayedUp={StayedUp}, window={ShowedWindow}, " +
            "probed={Probed}, exit={ExitCode}, {ElapsedMs:F0}ms",
            verdict.RelativePath, started, stayedUp, showedWindow, readings.Count, result.ExitCode,
            result.Duration.TotalMilliseconds);

        return Observation.Ok(ToolName, payload, summary, outcomeOk: started);
    }

    /// <summary>
    /// How much this run has applied so far, as one number.
    /// <para>
    /// Applied changes only, because that is the whole of what a relaunch could be showing:
    /// a proposal that was refused moved no bytes, and a status re-announced at the value it
    /// already held - which <c>AgentLoop</c> writes after every verified step - moves none either.
    /// That re-announcement is what made the build cache unreadable for months, so this counts
    /// states rather than listening for events.
    /// </para>
    /// </summary>
    private int AppliedChanges() =>
        _changes is null
            ? 0
            : _changes.All().Count(c =>
                string.Equals(c.RunId, RunContext.Current.RunId, StringComparison.Ordinal) &&
                c.Status == ChangeStatus.Applied);

    /// <summary>
    /// What the probe did, or why it did nothing - and never silence.
    /// <para>
    /// Every branch here says which of the four things happened, because they are four different
    /// facts and only one of them is evidence about the code: the probe read the window; the host
    /// has no probe to read it with; there was no window to read; the script did not parse. Run
    /// <c>ae72c5ad</c> is the whole argument for the distinction - a launch that reported less than
    /// it knew was read by three critics as a launch that had nothing to report.
    /// </para>
    /// </summary>
    private string ProbeReport(
        UiProbeScript script,
        IReadOnlyList<UiProbeReading> readings,
        bool showedWindow,
        bool watched)
    {
        string complaint = script.Problem is null ? string.Empty : $" The probe was only partly read: {script.Problem}.";

        if (script.Steps.Count == 0)
        {
            return complaint;
        }

        if (readings.Count > 0)
        {
            return $" Probe: {string.Join("; ", readings.Select(r => r.Describe()))}.{complaint}";
        }

        if (_probe is null)
        {
            return " No UI probe is available on this host, so nothing was read from the window." + complaint;
        }

        if (!watched)
        {
            return " There was no built executable to attach to, so the window could not be probed." + complaint;
        }

        return showedWindow
            ? " The probe ran and read nothing." + complaint
            : " No window appeared, so the probe never ran." + complaint;
    }

    /// <summary>
    /// The application's own executable under <c>bin</c>, or null when there is no obvious one.
    /// <para>
    /// Matched by name - <c>&lt;project&gt;.exe</c> - rather than taking any executable in the
    /// output, because <c>bin</c> is full of other projects' apphosts and picking the wrong one
    /// would launch the wrong application and report on it confidently. A project whose
    /// <c>AssemblyName</c> differs from its file name finds nothing here and falls back to
    /// <c>dotnet run</c>, which is the behaviour that shipped in task 71.
    /// </para>
    /// </summary>
    private static string? FindExecutable(string projectFullPath)
    {
        // The apphost is extensionless off Windows, which makes "is this the app or a stray file"
        // a guess rather than a match. Not worth being wrong about for a fallback that works.
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        string bin = Path.Combine(Path.GetDirectoryName(projectFullPath) ?? ".", "bin");
        if (!Directory.Exists(bin))
        {
            return null;
        }

        string expected = Path.GetFileNameWithoutExtension(projectFullPath) + ".exe";

        // Newest wins: Debug and Release both persist, and the one just built is the one the
        // model has been editing.
        return new DirectoryInfo(bin)
            .EnumerateFiles(expected, SearchOption.AllDirectories)
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .FirstOrDefault()?.FullName;
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

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, Launch> _launches =
        new(StringComparer.Ordinal);

    /// <summary>Records what the last launch in this run showed.</summary>
    /// <param name="summary">What the tool told the model.</param>
    /// <param name="started">Whether the application ran.</param>
    /// <param name="key">
    /// What this launch was: the project, the state of the change log, and the probe asked for.
    /// Two launches with the same key cannot see different things, which is what makes the second
    /// one reusable. Null skips the memo entirely.
    /// </param>
    /// <param name="payload">The result to hand back should the same launch be asked for again.</param>
    public void Record(string summary, bool started, string? key = null, LaunchAppResult? payload = null)
    {
        ArgumentNullException.ThrowIfNull(summary);
        _latest[RunContext.Current.RunId] = $"Runtime: {(started ? "ok" : "FAILED")} - {summary}";

        if (key is not null && payload is not null)
        {
            _launches[RunContext.Current.RunId] = new Launch(key, summary, payload);
        }
    }

    /// <summary>
    /// The previous launch, when it was this same launch - same project, same probe, and nothing
    /// applied in between.
    /// <para>
    /// Run <c>ae72c5ad</c> is what this is for. Step 12 launched the application; the panel refuted
    /// the work anyway; step 15 issued the identical call and got back a byte-identical string,
    /// which the next panel correctly read as a non-event. The launch was not wrong, it was spent -
    /// and nothing in the observation said so, exactly as <c>build</c>'s repeats said nothing
    /// before task 74. The sentry already counts this flag.
    /// </para>
    /// </summary>
    public bool TryReuse(string key, out LaunchAppResult payload, out string summary)
    {
        if (_launches.TryGetValue(RunContext.Current.RunId, out Launch? previous) &&
            string.Equals(previous.Key, key, StringComparison.Ordinal))
        {
            payload = previous.Payload;
            summary = previous.Summary;
            return true;
        }

        payload = null!;
        summary = string.Empty;
        return false;
    }

    /// <summary>What this run has shown about running its application, or null if it never did.</summary>
    public string? Latest => _latest.GetValueOrDefault(RunContext.Current.RunId);

    private sealed record Launch(string Key, string Summary, LaunchAppResult Payload);
}
