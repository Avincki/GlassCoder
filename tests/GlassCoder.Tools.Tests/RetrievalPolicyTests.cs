using GlassCoder.TestSupport;
using GlassCoder.Tools.Changes;
using GlassCoder.Tools.DependencyInjection;
using GlassCoder.Tools.Guardrails;
using GlassCoder.Tools.Retrieval;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace GlassCoder.Tools.Tests;

/// <summary>
/// The retrieval gate (workplan task 55): admission by budget, indication and progress, with
/// every refusal carrying a stable code. Nothing here opens a socket - the network arrives in
/// task 57, behind this.
/// </summary>
public sealed class RetrievalPolicyTests : IDisposable
{
    public RetrievalPolicyTests() => RunContext.Set(new RunContext("run-1", "task-1"));

    public void Dispose() => RunContext.Clear();

    [Fact]
    public void A_disabled_server_is_refused_even_when_the_master_switch_is_on()
    {
        IRetrievalPolicy policy = Policy(o =>
        {
            o.Enabled = true;
            o.AllowProactive = true;
            o.Learn.Enabled = true;
            o.GitHub.Enabled = false;
        });

        policy.TryAdmit(Request(RetrievalServer.Learn), out _).ShouldBeTrue();

        policy.TryAdmit(Request(RetrievalServer.GitHub), out RetrievalDenial? denial).ShouldBeFalse();
        denial!.Code.ShouldBe(ToolErrorCodes.RetrievalDisabled);
    }

    /// <summary>
    /// The default refusal, and the reason the feature is inert rather than merely off: nothing
    /// answers <see cref="IRetrievalSignals"/> until task 59.
    /// </summary>
    [Fact]
    public void Without_a_signal_or_proactive_permission_nothing_is_admitted()
    {
        IRetrievalPolicy policy = Policy(o =>
        {
            o.Enabled = true;
            o.Learn.Enabled = true;
            o.AllowProactive = false;
        });

        policy.TryAdmit(Request(), out RetrievalDenial? denial).ShouldBeFalse();
        denial!.Code.ShouldBe(ToolErrorCodes.RetrievalNotIndicated);
        denial.Hint.ShouldContain("find_symbol");
    }

    [Fact]
    public void A_signal_admits_what_proactive_permission_would_have()
    {
        IRetrievalPolicy policy = Policy(
            o =>
            {
                o.Enabled = true;
                o.Learn.Enabled = true;
                o.AllowProactive = false;
            },
            signals: new StubSignals("CS0246 on Microsoft.Fake.Type, declared by no workspace source"));

        policy.TryAdmit(Request(), out RetrievalDenial? denial).ShouldBeTrue();
        denial.ShouldBeNull();
    }

    [Fact]
    public void The_call_budget_is_spent_by_calls_that_were_recorded()
    {
        (IRetrievalPolicy policy, ChangeLog changes) = PolicyWithLog(o =>
        {
            o.Enabled = true;
            o.Learn.Enabled = true;
            o.AllowProactive = true;
            o.MaxCallsPerRun = 2;
            o.MaxCallsWithoutAppliedChange = 0;
        });

        for (int i = 0; i < 2; i++)
        {
            policy.TryAdmit(Request(), out _).ShouldBeTrue();
            policy.RecordCall(Request(), charsReturned: 100);
        }

        policy.TryAdmit(Request(), out RetrievalDenial? denial).ShouldBeFalse();
        denial!.Code.ShouldBe(ToolErrorCodes.RetrievalBudgetExhausted);

        policy.Stats.Allowed.ShouldBe(2);
        policy.Stats.CharsReturned.ShouldBe(200);
        policy.Stats.Blocked[ToolErrorCodes.RetrievalBudgetExhausted].ShouldBe(1);
        changes.All().ShouldBeEmpty();
    }

    /// <summary>
    /// The anti-search loop. Two calls that bought nothing is a run that has stopped doing the
    /// task; an applied change is the one event that honestly restarts the argument.
    /// </summary>
    [Fact]
    public void Calls_that_change_nothing_stop_being_admitted_until_something_is_applied()
    {
        (IRetrievalPolicy policy, ChangeLog changes) = PolicyWithLog(o =>
        {
            o.Enabled = true;
            o.Learn.Enabled = true;
            o.AllowProactive = true;
            o.MaxCallsPerRun = 10;
            o.MaxCallsWithoutAppliedChange = 2;
        });

        policy.RecordCall(Request(), 100);
        policy.RecordCall(Request(), 100);

        policy.TryAdmit(Request(), out RetrievalDenial? denial).ShouldBeFalse();
        denial!.Code.ShouldBe(ToolErrorCodes.RetrievalBudgetExhausted);
        denial.Message.ShouldContain("no change");

        CodeChange change = changes.Propose("src/App.cs", "edit_file", "before", "after");
        changes.Update(change.Id, ChangeStatus.Applied);

        policy.TryAdmit(Request(), out _).ShouldBeTrue("work landed, so the argument restarts");
    }

    /// <summary>
    /// Budgets are per run, and the reset hangs off the ambient run id rather than a BeginRun
    /// the loop has to remember - a reset nobody calls is a budget that never resets.
    /// </summary>
    [Fact]
    public void A_new_run_starts_with_a_fresh_budget()
    {
        IRetrievalPolicy policy = Policy(o =>
        {
            o.Enabled = true;
            o.Learn.Enabled = true;
            o.AllowProactive = true;
            o.MaxCallsPerRun = 1;
            o.MaxCallsWithoutAppliedChange = 0;
        });

        policy.RecordCall(Request(), 100);
        policy.TryAdmit(Request(), out _).ShouldBeFalse();

        RunContext.Set(new RunContext("run-2", "task-1"));

        policy.TryAdmit(Request(), out _).ShouldBeTrue();
        policy.Stats.Allowed.ShouldBe(0);
    }

    [Fact]
    public void Every_refusal_carries_a_stable_code_and_a_way_forward()
    {
        IRetrievalPolicy policy = Policy(o => o.Enabled = false);

        policy.TryAdmit(Request(), out RetrievalDenial? denial).ShouldBeFalse();
        denial!.Code.ShouldBe(ToolErrorCodes.RetrievalDisabled);
        denial.Message.ShouldNotBeNullOrWhiteSpace();
        denial.Hint.ShouldNotBeNullOrWhiteSpace();
    }

    // ---- validation ----

    [Fact]
    public void A_disabled_configuration_is_never_invalid()
    {
        Validate(o =>
        {
            o.Enabled = false;
            o.Learn.Enabled = true;
            o.Learn.Endpoint = "not a uri";
        }).Succeeded.ShouldBeTrue("nothing is constructed, so nothing can be wrong");
    }

    [Fact]
    public void An_enabled_server_needs_an_endpoint_and_a_named_subset()
    {
        ValidateOptionsResult result = Validate(o =>
        {
            o.Enabled = true;
            o.Learn.Enabled = true;
            o.Learn.Endpoint = "not a uri";
        });

        result.Failed.ShouldBeTrue();
        result.FailureMessage.ShouldContain("Endpoint");
        result.FailureMessage.ShouldContain("lists no tools");
    }

    [Fact]
    public void A_tool_without_a_local_description_is_refused_at_startup()
    {
        ValidateOptionsResult result = Validate(o =>
        {
            o.Enabled = true;
            o.Learn.Enabled = true;
            o.Learn.Tools.Add(new RetrievalToolOptions
            {
                ServerTool = "microsoft_docs_search",
                Name = "learn_search",
            });
        });

        result.Failed.ShouldBeTrue();
        result.FailureMessage.ShouldContain("no Description");
    }

    [Fact]
    public void Two_tools_cannot_claim_the_same_name()
    {
        ValidateOptionsResult result = Validate(o =>
        {
            o.Enabled = true;
            o.Learn.Enabled = true;
            o.GitHub.Enabled = true;
            o.Learn.Tools.Add(Tool("microsoft_docs_search", "lookup"));
            o.GitHub.Tools.Add(Tool("search_code", "lookup"));
        });

        result.Failed.ShouldBeTrue();
        result.FailureMessage.ShouldContain("more than once");
    }

    // ---- registration ----

    /// <summary>
    /// The master switch decides whether the machinery is constructed at all, which is what
    /// makes an off run cost nothing rather than cost a refusal.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void The_policy_is_registered_only_when_retrieval_is_enabled(bool enabled)
    {
        using TempWorkspace workspace = new();
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"{WorkspaceOptions.SectionName}:RepoRoot"] = workspace.Root,
                [$"{RetrievalOptions.SectionName}:Enabled"] = enabled ? "true" : "false",
            })
            .Build();

        ServiceCollection services = new();
        services.AddSingleton(configuration);
        services.AddLogging();
        services.AddGlassCoderTools(configuration);
        using ServiceProvider provider = services.BuildServiceProvider();

        (provider.GetService<IRetrievalPolicy>() is not null).ShouldBe(enabled);

        // Options bind either way: a broken retrieval section should fail at startup, not on
        // the first call of the first arm that switches it on.
        provider.GetRequiredService<IOptions<RetrievalOptions>>().Value.Enabled.ShouldBe(enabled);
    }

    /// <summary>Nothing in this task may register a tool - the schema is unchanged until 57.</summary>
    [Fact]
    public void Enabling_retrieval_advertises_no_tool_yet()
    {
        using TempWorkspace workspace = new();
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"{WorkspaceOptions.SectionName}:RepoRoot"] = workspace.Root,
                [$"{RetrievalOptions.SectionName}:Enabled"] = "true",
                [$"{RetrievalOptions.SectionName}:Learn:Enabled"] = "true",
                [$"{RetrievalOptions.SectionName}:Learn:Tools:0:ServerTool"] = "microsoft_docs_search",
                [$"{RetrievalOptions.SectionName}:Learn:Tools:0:Name"] = "learn_search",
                [$"{RetrievalOptions.SectionName}:Learn:Tools:0:Description"] = "Official docs.",
            })
            .Build();

        ServiceCollection services = new();
        services.AddSingleton(configuration);
        services.AddLogging();
        services.AddGlassCoderTools(configuration);
        using ServiceProvider provider = services.BuildServiceProvider();

        provider.GetRequiredService<Registry.IToolRegistry>().Functions
            .Select(f => f.Name)
            .ShouldNotContain("learn_search");
    }

    private static RetrievalRequest Request(RetrievalServer server = RetrievalServer.Learn) =>
        new(server, "learn_search", RetrievalReason.UnknownApi);

    private static RetrievalToolOptions Tool(string serverTool, string name) =>
        new() { ServerTool = serverTool, Name = name, Description = "why it exists" };

    private static IRetrievalPolicy Policy(
        Action<RetrievalOptions> configure, IRetrievalSignals? signals = null) =>
        PolicyWithLog(configure, signals).Policy;

    private static (IRetrievalPolicy Policy, ChangeLog Changes) PolicyWithLog(
        Action<RetrievalOptions> configure, IRetrievalSignals? signals = null)
    {
        RetrievalOptions options = new();
        configure(options);

        ChangeLog changes = new();
        return (
            new RetrievalPolicy(new StubMonitor(options), signals ?? new NoRetrievalSignals(), changes),
            changes);
    }

    private static ValidateOptionsResult Validate(Action<RetrievalOptions> configure)
    {
        RetrievalOptions options = new();
        configure(options);
        return new RetrievalOptionsValidator().Validate(null, options);
    }

    private sealed class StubSignals(string indication) : IRetrievalSignals
    {
        public bool ExternalKnowledgeIndicated => true;

        public string? Indication => indication;
    }

    private sealed class StubMonitor(RetrievalOptions options) : IOptionsMonitor<RetrievalOptions>
    {
        public RetrievalOptions CurrentValue => options;

        public RetrievalOptions Get(string? name) => options;

        public IDisposable? OnChange(Action<RetrievalOptions, string?> listener) => null;
    }
}
