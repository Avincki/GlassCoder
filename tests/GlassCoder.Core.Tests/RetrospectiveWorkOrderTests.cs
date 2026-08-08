using GlassCoder.Core.Verification;
using GlassCoder.TestSupport;

namespace GlassCoder.Core.Tests;

/// <summary>
/// The work order a retrospective leaves behind (workplan task 67).
/// <para>
/// One renderer serves both this and the file review's output, because a recommendation and a
/// review action are the same thing seen from two distances. What differs is where it lands: a
/// review of a workspace file belongs in that workspace, and a recommendation about GlassCoder
/// belongs where GlassCoder's source is - or nowhere, if nobody has said where that is.
/// </para>
/// </summary>
public sealed class RetrospectiveWorkOrderTests
{
    [Fact]
    public void The_work_order_round_trips_through_the_parser()
    {
        string rendered = ReviewActionFile.Render(Plan());

        ReviewActionFile.TryParse(rendered, out ReviewActionPlan? read).ShouldBeTrue();
        read.ShouldNotBeNull();
        read.Kind.ShouldBe(ReviewActionFile.RetrospectiveKind);
        read.Target.ShouldBe("harness");
        read.RunId.ShouldBe("216360bf");
        read.Items.Count.ShouldBe(2);
        read.Items[0].Accepted.ShouldBeTrue();
        read.Items[1].Accepted.ShouldBeFalse();

        // The rejected proposal is in the file because it is the context that explains the
        // accepted one - and because an agent reading this should know what was turned down.
        read.Items[1].Action.Id.ShouldBe("rename-probe");
    }

    [Fact]
    public void The_closing_instructions_do_not_leak_into_the_last_item()
    {
        // The parser reads an indented line after an item as that item's detail, which is right
        // for a wrapped bullet and wrong for the block that follows the list. A heading ends it.
        string rendered = ReviewActionFile.Render(Plan());

        ReviewActionFile.TryParse(rendered, out ReviewActionPlan? read).ShouldBeTrue();

        read.ShouldNotBeNull();
        read.Items[^1].Action.Detail.ShouldNotContain("HISTORY.md");
        rendered.ShouldContain("HISTORY.md", Case.Sensitive);
    }

    [Fact]
    public void A_file_review_still_reads_as_a_file_review()
    {
        // The generalisation must not have quietly turned every work order into a retrospective.
        ReviewActionPlan review = new(
            "src/A.cs", DateTimeOffset.UnixEpoch, "claude-opus-5", 0.2m, "# Findings",
            [new ReviewActionItem(new ReviewAction("guard", "Reject '..'", "line 233", ReviewActionPriority.High), true)]);

        string rendered = ReviewActionFile.Render(review);

        rendered.ShouldContain("glasscoder: review-actions");
        rendered.ShouldNotContain("target:");
        rendered.ShouldContain("# Review - src/A.cs");

        ReviewActionFile.TryParse(rendered, out ReviewActionPlan? read).ShouldBeTrue();
        read.ShouldNotBeNull();
        read.Kind.ShouldBe(ReviewActionFile.Kind);
        read.Target.ShouldBeNull();
    }

    [Fact]
    public void It_lands_in_the_harness_repository_under_a_run_named_file()
    {
        using TempWorkspace harness = new();

        string path = Writer(harness.Root).Write(Plan());

        Path.GetDirectoryName(path).ShouldBe(Path.Combine(harness.Root, "docs", "retrospectives"));
        Path.GetFileName(path).ShouldStartWith("retro-216360bf-");
        File.ReadAllText(path).ShouldContain("Judge the screen");
    }

    [Fact]
    public void Without_a_configured_source_tree_it_refuses_and_names_the_setting()
    {
        // The case every fresh install is in. A greyed button that does not say why is a bug
        // report waiting to happen, and this one has a one-line fix nobody could guess.
        RetrospectiveWriter writer = Writer(harnessRepoPath: string.Empty);

        writer.CanWrite.ShouldBeFalse();
        writer.UnavailableReason.ShouldNotBeNull().ShouldContain("HarnessRepoPath");
        Should.Throw<InvalidOperationException>(() => writer.Write(Plan()));
    }

    [Fact]
    public void A_source_tree_that_is_not_there_says_that_rather_than_the_other_thing()
    {
        RetrospectiveWriter writer = Writer(Path.Combine(Path.GetTempPath(), "glasscoder-not-a-real-repo"));

        writer.CanWrite.ShouldBeFalse();
        writer.UnavailableReason.ShouldNotBeNull().ShouldContain("not a directory");
    }

    [Fact]
    public void A_configured_directory_cannot_climb_out_of_the_repository()
    {
        using TempWorkspace harness = new();
        RetrospectiveWriter writer = Writer(harness.Root, workOrderDirectory: "../elsewhere");

        Should.Throw<InvalidOperationException>(() => writer.Write(Plan()))
            .Message.ShouldContain("outside the harness repository");
    }

    private static RetrospectiveWriter Writer(string harnessRepoPath, string workOrderDirectory = "docs/retrospectives") =>
        new(TempWorkspace.Wrap(new RetrospectiveOptions
        {
            HarnessRepoPath = harnessRepoPath,
            WorkOrderDirectory = workOrderDirectory,
        }));

    private static ReviewActionPlan Plan() =>
        new(
            "run 216360bf",
            DateTimeOffset.UnixEpoch,
            "claude-opus-5",
            1.75m,
            "These come from a retrospective on run `216360bf`.",
            [
                new ReviewActionItem(
                    new ReviewAction("screen-oracle", "Judge the screen", "nothing does", ReviewActionPriority.High),
                    Accepted: true),
                new ReviewActionItem(
                    new ReviewAction("rename-probe", "Rename the probe", "taste", ReviewActionPriority.Optional),
                    Accepted: false),
            ])
        {
            Kind = ReviewActionFile.RetrospectiveKind,
            Target = "harness",
            RunId = "216360bf",
            Heading = "GlassCoder retrospective - run 216360bf",
            Closing = """
                ## How to use this

                Implement the ticked items above, in this repository, in priority order.

                Add what you implement to HISTORY.md.
                """,
        };
}
