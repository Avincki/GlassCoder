using GlassCoder.TestSupport;
using GlassCoder.Tools.Changes;
using GlassCoder.Tools.FileSystem;
using GlassCoder.Tools.Planning;
using GlassCoder.Tools.Verification;
using Microsoft.Extensions.Options;

namespace GlassCoder.Tools.Tests;

/// <summary>
/// The plan (task 24), the change log and diff (task 27), and the approval gate (task 28).
/// </summary>
public sealed class PlanningAndChangeTests : IDisposable
{
    private readonly TempWorkspace _workspace = new();

    public void Dispose() => _workspace.Dispose();

    [Fact]
    public void The_agent_can_record_and_advance_a_plan()
    {
        TodoList list = new();
        TodoTool tool = new(list);

        tool.UpdateTodos([
            new TodoItem("find", "Find the bug", TodoStatus.Completed),
            new TodoItem("fix", "Fix it", TodoStatus.InProgress),
            new TodoItem("verify", "Run the tests"),
        ]).Ok.ShouldBeTrue();

        list.Items.Count.ShouldBe(3);
        list.Items[1].Status.ShouldBe(TodoStatus.InProgress);
    }

    [Fact]
    public void A_plan_with_two_items_in_progress_is_refused()
    {
        // Working on one thing at a time is the point of the plan.
        TodoTool tool = new(new TodoList());

        ToolObservation<TodoResult> observation = tool.UpdateTodos([
            new TodoItem("a", "First", TodoStatus.InProgress),
            new TodoItem("b", "Second", TodoStatus.InProgress),
        ]);

        observation.Ok.ShouldBeFalse();
        observation.Error!.Message.ShouldContain("one thing at a time");
    }

    [Fact]
    public void A_plan_with_duplicate_or_empty_ids_is_refused()
    {
        TodoTool tool = new(new TodoList());

        tool.UpdateTodos([new TodoItem("a", "First"), new TodoItem("a", "Again")]).Ok.ShouldBeFalse();
        tool.UpdateTodos([new TodoItem("", "Nameless")]).Ok.ShouldBeFalse();
    }

    [Fact]
    public void The_plan_raises_a_change_so_the_ui_can_follow()
    {
        TodoList list = new();
        int notifications = 0;
        list.Changed += (_, _) => notifications++;

        new TodoTool(list).UpdateTodos([new TodoItem("a", "First")]);

        notifications.ShouldBe(1);
    }

    [Fact]
    public void A_diff_reports_added_removed_and_context_lines()
    {
        IReadOnlyList<DiffLine> diff = TextDiff.Compute("one\ntwo\nthree", "one\ntwo point five\nthree", contextLines: 5);

        diff.ShouldContain(d => d.Kind == DiffKind.Removed && d.Text == "two");
        diff.ShouldContain(d => d.Kind == DiffKind.Added && d.Text == "two point five");
        diff.ShouldContain(d => d.Kind == DiffKind.Context && d.Text == "one");
    }

    [Fact]
    public void A_diff_of_identical_text_has_no_changes()
    {
        TextDiff.ChangedRange(TextDiff.Compute("same", "same")).ShouldBeNull();
    }

    [Fact]
    public void A_one_line_edit_in_a_large_file_stays_a_small_diff()
    {
        string before = string.Join('\n', Enumerable.Range(1, 200).Select(i => $"line {i}"));
        string after = before.Replace("line 100", "line one hundred", StringComparison.Ordinal);

        IReadOnlyList<DiffLine> diff = TextDiff.Compute(before, after, contextLines: 2);

        diff.Count.ShouldBeLessThan(12);
        TextDiff.ChangedRange(diff)!.Value.Start.ShouldBe(100);
    }

    [Fact]
    public async Task An_applied_edit_appears_in_the_change_log_with_its_diff_and_status()
    {
        ChangeLog log = new();
        _workspace.WriteFile("src/Pager.cs", "class Pager\n{\n    int Last => 10;\n}\n");

        ToolObservation<EditFileResult> observation = await Tool(log).EditFileAsync("src/Pager.cs", "=> 10;", "=> 9;");

        observation.Ok.ShouldBeTrue(observation.Error?.Message);
        CodeChange change = log.All().ShouldHaveSingleItem();
        change.Status.ShouldBe(ChangeStatus.Applied);
        change.Path.ShouldBe("src/Pager.cs");
        change.Diff().ShouldContain(d => d.Kind == DiffKind.Added && d.Text.Contains("=> 9;", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_rejected_edit_is_still_recorded_so_the_ui_can_show_what_was_attempted()
    {
        ChangeLog log = new();
        _workspace.WriteFile("src/Proj.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");
        string path = _workspace.WriteFile("src/Pager.cs", "public class Pager { public int Last => 1; }");

        await Tool(log).EditFileAsync("src/Pager.cs", "public int Last => 1;", "public int Last => ;");

        CodeChange change = log.All().ShouldHaveSingleItem();
        change.Status.ShouldBe(ChangeStatus.Rejected);
        change.Note.ShouldNotBeNull();
        File.ReadAllText(path).ShouldContain("=> 1;");
    }

    [Fact]
    public async Task A_human_rejection_stops_the_write()
    {
        ChangeLog log = new();
        string path = _workspace.WriteFile("src/notes.md", "old line\n");
        string before = File.ReadAllText(path);

        ToolObservation<EditFileResult> observation = await Tool(log, new RejectingGate())
            .EditFileAsync("src/notes.md", "old line", "new line");

        observation.Ok.ShouldBeFalse();
        observation.Error!.Code.ShouldBe(ToolErrorCodes.ApprovalRefused);
        File.ReadAllText(path).ShouldBe(before);
        log.All().ShouldHaveSingleItem().Status.ShouldBe(ChangeStatus.Rejected);
    }

    [Fact]
    public async Task Approval_fails_closed_when_it_is_required_and_nobody_can_be_asked()
    {
        // Approving anyway would turn a safety setting into a no-op exactly when someone
        // believed it was protecting them.
        AutoApprovalGate gate = new(Options.Create(new ApprovalOptions { RequireApprovalForWrites = true }));

        ApprovalDecision decision = await gate.RequestAsync(new ChangeLog().Propose("a.cs", "edit_file", "a", "b"));

        decision.Approved.ShouldBeFalse();
        decision.Reason.ShouldContain("no way to ask");
    }

    [Fact]
    public async Task Approval_is_transparent_when_it_is_not_required()
    {
        AutoApprovalGate gate = new(Options.Create(new ApprovalOptions()));

        (await gate.RequestAsync(new ChangeLog().Propose("a.cs", "edit_file", "a", "b"))).Approved.ShouldBeTrue();
    }

    [Fact]
    public void Changes_are_grouped_per_task()
    {
        ChangeLog log = new();
        RunContext.Set(new RunContext("run-1", "task-a"));
        log.Propose("a.cs", "edit_file", "1", "2");
        RunContext.Set(new RunContext("run-2", "task-b"));
        log.Propose("b.cs", "edit_file", "1", "2");
        RunContext.Clear();

        log.ForTask("task-a").ShouldHaveSingleItem().Path.ShouldBe("a.cs");
        log.All().Count.ShouldBe(2);
    }

    private EditFileTool Tool(IChangeLog log, IApprovalGate? gate = null)
    {
        IOptions<VerificationOptions> options = Options.Create(new VerificationOptions());
        Guardrails.PathGuard guard = _workspace.Guard("src");

        return new EditFileTool(
            guard,
            new RoslynCodeAnalyzer(guard, options),
            new DiagnosticSummarizer(options),
            options,
            log,
            gate);
    }

    private sealed class RejectingGate : IApprovalGate
    {
        public bool IsInteractive => true;

        public Task<ApprovalDecision> RequestAsync(CodeChange change, CancellationToken cancellationToken = default) =>
            Task.FromResult(ApprovalDecision.Reject("A reviewer rejected this change."));
    }
}
