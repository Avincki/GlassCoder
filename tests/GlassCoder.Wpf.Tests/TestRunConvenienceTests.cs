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

        (bool srcEmpty, bool testsEmpty, bool readmeKept) = OverPane(workspace, shell, (dispatcher, pane) =>
        {
            CleanAndWait(dispatcher, pane);

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
    public void A_read_only_file_deep_in_a_subfolder_does_not_stop_the_clean()
    {
        // Build output copies the read-only attribute in from packages, and a plain recursive
        // delete refuses such files - which used to leave the whole subfolder standing.
        using TempWorkspace workspace = new();
        workspace.WriteFile("src/App/obj/App.dll", "not really a dll");
        File.SetAttributes(
            Path.Combine(workspace.Root, "src", "App", "obj", "App.dll"), FileAttributes.ReadOnly);

        bool srcEmpty = OverPane(workspace, new FakeShell(), (dispatcher, pane) =>
        {
            CleanAndWait(dispatcher, pane);
            return Directory.EnumerateFileSystemEntries(Path.Combine(workspace.Root, "src")).Any() == false;
        });

        srcEmpty.ShouldBeTrue("read-only is an attribute, not an occupant");
    }

    [Fact]
    public void A_lock_that_lets_go_between_passes_costs_nothing()
    {
        // The sync client's actual behaviour: held during the delete storm the first pass
        // raises, free once the dust settles. The second pass must find nothing left to fail on.
        using TempWorkspace workspace = new();
        workspace.WriteFile("src/App/obj/held.tmp", "held");
        string heldPath = Path.Combine(workspace.Root, "src", "App", "obj", "held.tmp");
        FileStream hold = new(heldPath, FileMode.Open, FileAccess.Read, FileShare.None);

        try
        {
            (bool srcEmpty, string status) = OverPane(workspace, new FakeShell(), (dispatcher, pane) =>
            {
                pane.CleanCommand.Execute(null);

                UiThread.Pump(dispatcher, () => pane.Status.Contains("waiting"), TimeSpan.FromSeconds(15))
                    .ShouldBeTrue("the sweep never said it was waiting out the held item");
                hold.Dispose();

                UiThread.Pump(dispatcher, () => pane.Status.Contains("Cleaned"), TimeSpan.FromSeconds(15))
                    .ShouldBeTrue("the clean never finished");

                return (
                    Directory.EnumerateFileSystemEntries(Path.Combine(workspace.Root, "src")).Any() == false,
                    pane.Status);
            });

            srcEmpty.ShouldBeTrue("what let go between passes should be gone after the second");
            status.ShouldNotContain("could not be");
        }
        finally
        {
            hold.Dispose();
        }
    }

    [Fact]
    public void One_locked_file_costs_its_own_folder_chain_and_nothing_else()
    {
        // The sync client's failure mode: one held handle deep in obj. The sweep must not let
        // it protect siblings or unrelated subfolders the way Delete(recursive: true) did.
        using TempWorkspace workspace = new();
        workspace.WriteFile("src/App/Program.cs", "class P { }");
        workspace.WriteFile("src/App/obj/locked.tmp", "held");
        workspace.WriteFile("src/Other/Lib.cs", "class L { }");

        string lockedPath = Path.Combine(workspace.Root, "src", "App", "obj", "locked.tmp");
        using FileStream hold = new(lockedPath, FileMode.Open, FileAccess.Read, FileShare.None);

        (bool siblingGone, bool otherGone, bool lockedKept, string status) =
            OverPane(workspace, new FakeShell(), (dispatcher, pane) =>
            {
                // Held throughout, so both passes fail on it - and the summary, with its honest
                // "could not be", has to be what survives the refresh that follows.
                CleanAndWait(dispatcher, pane);

                return (
                    !File.Exists(Path.Combine(workspace.Root, "src", "App", "Program.cs")),
                    !Directory.Exists(Path.Combine(workspace.Root, "src", "Other")),
                    File.Exists(lockedPath),
                    pane.Status);
            });

        siblingGone.ShouldBeTrue("a sibling of a locked file is not itself locked");
        otherGone.ShouldBeTrue("a subfolder away from the lock should never notice it");
        lockedKept.ShouldBeTrue();
        status.ShouldContain("could not be");
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

        (bool srcExists, bool testsExists) = OverPane(workspace, new FakeShell(), (dispatcher, pane) =>
        {
            CleanAndWait(dispatcher, pane);
            return (
                Directory.Exists(Path.Combine(workspace.Root, "src")),
                Directory.Exists(Path.Combine(workspace.Root, "tests")));
        });

        srcExists.ShouldBeTrue();
        testsExists.ShouldBeTrue();
    }

    // ── Run app ──

    [Fact]
    public void Run_app_launches_the_application_project()
    {
        using TempWorkspace workspace = new();
        workspace.WriteFile(
            "src/App/App.csproj",
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>WinExe</OutputType>" +
            "<UseWPF>true</UseWPF></PropertyGroup></Project>");
        FakeShell shell = new();

        string status = OverPane(workspace, shell, pane =>
        {
            pane.RunAppCommand.Execute(null);
            return pane.Status;
        });

        shell.LaunchedProject.ShouldNotBeNull();
        shell.LaunchedProject.ShouldEndWith("App.csproj");
        status.ShouldContain("Launched");
    }

    [Fact]
    public void A_workspace_of_libraries_has_nothing_to_run()
    {
        using TempWorkspace workspace = new();
        workspace.WriteFile("src/Lib/Lib.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");
        FakeShell shell = new();

        string status = OverPane(workspace, shell, pane =>
        {
            pane.RunAppCommand.Execute(null);
            return pane.Status;
        });

        shell.LaunchedProject.ShouldBeNull("a library is not an application");
        status.ShouldContain("No application");
    }

    [Fact]
    public void A_project_copy_under_build_output_is_never_the_one_that_runs()
    {
        // Publish output under bin holds copies of project files, and running a copy runs
        // yesterday's app.
        using TempWorkspace workspace = new();
        workspace.WriteFile(
            "src/App/bin/Release/publish/App.csproj",
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>WinExe</OutputType></PropertyGroup></Project>");
        FakeShell shell = new();

        string status = OverPane(workspace, shell, pane =>
        {
            pane.RunAppCommand.Execute(null);
            return pane.Status;
        });

        shell.LaunchedProject.ShouldBeNull();
        status.ShouldContain("No application");
    }

    [Fact]
    public void With_several_applications_the_first_alphabetically_runs_and_the_rest_are_counted()
    {
        using TempWorkspace workspace = new();
        const string application =
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>Exe</OutputType></PropertyGroup></Project>";
        workspace.WriteFile("src/Beta/Beta.csproj", application);
        workspace.WriteFile("src/Alpha/Alpha.csproj", application);
        FakeShell shell = new();

        string status = OverPane(workspace, shell, pane =>
        {
            pane.RunAppCommand.Execute(null);
            return pane.Status;
        });

        shell.LaunchedProject.ShouldNotBeNull();
        shell.LaunchedProject.ShouldEndWith("Alpha.csproj");
        status.ShouldContain("1 other application");
    }

    [Fact]
    public void Run_app_stands_down_while_a_run_is_in_flight()
    {
        using TempWorkspace workspace = new();

        bool executable = OverPane(workspace, new FakeShell(), pane =>
        {
            pane.IsAgentRunning = true;
            return pane.RunAppCommand.CanExecute(null);
        });

        executable.ShouldBeFalse("a build racing the agent's own builds helps neither");
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

    /// <summary>Presses Clean and pumps until its summary lands - the sweep is asynchronous.</summary>
    private static void CleanAndWait(Dispatcher dispatcher, WorkspaceViewModel pane)
    {
        pane.CleanCommand.Execute(null);
        UiThread.Pump(dispatcher, () => pane.Status.Contains("Cleaned"), TimeSpan.FromSeconds(15))
            .ShouldBeTrue("the clean never reported finishing");
    }

    /// <summary>Builds the workspace pane over the throwaway root, with the writable roots set.</summary>
    private static T OverPane<T>(TempWorkspace workspace, FakeShell shell, Func<WorkspaceViewModel, T> assert) =>
        OverPane(workspace, shell, (_, pane) => assert(pane));

    /// <summary>The same pane, with the dispatcher in hand for tests that must pump past an await.</summary>
    private static T OverPane<T>(
        TempWorkspace workspace, FakeShell shell, Func<Dispatcher, WorkspaceViewModel, T> assert) =>
        UiThread.Run(dispatcher =>
        {
            using ServiceProvider provider = Build(dispatcher, workspace.Root, shell, new FakeUiStateStore());
            WorkspaceViewModel pane = provider.GetRequiredService<WorkspaceViewModel>();

            UiThread.Pump(dispatcher, () => pane.Loaded.IsCompleted, TimeSpan.FromSeconds(15))
                .ShouldBeTrue("the pane never finished its first read of the workspace");

            return assert(dispatcher, pane);
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

        public string? LaunchedProject { get; private set; }

        public bool Confirm(string title, string message)
        {
            LastQuestion = message;
            return Answer;
        }

        public string? LaunchApp(string projectFile)
        {
            LaunchedProject = projectFile;
            return null;
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
