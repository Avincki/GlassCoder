using System.IO;
using System.Windows.Threading;
using GlassCoder.Core.Agent;
using GlassCoder.Core.DependencyInjection;
using GlassCoder.TestSupport;
using GlassCoder.Wpf.DependencyInjection;
using GlassCoder.Wpf.Services;
using GlassCoder.Wpf.ViewModels;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace GlassCoder.Wpf.Tests;

/// <summary>
/// The two conveniences that make repeated test runs cheap: the goal box remembers the last
/// run's prompt across a restart, and the workspace pane can empty the writable roots so the
/// next run starts from the blank workspace the opening message promises.
/// <para>
/// Both are conveniences with teeth. The goal lives in a UI-state store rather than the settings
/// file, because everything the settings store saves feeds <c>IConfiguration</c> and the
/// provenance stamp hashes that configuration to identify a run's arm - a prompt saved there
/// would relabel every arm on every new prompt. And Clean only ever reaches inside the writable
/// roots, because that is the whole of what a run could have made.
/// </para>
/// </summary>
public sealed class TestRunConvenienceTests
{
    // ── Clean ──

    [Fact]
    public void Clean_empties_the_writable_roots_and_touches_nothing_else()
    {
        using TempWorkspace workspace = new();
        workspace.WriteFile("src/App/Program.cs", "class P { }");
        workspace.WriteFile("tests/T.cs", "class T { }");
        workspace.WriteFile("README.md", "keep me");
        FakeShell shell = new();

        (bool srcEmpty, bool testsEmpty, bool readmeKept) = OverPane(workspace, shell, pane =>
        {
            pane.CleanCommand.Execute(null);

            return (
                Directory.EnumerateFileSystemEntries(Path.Combine(workspace.Root, "src")).Any() == false,
                Directory.EnumerateFileSystemEntries(Path.Combine(workspace.Root, "tests")).Any() == false,
                File.Exists(Path.Combine(workspace.Root, "README.md")));
        });

        srcEmpty.ShouldBeTrue("everything under a writable root is a run's to have made, and Clean's to remove");
        testsEmpty.ShouldBeTrue();
        readmeKept.ShouldBeTrue("outside the writable roots is outside what any run could have made");
        shell.LastQuestion.ShouldNotBeNull();
        shell.LastQuestion.ShouldContain("src");
    }

    [Fact]
    public void A_declined_confirmation_deletes_nothing()
    {
        using TempWorkspace workspace = new();
        workspace.WriteFile("src/App/Program.cs", "class P { }");
        FakeShell shell = new() { Answer = false };

        bool kept = OverPane(workspace, shell, pane =>
        {
            pane.CleanCommand.Execute(null);
            return File.Exists(Path.Combine(workspace.Root, "src", "App", "Program.cs"));
        });

        kept.ShouldBeTrue("no is no");
    }

    [Fact]
    public void Clean_stands_down_while_a_run_is_in_flight()
    {
        using TempWorkspace workspace = new();

        bool executable = OverPane(workspace, new FakeShell(), pane =>
        {
            pane.IsAgentRunning = true;
            return pane.CleanCommand.CanExecute(null);
        });

        executable.ShouldBeFalse("emptying folders mid-run would pull the workspace out from under the agent");
    }

    [Fact]
    public void A_missing_writable_root_is_recreated_empty()
    {
        // A fresh checkout may not have the roots yet; a clean leaves exactly what the opening
        // window promises the agent - the roots, present and empty.
        using TempWorkspace workspace = new();
        workspace.WriteFile("src/App/Program.cs", "class P { }");

        (bool srcExists, bool testsExists) = OverPane(workspace, new FakeShell(), pane =>
        {
            pane.CleanCommand.Execute(null);
            return (
                Directory.Exists(Path.Combine(workspace.Root, "src")),
                Directory.Exists(Path.Combine(workspace.Root, "tests")));
        });

        srcExists.ShouldBeTrue();
        testsExists.ShouldBeTrue();
    }

    // ── The last goal ──

    [Fact]
    public void The_goal_box_opens_with_the_last_run_s_prompt()
    {
        using TempWorkspace workspace = new();
        FakeUiStateStore store = new() { LastGoal = "make a wpf app that multiplies two numbers" };

        string goal = OverShell(workspace, store, (_, shell) => shell.Goal);

        goal.ShouldBe("make a wpf app that multiplies two numbers");
    }

    [Fact]
    public void Pressing_run_saves_the_goal_for_the_next_start()
    {
        using TempWorkspace workspace = new();
        FakeUiStateStore store = new();

        string? saved = OverShell(workspace, store, (dispatcher, shell) =>
        {
            shell.Goal = "multiply two numbers";
            shell.RunCommand.Execute(null);
            UiThread.Pump(dispatcher, () => !shell.IsRunning).ShouldBeTrue("the stub run never finished");
            return store.LastGoal;
        });

        saved.ShouldBe("multiply two numbers");
    }

    [Fact]
    public void An_empty_goal_is_not_saved_over_the_last_real_one()
    {
        using TempWorkspace workspace = new();
        FakeUiStateStore store = new() { LastGoal = "the real prompt" };

        string? saved = OverShell(workspace, store, (dispatcher, shell) =>
        {
            shell.Goal = "   ";
            shell.RunCommand.Execute(null);
            UiThread.Pump(dispatcher, () => !shell.IsRunning).ShouldBeTrue();
            return store.LastGoal;
        });

        saved.ShouldBe("the real prompt", "a run that never started has nothing worth remembering");
    }

    // ── Scaffolding ──

    /// <summary>Builds the workspace pane over the throwaway root, with the writable roots set.</summary>
    private static T OverPane<T>(TempWorkspace workspace, FakeShell shell, Func<WorkspaceViewModel, T> assert) =>
        UiThread.Run(dispatcher =>
        {
            using ServiceProvider provider = Build(dispatcher, workspace.Root, shell, new FakeUiStateStore());
            WorkspaceViewModel pane = provider.GetRequiredService<WorkspaceViewModel>();

            UiThread.Pump(dispatcher, () => pane.Loaded.IsCompleted, TimeSpan.FromSeconds(15))
                .ShouldBeTrue("the pane never finished its first read of the workspace");

            return assert(pane);
        });

    /// <summary>Builds the whole shell, with the agent loop stubbed so Run finishes instantly.</summary>
    private static T OverShell<T>(
        TempWorkspace workspace, FakeUiStateStore store, Func<Dispatcher, MainWindowViewModel, T> assert) =>
        UiThread.Run(dispatcher =>
        {
            using ServiceProvider provider = Build(dispatcher, workspace.Root, new FakeShell(), store);
            return assert(dispatcher, provider.GetRequiredService<MainWindowViewModel>());
        });

    private static ServiceProvider Build(
        Dispatcher dispatcher, string repoRoot, FakeShell shell, FakeUiStateStore store)
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["GlassCoder:Workspace:RepoRoot"] = repoRoot,
                ["GlassCoder:Workspace:WritablePaths:0"] = "src",
                ["GlassCoder:Workspace:WritablePaths:1"] = "tests",
                ["GlassCoder:Models:Roles:worker:Endpoint"] = "http://localhost:8001/v1",
                ["GlassCoder:Models:Roles:worker:ModelAlias"] = "worker",
                ["GlassCoder:Telemetry:Enabled"] = "false",
                ["GlassCoder:Metrics:Directory"] = Path.Combine(repoRoot, "metrics"),
            })
            .Build();

        ServiceCollection services = new();
        services.AddSingleton(configuration);
        services.AddLogging();
        services.AddGlassCoder(configuration);
        services.AddGlassCoderDesktop(dispatcher);

        // Last registration wins for single resolution, so the fakes stand in for the dialogs
        // and the registry - and the stub loop keeps Run off the network entirely.
        services.AddSingleton<IDesktopShell>(shell);
        services.AddSingleton<IUiStateStore>(store);
        services.Replace(ServiceDescriptor.Singleton<IAgentLoop>(new StubLoop()));

        return services.BuildServiceProvider();
    }

    /// <summary>Answers the confirmation without a window, and remembers what was asked.</summary>
    private sealed class FakeShell : IDesktopShell
    {
        public bool Answer { get; init; } = true;

        public string? LastQuestion { get; private set; }

        public bool Confirm(string title, string message)
        {
            LastQuestion = message;
            return Answer;
        }

        public void OpenFolder(string path)
        {
        }

        public void OpenFileViewer(string fullPath, string displayPath)
        {
        }

        public void Restart()
        {
        }

        public string? PickFolder(string title, string? initialDirectory) => null;

        public string? PickFileToOpen(string title, string filter, string? initialDirectory) => null;

        public string? PickFileToSave(
            string title, string filter, string defaultFileName, string? initialDirectory) => null;

        public string? PromptForPassphrase(string title, string message, bool confirm) => null;
    }

    private sealed class FakeUiStateStore : IUiStateStore
    {
        public string? LastGoal { get; set; }
    }

    /// <summary>Completes immediately, so a shell test never waits on a model that is not there.</summary>
    private sealed class StubLoop : IAgentLoop
    {
        public Task<AgentRunResult> RunAsync(AgentRunRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new AgentRunResult
            {
                RunId = request.RunId,
                TaskId = request.TaskId,
                StopReason = AgentStopReason.Completed,
                Steps = 0,
                Elapsed = TimeSpan.Zero,
                Messages = [],
            });
    }
}
