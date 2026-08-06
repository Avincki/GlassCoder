using GlassCoder.TestSupport;
using GlassCoder.Tools;
using GlassCoder.Tools.Changes;
using GlassCoder.Tools.FileSystem;
using Microsoft.Extensions.Options;

namespace GlassCoder.Tools.Tests;

/// <summary>
/// Removing, relocating and undoing a file (workplan tasks 49 and 50).
/// <para>
/// The agent could create a file and change one, and nothing else. That is why the nested-project
/// hazard <c>list_projects</c> reports was diagnosable and not fixable: resolving it means moving
/// a file out of another project's glob, and no tool could.
/// </para>
/// </summary>
public sealed class FileOperationTests : IDisposable
{
    private readonly TempWorkspace _workspace = new();
    private readonly ChangeLog _changes = new();

    [Fact]
    public async Task A_deleted_file_leaves_the_tree_and_its_contents_in_the_change_log()
    {
        string path = _workspace.WriteFile("src/Dead.cs", "public class Dead { }\n");

        ToolObservation<FileOperationResult> observation =
            await Tool().RunAsync(FileOperation.Delete, "src/Dead.cs");

        observation.Ok.ShouldBeTrue(observation.Error?.Message);
        File.Exists(path).ShouldBeFalse();

        // Before-text to nothing, so the Changes surface shows a removal rather than an absence
        // nobody can account for.
        CodeChange change = _changes.All().Single();
        change.Status.ShouldBe(ChangeStatus.Applied);
        change.BeforeText.ShouldBe("public class Dead { }\n");
        change.AfterText.ShouldBeEmpty();

        // And the model is told the content survives: run d21eb210 deleted its deliverable and,
        // not knowing restoring was possible, removed the references to it instead.
        observation.Summary.ShouldContain("preserved in the change log");
    }

    [Fact]
    public async Task A_move_records_a_removal_and_an_addition()
    {
        _workspace.WriteFile("src/Inner/Thing.cs", "public class Thing { }\n");

        ToolObservation<FileOperationResult> observation =
            await Tool().RunAsync(FileOperation.Move, "src/Inner/Thing.cs", "src/Outer/Thing.cs");

        observation.Ok.ShouldBeTrue(observation.Error?.Message);
        File.Exists(Path.Combine(_workspace.Root, "src", "Inner", "Thing.cs")).ShouldBeFalse();
        File.Exists(Path.Combine(_workspace.Root, "src", "Outer", "Thing.cs")).ShouldBeTrue();

        // Two entries, because CodeChange.Path is singular - and because that is how a reviewer
        // wants to read a move.
        _changes.All().Count.ShouldBe(2);
        _changes.All().ShouldAllBe(c => c.Status == ChangeStatus.Applied);
        _changes.All().Select(c => c.Path).ShouldBe(["src/Inner/Thing.cs", "src/Outer/Thing.cs"], ignoreOrder: true);
    }

    [Fact]
    public async Task A_move_creates_the_directories_it_needs()
    {
        _workspace.WriteFile("src/A.cs", "class A { }\n");

        ToolObservation<FileOperationResult> observation =
            await Tool().RunAsync(FileOperation.Move, "src/A.cs", "src/Deeply/Nested/A.cs");

        observation.Ok.ShouldBeTrue(observation.Error?.Message);
        File.Exists(Path.Combine(_workspace.Root, "src", "Deeply", "Nested", "A.cs")).ShouldBeTrue();
    }

    [Fact]
    public async Task A_move_onto_something_that_exists_is_refused()
    {
        _workspace.WriteFile("src/A.cs", "class A { }\n");
        _workspace.WriteFile("src/B.cs", "class B { }\n");

        ToolObservation<FileOperationResult> observation =
            await Tool().RunAsync(FileOperation.Move, "src/A.cs", "src/B.cs");

        observation.Ok.ShouldBeFalse();
        observation.Error!.Code.ShouldBe(ToolErrorCodes.AlreadyExists);
        File.Exists(Path.Combine(_workspace.Root, "src", "A.cs")).ShouldBeTrue("nothing should have moved");
    }

    [Fact]
    public async Task A_directory_is_refused_outright()
    {
        // A tool that can empty bin/ is a tool that can empty something else.
        _workspace.CreateDirectory("src/Folder");

        ToolObservation<FileOperationResult> observation =
            await Tool().RunAsync(FileOperation.Delete, "src/Folder");

        observation.Ok.ShouldBeFalse();
        observation.Error!.Code.ShouldBe(ToolErrorCodes.InvalidArgument);
        Directory.Exists(Path.Combine(_workspace.Root, "src", "Folder")).ShouldBeTrue();
    }

    [Fact]
    public async Task Anything_outside_the_writable_set_is_refused()
    {
        _workspace.WriteFile("docs/Notes.md", "# notes\n");

        // The guard is rooted with only src writable.
        ToolObservation<FileOperationResult> observation =
            await Tool().RunAsync(FileOperation.Delete, "docs/Notes.md");

        observation.Ok.ShouldBeFalse();
        observation.Error!.Code.ShouldBe(ToolErrorCodes.PathNotAllowed);
        File.Exists(Path.Combine(_workspace.Root, "docs", "Notes.md")).ShouldBeTrue();
    }

    [Fact]
    public async Task A_refused_approval_leaves_the_file_alone()
    {
        string path = _workspace.WriteFile("src/Keep.cs", "class Keep { }\n");

        ToolObservation<FileOperationResult> observation =
            await Tool(new RefusingGate()).RunAsync(FileOperation.Delete, "src/Keep.cs");

        observation.Ok.ShouldBeFalse();
        observation.Error!.Code.ShouldBe(ToolErrorCodes.ApprovalRefused);
        File.Exists(path).ShouldBeTrue();
        _changes.All().Single().Status.ShouldBe(ChangeStatus.Rejected);
    }

    [Fact]
    public async Task Revert_puts_a_file_back_the_way_the_run_found_it()
    {
        string path = _workspace.WriteFile("src/A.cs", "original\n");
        Run("run-1");

        // Two edits, so the revert has to reach past the most recent one to the run's own start.
        Applied("src/A.cs", "original\n", "first edit\n");
        Applied("src/A.cs", "first edit\n", "second edit\n");
        await File.WriteAllTextAsync(path, "second edit\n");

        ToolObservation<FileOperationResult> observation = await Tool().RunAsync(FileOperation.Revert, "src/A.cs");

        observation.Ok.ShouldBeTrue(observation.Error?.Message);
        (await File.ReadAllTextAsync(path)).ShouldBe("original\n");
    }

    [Fact]
    public async Task Reverting_a_file_the_run_created_removes_it()
    {
        string path = _workspace.WriteFile("src/New.cs", "class New { }\n");
        Run("run-1");
        Applied("src/New.cs", string.Empty, "class New { }\n");

        ToolObservation<FileOperationResult> observation = await Tool().RunAsync(FileOperation.Revert, "src/New.cs");

        observation.Ok.ShouldBeTrue(observation.Error?.Message);
        File.Exists(path).ShouldBeFalse();
        observation.Summary.ShouldContain("this run created it");
    }

    [Fact]
    public async Task A_file_this_run_never_touched_cannot_be_reverted()
    {
        // The bound that matters: this is the agent undoing its own work, not a general undo of
        // the working tree. It must never discard something the operator did by hand.
        _workspace.WriteFile("src/Untouched.cs", "hand written\n");
        Run("run-1");
        Applied("src/Other.cs", "a\n", "b\n");

        ToolObservation<FileOperationResult> observation =
            await Tool().RunAsync(FileOperation.Revert, "src/Untouched.cs");

        observation.Ok.ShouldBeFalse();
        observation.Error!.Code.ShouldBe(ToolErrorCodes.NotFound);
        observation.Error.Hint.ShouldContain("list_changes");
    }

    [Fact]
    public async Task Another_runs_changes_are_not_this_runs_to_revert()
    {
        string path = _workspace.WriteFile("src/A.cs", "current\n");
        Run("run-earlier");
        Applied("src/A.cs", "much older\n", "current\n");
        Run("run-now");

        ToolObservation<FileOperationResult> observation = await Tool().RunAsync(FileOperation.Revert, "src/A.cs");

        observation.Ok.ShouldBeFalse();
        (await File.ReadAllTextAsync(path)).ShouldBe("current\n");
    }

    private FileOperationTool Tool(IApprovalGate? approval = null) =>
        new(_workspace.Guard("src"), _changes, approval);

    private static void Run(string runId) => RunContext.Set(new RunContext(runId, "task"));

    private void Applied(string path, string before, string after) =>
        _changes.Update(_changes.Propose(path, "edit_file", before, after).Id, ChangeStatus.Applied);

    public void Dispose() => _workspace.Dispose();

    /// <summary>A gate that always says no, for the approval-refused cases.</summary>
    private sealed class RefusingGate : IApprovalGate
    {
        public bool IsInteractive => true;

        public Task<ApprovalDecision> RequestAsync(CodeChange change, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ApprovalDecision(false, "A human said no."));

        public Task<ApprovalDecision> RequestActionAsync(AgentAction action, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ApprovalDecision(false, "A human said no."));
    }
}

/// <summary>
/// What this run has already changed (workplan task 50).
/// </summary>
public sealed class ListChangesTests
{
    [Fact]
    public void A_run_that_has_changed_nothing_says_so()
    {
        ChangeLog changes = new();
        RunContext.Set(new RunContext("run-empty", "task"));

        ToolObservation<ListChangesResult> observation = new ListChangesTool(changes).ListChanges();

        observation.Ok.ShouldBeTrue();
        observation.Data!.Files.ShouldBeEmpty();
        observation.Summary.ShouldContain("has not changed anything");
    }

    [Fact]
    public void Changes_are_rolled_up_per_file_with_their_status()
    {
        ChangeLog changes = new();
        RunContext.Set(new RunContext("run-1", "task"));

        changes.Update(changes.Propose("src/A.cs", "create_file", string.Empty, "one\ntwo\n").Id, ChangeStatus.Applied);
        changes.Update(changes.Propose("src/A.cs", "edit_file", "one\ntwo\n", "one\ntwo\nthree\n").Id, ChangeStatus.Applied);
        changes.Update(changes.Propose("src/B.cs", "edit_file", "x\n", "y\n").Id, ChangeStatus.Rejected);

        ListChangesResult result = new ListChangesTool(changes).ListChanges().Data!;

        result.Files.Count.ShouldBe(2);
        result.Applied.ShouldBe(2);
        result.Rejected.ShouldBe(1);

        ChangedFile a = result.Files.Single(f => f.Path == "src/A.cs");

        // Four, not three: the rollup is FileChangeSummary, the same one the workspace pane
        // draws, and it counts the empty line a trailing newline leaves behind. Two edits roll
        // into one net count from the first before-text to the last after-text rather than
        // being added together.
        a.LinesAdded.ShouldBe(4);
        a.Status.ShouldBe("Applied");
        a.Tools.ShouldBe(["create_file", "edit_file"]);

        result.Files.Single(f => f.Path == "src/B.cs").Status.ShouldBe("Rejected");
    }

    [Fact]
    public void Only_this_run_is_reported()
    {
        ChangeLog changes = new();

        RunContext.Set(new RunContext("run-earlier", "task"));
        changes.Update(changes.Propose("src/Old.cs", "edit_file", "a\n", "b\n").Id, ChangeStatus.Applied);

        RunContext.Set(new RunContext("run-now", "task"));
        changes.Update(changes.Propose("src/New.cs", "edit_file", "c\n", "d\n").Id, ChangeStatus.Applied);

        ListChangesResult result = new ListChangesTool(changes).ListChanges().Data!;

        result.Files.Select(f => f.Path).ShouldBe(["src/New.cs"]);
    }
}
