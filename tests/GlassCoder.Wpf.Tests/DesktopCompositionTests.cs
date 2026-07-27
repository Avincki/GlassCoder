using System.Windows.Threading;
using GlassCoder.Core.DependencyInjection;
using GlassCoder.TestSupport;
using GlassCoder.Tools.Changes;
using GlassCoder.Tools.Guardrails;
using GlassCoder.Wpf.DependencyInjection;
using GlassCoder.Wpf.Services;
using GlassCoder.Wpf.ViewModels;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace GlassCoder.Wpf.Tests;

/// <summary>
/// The desktop composition root: that the graph the window is built from can actually be built,
/// in both of the shapes configuration can give it.
/// <para>
/// This covers a gap that shipped a hung application. Enabling the git tools closed a cycle -
/// the change view holds <c>GitTool</c> so it can decide whether to show its git controls,
/// <c>GitTool</c> holds the approval gate so a push still asks a human, and the gate held the
/// change view to ask with. Because the change view is registered through a factory, the
/// container could not report the cycle; it deadlocked instead, before the window appeared and
/// before anything reached the log. Every test here resolves under a timeout for that reason -
/// see <see cref="UiThread"/>.
/// </para>
/// </summary>
public sealed class DesktopCompositionTests
{
    [Fact]
    public void The_shell_resolves_with_the_git_tools_enabled()
    {
        bool gitAvailable = Resolve(gitEnabled: true, provider =>
            provider.GetRequiredService<MainWindowViewModel>().Changes.GitAvailable);

        gitAvailable.ShouldBeTrue();
    }

    [Fact]
    public void The_shell_resolves_with_the_git_tools_disabled()
    {
        bool gitAvailable = Resolve(gitEnabled: false, provider =>
            provider.GetRequiredService<MainWindowViewModel>().Changes.GitAvailable);

        gitAvailable.ShouldBeFalse();
    }

    /// <summary>
    /// The other order. A tool resolved before any view model reaches the gate first, and the
    /// cycle has to stay broken whichever end of it the container starts from.
    /// </summary>
    [Fact]
    public void The_approval_gate_resolves_before_the_change_view_it_asks()
    {
        bool interactive = Resolve(gitEnabled: true, provider =>
        {
            IApprovalGate gate = provider.GetRequiredService<IApprovalGate>();
            provider.GetRequiredService<ChangesViewModel>();
            return gate.IsInteractive;
        });

        interactive.ShouldBeTrue();
    }

    /// <summary>
    /// Resolving the change view late is only correct while late still means the same instance.
    /// Were it registered per-resolution the gate would prompt on a view model nobody is looking
    /// at, and an approval request would hang until it timed out into a refusal.
    /// </summary>
    [Fact]
    public void The_gate_and_the_shell_share_one_change_view()
    {
        (bool SameAsAccessor, bool SameAsShell) shared = Resolve(gitEnabled: true, provider =>
        {
            ChangesViewModel changes = provider.GetRequiredService<ChangesViewModel>();
            ChangesViewModel viaAccessor = provider.GetRequiredService<Func<ChangesViewModel>>()();
            ChangesViewModel onShell = provider.GetRequiredService<MainWindowViewModel>().Changes;

            return (ReferenceEquals(changes, viaAccessor), ReferenceEquals(changes, onShell));
        });

        shared.SameAsAccessor.ShouldBeTrue();
        shared.SameAsShell.ShouldBeTrue();
    }

    /// <summary>The workspace pane roots itself where the guard is rooted, not where the app runs.</summary>
    [Fact]
    public void The_workspace_pane_is_rooted_where_the_guard_is()
    {
        using TempWorkspace workspace = new();

        string root = UiThread.Run(dispatcher =>
        {
            using ServiceProvider provider = Build(dispatcher, workspace.Root, gitEnabled: true);
            provider.GetRequiredService<IPathGuard>().RepoRoot.ShouldBe(workspace.Root);
            return provider.GetRequiredService<WorkspaceViewModel>().RootPath;
        });

        root.ShouldBe(workspace.Root);
    }

    /// <summary>
    /// Builds the graph over a throwaway root and hands <paramref name="select"/> the provider.
    /// Whatever is asserted on is computed inside, while the provider is still alive.
    /// </summary>
    private static T Resolve<T>(bool gitEnabled, Func<IServiceProvider, T> select)
    {
        using TempWorkspace workspace = new();

        return UiThread.Run(dispatcher =>
        {
            using ServiceProvider provider = Build(dispatcher, workspace.Root, gitEnabled);
            return select(provider);
        });
    }

    /// <summary>
    /// The shared bootstrap plus the desktop registrations - the same two calls
    /// <see cref="App.OnStartup"/> makes, over configuration this test owns rather than whatever
    /// the machine happens to have saved.
    /// </summary>
    private static ServiceProvider Build(Dispatcher dispatcher, string repoRoot, bool gitEnabled)
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["GlassCoder:Workspace:RepoRoot"] = repoRoot,
                ["GlassCoder:Models:Roles:worker:Endpoint"] = "http://localhost:8001/v1",
                ["GlassCoder:Models:Roles:worker:ModelAlias"] = "worker",
                ["GlassCoder:Git:Enabled"] = gitEnabled ? "true" : "false",
                // Nothing here traces, and a tracer provider is one more thing to dispose.
                ["GlassCoder:Telemetry:Enabled"] = "false",
                // Keep the metrics view off the developer's own metrics file.
                ["GlassCoder:Metrics:Directory"] = System.IO.Path.Combine(repoRoot, "metrics"),
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
