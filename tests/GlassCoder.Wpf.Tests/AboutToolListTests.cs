using System.Windows.Threading;
using GlassCoder.Core.DependencyInjection;
using GlassCoder.TestSupport;
using GlassCoder.Wpf.DependencyInjection;
using GlassCoder.Wpf.ViewModels;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace GlassCoder.Wpf.Tests;

/// <summary>
/// Where the tool inventory lives (workplan task 64): out of the status bar, into About, with the
/// switched-off tools shown rather than silently missing.
/// </summary>
public sealed class AboutToolListTests
{
    /// <summary>
    /// The shell used to open with the whole list in <c>Status</c>, which wrapped to two lines of
    /// a one-line surface and was overwritten by the first thing that happened.
    /// </summary>
    [Fact]
    public void The_status_bar_no_longer_names_any_tool()
    {
        string status = Resolve(git: true, provider =>
            provider.GetRequiredService<MainWindowViewModel>().Status);

        status.ShouldBe("Ready.");
    }

    [Fact]
    public void About_lists_every_tool_with_a_name_and_a_purpose()
    {
        IReadOnlyList<AboutViewModel.ToolRow> tools = Resolve(git: true, provider =>
            provider.GetRequiredService<AboutViewModel>().Tools);

        tools.ShouldNotBeEmpty();

        foreach (AboutViewModel.ToolRow row in tools)
        {
            row.Name.ShouldNotBeNullOrWhiteSpace();
            row.Description.ShouldNotBeNullOrWhiteSpace();
        }
    }

    /// <summary>
    /// With git off the five git tools are absent from the registry entirely, and About is the
    /// one surface that still has to account for them.
    /// </summary>
    [Fact]
    public void A_switched_off_tool_appears_inactive_and_names_its_setting()
    {
        (int Count, AboutViewModel.ToolRow Commit, string Heading) about = Resolve(git: false, provider =>
        {
            AboutViewModel model = provider.GetRequiredService<AboutViewModel>();
            return (model.Tools.Count, model.Tools.Single(t => t.Name == "git_commit"), model.ToolHeading);
        });

        about.Commit.IsActive.ShouldBeFalse();
        about.Commit.Detail.ShouldBe("off · Git:Enabled");
        about.Commit.Description.ShouldNotBeNullOrWhiteSpace();

        // The heading says both numbers, so "13 of 18" reads as a configuration rather than a loss.
        about.Heading.ShouldStartWith("Tools · ");
        about.Heading.ShouldContain($"of {about.Count} active");
    }

    /// <summary>
    /// The same list either way, differing only in what is marked. This is the property that
    /// separates the About list from the registry it is built beside.
    /// </summary>
    [Fact]
    public void The_list_is_the_same_length_with_the_git_tools_on_or_off()
    {
        (int All, int Active) on = Counts(git: true);
        (int All, int Active) off = Counts(git: false);

        on.All.ShouldBe(off.All);
        on.Active.ShouldBe(off.Active + 5, "the five git tools move from inactive to active");

        static (int All, int Active) Counts(bool git) => Resolve(git, provider =>
        {
            IReadOnlyList<AboutViewModel.ToolRow> tools = provider.GetRequiredService<AboutViewModel>().Tools;
            return (tools.Count, tools.Count(t => t.IsActive));
        });
    }

    /// <summary>An active row carries its schema size; an inactive one has no schema to size.</summary>
    [Fact]
    public void An_active_row_reports_its_schema_size()
    {
        AboutViewModel.ToolRow read = Resolve(git: true, provider =>
            provider.GetRequiredService<AboutViewModel>().Tools.Single(t => t.Name == "read_file"));

        read.IsActive.ShouldBeTrue();
        read.Detail.ShouldEndWith("char schema");
    }

    private static T Resolve<T>(bool git, Func<IServiceProvider, T> select)
    {
        using TempWorkspace workspace = new();

        return UiThread.Run(dispatcher =>
        {
            using ServiceProvider provider = Build(dispatcher, workspace.Root, git);
            return select(provider);
        });
    }

    private static ServiceProvider Build(Dispatcher dispatcher, string repoRoot, bool git)
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["GlassCoder:Workspace:RepoRoot"] = repoRoot,
                ["GlassCoder:Models:Roles:worker:Endpoint"] = "http://localhost:8001/v1",
                ["GlassCoder:Models:Roles:worker:ModelAlias"] = "worker",
                ["GlassCoder:Git:Enabled"] = git ? "true" : "false",
                ["GlassCoder:Telemetry:Enabled"] = "false",
                ["GlassCoder:Metrics:Enabled"] = "false",
                ["GlassCoder:Provenance:Enabled"] = "false",
            })
            .Build();

        ServiceCollection services = new();
        services.AddSingleton(configuration);
        services.AddLogging();
        services.AddGlassCoder(configuration);
        services.AddGlassCoderDesktop(dispatcher);
        return services.BuildServiceProvider();
    }
}
