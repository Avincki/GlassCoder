using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace GlassCoder.Core.Context;

/// <summary>
/// Estimates the token cost of context before it is sent (workplan task 12).
/// </summary>
/// <remarks>
/// Behind an interface because the honest implementation depends on the served model's
/// tokeniser, which lives below the seam. The harness only needs an estimate that is stable
/// and never wildly low; the authoritative count comes back in <see cref="UsageDetails"/>.
/// </remarks>
public interface ITokenEstimator
{
    /// <summary>Estimated tokens for a string.</summary>
    int Estimate(string? text);

    /// <summary>Estimated tokens for one message, including its tool calls and results.</summary>
    int Estimate(ChatMessage message);

    /// <summary>Estimated tokens for a whole window.</summary>
    int Estimate(IEnumerable<ChatMessage> messages);
}

/// <summary>
/// A characters-per-token estimate (CLAUDE.md §19: do not encode assumptions about what is
/// served below the seam). Roughly four characters per token holds well enough for English
/// prose and source code to drive a compaction decision.
/// </summary>
public sealed class HeuristicTokenEstimator : ITokenEstimator
{
    private const int PerMessageOverhead = 4;

    private readonly double _charactersPerToken;

    /// <summary>Creates the estimator from bound configuration.</summary>
    public HeuristicTokenEstimator(IOptions<ContextOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        double configured = options.Value.CharactersPerToken;
        _charactersPerToken = configured > 0 ? configured : 4.0;
    }

    /// <inheritdoc />
    public int Estimate(string? text) =>
        string.IsNullOrEmpty(text) ? 0 : (int)Math.Ceiling(text.Length / _charactersPerToken);

    /// <inheritdoc />
    public int Estimate(ChatMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        int tokens = PerMessageOverhead;
        foreach (AIContent content in message.Contents)
        {
            tokens += content switch
            {
                TextContent text => Estimate(text.Text),
                FunctionCallContent call => Estimate(call.Name) +
                    (call.Arguments is null ? 0 : call.Arguments.Sum(a => Estimate(a.Key) + Estimate(a.Value?.ToString()))),
                FunctionResultContent result => Estimate(result.Result?.ToString()),
                _ => Estimate(content.ToString()),
            };
        }

        return tokens;
    }

    /// <inheritdoc />
    public int Estimate(IEnumerable<ChatMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);
        return messages.Sum(Estimate);
    }
}
