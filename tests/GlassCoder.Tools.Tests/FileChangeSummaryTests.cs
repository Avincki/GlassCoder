using GlassCoder.Tools.Changes;

namespace GlassCoder.Tools.Tests;

/// <summary>
/// The workspace pane's per-file counts (workplan task 39). The invariant under test is that
/// the numbers are <em>net</em> - what a user comparing the file on disk against the session's
/// start would count - not a tally of the agent's individual edits.
/// </summary>
public sealed class FileChangeSummaryTests
{
    [Fact]
    public void A_single_applied_change_counts_its_added_and_removed_lines()
    {
        CodeChange change = Change("src/Pager.cs", "a\nb\nc", "a\nx\nc", ChangeStatus.Applied);

        IReadOnlyDictionary<string, FileChangeStats> stats = FileChangeSummary.Summarise([change]);

        stats["src/Pager.cs"].ShouldBe(new FileChangeStats(LinesAdded: 1, LinesRemoved: 1));
    }

    [Fact]
    public void A_created_file_counts_every_line_as_added()
    {
        CodeChange change = Change("src/New.cs", "", "line1\nline2\nline3", ChangeStatus.Applied);

        FileChangeStats? stats = FileChangeSummary.ForPath([change], "src/New.cs");

        stats.ShouldBe(new FileChangeStats(LinesAdded: 3, LinesRemoved: 0));
    }

    [Fact]
    public void Repeated_edits_to_one_file_net_rather_than_sum()
    {
        // The agent adds a line, then rewrites it. Per-change sums would report two additions;
        // against the session's start exactly one line is new.
        CodeChange firstEdit = Change("src/Pager.cs", "a", "a\nfirst", ChangeStatus.Applied);
        CodeChange rewrite = Change("src/Pager.cs", "a\nfirst", "a\nsecond", ChangeStatus.Applied);

        IReadOnlyDictionary<string, FileChangeStats> stats =
            FileChangeSummary.Summarise([firstEdit, rewrite]);

        stats["src/Pager.cs"].ShouldBe(new FileChangeStats(LinesAdded: 1, LinesRemoved: 0));
    }

    [Fact]
    public void Edits_that_cancel_out_still_report_the_file_at_zero()
    {
        CodeChange add = Change("src/Pager.cs", "a\nb", "a\nx\nb", ChangeStatus.Applied);
        CodeChange undo = Change("src/Pager.cs", "a\nx\nb", "a\nb", ChangeStatus.Applied);

        IReadOnlyDictionary<string, FileChangeStats> stats = FileChangeSummary.Summarise([add, undo]);

        stats["src/Pager.cs"].ShouldBe(new FileChangeStats(LinesAdded: 0, LinesRemoved: 0));
    }

    [Fact]
    public void Proposed_and_rejected_changes_do_not_mark_a_file_as_modified()
    {
        CodeChange proposed = Change("src/A.cs", "a", "b", ChangeStatus.Proposed);
        CodeChange rejected = Change("src/B.cs", "a", "b", ChangeStatus.Rejected);

        IReadOnlyDictionary<string, FileChangeStats> stats =
            FileChangeSummary.Summarise([proposed, rejected]);

        stats.ShouldBeEmpty();
    }

    [Fact]
    public void A_reverted_change_drops_out_of_the_rollup()
    {
        CodeChange kept = Change("src/Pager.cs", "a", "a\nkept", ChangeStatus.Applied);
        CodeChange undone = Change("src/Pager.cs", "a\nkept", "a\nkept\nundone", ChangeStatus.Reverted);

        IReadOnlyDictionary<string, FileChangeStats> stats = FileChangeSummary.Summarise([kept, undone]);

        // Only the surviving change counts: before the first applied, after the last applied.
        stats["src/Pager.cs"].ShouldBe(new FileChangeStats(LinesAdded: 1, LinesRemoved: 0));
    }

    [Fact]
    public void ForPath_returns_null_for_a_file_the_session_never_touched()
    {
        CodeChange change = Change("src/Pager.cs", "a", "b", ChangeStatus.Applied);

        FileChangeSummary.ForPath([change], "src/Other.cs").ShouldBeNull();
    }

    [Fact]
    public void Files_are_reported_independently()
    {
        CodeChange one = Change("src/A.cs", "a", "a\nnew", ChangeStatus.Applied);
        CodeChange two = Change("src/B.cs", "x\ny", "y", ChangeStatus.Applied);

        IReadOnlyDictionary<string, FileChangeStats> stats = FileChangeSummary.Summarise([one, two]);

        stats["src/A.cs"].ShouldBe(new FileChangeStats(LinesAdded: 1, LinesRemoved: 0));
        stats["src/B.cs"].ShouldBe(new FileChangeStats(LinesAdded: 0, LinesRemoved: 1));
    }

    private static CodeChange Change(string path, string before, string after, ChangeStatus status) =>
        new()
        {
            Id = Guid.NewGuid().ToString("n")[..12],
            RunId = "run",
            TaskId = "task",
            Path = path,
            Tool = "edit_file",
            BeforeText = before,
            AfterText = after,
            CreatedAt = DateTimeOffset.UnixEpoch,
            Status = status,
        };
}
