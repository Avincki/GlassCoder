using GlassCoder.Tools.Execution;

namespace GlassCoder.TestSupport;

/// <summary>
/// A scripted <see cref="ICommandExecutor"/>. Lets the build and test tools be exercised without
/// a container, a compiler or a minute of wall-clock per assertion.
/// </summary>
public sealed class ScriptedCommandExecutor : ICommandExecutor
{
    private readonly Queue<CommandResult> _scripted = new();

    /// <summary>Every command that was run, in order.</summary>
    public List<CommandRequest> Commands { get; } = [];

    /// <summary>Set to make the executor report itself unavailable.</summary>
    public string? Unavailable { get; set; }

    /// <inheritdoc />
    public string Sandbox => "test";

    /// <inheritdoc />
    public Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(Unavailable is null);

    /// <summary>Queues one result for the next command.</summary>
    public ScriptedCommandExecutor Enqueue(int exitCode, string output = "")
    {
        _scripted.Enqueue(new CommandResult(exitCode, output, string.Empty, TimeSpan.Zero, false, "test"));
        return this;
    }

    /// <inheritdoc />
    public Task<CommandResult> ExecuteAsync(CommandRequest request, CancellationToken cancellationToken = default)
    {
        if (Unavailable is not null)
        {
            return Task.FromResult(CommandResult.Unavailable(Unavailable, Sandbox));
        }

        Commands.Add(request);
        return Task.FromResult(_scripted.Count > 0
            ? _scripted.Dequeue()
            : new CommandResult(0, string.Empty, string.Empty, TimeSpan.Zero, false, Sandbox));
    }
}
