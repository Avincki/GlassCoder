using GlassCoder.Core.Diagnostics;
using GlassCoder.TestSupport;
using GlassCoder.Wpf.ViewModels;

namespace GlassCoder.Wpf.Tests;

/// <summary>
/// The critic exchange on the transcript surface. Run 05e1bedb's completion was accepted 2/3,
/// and nothing on screen said so: the panel's step looked like a blank row, the dissenting
/// critic's reason was discarded, and the post-run review lived only in a dismissable banner.
/// Every entry in the tools column now names its actor, the step detail carries the panel's
/// verdict vote by vote, and the review is a row of the transcript like everything else.
/// </summary>
public sealed class TranscriptCriticTests
{
    private static readonly DateTimeOffset Origin = new(2026, 8, 6, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Every_tool_entry_names_its_actor()
    {
        StepRowViewModel row = Row(Step("run-1", 0, Call("read_file"), Call("grep")));

        row.Tools.ShouldBe("worker.read_file, worker.grep");
    }

    [Fact]
    public void A_verified_step_shows_the_ladder_climb()
    {
        StepRowViewModel row = Row(Step("run-1", 0, Call("edit_file")) with
        {
            Verification = Ladder(passed: true, "UnitTests"),
        });

        row.Tools.ShouldBe("worker.edit_file, harness.verify→UnitTests");
        row.Severity.ShouldBe("info");
    }

    [Fact]
    public void A_failed_climb_names_the_rung_and_colours_the_row()
    {
        StepRowViewModel row = Row(Step("run-1", 0, Call("edit_file")) with
        {
            Verification = Ladder(passed: false, "Compile", failedRung: "Compile"),
        });

        row.Tools.ShouldBe("worker.edit_file, harness.verify→Compile");
        row.Severity.ShouldBe("warning");
    }

    [Fact]
    public void A_critiqued_step_shows_the_tally_and_the_votes()
    {
        // The completion-claim step: no tool call, one panel. The tally is in the column; the
        // dissenting reason - the line the tally hid - is in the detail, labelled with its lens.
        StepRowViewModel row = Row(Step("run-1", 13) with
        {
            ResponseText = "done",
            Verification = Critiqued(refuted: false, refuting: 1,
                Vote(refuted: false, 0.7, "The tests support the claim.", "correctness"),
                Vote(refuted: true, 0.8, "It does not handle NaN.", "evidence"),
                Vote(refuted: false, 0.6, "Nothing else depends on this code.", "regression")),
        });

        row.Tools.ShouldBe("critic.refute 1/3");
        row.Severity.ShouldBe("info", customMessage: "an accepted claim is not a warning");
        row.Detail.ShouldContain("[critic · evidence · REFUTED 0.80] It does not handle NaN.");
        row.Detail.ShouldContain("[critic · correctness · accepted 0.70] The tests support the claim.");
    }

    [Fact]
    public void A_refuting_panel_colours_the_row()
    {
        StepRowViewModel row = Row(Step("run-1", 13) with
        {
            Verification = Critiqued(refuted: true, refuting: 3,
                Vote(refuted: true, 0.8, "The claim is unsupported.", "evidence"),
                Vote(refuted: true, 0.7, "The change does not do what the goal asked.", "correctness"),
                Vote(refuted: true, 0.6, "Callers of the old behaviour break.", "regression")),
        });

        row.Tools.ShouldBe("critic.refute 3/3");
        row.Severity.ShouldBe("warning");
    }

    [Fact]
    public void An_unreachable_critic_reads_as_unreachable_rather_than_as_a_vote()
    {
        StepRowViewModel row = Row(Step("run-1", 13) with
        {
            Verification = Critiqued(refuted: false, refuting: 0,
                new ReviewVoteRecord(false, 0d, "Critic unavailable: 429.", Available: false, "regression")),
        });

        row.Detail.ShouldContain("[critic · regression · unreachable] Critic unavailable: 429.");
    }

    [Fact]
    public void A_recorded_review_becomes_a_row_after_its_run()
    {
        UiThread.Run<object?>(dispatcher =>
        {
            TranscriptBus bus = new(new RecordingStepLogger());
            bus.LogStep(Step("run-1", 0, Call("edit_file")));
            bus.LogStep(Step("run-1", 1) with { ResponseText = "done" });
            bus.LogReview(Review("run-1"));
            bus.LogStep(Step("run-2", 0, Call("read_file")) with { StartedAt = Origin.AddMinutes(10) });

            TranscriptViewModel transcript = new(bus, dispatcher);

            // The review sits after its own run, not after run two, and numbers on from the
            // run's last step.
            transcript.Steps.Select(r => r.Tools).ShouldBe(
                ["worker.edit_file", "-", "critic.review", "worker.read_file"]);
            StepRowViewModel review = transcript.Steps[2];
            review.Index.ShouldBe(2);
            review.Outcome.ShouldBe("review: refuted");
            review.Severity.ShouldBe("warning");
            review.Detail.ShouldContain("[critic · evidence · REFUTED 0.90] The null test was deleted.");
            return null;
        });
    }

    [Fact]
    public void The_tool_filter_matches_prefixed_and_synthesized_entries()
    {
        UiThread.Run<object?>(dispatcher =>
        {
            TranscriptBus bus = new(new RecordingStepLogger());
            bus.LogStep(Step("run-1", 0, Call("read_file")));
            bus.LogStep(Step("run-1", 1) with
            {
                Verification = Critiqued(refuted: false, refuting: 0,
                    Vote(refuted: false, 0.7, "fine", "correctness")),
            });
            bus.LogReview(Review("run-1"));

            TranscriptViewModel transcript = new(bus, dispatcher);

            transcript.ToolFilter = "read_file";
            transcript.View.Cast<object>().Count().ShouldBe(1, "the prefix must not break the plain tool names");

            transcript.ToolFilter = "critic";
            transcript.View.Cast<object>().Count().ShouldBe(2, "the panel's step and the review are both the critic's");
            return null;
        });
    }

    private static StepRowViewModel Row(StepRecord record) => new(record, Origin);

    private static StepRecord Step(string runId, int index, params ToolCallRecord[] calls) => new()
    {
        RunId = runId,
        TaskId = "task-1",
        StepIndex = index,
        Role = "worker",
        StartedAt = Origin,
        Prompt = [],
        ToolCalls = calls,
        ModelLatencyMs = 100,
        StepLatencyMs = 120,
        Outcome = "continued",
    };

    private static ToolCallRecord Call(string name) =>
        new("c1", name, null, "Succeeded", Parsed: true, 10, null, null);

    private static StepVerificationRecord Ladder(bool passed, string highest, string? failedRung = null) =>
        new(passed, highest, failedRung, 5000, "climb summary", 0m);

    private static StepVerificationRecord Critiqued(bool refuted, int refuting, params ReviewVoteRecord[] votes) =>
        new(!refuted, "Critique", null, 12000, "panel summary", 0m)
        {
            Critique = new StepCritiqueRecord(
                "critic", refuted, false, refuting, votes.Count(v => v.Available), votes.Count(v => !v.Available), votes),
        };

    private static ReviewVoteRecord Vote(bool refuted, double confidence, string reason, string lens) =>
        new(refuted, confidence, reason, Available: true, lens);

    private static ReviewRecord Review(string runId) => new()
    {
        RunId = runId,
        TaskId = "task-1",
        CriticRole = "critic",
        Refuted = true,
        Inconclusive = false,
        Summary = "1/1 critics refuted the change.",
        Votes = [Vote(refuted: true, 0.9, "The null test was deleted.", "evidence")],
        RespondingVotes = 1,
        RecordedAt = Origin.AddMinutes(5),
    };
}
