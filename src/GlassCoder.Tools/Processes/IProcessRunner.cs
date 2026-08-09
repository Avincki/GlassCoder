namespace GlassCoder.Tools.Processes;

/// <summary>One child process to run.</summary>
/// <param name="FileName">Executable to launch.</param>
/// <param name="Arguments">Arguments, passed as a list so nothing needs shell quoting.</param>
public sealed record ProcessRunRequest(string FileName, IReadOnlyList<string> Arguments)
{
    /// <summary>Working directory. Defaults to the current directory when null.</summary>
    public string? WorkingDirectory { get; init; }

    /// <summary>Hard timeout. The process tree is killed when it elapses.</summary>
    public TimeSpan? Timeout { get; init; }

    /// <summary>Environment variables to add or override. A null value removes the variable.</summary>
    public IReadOnlyDictionary<string, string?>? Environment { get; init; }

    /// <summary>Text written to the child's stdin, which is then closed.</summary>
    public string? StandardInput { get; init; }

    /// <summary>
    /// Called with each line of stdout as it arrives, for a caller that wants to watch rather
    /// than wait (workplan task 67).
    /// <para>
    /// The line is still captured into <see cref="ProcessRunResult.StandardOutput"/>, so a caller
    /// that sets this loses nothing - this is an addition to the buffered result, not an
    /// alternative to it. It is invoked on the reader thread, and a callback that throws is
    /// swallowed: a subprocess must not die because whoever was watching it made a mistake.
    /// </para>
    /// </summary>
    public Action<string>? OnOutputLine { get; init; }

    /// <summary>
    /// Polled with the child's process id while it runs. The first <see langword="true"/> ends the
    /// wait early and the process tree is killed, exactly as a timeout would kill it.
    /// <para>
    /// This exists for a caller whose evidence arrives before the process does anything else -
    /// <c>launch_app</c> wants "it drew a window", and a desktop application never exits to say
    /// so. Without it the only available signal is the clock, and the whole timeout is spent every
    /// time. Deliberately a predicate over a process id rather than anything window-shaped: this
    /// class runs builds and test suites too, and it has no business knowing what a window is.
    /// </para>
    /// <para>
    /// Invoked on the waiting thread, and a predicate that throws is swallowed and read as "not
    /// ready yet" - the same bargain <see cref="OnOutputLine"/> strikes, for the same reason.
    /// </para>
    /// </summary>
    public Func<int, bool>? ReadyWhen { get; init; }

    /// <summary>How often <see cref="ReadyWhen"/> is polled. Ignored when it is null.</summary>
    public TimeSpan ReadyPollInterval { get; init; } = TimeSpan.FromMilliseconds(200);
}

/// <summary>What a child process did.</summary>
/// <param name="ExitCode">Process exit code; -1 when it was killed.</param>
/// <param name="StandardOutput">Captured stdout.</param>
/// <param name="StandardError">Captured stderr.</param>
/// <param name="Duration">Wall-clock from launch to exit.</param>
/// <param name="TimedOut">Whether the run was killed for exceeding its timeout.</param>
public sealed record ProcessRunResult(
    int ExitCode,
    string StandardOutput,
    string StandardError,
    TimeSpan Duration,
    bool TimedOut)
{
    /// <summary>Whether the process exited cleanly.</summary>
    public bool Succeeded => ExitCode == 0 && !TimedOut;

    /// <summary>
    /// Whether the run ended because <see cref="ProcessRunRequest.ReadyWhen"/> said so, rather
    /// than by the process exiting or the clock running out. The process was still alive and was
    /// killed, so <see cref="ExitCode"/> is -1 and <see cref="TimedOut"/> is false: this is the
    /// third outcome, and it is the good one for anything that is not supposed to exit.
    /// </summary>
    public bool ReadySignalled { get; init; }
}

/// <summary>
/// The process-execution seam (CLAUDE.md §13, workplan task 8). Everything that shells out -
/// <c>build</c>, <c>run_tests</c>, later <c>bash</c> - goes through this interface so unit
/// tests can fake it and no test ever launches a real compiler.
/// </summary>
public interface IProcessRunner
{
    /// <summary>Runs a process to completion, capturing both streams.</summary>
    Task<ProcessRunResult> RunAsync(ProcessRunRequest request, CancellationToken cancellationToken = default);
}
