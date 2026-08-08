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
