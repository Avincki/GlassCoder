using System.Globalization;
using System.Text;
using Microsoft.Extensions.AI;

namespace GlassCoder.Core.Context;

/// <summary>Outcome of a compaction pass.</summary>
/// <param name="Messages">The window to send.</param>
/// <param name="Compacted">Whether anything was summarised.</param>
/// <param name="TurnsSummarised">How many messages were folded into the summary.</param>
public sealed record CompactionResult(IReadOnlyList<ChatMessage> Messages, bool Compacted, int TurnsSummarised);

/// <summary>
/// Summarises older turns when the window is over budget (CLAUDE.md §12, workplan task 12).
/// </summary>
public interface IConversationCompactor
{
    /// <summary>
    /// Compacts <paramref name="messages"/> to fit <paramref name="tokenBudget"/>, preserving
    /// the system prompt and the original goal.
    /// </summary>
    CompactionResult Compact(IReadOnlyList<ChatMessage> messages, int tokenBudget, int keepRecentTurns);
}

/// <summary>
/// Compacts by replacing older turns with a deterministic digest of what they did.
/// <para>
/// A model-written summary is the obvious alternative and it is the wrong default here: it
/// costs a model call inside the loop, it is non-reproducible so it silently contaminates
/// ablation arms, and it can hallucinate a history that never happened. What the agent
/// actually needs from its distant past is which tools it ran, on what, and how they turned
/// out - and that can be stated exactly.
/// </para>
/// </summary>
public sealed class DigestCompactor : IConversationCompactor
{
    private readonly ITokenEstimator _estimator;

    /// <summary>Creates the compactor.</summary>
    public DigestCompactor(ITokenEstimator estimator) => _estimator = estimator;

    /// <inheritdoc />
    public CompactionResult Compact(IReadOnlyList<ChatMessage> messages, int tokenBudget, int keepRecentTurns)
    {
        ArgumentNullException.ThrowIfNull(messages);

        if (_estimator.Estimate(messages) <= tokenBudget)
        {
            return new CompactionResult(messages, Compacted: false, TurnsSummarised: 0);
        }

        // The system prompt and the goal are load-bearing: without them the agent forgets what
        // it is doing, which is a worse failure than forgetting how it got here.
        List<ChatMessage> preserved = [.. messages.TakeWhile(m => m.Role == ChatRole.System)];
        int preservedCount = preserved.Count;

        for (int i = preservedCount; i < messages.Count; i++)
        {
            if (messages[i].Role == ChatRole.User)
            {
                preserved.Add(messages[i]);
                preservedCount = i + 1;
                break;
            }
        }

        int keep = Math.Max(0, Math.Min(keepRecentTurns, messages.Count - preservedCount));
        int summariseCount = messages.Count - preservedCount - keep;

        if (summariseCount <= 0)
        {
            // Nothing left to fold: the recent turns alone are over budget. Returning them
            // unchanged is right - the caller's token limit will stop the run rather than the
            // window silently dropping the very context the agent is working from.
            return new CompactionResult(messages, Compacted: false, TurnsSummarised: 0);
        }

        IReadOnlyList<ChatMessage> summarised = [.. messages.Skip(preservedCount).Take(summariseCount)];
        List<ChatMessage> window = [.. preserved, new ChatMessage(ChatRole.User, Digest(summarised))];
        window.AddRange(messages.Skip(preservedCount + summariseCount));

        return new CompactionResult(window, Compacted: true, TurnsSummarised: summariseCount);
    }

    private static string Digest(IReadOnlyList<ChatMessage> messages)
    {
        CultureInfo culture = CultureInfo.InvariantCulture;
        StringBuilder digest = new();
        digest.AppendLine(culture, $"[Earlier in this run, {messages.Count} messages were summarised to save context.]");

        List<string> calls = [];
        foreach (ChatMessage message in messages)
        {
            foreach (AIContent content in message.Contents)
            {
                if (content is FunctionCallContent call)
                {
                    string arguments = call.Arguments is { Count: > 0 }
                        ? string.Join(", ", call.Arguments.Select(a => $"{a.Key}={Shorten(a.Value?.ToString())}"))
                        : string.Empty;
                    calls.Add($"{call.Name}({arguments})");
                }
            }
        }

        if (calls.Count > 0)
        {
            digest.AppendLine("Tools already run, in order:");
            foreach (string call in calls)
            {
                digest.AppendLine(culture, $"  - {call}");
            }

            digest.AppendLine("Do not repeat a call above unless the file has changed since.");
        }

        string? lastAssistantText = messages
            .Where(m => m.Role == ChatRole.Assistant && !string.IsNullOrWhiteSpace(m.Text))
            .Select(m => m.Text)
            .LastOrDefault();

        if (!string.IsNullOrWhiteSpace(lastAssistantText))
        {
            digest.AppendLine(culture, $"Your last reasoning before this summary: {Shorten(lastAssistantText, 600)}");
        }

        return digest.ToString();
    }

    private static string Shorten(string? value, int maxLength = 80)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        string single = value.ReplaceLineEndings(" ");
        return single.Length <= maxLength ? single : string.Concat(single.AsSpan(0, maxLength), "…");
    }
}
