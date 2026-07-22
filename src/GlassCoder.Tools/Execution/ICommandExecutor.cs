namespace GlassCoder.Tools.Execution;

/// <summary>One command to execute on behalf of the agent.</summary>
/// <param name="FileName">Executable, for example <c>dotnet</c>.</param>
/// <param name="Arguments">Arguments, as a list so nothing needs shell quoting.</param>
public sealed record CommandRequest(string FileName, IReadOnlyList<string> Arguments)
{
    /// <summary>Absolute host directory to run in. Must be inside the workspace.</summary>
    public string? WorkingDirectory { get; init; }

    /// <summary>Timeout. Falls back to the configured default.</summary>
    public TimeSpan? Timeout { get; init; }

    /// <summary>
    /// Whether this command legitimately needs the network - a NuGet restore, and nothing else.
    /// The sandbox still has the final say.
    /// </summary>
    public bool RequiresNetwork { get; init; }
}

/// <summary>What a command did, and where it ran.</summary>
/// <param name="ExitCode">Process exit code; -1 when it was killed.</param>
/// <param name="StandardOutput">Captured stdout.</param>
/// <param name="StandardError">Captured stderr.</param>
/// <param name="Duration">Wall-clock.</param>
/// <param name="TimedOut">Whether it was killed for exceeding its timeout.</param>
/// <param name="Sandbox">Where it ran, for the transcript: <c>docker</c> or <c>host</c>.</param>
/// <param name="FailureReason">Why it could not run at all.</param>
public sealed record CommandResult(
    int ExitCode,
    string StandardOutput,
    string StandardError,
    TimeSpan Duration,
    bool TimedOut,
    string Sandbox,
    string? FailureReason = null)
{
    /// <summary>Whether the command ran and exited cleanly.</summary>
    public bool Succeeded => ExitCode == 0 && !TimedOut && FailureReason is null;

    /// <summary>Both streams, in the order a terminal would have shown them.</summary>
    public string CombinedOutput =>
        string.IsNullOrEmpty(StandardError) ? StandardOutput : $"{StandardOutput}{StandardError}";

    /// <summary>A result for a command that never started.</summary>
    public static CommandResult Unavailable(string reason, string sandbox) =>
        new(-1, string.Empty, string.Empty, TimeSpan.Zero, false, sandbox, reason);
}

/// <summary>
/// Runs a command somewhere the agent is allowed to run it (workplan task 17). Everything that
/// executes repository code - <c>build</c>, <c>run_tests</c>, later <c>bash</c> - goes through
/// this and nothing else.
/// </summary>
public interface ICommandExecutor
{
    /// <summary>Where this executor runs commands, for the transcript.</summary>
    string Sandbox { get; }

    /// <summary>Whether the executor can currently run anything.</summary>
    Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default);

    /// <summary>Runs a command to completion.</summary>
    Task<CommandResult> ExecuteAsync(CommandRequest request, CancellationToken cancellationToken = default);
}
