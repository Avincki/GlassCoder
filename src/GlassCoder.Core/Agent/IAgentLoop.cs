namespace GlassCoder.Core.Agent;

/// <summary>
/// The controller loop (CLAUDE.md §3.1). <b>This is the agent</b> - not the model, and not a
/// framework's function-invocation middleware.
/// </summary>
public interface IAgentLoop
{
    /// <summary>Runs one task to completion or to the first limit that trips.</summary>
    Task<AgentRunResult> RunAsync(AgentRunRequest request, CancellationToken cancellationToken = default);
}
