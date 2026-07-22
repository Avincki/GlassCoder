using Microsoft.Extensions.AI;

namespace GlassCoder.Tools.Registry;

/// <summary>
/// The typed tool registry (CLAUDE.md §7, workplan task 7): schemas generated from method
/// signatures, and one place that executes a call and turns anything that goes wrong into an
/// observation.
/// </summary>
public interface IToolRegistry
{
    /// <summary>
    /// Tools in advertised order. The order is part of the contract: cheaper, higher-value
    /// oracles come first so the model reaches for them first.
    /// </summary>
    IReadOnlyList<AIFunction> Functions { get; }

    /// <summary>The same tools as <see cref="ChatOptions.Tools"/> expects them.</summary>
    IReadOnlyList<AITool> Tools { get; }

    /// <summary>Looks up a tool by its wire name.</summary>
    bool TryGetFunction(string name, out AIFunction? function);

    /// <summary>
    /// Executes one model-issued tool call. Never throws for a tool-level problem: an unknown
    /// tool, unbindable arguments and a tool that faulted all come back as an observation the
    /// loop can feed straight to the model.
    /// </summary>
    Task<ToolInvocation> InvokeAsync(FunctionCallContent call, CancellationToken cancellationToken = default);
}
