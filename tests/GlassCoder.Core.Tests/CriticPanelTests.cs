using GlassCoder.Core.Verification;
using GlassCoder.Models;
using GlassCoder.Models.Configuration;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace GlassCoder.Core.Tests;

/// <summary>
/// The critic panel (workplan task 23).
/// <para>
/// The property under test throughout is that a critic which did not answer is not a vote.
/// Against a local endpoint that is nearly unreachable - the server is up or the run fails - but
/// against a hosted API it is routine: a rate limit, a timeout, an expired key. The tally counts
/// refutations, so an unreachable critic counted at all is arithmetically indistinguishable from
/// one that read the change and approved it.
/// </para>
/// </summary>
public sealed class CriticPanelTests
{
    // Matched against the whole system prompt, so they carry the "lens - " prefix: the fixed
    // instruction text mentions "evidence" too, and a looser match selects every critic at once.
    private const string Correctness = "lens - correctness";
    private const string Evidence = "lens - evidence";

    [Fact]
    public async Task An_unreachable_panel_is_inconclusive_rather_than_approving()
    {
        CriticPanel panel = Panel(_ => throw new HttpRequestException("401 Unauthorized"));

        CritiqueResult result = await panel.CritiqueAsync("goal", "change", "evidence");

        result.Inconclusive.ShouldBeTrue();
        result.Refuted.ShouldBeFalse();
        result.RespondingVotes.ShouldBe(0);
        result.UnavailableVotes.ShouldBe(3);
        result.Summary.ShouldContain("inconclusive");
        result.Summary.ShouldNotContain("accepted the change");
    }

    [Fact]
    public async Task Two_unreachable_critics_and_one_refutation_do_not_produce_not_refuted()
    {
        // The exact arithmetic the quorum exists to prevent: with three critics and a threshold
        // of two, two silent critics plus one genuine refutation used to tally as "not refuted".
        CriticPanel panel = Panel(lens => lens.Contains(Correctness, StringComparison.Ordinal)
            ? Verdict(refuted: true, "Ascending sorts the caller's array in place.")
            : throw new TimeoutException("the critic timed out"));

        CritiqueResult result = await panel.CritiqueAsync("goal", "change", "evidence");

        result.Inconclusive.ShouldBeTrue();
        result.Refuted.ShouldBeFalse();
        result.RespondingVotes.ShouldBe(1);
        result.Summary.ShouldContain("only 1 of 3");
        result.Summary.ShouldNotContain("accepted the change");
    }

    [Fact]
    public async Task A_panel_that_lost_a_member_never_claims_consensus()
    {
        CriticPanel panel = Panel(lens => lens.Contains(Evidence, StringComparison.Ordinal)
            ? throw new HttpRequestException("429 Too Many Requests")
            : Verdict(refuted: false, "The change looks right."));

        CritiqueResult result = await panel.CritiqueAsync("goal", "change", "evidence");

        // Quorum met, so this is a real finding - but it is not a full panel and must not read
        // like one.
        result.Inconclusive.ShouldBeFalse();
        result.Refuted.ShouldBeFalse();
        result.RespondingVotes.ShouldBe(2);
        result.UnavailableVotes.ShouldBe(1);
        result.Summary.ShouldContain("2/2 critics accepted");
        result.Summary.ShouldContain("not a full panel");
    }

    [Fact]
    public async Task A_full_panel_that_refutes_says_why()
    {
        CriticPanel panel = Panel(_ => Verdict(refuted: true, "It sorts in place."));

        CritiqueResult result = await panel.CritiqueAsync("goal", "change", "evidence");

        result.Refuted.ShouldBeTrue();
        result.RefutingVotes.ShouldBe(3);
        result.RespondingVotes.ShouldBe(3);
        result.Summary.ShouldContain("It sorts in place.");
        result.Summary.ShouldNotContain("not a full panel");
    }

    [Fact]
    public async Task The_critics_are_told_what_evidence_the_worker_can_produce()
    {
        // Run 008007e1 was refuted 3/3 partly for lacking a runtime UI demonstration and
        // spiralled into re-scaffolding until the token limit; run ca727be3 drew the same
        // demand. No tool can answer it - live UI proof is the operator's Run-app button - so
        // the prompt bounds refutation to evidence a worker can actually produce.
        List<string> prompts = [];
        CriticPanel panel = Panel(system =>
        {
            prompts.Add(system);
            return Verdict(refuted: false, "fine");
        });

        await panel.CritiqueAsync("goal", "change", "evidence");

        prompts.Count.ShouldBe(3);
        prompts.ShouldAllBe(p => p.Contains("cannot launch the application", StringComparison.Ordinal));
        prompts.ShouldAllBe(p => p.Contains("never, by itself, grounds to refute", StringComparison.Ordinal));

        // Word-choice literalism refuted runs 008007e1 and f4ed50e0 over "dialog" versus
        // "window" while the built behaviour covered the ask - two-for-two across critiqued
        // runs of that goal.
        prompts.ShouldAllBe(p => p.Contains("not its word choice", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_critic_that_answers_with_nothing_has_not_judged()
    {
        // An empty completion is a failure to judge, not an acceptance.
        CriticPanel panel = Panel(_ => new ChatResponse(new ChatMessage(ChatRole.Assistant, string.Empty)));

        CritiqueResult result = await panel.CritiqueAsync("goal", "change", "evidence");

        result.Inconclusive.ShouldBeTrue();
        result.RespondingVotes.ShouldBe(0);
    }

    [Fact]
    public async Task The_role_the_caller_asks_for_is_the_role_that_answers()
    {
        RoleAwareFactory factory = Factory();
        CriticPanel panel = new(factory, Options.Create(Settings()));

        await panel.CritiqueAsync("goal", "change", "evidence", "critic-remote");

        factory.Requested.Distinct().ShouldHaveSingleItem().ShouldBe("critic-remote");
    }

    [Fact]
    public async Task An_unconfigured_role_is_reported_rather_than_silently_swapped_for_the_default()
    {
        // Falling back to the local critic would answer a question the caller did not ask, and
        // the transcript would record the wrong oracle.
        RoleAwareFactory factory = Factory();
        CriticPanel panel = new(factory, Options.Create(Settings()));

        CritiqueResult result = await panel.CritiqueAsync("goal", "change", "evidence", "critic-nonexistent");

        result.Inconclusive.ShouldBeTrue();
        result.Summary.ShouldContain("critic-nonexistent");
        factory.Requested.ShouldBeEmpty("no model should be called for a role that is not configured");
    }

    [Fact]
    public void A_role_that_declares_it_needs_a_key_cannot_critique_without_one()
    {
        // This is the case the approval-gate button gets wrong when it only checks whether a role
        // is configured: present in the dictionary, and answers nothing.
        RoleAwareFactory factory = Factory(remoteRequiresKey: true);
        CriticPanel panel = new(factory, Options.Create(Settings()));

        panel.CanCritique("critic-remote").ShouldBeFalse();
        panel.CanCritique("critic").ShouldBeTrue();
    }

    [Fact]
    public void Critique_switched_off_disables_every_role()
    {
        CritiqueOptions off = Settings();
        off.Enabled = false;

        CriticPanel panel = new(Factory(), Options.Create(off));

        panel.Enabled.ShouldBeFalse();
        panel.CanCritique("critic-remote").ShouldBeFalse();
    }

    [Fact]
    public async Task The_panel_is_priced_at_its_own_role_rather_than_the_workers()
    {
        // A paid critic charged at the local worker's rate of zero makes Agent:MaxCostUsd a
        // budget that cannot trip.
        RoleAwareFactory factory = Factory();
        CriticPanel panel = new(factory, Options.Create(Settings()));

        CritiqueResult result = await panel.CritiqueAsync("goal", "change", "evidence", "critic-remote");

        result.Role.ShouldBe("critic-remote");
        result.InputTokens.ShouldBe(90);    // 3 critics x 30
        result.OutputTokens.ShouldBe(90);
        result.EstimatedCostUsd.ShouldBeGreaterThan(0m);
    }

    [Fact]
    public async Task Every_verdict_carries_the_lens_it_was_asked_to_judge_through()
    {
        // The lens is stamped by the panel after parsing, so a critic that writes a "lens"
        // field into its own JSON is labelling nothing - a reviewer trusted to grade itself
        // would be the one thing on the panel with no oracle behind it.
        CriticPanel panel = Panel(_ => new ChatResponse(new ChatMessage(
            ChatRole.Assistant,
            "{\"refuted\": false, \"confidence\": 0.8, \"reason\": \"fine\", \"lens\": \"spoofed\"}")));

        CritiqueResult result = await panel.CritiqueAsync("goal", "change", "evidence");

        result.Votes.Select(v => v.Lens)
            .ShouldBe(["correctness", "regression", "evidence"], ignoreOrder: true);
    }

    [Fact]
    public async Task An_unreachable_critic_still_carries_its_lens()
    {
        // "The evidence critic could not be reached" is readable; an unlabelled non-vote is not.
        CriticPanel panel = Panel(lens => lens.Contains(Evidence, StringComparison.Ordinal)
            ? throw new HttpRequestException("429 Too Many Requests")
            : Verdict(refuted: false, "fine"));

        CritiqueResult result = await panel.CritiqueAsync("goal", "change", "evidence");

        result.Votes.Single(v => !v.Available).Lens.ShouldBe("evidence");
    }

    private static ChatResponse Verdict(bool refuted, string reason) =>
        new(new ChatMessage(
            ChatRole.Assistant,
            $"{{\"refuted\": {(refuted ? "true" : "false")}, \"confidence\": 0.8, \"reason\": \"{reason}\"}}"))
        {
            Usage = new UsageDetails { InputTokenCount = 30, OutputTokenCount = 30, TotalTokenCount = 60 },
        };

    private static CritiqueOptions Settings() => new()
    {
        Enabled = true,
        Role = "critic",
        RemoteRole = "critic-remote",
        CriticCount = 3,
        Quorum = 2,
    };

    private static CriticPanel Panel(Func<string, ChatResponse> respond) =>
        new(Factory(respond), Options.Create(Settings()));

    private static RoleAwareFactory Factory(
        Func<string, ChatResponse>? respond = null,
        bool remoteRequiresKey = false)
    {
        respond ??= _ => Verdict(refuted: false, "fine");
        LensScriptedClient client = new(respond);

        return new RoleAwareFactory(new Dictionary<string, (IChatClient, ModelRoleOptions)>(StringComparer.OrdinalIgnoreCase)
        {
            ["critic"] = (client, new ModelRoleOptions { Endpoint = "http://localhost:8003/v1", ModelAlias = "critic" }),
            ["critic-remote"] = (client, new ModelRoleOptions
            {
                Endpoint = "https://api.anthropic.com/v1",
                ModelAlias = "claude-opus-4-8",
                RequiresApiKey = remoteRequiresKey,
                InputCostPerMillionTokens = 5m,
                OutputCostPerMillionTokens = 25m,
            }),
        });
    }

    /// <summary>
    /// Answers according to the lens in the system prompt rather than call order. The critics run
    /// in parallel, so anything keyed on arrival order would be a flaky test.
    /// </summary>
    private sealed class LensScriptedClient(Func<string, ChatResponse> respond) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            string system = messages.First(m => m.Role == ChatRole.System).Text ?? string.Empty;
            return Task.FromResult(respond(system));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            ChatResponse response = await GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
            foreach (ChatResponseUpdate update in response.ToChatResponseUpdates())
            {
                yield return update;
            }
        }

        public object? GetService(Type serviceType, object? serviceKey = null) =>
            serviceKey is null && serviceType?.IsInstanceOfType(this) == true ? this : null;

        public void Dispose()
        {
        }
    }

    /// <summary>A factory that can tell its roles apart, which the shared fake deliberately cannot.</summary>
    private sealed class RoleAwareFactory(IDictionary<string, (IChatClient Client, ModelRoleOptions Options)> roles)
        : IChatClientFactory
    {
        private readonly Lock _gate = new();

        public List<string> Requested { get; } = [];

        public IReadOnlyList<string> Roles => [.. roles.Keys];

        public string DefaultRole => ModelRoles.Critic;

        public bool ContainsRole(string role) => roles.ContainsKey(role);

        public IChatClient GetClient(string? role = null)
        {
            string resolved = role ?? DefaultRole;
            lock (_gate)
            {
                Requested.Add(resolved);
            }

            return roles[resolved].Client;
        }

        public ModelRoleOptions GetRoleOptions(string? role = null) => roles[role ?? DefaultRole].Options;
    }
}
