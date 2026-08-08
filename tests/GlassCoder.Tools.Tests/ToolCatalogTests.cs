using GlassCoder.TestSupport;
using GlassCoder.Tools.DependencyInjection;
using GlassCoder.Tools.Git;
using GlassCoder.Tools.Guardrails;
using GlassCoder.Tools.Registry;
using GlassCoder.Tools.Retrieval;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace GlassCoder.Tools.Tests;

/// <summary>
/// The catalogue behind the About list (workplan task 64): every tool the build knows about,
/// marked with whether this session offers it.
/// <para>
/// The property under test is that a switched-off tool still appears. It is the one thing the
/// registry cannot supply - a disabled tool set is never constructed, so there is no
/// <see cref="AIFunction"/> to read - and it is why this class reflects over types instead of
/// reading the container.
/// </para>
/// </summary>
public sealed class ToolCatalogTests : IDisposable
{
    private readonly TempWorkspace _workspace = new();

    public void Dispose() => _workspace.Dispose();

    [Fact]
    public void Every_registered_tool_is_listed_and_marked_active()
    {
        using ServiceProvider provider = Build(git: true, bash: true);
        IToolRegistry registry = provider.GetRequiredService<IToolRegistry>();

        IReadOnlyList<ToolCatalogEntry> catalogue = ToolCatalog.Describe(registry);

        foreach (AIFunction function in registry.Functions)
        {
            ToolCatalogEntry entry = catalogue.Single(e => e.Name == function.Name);
            entry.Active.ShouldBeTrue();
            entry.EnabledBy.ShouldBeNull("an active tool has nothing left to switch on");
            entry.SchemaCharacters.ShouldNotBeNull();
        }
    }

    /// <summary>
    /// The whole point. With the git tools off nothing constructs <see cref="GitTool"/>, so the
    /// registry has never heard of <c>git_commit</c> - and the list still has to name it, still
    /// has to say what it is for, and has to say which setting brings it back.
    /// </summary>
    [Fact]
    public void A_switched_off_tool_is_listed_inactive_with_the_setting_that_enables_it()
    {
        using ServiceProvider provider = Build(git: false, bash: false);

        IReadOnlyList<ToolCatalogEntry> catalogue =
            ToolCatalog.Describe(provider.GetRequiredService<IToolRegistry>());

        ToolCatalogEntry commit = catalogue.Single(e => e.Name == "git_commit");
        commit.Active.ShouldBeFalse();
        commit.EnabledBy.ShouldBe("GlassCoder:Git:Enabled");
        commit.Description.ShouldNotBeNullOrWhiteSpace("an inactive tool still says what it is for");
        commit.SchemaCharacters.ShouldBeNull("nothing was generated, so there is nothing to measure");

        ToolCatalogEntry bash = catalogue.Single(e => e.Name == "bash");
        bash.Active.ShouldBeFalse();
        bash.EnabledBy.ShouldBe("GlassCoder:Sandbox:EnableBashTool");
    }

    /// <summary>
    /// The set does not change when the switches do - only the marks on it. A list whose length
    /// moved with configuration would be the registry again under another name.
    /// </summary>
    [Fact]
    public void The_set_is_the_same_whichever_switches_are_on()
    {
        using ServiceProvider with = Build(git: true, bash: true);
        using ServiceProvider without = Build(git: false, bash: false);

        string[] namesWith = Names(with);
        string[] namesWithout = Names(without);

        namesWith.ShouldBe(namesWithout);
        namesWith.ShouldContain("git_commit");
        namesWith.ShouldContain("bash");

        static string[] Names(ServiceProvider provider) =>
            [.. ToolCatalog.Describe(provider.GetRequiredService<IToolRegistry>()).Select(e => e.Name)];
    }

    /// <summary>
    /// Descriptions come from the live function when there is one and from the attribute when
    /// there is not. Those are two routes to what must be one string, so they are checked against
    /// each other wherever both exist.
    /// </summary>
    [Fact]
    public void The_live_description_and_the_declared_one_agree()
    {
        using ServiceProvider provider = Build(git: true, bash: true);
        IToolRegistry registry = provider.GetRequiredService<IToolRegistry>();

        foreach (ToolCatalogEntry entry in ToolCatalog.Describe(registry).Where(e => e.Active))
        {
            registry.TryGetFunction(entry.Name, out AIFunction? function).ShouldBeTrue();
            entry.Description.ShouldBe(function!.Description);
        }
    }

    /// <summary>Advertised order is part of the contract, so the catalogue keeps it.</summary>
    [Fact]
    public void Active_tools_appear_in_advertised_order()
    {
        using ServiceProvider provider = Build(git: true, bash: true);
        IToolRegistry registry = provider.GetRequiredService<IToolRegistry>();

        string[] advertised = [.. registry.Functions.Select(f => f.Name)];
        string[] catalogued = [.. ToolCatalog.Describe(registry).Where(e => e.Active).Select(e => e.Name)];

        catalogued.ShouldBe(advertised);
    }

    /// <summary>
    /// A tool declared but added by no registration path reports itself, rather than reading as a
    /// deliberate switch-off. That class of dormancy is real here: <c>ModelContextProtocol</c> sat
    /// pinned and referenced by nothing for the life of the project.
    /// </summary>
    [Fact]
    public void Every_inactive_tool_has_a_switch_that_would_enable_it()
    {
        using ServiceProvider provider = Build(git: false, bash: false);

        string[] orphaned =
        [
            .. ToolCatalog.Describe(provider.GetRequiredService<IToolRegistry>())
                .Where(e => !e.Active && e.EnabledBy is null)
                .Select(e => e.Name),
        ];

        orphaned.ShouldBeEmpty("a tool no registration path adds is declared and unreachable");
    }

    /// <summary>
    /// The case neither of the other two sources can cover: a retrieval tool that is configured
    /// and switched off. Reflection cannot see it because it is not a method, and the registry
    /// cannot list it because being off is precisely what keeps it out - so on a default install,
    /// where every MCP tool is inactive, the list would show none of them at all.
    /// </summary>
    [Fact]
    public void A_configured_retrieval_tool_is_listed_even_with_retrieval_switched_off()
    {
        using ServiceProvider provider = Build(git: false, bash: false);

        IReadOnlyList<ToolCatalogEntry> catalogue = ToolCatalog.Describe(
            provider.GetRequiredService<IToolRegistry>(), Configured(enabled: false, learn: false));

        ToolCatalogEntry search = catalogue.Single(e => e.Name == "learn_search");
        search.Active.ShouldBeFalse();
        search.EnabledBy.ShouldBe("GlassCoder:Retrieval:Enabled");
        search.Description.ShouldBe("Official docs for a type or member.");
    }

    /// <summary>With the master on, the server's own switch is the one worth naming.</summary>
    [Fact]
    public void With_the_master_on_the_server_switch_is_what_is_named()
    {
        using ServiceProvider provider = Build(git: false, bash: false);

        ToolCatalogEntry search = ToolCatalog
            .Describe(provider.GetRequiredService<IToolRegistry>(), Configured(enabled: true, learn: false))
            .Single(e => e.Name == "learn_search");

        search.EnabledBy.ShouldBe("GlassCoder:Retrieval:Learn:Enabled");
    }

    /// <summary>
    /// Both switches on and the tool still absent means a cold corpus, not a setting - so saying
    /// "off" would send someone to a checkbox that is already ticked.
    /// </summary>
    [Fact]
    public void A_switched_on_tool_with_no_corpus_explains_itself_rather_than_blaming_a_setting()
    {
        using ServiceProvider provider = Build(git: false, bash: false);

        ToolCatalogEntry search = ToolCatalog
            .Describe(provider.GetRequiredService<IToolRegistry>(), Configured(enabled: true, learn: true))
            .Single(e => e.Name == "learn_search");

        search.Active.ShouldBeFalse();
        search.EnabledBy.ShouldBeNull();
        search.Unavailable.ShouldContain("Record");
    }

    /// <summary>A registered retrieval tool is active and is not duplicated by the configuration.</summary>
    [Fact]
    public void A_registered_retrieval_tool_is_not_listed_twice()
    {
        using ServiceProvider provider = Build(git: false, bash: false);
        RetrievalOptions configured = Configured(enabled: true, learn: true);

        IToolRegistry registry = new ToolRegistry(
            provider.GetRequiredService<IEnumerable<IToolSet>>(),
            [new StubSource("learn_search", "Official docs for a type or member.")]);

        ToolCatalogEntry[] found = [.. ToolCatalog.Describe(registry, configured).Where(e => e.Name == "learn_search")];

        found.Length.ShouldBe(1);
        found[0].Active.ShouldBeTrue();
    }

    private static RetrievalOptions Configured(bool enabled, bool learn)
    {
        RetrievalOptions options = new() { Enabled = enabled };
        options.Learn.Enabled = learn;
        options.Learn.Tools.Add(new RetrievalToolOptions
        {
            ServerTool = "microsoft_docs_search",
            Name = "learn_search",
            Description = "Official docs for a type or member.",
        });

        return options;
    }

    private sealed class StubSource(string name, string description) : IToolFunctionSource
    {
        public IReadOnlyList<AIFunction> Functions { get; } =
        [
            AIFunctionFactory.Create(
                () => "answer", new AIFunctionFactoryOptions { Name = name, Description = description }),
        ];
    }

    /// <summary>The real registration path, over a throwaway root, with the opt-in sets as asked.</summary>
    private ServiceProvider Build(bool git, bool bash)
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"{WorkspaceOptions.SectionName}:RepoRoot"] = _workspace.Root,
                ["GlassCoder:Git:Enabled"] = git ? "true" : "false",
                ["GlassCoder:Sandbox:EnableBashTool"] = bash ? "true" : "false",
            })
            .Build();

        ServiceCollection services = new();
        services.AddSingleton(configuration);
        services.AddLogging();
        services.AddGlassCoderTools(configuration);
        return services.BuildServiceProvider();
    }
}
