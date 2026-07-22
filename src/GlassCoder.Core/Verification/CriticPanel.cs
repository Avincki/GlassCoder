using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using GlassCoder.Models;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GlassCoder.Core.Verification;

/// <summary>One critic's verdict on a change.</summary>
/// <param name="Refuted">Whether this critic thinks the change is wrong.</param>
/// <param name="Confidence">How sure it is, 0 to 1.</param>
/// <param name="Reason">Why, in one or two sentences.</param>
public sealed record CritiqueVerdict(
    [property: JsonPropertyName("refuted")] bool Refuted,
    [property: JsonPropertyName("confidence")] double Confidence,
    [property: JsonPropertyName("reason")] string Reason);

/// <summary>What a panel of critics concluded.</summary>
/// <param name="Refuted">Whether the panel refuted the change.</param>
/// <param name="Votes">Every verdict, including the ones in the minority.</param>
/// <param name="RefutingVotes">How many critics refuted.</param>
/// <param name="Summary">What to tell the agent.</param>
public sealed record CritiqueResult(bool Refuted, IReadOnlyList<CritiqueVerdict> Votes, int RefutingVotes, string Summary);

/// <summary>Critique settings (CLAUDE.md §8, workplan task 23).</summary>
public sealed class CritiqueOptions
{
    /// <summary>Configuration section these options bind from.</summary>
    public const string SectionName = "GlassCoder:Critique";

    /// <summary>Whether the critique rung runs at all. Off by default - it is a Phase 2 capability.</summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// The role the critics run on. Ideally a different model family from the worker, so the
    /// critic's blind spots are not the worker's blind spots.
    /// </summary>
    public string Role { get; set; } = ModelRoles.Critic;

    /// <summary>How many critics vote. An even number can tie; odd numbers are kinder.</summary>
    public int CriticCount { get; set; } = 3;

    /// <summary>Refuting votes needed to refute. Defaults to a simple majority.</summary>
    public int RefutationThreshold { get; set; }

    /// <summary>Whether a refutation blocks the change, or is only reported.</summary>
    public bool Gates { get; set; }
}

/// <summary>
/// The self-critique and multi-critic refutation rung (CLAUDE.md §8, workplan task 23).
/// <para>
/// Each critic is asked to <em>refute</em> the change rather than to review it. That asymmetry
/// is the point: "is this good?" invites agreement from a model that has just been shown a
/// plausible-looking diff, while "find what is wrong with this" gives it a job it can fail at
/// honestly. Critics vote independently and never see each other's verdicts.
/// </para>
/// </summary>
public interface ICriticPanel
{
    /// <summary>Whether critique is switched on.</summary>
    bool Enabled { get; }

    /// <summary>Asks the panel to try to refute a change.</summary>
    Task<CritiqueResult> CritiqueAsync(
        string goal,
        string change,
        string evidence,
        CancellationToken cancellationToken = default);
}

/// <summary>Default <see cref="ICriticPanel"/>: N independent refutation attempts, fanned out in parallel.</summary>
public sealed class CriticPanel : ICriticPanel
{
    private static readonly JsonSerializerOptions VerdictOptions = new(JsonSerializerDefaults.Web);

    private static readonly string[] Lenses =
    [
        "correctness: does the change actually do what the goal asked, in every case the goal implies",
        "regression: does the change break behaviour that other code depends on",
        "evidence: does the evidence given actually prove the change works, or only that it compiles",
    ];

    private readonly IChatClientFactory _clients;
    private readonly CritiqueOptions _options;
    private readonly ILogger<CriticPanel> _logger;

    /// <summary>Creates the panel.</summary>
    public CriticPanel(
        IChatClientFactory clients,
        IOptions<CritiqueOptions> options,
        ILogger<CriticPanel>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        _clients = clients;
        _options = options.Value;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<CriticPanel>.Instance;
    }

    /// <inheritdoc />
    public bool Enabled => _options.Enabled && _clients.ContainsRole(_options.Role);

    /// <inheritdoc />
    public async Task<CritiqueResult> CritiqueAsync(
        string goal,
        string change,
        string evidence,
        CancellationToken cancellationToken = default)
    {
        if (!Enabled)
        {
            return new CritiqueResult(false, [], 0, "Critique is disabled.");
        }

        int criticCount = Math.Max(1, _options.CriticCount);
        Channel<CritiqueVerdict> verdicts = Channel.CreateUnbounded<CritiqueVerdict>();

        // Genuine parallelism: the critics are independent, so their latency should overlap
        // rather than stack (CLAUDE.md §14).
        await Parallel.ForEachAsync(
            Enumerable.Range(0, criticCount),
            new ParallelOptions { MaxDegreeOfParallelism = criticCount, CancellationToken = cancellationToken },
            async (index, token) =>
            {
                CritiqueVerdict verdict = await AskAsync(index, goal, change, evidence, token).ConfigureAwait(false);
                await verdicts.Writer.WriteAsync(verdict, token).ConfigureAwait(false);
            }).ConfigureAwait(false);

        verdicts.Writer.Complete();

        List<CritiqueVerdict> votes = [];
        await foreach (CritiqueVerdict verdict in verdicts.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            votes.Add(verdict);
        }

        int refuting = votes.Count(v => v.Refuted);
        int threshold = _options.RefutationThreshold > 0
            ? _options.RefutationThreshold
            : (criticCount / 2) + 1;

        bool refuted = refuting >= threshold;
        string summary = refuted
            ? $"{refuting}/{votes.Count} critics refuted the change: " +
              string.Join(" | ", votes.Where(v => v.Refuted).Select(v => v.Reason))
            : $"{votes.Count - refuting}/{votes.Count} critics accepted the change.";

        _logger.LogInformation("Critique panel: {Refuting}/{Total} refuted (threshold {Threshold})",
            refuting, votes.Count, threshold);

        return new CritiqueResult(refuted, votes, refuting, summary);
    }

    private async Task<CritiqueVerdict> AskAsync(
        int index,
        string goal,
        string change,
        string evidence,
        CancellationToken cancellationToken)
    {
        string lens = Lenses[index % Lenses.Length];

        List<ChatMessage> messages =
        [
            new(ChatRole.System,
                "You are a reviewer whose job is to REFUTE a proposed code change. " +
                $"Judge it through this lens - {lens}. " +
                "Default to refuted:true when the evidence does not establish that the change is correct. " +
                "Reply with JSON only: {\"refuted\": bool, \"confidence\": number between 0 and 1, \"reason\": string}."),
            new(ChatRole.User,
                $"Goal:\n{goal}\n\nProposed change:\n{change}\n\nEvidence offered:\n{evidence}\n\n" +
                "Can you refute this change?"),
        ];

        try
        {
            IChatClient client = _clients.GetClient(_options.Role);
            ChatResponse response = await client
                .GetResponseAsync(messages, new ChatOptions { Temperature = 0f }, cancellationToken)
                .ConfigureAwait(false);

            return Parse(response.Text);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A critic that cannot be reached must not silently become an approving vote.
            _logger.LogWarning(ex, "Critic {Index} failed; counting it as a refusal to judge", index);
            return new CritiqueVerdict(false, 0d, $"Critic unavailable: {ex.Message}");
        }
    }

    private static CritiqueVerdict Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return new CritiqueVerdict(false, 0d, "The critic returned nothing.");
        }

        int start = text.IndexOf('{', StringComparison.Ordinal);
        int end = text.LastIndexOf('}');
        if (start >= 0 && end > start)
        {
            try
            {
                CritiqueVerdict? verdict = JsonSerializer.Deserialize<CritiqueVerdict>(
                    text[start..(end + 1)], VerdictOptions);

                if (verdict is not null)
                {
                    return verdict;
                }
            }
            catch (JsonException)
            {
                // Fall through to the text heuristic below.
            }
        }

        bool refuted = text.Contains("refuted\": true", StringComparison.OrdinalIgnoreCase) ||
                       text.Contains("refuted:true", StringComparison.OrdinalIgnoreCase);

        return new CritiqueVerdict(refuted, 0.3d, text.Length > 400 ? text[..400] : text);
    }
}
