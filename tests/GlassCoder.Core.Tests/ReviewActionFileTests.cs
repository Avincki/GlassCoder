using GlassCoder.Core.Verification;
using GlassCoder.TestSupport;

namespace GlassCoder.Core.Tests;

/// <summary>
/// The on-disk form of a review (workplan task 43).
/// <para>
/// The format is a contract with a reader that does not exist yet - something will eventually
/// consume these files and act on the ticked items. The parser is here from the start precisely
/// so the round-trip is provable now, while the format is still cheap to change.
/// </para>
/// </summary>
public sealed class ReviewActionFileTests
{
    [Fact]
    public void A_rendered_plan_parses_back_to_itself()
    {
        ReviewActionPlan plan = Plan();

        ReviewActionFile.TryParse(ReviewActionFile.Render(plan), out ReviewActionPlan? parsed).ShouldBeTrue();

        parsed.ShouldNotBeNull();
        parsed.File.ShouldBe("src/A.cs");
        parsed.Model.ShouldBe("claude-opus-5");
        parsed.CostUsd.ShouldBe(0.214m);
        parsed.Report.ShouldContain("The guard is missing.");
        parsed.Items.Count.ShouldBe(3);

        ReviewActionItem first = parsed.Items[0];
        first.Action.Id.ShouldBe("guard");
        first.Action.Title.ShouldBe("Reject '..' before combining");
        first.Action.Detail.ShouldBe("WorkspaceViewModel.cs:233 builds a path from a node name.");
        first.Action.Priority.ShouldBe(ReviewActionPriority.High);
    }

    [Fact]
    public void Only_the_ticked_items_come_back_accepted()
    {
        // The rule the future consumer runs on: do the ticked ones. Everything else is context.
        ReviewActionPlan parsed = Roundtrip(Plan());

        parsed.Accepted.Select(a => a.Id).ShouldBe(["guard", "cover"]);
        parsed.Items.Count.ShouldBe(3, "the declined proposal stays in the file as context");
        parsed.Items.Single(i => i.Action.Id == "tidy").Accepted.ShouldBeFalse();
    }

    [Fact]
    public void Every_priority_survives_the_round_trip()
    {
        foreach (ReviewActionPriority priority in Enum.GetValues<ReviewActionPriority>())
        {
            ReviewActionPlan plan = Plan() with
            {
                Items = [new ReviewActionItem(new ReviewAction("only", "Do it", "Because.", priority), true)],
            };

            Roundtrip(plan).Items[0].Action.Priority.ShouldBe(priority);
        }
    }

    [Fact]
    public void A_title_that_spans_lines_is_flattened_so_it_cannot_break_the_list()
    {
        ReviewActionPlan plan = Plan() with
        {
            Items =
            [
                new ReviewActionItem(
                    new ReviewAction("wrapped", "Reject\n  '..'  before\ncombining", "Line\none.", ReviewActionPriority.Low),
                    true),
            ],
        };

        ReviewActionPlan parsed = Roundtrip(plan);

        parsed.Items.Count.ShouldBe(1);
        parsed.Items[0].Action.Title.ShouldBe("Reject '..' before combining");
        parsed.Items[0].Action.Detail.ShouldBe("Line one.");
    }

    [Fact]
    public void A_markdown_file_that_is_not_ours_is_refused()
    {
        // A permissive parser would happily "read" an unrelated document and hand back an empty
        // plan, which downstream would take for "the reviewer proposed nothing".
        ReviewActionFile.TryParse("# Just some notes\n\n- [x] buy milk", out ReviewActionPlan? plan).ShouldBeFalse();
        plan.ShouldBeNull();

        ReviewActionFile.TryParse("---\nglasscoder: something-else\n---\n", out plan).ShouldBeFalse();
        ReviewActionFile.TryParse(null, out plan).ShouldBeFalse();
    }

    [Fact]
    public void Front_matter_this_version_does_not_know_about_is_ignored()
    {
        string rendered = ReviewActionFile.Render(Plan())
            .Replace("via: claude-code", "via: claude-code\nsomethingNew: 42", StringComparison.Ordinal);

        ReviewActionFile.TryParse(rendered, out ReviewActionPlan? parsed).ShouldBeTrue();
        parsed!.Items.Count.ShouldBe(3);
    }

    [Fact]
    public void A_review_with_no_proposals_still_renders_and_parses()
    {
        ReviewActionPlan plan = Plan() with { Items = [] };

        ReviewActionPlan parsed = Roundtrip(plan);

        parsed.Items.ShouldBeEmpty();
        parsed.Report.ShouldContain("The guard is missing.");
    }

    [Fact]
    public void The_suggested_name_is_a_legal_file_name()
    {
        string name = ReviewActionFile.SuggestFileName("src/GlassCoder.Wpf/A.cs", DateTimeOffset.UnixEpoch);

        name.ShouldBe("A.cs-19700101-000000.md");
        name.IndexOfAny(Path.GetInvalidFileNameChars()).ShouldBe(-1);
    }

    [Fact]
    public void The_writer_puts_the_file_inside_the_workspace()
    {
        using TempWorkspace workspace = new();
        ReviewActionWriter writer = new(workspace.Guard(), TempWorkspace.Wrap(new FileReviewOptions()));

        string path = writer.Write(Plan());

        File.Exists(path).ShouldBeTrue();
        path.ShouldStartWith(workspace.Root);
        path.ShouldContain(".glasscoder");
        ReviewActionFile.TryParse(File.ReadAllText(path), out _).ShouldBeTrue();
    }

    [Fact]
    public void The_writer_refuses_an_output_directory_outside_the_workspace()
    {
        // A human action rather than an agent one, so it does not go through the writable
        // allow-list - but a review of a file in this repository has no business landing
        // somewhere else either.
        using TempWorkspace workspace = new();
        FileReviewOptions options = new() { OutputDirectory = "../../escaped" };
        ReviewActionWriter writer = new(workspace.Guard(), TempWorkspace.Wrap(options));

        Should.Throw<InvalidOperationException>(() => writer.Write(Plan()));
    }

    private static ReviewActionPlan Roundtrip(ReviewActionPlan plan)
    {
        ReviewActionFile.TryParse(ReviewActionFile.Render(plan), out ReviewActionPlan? parsed).ShouldBeTrue();
        return parsed!;
    }

    private static ReviewActionPlan Plan() => new(
        "src/A.cs",
        DateTimeOffset.UnixEpoch,
        "claude-opus-5",
        0.214m,
        "# Review\n\nThe guard is missing.",
        [
            new ReviewActionItem(
                new ReviewAction(
                    "guard",
                    "Reject '..' before combining",
                    "WorkspaceViewModel.cs:233 builds a path from a node name.",
                    ReviewActionPriority.High),
                Accepted: true),
            new ReviewActionItem(
                new ReviewAction("cover", "Add a regression test", "None covers the escape.", ReviewActionPriority.Medium),
                Accepted: true),
            new ReviewActionItem(
                new ReviewAction("tidy", "Rename the probe constant", string.Empty, ReviewActionPriority.Optional),
                Accepted: false),
        ]);
}
