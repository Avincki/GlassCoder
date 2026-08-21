using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using GlassCoder.Models;
using GlassCoder.Models.Configuration;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GlassCoder.Core.Verification;

/// <summary>One critic's verdict on a change.</summary>
/// <param name="Refuted">Whether this critic thinks the change is wrong.</param>
/// <param name="Confidence">How sure it is, 0 to 1.</param>
/// <param name="Reason">Why, in one or two sentences.</param>
/// <param name="Available">
/// Whether this critic actually judged. False means it could not be reached, which is a
/// different fact from "it read the change and accepted it" and must never be tallied as one.
/// </param>
public sealed record CritiqueVerdict(
    [property: JsonPropertyName("refuted")] bool Refuted,
    [property: JsonPropertyName("confidence")] double Confidence,
    [property: JsonPropertyName("reason")] string Reason,
    [property: JsonIgnore] bool Available = true)
{
    /// <summary>
    /// The lens this critic was asked to judge through. Stamped by the panel after parsing -
    /// <see cref="JsonIgnoreAttribute"/> is what keeps a critic from labelling itself.
    /// </summary>
    [JsonIgnore]
    public string? Lens { get; init; }
}

/// <summary>What a panel of critics concluded.</summary>
/// <param name="Refuted">Whether the panel refuted the change. Always false when inconclusive.</param>
/// <param name="Votes">Every verdict, including the ones in the minority and the ones that never arrived.</param>
/// <param name="RefutingVotes">How many critics refuted.</param>
/// <param name="Summary">What to tell the agent.</param>
public sealed record CritiqueResult(bool Refuted, IReadOnlyList<CritiqueVerdict> Votes, int RefutingVotes, string Summary)
{
    /// <summary>How many critics actually judged.</summary>
    public int RespondingVotes { get; init; }

    /// <summary>How many critics could not be reached.</summary>
    public int UnavailableVotes { get; init; }

    /// <summary>
    /// Whether too little of the panel voted to conclude anything. Distinct from
    /// <see cref="Refuted"/> being false, which is a finding rather than the absence of one.
    /// </summary>
    public bool Inconclusive { get; init; }

    /// <summary>The role the critics ran on, so the transcript records which oracle spoke.</summary>
    public string Role { get; init; } = string.Empty;

    /// <summary>The alias that role addresses. Empty when nothing configured the role.</summary>
    public string ModelId { get; init; } = string.Empty;

    /// <summary>
    /// The weights behind that alias, when the server names them.
    /// <para>
    /// The role alone was never enough: <c>critic</c> says which seat voted, not who sat in it,
    /// and the whole reason a panel is worth reading is that its judgement is a model's. A remote
    /// critic and a local one leave the same word in the transcript.
    /// </para>
    /// </summary>
    public string? Checkpoint { get; init; }

    /// <summary>Prompt tokens across the panel.</summary>
    public long InputTokens { get; init; }

    /// <summary>Completion tokens across the panel.</summary>
    public long OutputTokens { get; init; }

    /// <summary>What the panel cost, at the critic role's own prices rather than the worker's.</summary>
    public decimal EstimatedCostUsd { get; init; }
}

/// <summary>Critique settings (CLAUDE.md §8, workplan task 23).</summary>
public sealed class CritiqueOptions
{
    /// <summary>Configuration section these options bind from.</summary>
    public const string SectionName = "GlassCoder:Critique";

    /// <summary>Whether the critique rung runs at all. Off by default - it is a Phase 2 capability.</summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// The role the critics run on when a caller does not name one. Ideally a different model
    /// family from the worker, so the critic's blind spots are not the worker's blind spots.
    /// </summary>
    public string Role { get; set; } = ModelRoles.Critic;

    /// <summary>
    /// The role a caller gets when it asks for the second opinion rather than the default one -
    /// a hosted model, usually. Null means there is no second critic to offer, and the UI that
    /// offers one should say so rather than fail on press.
    /// </summary>
    public string? RemoteRole { get; set; }

    /// <summary>How many critics vote. An even number can tie; odd numbers are kinder.</summary>
    public int CriticCount { get; set; } = 3;

    /// <summary>Refuting votes needed to refute. Defaults to a majority of the critics that voted.</summary>
    public int RefutationThreshold { get; set; }

    /// <summary>
    /// How many critics must actually judge before the panel concludes anything. Below it the
    /// result is inconclusive rather than accepting. Defaults to a majority of the panel.
    /// </summary>
    public int Quorum { get; set; }

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
/// <para>
/// The panel judges <em>finished</em> work - a completion claim, a finished run - never an
/// intermediate step. Refutation is only a fair question of a claim the evidence could in
/// principle establish; asked of a single step toward a larger goal it refutes everything,
/// which is a panel nobody can act on (run 4b582162).
/// </para>
/// <para>
/// The role is chosen per call rather than per process. A hosted critic costs money and a local
/// one does not, so which oracle judges a run is a decision the caller makes before the run
/// starts - and one the transcript then records.
/// </para>
/// </summary>
public interface ICriticPanel
{
    /// <summary>Whether critique is switched on and the default critic can be addressed.</summary>
    bool Enabled { get; }

    /// <summary>
    /// Whether a named role can be critiqued on right now - configured, and holding whatever
    /// credential it declared it needs. A caller offering a critic should ask this first.
    /// </summary>
    bool CanCritique(string? role);

    /// <summary>The role a request for <paramref name="role"/> would actually run on.</summary>
    string ResolveRole(string? role);

    /// <summary>Asks the panel to try to refute the claim that a finished change meets its goal.</summary>
    /// <param name="goal">What the change was supposed to achieve.</param>
    /// <param name="change">The change itself.</param>
    /// <param name="evidence">What the harness established: verification summary and runtime evidence.</param>
    /// <param name="role">Which critic role to run on.</param>
    /// <param name="claim">
    /// The worker's own account of its work, rendered under its own heading. Separate from
    /// <paramref name="evidence"/> on purpose - it is an assertion, and filing it as evidence is
    /// how run 4c7de12b's panel came to reason from it.
    /// </param>
    /// <param name="cancellationToken">Cancellation.</param>
    Task<CritiqueResult> CritiqueAsync(
        string goal,
        string change,
        string evidence,
        string? role = null,
        string? claim = null,
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
    private readonly IServedModelIdentity? _identity;
    private readonly CritiqueOptions _options;
    private readonly ILogger<CriticPanel> _logger;

    /// <summary>Creates the panel.</summary>
    public CriticPanel(
        IChatClientFactory clients,
        IOptions<CritiqueOptions> options,
        ILogger<CriticPanel>? logger = null,
        IServedModelIdentity? identity = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        _clients = clients;
        _identity = identity;
        _options = options.Value;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<CriticPanel>.Instance;
    }

    /// <inheritdoc />
    public bool Enabled => CanCritique(null);

    /// <inheritdoc />
    public string ResolveRole(string? role) =>
        string.IsNullOrWhiteSpace(role) ? _options.Role : role;

    /// <inheritdoc />
    public bool CanCritique(string? role)
    {
        if (!_options.Enabled)
        {
            return false;
        }

        string resolved = ResolveRole(role);

        // Configured is not the same as usable: a hosted critic whose key never arrived is in the
        // dictionary and answers nothing.
        return _clients.ContainsRole(resolved) && _clients.GetRoleOptions(resolved).IsUsable;
    }

    /// <inheritdoc />
    public async Task<CritiqueResult> CritiqueAsync(
        string goal,
        string change,
        string evidence,
        string? role = null,
        string? claim = null,
        CancellationToken cancellationToken = default)
    {
        string resolved = ResolveRole(role);

        if (!_options.Enabled)
        {
            return new CritiqueResult(false, [], 0, "Critique is disabled.") { Role = resolved };
        }

        if (!CanCritique(resolved))
        {
            // Asking for a critic that cannot be addressed is a fact worth reporting, not a
            // silent fallback to a different oracle than the caller chose.
            return new CritiqueResult(
                false,
                [],
                0,
                $"Critique inconclusive: the critic role '{resolved}' is not configured or is missing its API key.")
            {
                Role = resolved,
                Inconclusive = true,
            };
        }

        int criticCount = Math.Max(1, _options.CriticCount);
        Channel<CriticAnswer> answers = Channel.CreateUnbounded<CriticAnswer>();

        // Genuine parallelism: the critics are independent, so their latency should overlap
        // rather than stack (CLAUDE.md §14).
        await Parallel.ForEachAsync(
            Enumerable.Range(0, criticCount),
            new ParallelOptions { MaxDegreeOfParallelism = criticCount, CancellationToken = cancellationToken },
            async (index, token) =>
            {
                CriticAnswer answer = await AskAsync(index, resolved, goal, change, evidence, claim, token).ConfigureAwait(false);
                await answers.Writer.WriteAsync(answer, token).ConfigureAwait(false);
            }).ConfigureAwait(false);

        answers.Writer.Complete();

        List<CritiqueVerdict> votes = [];
        long inputTokens = 0;
        long outputTokens = 0;
        await foreach (CriticAnswer answer in answers.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            votes.Add(answer.Verdict);
            inputTokens += answer.InputTokens;
            outputTokens += answer.OutputTokens;
        }

        // Stamped after the tally rather than inside it, so every way out of Tally - a full
        // panel, an inconclusive one - carries it without the tally knowing about model identity
        // at all. The role says which seat voted; this says who was sitting in it.
        return await Describe(Tally(votes, criticCount, resolved, inputTokens, outputTokens), resolved, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Names the model that judged, so a verdict is attributable to something more specific than
    /// a role. A critic that could not be resolved leaves both fields absent, which reads as
    /// unknown rather than as the alias standing in for the weights.
    /// </summary>
    private async Task<CritiqueResult> Describe(
        CritiqueResult result, string role, CancellationToken cancellationToken)
    {
        if (!_clients.ContainsRole(role))
        {
            return result;
        }

        string? checkpoint = _identity is null
            ? null
            : await _identity.ResolveAsync(role, cancellationToken).ConfigureAwait(false);

        return result with
        {
            ModelId = _clients.GetRoleOptions(role).ModelAlias,
            Checkpoint = checkpoint,
        };
    }

    /// <summary>
    /// Turns votes into a verdict.
    /// <para>
    /// The rule that matters here is that a critic which could not be reached is not a vote. The
    /// tally counts refutations, so counting an unreachable critic at all would make it
    /// arithmetically indistinguishable from one that read the change and accepted it - and with
    /// a hosted critic, unreachable is routine rather than exotic: a rate limit, a timeout, an
    /// expired key. Below quorum the panel says so instead of concluding.
    /// </para>
    /// </summary>
    private CritiqueResult Tally(
        List<CritiqueVerdict> votes,
        int criticCount,
        string role,
        long inputTokens,
        long outputTokens)
    {
        List<CritiqueVerdict> responding = [.. votes.Where(v => v.Available)];
        int unavailable = votes.Count - responding.Count;
        int quorum = _options.Quorum > 0 ? _options.Quorum : (criticCount / 2) + 1;

        decimal cost = 0m;
        if (_clients.ContainsRole(role))
        {
            ModelRoleOptions prices = _clients.GetRoleOptions(role);
            cost = ((decimal)inputTokens / 1_000_000m * prices.InputCostPerMillionTokens) +
                   ((decimal)outputTokens / 1_000_000m * prices.OutputCostPerMillionTokens);
        }

        if (responding.Count < quorum)
        {
            string why = string.Join(" | ", votes.Where(v => !v.Available).Select(v => v.Reason).Take(2));
            _logger.LogWarning(
                "Critique panel inconclusive on role {Role}: {Responding}/{Total} critics voted, quorum {Quorum}",
                role, responding.Count, votes.Count, quorum);

            return new CritiqueResult(
                false,
                votes,
                0,
                $"Critique inconclusive: only {responding.Count} of {votes.Count} critics could be reached " +
                $"(quorum {quorum}). {why}".TrimEnd())
            {
                Role = role,
                RespondingVotes = responding.Count,
                UnavailableVotes = unavailable,
                Inconclusive = true,
                InputTokens = inputTokens,
                OutputTokens = outputTokens,
                EstimatedCostUsd = cost,
            };
        }

        int refuting = responding.Count(v => v.Refuted);
        int threshold = _options.RefutationThreshold > 0
            ? _options.RefutationThreshold
            : (responding.Count / 2) + 1;

        bool refuted = refuting >= threshold;

        // A panel that lost members never claims consensus, whichever way it landed.
        string caveat = unavailable > 0
            ? $" {unavailable} of {votes.Count} critics could not be reached, so this is not a full panel."
            : string.Empty;

        string summary = refuted
            ? $"{refuting}/{responding.Count} critics refuted the change: " +
              string.Join(" | ", responding.Where(v => v.Refuted).Select(v => v.Reason)) + caveat
            : $"{responding.Count - refuting}/{responding.Count} critics accepted the change.{caveat}";

        _logger.LogInformation(
            "Critique panel on role {Role}: {Refuting}/{Responding} refuted (threshold {Threshold}, {Unavailable} unreachable)",
            role, refuting, responding.Count, threshold, unavailable);

        return new CritiqueResult(refuted, votes, refuting, summary)
        {
            Role = role,
            RespondingVotes = responding.Count,
            UnavailableVotes = unavailable,
            InputTokens = inputTokens,
            OutputTokens = outputTokens,
            EstimatedCostUsd = cost,
        };
    }

    /// <summary>
    /// The worker's own account of its work, under a heading that says what it is.
    /// <para>
    /// It used to be appended to <c>evidence</c> and rendered under the <c>Evidence:</c> heading,
    /// one line below a system prompt telling critics to judge only the evidence in front of them.
    /// In run <c>4c7de12b</c> the two accepting critics reasoned from exactly that. An assertion
    /// filed as evidence is the harness misleading its own reviewers.
    /// </para>
    /// </summary>
    private static string Claim(string? claim) =>
        string.IsNullOrWhiteSpace(claim)
            ? string.Empty
            : $"The worker's own claim - this is an assertion, NOT evidence. Judge it against the " +
              $"evidence above; it establishes nothing on its own:\n{claim}\n\n";

    private async Task<CriticAnswer> AskAsync(
        int index,
        string role,
        string goal,
        string change,
        string evidence,
        string? claim,
        CancellationToken cancellationToken)
    {
        string lens = Lenses[index % Lenses.Length];

        // The word before the colon - "correctness", "regression", "evidence" - is the label a
        // verdict carries outward, so a reader knows which question each paragraph answers.
        string lensName = lens[..lens.IndexOf(':', StringComparison.Ordinal)];

        // The object of refutation is the CLAIM that finished work meets its goal - a statement
        // evidence can actually establish. The earlier wording asked whether "the change is
        // correct", and against one intermediate step no evidence can establish that, so a
        // temperature-0 critic refuted every step it saw (run 4b582162, 14 of 14).
        //
        // The evidence universe is stated because critics kept demanding what no tool can
        // produce: run 008007e1 was refuted 3/3 partly for lacking a runtime UI demonstration,
        // spiralled into re-scaffolding, and died at the token limit; run ca727be3 was refuted
        // 2/3 on the same ground with no tool that could ever answer it. A demand the worker
        // cannot meet is not a refutation, it is a deadlock.
        //
        // What the worker can meet has moved, and this sentence has to move with it or it becomes
        // the fourth recorded instance of a prompt asserting a limitation a later task removed.
        // Since 2026-08-15 a launch reads back the text the window is showing, without being asked,
        // so the values are in the evidence. Run dd11ef7c is what that costs when the prompt is
        // stale: three critics accepted 3/3, one of them calling a sentence that explicitly
        // disclaimed correctness "runtime confirmation", over a converter showing 0 beside 0.
        //
        // Bounded the same way as before. Pixels, layout and clipping are still nobody's to prove
        // here, and the readback is conditioned on the evidence saying it happened rather than on
        // any belief about the host: a host with no automation client says so in the launch summary
        // itself, and a critic that demanded a readback there would be back to the deadlock.
        List<ChatMessage> messages =
        [
            new(ChatRole.System,
                "You are a reviewer whose job is to REFUTE the claim that a finished change meets its goal. " +
                $"Judge it through this lens - {lens}. " +
                "Judge only the change and evidence in front of you; default to refuted:true when the " +
                "evidence cannot support the claim. " +
                "The worker can produce this evidence: builds, test runs, file contents, static checks, " +
                "and it can launch the application - which reports whether a window appeared and reads " +
                "back the text that window is showing, labels and box values alike. It cannot judge " +
                "pixels, layout or clipping, so the absence of *visual* proof is never, by itself, " +
                "grounds to refute; the absence of a *launch*, for a goal about a running application, " +
                "is. When the evidence carries a 'Window:' or 'Probe:' line, judge those values against " +
                "the goal - they are what the application actually showed. When it carries a launch that " +
                "read nothing while saying nothing about being unable to read, that is evidence the " +
                "worker could have produced and did not. Refute over evidence the worker could " +
                "have produced and did not, or over what the produced evidence actually shows. " +
                "Judge the behaviour the goal asks for, not its word choice: a window presenting the " +
                "required controls satisfies a goal that says 'dialog', and the like. " +
                "Reply with JSON only: {\"refuted\": bool, \"confidence\": number between 0 and 1, \"reason\": string}."),
            new(ChatRole.User,
                $"Goal:\n{goal}\n\nThe change:\n{change}\n\nEvidence:\n{evidence}\n\n" +
                Claim(claim) +
                "Can you refute the claim that this change meets the goal?"),
        ];

        try
        {
            IChatClient client = _clients.GetClient(role);
            ChatResponse response = await client
                .GetResponseAsync(messages, new ChatOptions { Temperature = 0f }, cancellationToken)
                .ConfigureAwait(false);

            return new CriticAnswer(
                Parse(response.Text) with { Lens = lensName },
                response.Usage?.InputTokenCount ?? 0,
                response.Usage?.OutputTokenCount ?? 0);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A critic that cannot be reached must not silently become an approving vote: it is
            // recorded as having failed to judge, and the tally excludes it.
            _logger.LogWarning(ex, "Critic {Index} on role {Role} failed; recording it as a non-vote", index, role);
            return new CriticAnswer(
                new CritiqueVerdict(false, 0d, $"Critic unavailable: {ex.Message}", Available: false) { Lens = lensName },
                0,
                0);
        }
    }

    private static CritiqueVerdict Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            // The critic answered, but said nothing usable. That is a failure to judge rather
            // than an acceptance, and the quorum should feel it.
            return new CritiqueVerdict(false, 0d, "The critic returned nothing.", Available: false);
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

    /// <summary>One critic's answer and what it cost to get it.</summary>
    private sealed record CriticAnswer(CritiqueVerdict Verdict, long InputTokens, long OutputTokens);
}
