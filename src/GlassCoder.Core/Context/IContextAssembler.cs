using Microsoft.Extensions.AI;

namespace GlassCoder.Core.Context;

/// <summary>What the assembler decided to send, and what it cost.</summary>
/// <param name="Messages">The window to send to the model.</param>
/// <param name="EstimatedTokens">Estimated size of that window.</param>
/// <param name="Compacted">Whether older turns were summarised to make it fit.</param>
/// <param name="TurnsSummarised">How many messages were folded into a summary.</param>
public sealed record AssembledContext(
    IReadOnlyList<ChatMessage> Messages,
    int EstimatedTokens,
    bool Compacted,
    int TurnsSummarised);

/// <summary>
/// Assembles the window for each step: system prompt, lean root context, then the conversation
/// (CLAUDE.md §12, workplan task 12).
/// </summary>
public interface IContextAssembler
{
    /// <summary>Builds the opening window for a run.</summary>
    IReadOnlyList<ChatMessage> CreateInitialMessages(string systemPrompt, string goal);

    /// <summary>Builds the window to send, compacting the history if it is over budget.</summary>
    AssembledContext Assemble(IReadOnlyList<ChatMessage> history);
}
