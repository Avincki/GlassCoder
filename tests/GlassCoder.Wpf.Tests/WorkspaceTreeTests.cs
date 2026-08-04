using System.IO;
using System.Windows.Threading;
using GlassCoder.Core.DependencyInjection;
using GlassCoder.TestSupport;
using GlassCoder.Tools.Changes;
using GlassCoder.Wpf.DependencyInjection;
using GlassCoder.Wpf.ViewModels;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace GlassCoder.Wpf.Tests;

/// <summary>
/// The workspace tree: that it shows what the workspace holds, and colours what the run in
/// progress did to it.
/// <para>
/// Both halves come from watching a run. The tree only knew about files the change log had
/// recorded, so the three files <c>dotnet new</c> writes were invisible until someone pressed
/// Refresh - and the green from the previous run was still on the tree while the next one was
/// being read, which makes the pane say something that is no longer true.
/// </para>
/// </summary>
public sealed class WorkspaceTreeTests
{
    // ── What the workspace contains ──

    [Fact]
    public void Folders_start_open_and_files_do_not()
    {
        using TempWorkspace workspace = new();
        workspace.WriteFile("src/App/Program.cs", "class P { }");

        (bool folder, bool file) = Over(workspace, (_, tree) =>
            (Node(tree, "src/App")!.IsExpanded, Node(tree, "src/App/Program.cs")!.IsExpanded));

        folder.ShouldBeTrue("a tree that starts closed hides the thing the pane exists to show");
        file.ShouldBeFalse();
    }

    /// <summary>
    /// The case that prompted this: <c>dotnet new</c> writes three files and the change log
    /// records one, so the other two existed on disk and nowhere on screen.
    /// </summary>
    [Fact]
    public void A_file_written_by_something_other_than_a_tool_still_appears()
    {
        using TempWorkspace workspace = new();
        workspace.WriteFile("src/App/App.csproj", "<Project />");

        bool appeared = Over(workspace, (dispatcher, tree) =>
        {
            workspace.WriteFile("src/App/UnitTest1.cs", "class T { }");
            return UiThread.Pump(dispatcher, () => Node(tree, "src/App/UnitTest1.cs") is not null);
        });

        appeared.ShouldBeTrue("the tree follows the workspace, not only the change log");
    }

    /// <summary>
    /// A file appears when its path does, not when whatever is writing it has finished. An
    /// agent's half-written file is exactly the one an operator wants to watch.
    /// </summary>
    [Fact]
    public void A_file_still_being_written_appears_while_it_is_open()
    {
        using TempWorkspace workspace = new();
        workspace.WriteFile("src/App/App.csproj", "<Project />");

        bool appeared = Over(workspace, (dispatcher, tree) =>
        {
            string path = Path.Combine(workspace.Root, "src", "App", "Half.cs");
            using FileStream open = File.Create(path);
            open.Write("public class Half"u8);
            open.Flush();

            return UiThread.Pump(dispatcher, () => Node(tree, "src/App/Half.cs") is not null);
        });

        appeared.ShouldBeTrue();
    }

    [Fact]
    public void A_deleted_file_leaves_the_tree()
    {
        using TempWorkspace workspace = new();
        string doomed = workspace.WriteFile("src/App/Gone.cs", "class G { }");
        workspace.WriteFile("src/App/Stays.cs", "class S { }");

        (bool left, bool kept) = Over(workspace, (dispatcher, tree) =>
        {
            Node(tree, "src/App/Gone.cs").ShouldNotBeNull();
            File.Delete(doomed);

            bool gone = UiThread.Pump(dispatcher, () => Node(tree, "src/App/Gone.cs") is null);
            return (gone, Node(tree, "src/App/Stays.cs") is not null);
        });

        left.ShouldBeTrue();
        kept.ShouldBeTrue("removing one node must not take its siblings with it");
    }

    /// <summary>
    /// Build output is hidden from the tree because the deny globs hide it from the agent, and
    /// a watcher that forwarded it would undo that one event at a time.
    /// </summary>
    [Fact]
    public void Denied_paths_stay_out_of_the_tree_when_they_are_written()
    {
        using TempWorkspace workspace = new();
        workspace.WriteFile("src/App/App.csproj", "<Project />");

        bool leaked = Over(workspace, (dispatcher, tree) =>
        {
            workspace.WriteFile("src/App/obj/Debug/App.dll", "binary");
            workspace.WriteFile("src/App/Marker.cs", "class M { }");

            // The marker is the clock: once it has arrived, the obj write has had its chance.
            UiThread.Pump(dispatcher, () => Node(tree, "src/App/Marker.cs") is not null);
            return Node(tree, "src/App/obj") is not null;
        });

        leaked.ShouldBeFalse();
    }

    // ── What this run did to it ──

    [Fact]
    public void A_new_run_takes_off_the_last_run_s_colouring()
    {
        using TempWorkspace workspace = new();
        workspace.WriteFile("src/App/Program.cs", "one\ntwo\n");

        (bool before, bool after, int added) = Over(workspace, (_, tree) =>
        {
            Apply(tree.Changes, "run-1", "src/App/Program.cs", "one\n", "one\ntwo\n");
            FileNodeViewModel node = Node(tree, "src/App/Program.cs")!;
            bool wasMarked = node.IsModified;

            tree.Workspace.BeginRun();

            return (wasMarked, node.IsModified, node.LinesAdded);
        });

        before.ShouldBeTrue();
        after.ShouldBeFalse("pressing Run clears what the previous run left behind");
        added.ShouldBe(0, "and takes the line counts with it");
    }

    [Fact]
    public void Only_the_run_in_progress_colours_the_tree()
    {
        using TempWorkspace workspace = new();
        workspace.WriteFile("src/App/Old.cs", "old\n");
        workspace.WriteFile("src/App/New.cs", "new\n");

        (bool old, bool fresh) = Over(workspace, (_, tree) =>
        {
            Apply(tree.Changes, "run-1", "src/App/Old.cs", string.Empty, "old\n");

            tree.Workspace.BeginRun();
            Apply(tree.Changes, "run-2", "src/App/New.cs", string.Empty, "new\n");

            return (Node(tree, "src/App/Old.cs")!.IsModified, Node(tree, "src/App/New.cs")!.IsModified);
        });

        old.ShouldBeFalse("the previous run's file is not part of this run's story");
        fresh.ShouldBeTrue();
    }

    /// <summary>
    /// The counts are the current run's, not the session's. A file both runs touched would
    /// otherwise report everything since the window opened as though this run had done it.
    /// </summary>
    [Fact]
    public void The_counts_are_this_run_s_arithmetic_alone()
    {
        using TempWorkspace workspace = new();
        workspace.WriteFile("src/App/Program.cs", "a\nb\nc\n");

        (int added, int removed) = Over(workspace, (_, tree) =>
        {
            Apply(tree.Changes, "run-1", "src/App/Program.cs", "a\n", "a\nb\n");

            tree.Workspace.BeginRun();
            Apply(tree.Changes, "run-2", "src/App/Program.cs", "a\nb\n", "a\nb\nc\n");

            FileNodeViewModel node = Node(tree, "src/App/Program.cs")!;
            return (node.LinesAdded, node.LinesRemoved);
        });

        added.ShouldBe(1, "run 2 added one line; run 1's line belongs to run 1");
        removed.ShouldBe(0);
    }

    /// <summary>
    /// A revert still unmarks. <see cref="ChangeLog.Update"/> keeps the change's original run id,
    /// so undoing this run's work is this run's business however long after it happens.
    /// </summary>
    [Fact]
    public void Reverting_this_run_s_change_unmarks_the_file()
    {
        using TempWorkspace workspace = new();
        workspace.WriteFile("src/App/Program.cs", "a\n");

        bool marked = Over(workspace, (_, tree) =>
        {
            tree.Workspace.BeginRun();
            CodeChange change = Apply(tree.Changes, "run-1", "src/App/Program.cs", string.Empty, "a\n");
            tree.Changes.Update(change.Id, ChangeStatus.Reverted);

            return Node(tree, "src/App/Program.cs")!.IsModified;
        });

        marked.ShouldBeFalse();
    }

    /// <summary>A file the run creates gets a node, and the folders above it are opened to it.</summary>
    [Fact]
    public void A_change_to_a_file_the_tree_has_not_seen_creates_it_expanded()
    {
        using TempWorkspace workspace = new();
        workspace.WriteFile("README.md", "# nothing else yet");

        (bool marked, bool opened) = Over(workspace, (_, tree) =>
        {
            tree.Workspace.BeginRun();
            Apply(tree.Changes, "run-1", "src/App/Program.cs", string.Empty, "class P { }\n");

            return (Node(tree, "src/App/Program.cs")!.IsModified, Node(tree, "src/App")!.IsExpanded);
        });

        marked.ShouldBeTrue();
        opened.ShouldBeTrue();
    }

    // ── Scaffolding ──

    /// <summary>
    /// Records an applied change under a named run. The run id is ambient rather than a
    /// parameter, which is what lets a tool record one without carrying harness bookkeeping.
    /// </summary>
    private static CodeChange Apply(IChangeLog changes, string runId, string path, string before, string after)
    {
        RunContext.Set(new RunContext(runId, "task"));
        try
        {
            CodeChange change = changes.Propose(path, "edit_file", before, after);
            return changes.Update(change.Id, ChangeStatus.Applied)!;
        }
        finally
        {
            RunContext.Clear();
        }
    }

    /// <summary>The node at a repo-relative path, or null when the tree does not hold one.</summary>
    private static FileNodeViewModel? Node(Tree tree, string path)
    {
        IReadOnlyList<FileNodeViewModel> level = tree.Workspace.RootNodes;
        FileNodeViewModel? found = null;

        foreach (string segment in path.Split('/'))
        {
            found = level.FirstOrDefault(node =>
                string.Equals(node.Name, segment, StringComparison.OrdinalIgnoreCase));

            if (found is null)
            {
                return null;
            }

            level = found.Children;
        }

        return found;
    }

    /// <summary>
    /// Builds the pane over a throwaway workspace, waits for its first read, and hands
    /// <paramref name="assert"/> the dispatcher it was built on. Everything is computed inside,
    /// while the container - and so the file-system watcher - is still alive.
    /// </summary>
    private static T Over<T>(TempWorkspace workspace, Func<Dispatcher, Tree, T> assert) =>
        UiThread.Run(dispatcher =>
        {
            IConfiguration configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["GlassCoder:Workspace:RepoRoot"] = workspace.Root,
                    ["GlassCoder:Models:Roles:worker:Endpoint"] = "http://localhost:8001/v1",
                    ["GlassCoder:Models:Roles:worker:ModelAlias"] = "worker",
                    ["GlassCoder:Telemetry:Enabled"] = "false",
                    ["GlassCoder:Metrics:Directory"] = Path.Combine(workspace.Root, "metrics"),
                })
                .Build();

            ServiceCollection services = new();
            services.AddSingleton(configuration);
            services.AddLogging();
            services.AddGlassCoder(configuration);
            services.AddGlassCoderDesktop(dispatcher);

            using ServiceProvider provider = services.BuildServiceProvider();
            WorkspaceViewModel pane = provider.GetRequiredService<WorkspaceViewModel>();

            UiThread.Pump(dispatcher, () => pane.Loaded.IsCompleted, TimeSpan.FromSeconds(15))
                .ShouldBeTrue("the pane never finished its first read of the workspace");

            return assert(dispatcher, new Tree(pane, provider.GetRequiredService<IChangeLog>()));
        });

    /// <summary>The pane and the log it draws its colouring from.</summary>
    private sealed record Tree(WorkspaceViewModel Workspace, IChangeLog Changes);
}
