using GlassCoder.Core.Diagnostics;
using GlassCoder.Core.Verification;

namespace GlassCoder.Core.Tests;

/// <summary>
/// The run digest a retrospective reads, which is the instrument the harness learns through.
/// <para>
/// Run <c>46231701</c>'s process reviewer reported the digest as self-contradictory - "the header
/// says 2/3 accepted, the line below says 1/3" - and it was right about the rendering and wrong
/// about the run. A lossy instrument does not merely lose information; it manufactures findings.
/// </para>
/// </summary>
public sealed class RetrospectiveTranscriptTests
{
    [Fact]
    public void A_split_panel_renders_a_ratio_that_says_which_side_it_counts()
    {
        string digest = Render(Critique(refuted: false, refutingVotes: 1, respondingVotes: 3));

        digest.ShouldContain("accepted (1 of 3 refuted)");

        // The exact string that misled a reviewer: a verdict word against a bare losing-side tally.
        digest.ShouldNotContain("accepted 1/3");
    }

    [Fact]
    public void Every_vote_carries_its_own_verdict()
    {
        string digest = Render(Critique(refuted: false, refutingVotes: 1, respondingVotes: 3));

        // Lens and reasoning were already there. Which of them dissented was not, so the one
        // paragraph a reader is looking for was indistinguishable from the two agreeing with it.
        digest.ShouldContain("[correctness: accepted]");
        digest.ShouldContain("[evidence: refuted]");
        digest.ShouldContain("nothing ran the application");
    }

    [Fact]
    public void A_critic_that_never_answered_is_not_rendered_as_agreeing()
    {
        string digest = Render(new StepCritiqueRecord(
            "critic",
            Refuted: false,
            Inconclusive: false,
            RefutingVotes: 0,
            RespondingVotes: 1,
            UnavailableVotes: 1,
            Votes:
            [
                new ReviewVoteRecord(false, 0.9, "Reads correctly.", Available: true, "correctness"),
                new ReviewVoteRecord(false, 0, "", Available: false, "regression"),
            ]));

        digest.ShouldContain("[regression: no answer]");
        digest.ShouldNotContain("[regression: accepted]");
    }

    [Fact]
    public void A_climb_that_verified_nothing_is_not_rendered_as_a_clean_pass()
    {
        // Run ae72c5ad, exactly: a UnitTests rung that ran and found no test, recorded honestly
        // on the step and then retold here as "verification: passed at UnitTests". Both reviewers
        // of that run read this line and reported the harness as passing a test gate over a
        // workspace with no tests in it - a finding manufactured by the instrument.
        string digest = Render(unverified: true);

        digest.ShouldContain("verification: verified nothing (0 tests) at UnitTests");
        digest.ShouldNotContain("verification: passed", Case.Sensitive);
    }

    [Fact]
    public void A_pass_with_a_notice_carries_it()
    {
        Render(unverified: false, noticed: true).ShouldContain("passed (with a notice)");
    }

    // ── The plan ──
    //
    // Every digest said "Plan updated: 3/5 complete" and never once what the five were, so three
    // retrospectives running reasoned about planning behaviour from a ratio. The plan is the only
    // thing in the transcript the agent wrote about the whole job rather than the step in front of
    // it, and it was the one thing the digest did not carry.

    /// <summary>The payload an <c>update_todos</c> call really returns, from this repository's logs.</summary>
    private const string PlanPayload =
        """
        {"ok":true,"tool":"update_todos","summary":"Plan updated: 1/3 complete.","data":{"items":[
        {"id":"create-solution","title":"Create solution structure","status":"Completed"},
        {"id":"create-wpf-app","title":"Create WPF application with UI","status":"InProgress"},
        {"id":"create-tests","title":"Create unit tests","status":"Pending"}],"pending":2,"completed":1}}
        """;

    [Fact]
    public void The_digest_says_what_the_plan_actually_was()
    {
        string digest = RenderPlan(PlanPayload);

        digest.ShouldContain("## The plan it made");
        digest.ShouldContain("[done] Create solution structure");
        digest.ShouldContain("[in progress] Create WPF application with UI");
        digest.ShouldContain("[to do] Create unit tests");
    }

    [Fact]
    public void The_digest_says_when_the_plan_was_written_and_how_often_it_moved()
    {
        // A plan authored at step 0, before any tool has reported anything, and never touched
        // again is a different object from one that absorbed what the run learned - and that is
        // the distinction three reviewers in a row have had to guess at.
        string digest = RenderPlan(PlanPayload);

        digest.ShouldContain("Written at step 0");
        digest.ShouldContain("last updated at step 4");
        digest.ShouldContain("2 updates");
        digest.ShouldContain("1 of 3 complete");
    }

    [Fact]
    public void A_run_that_never_planned_says_so()
    {
        string digest = Render(unverified: false);

        digest.ShouldContain("No plan was recorded");
    }

    [Fact]
    public void Every_update_shows_the_plan_as_it_then_stood()
    {
        // The plan at the top is where the run ended up. A reader of step 4 wants what the run
        // thought the job was at step 4 - and which step moved it.
        string digest = RenderPlan(PlanPayload);

        digest.ShouldContain("- plan (first written)");
        digest.ShouldContain("- plan (1 item moved, 2 added), 1 of 3 complete:");
        digest.ShouldContain("    - [in progress] Create WPF application with UI");
    }

    [Fact]
    public void A_plan_that_moved_nothing_is_not_reprinted()
    {
        // A re-announcement is a fact about the run, not another copy of the list. This repository
        // has spent whole steps on updates that moved nothing.
        string digest = RetrospectiveTranscript.Render(
            [Planned(0, PlanPayload), Planned(3, PlanPayload)],
            new RetrospectiveRequest("run-1") { StopReason = "Completed", Steps = 2 });

        digest.ShouldContain("- plan: unchanged, still 1 of 3 complete");

        // Once in the step block, once at the top - and not a third time for the repeat.
        digest.Split("[in progress] Create WPF application with UI").Length.ShouldBe(3);
    }

    [Fact]
    public void An_update_whose_items_cannot_be_read_says_that_rather_than_nothing()
    {
        string digest = RetrospectiveTranscript.Render(
            [Planned(0, "not json at all")],
            new RetrospectiveRequest("run-1") { StopReason = "Completed", Steps = 1 });

        digest.ShouldContain("could not be read back");
    }

    /// <summary>The plan as it was first written: one item, nothing done.</summary>
    private const string FirstPlanPayload =
        """
        {"ok":true,"tool":"update_todos","summary":"Plan updated: 0/1 complete.","data":{"items":[
        {"id":"create-solution","title":"Create solution structure","status":"Pending"}],"pending":1,"completed":0}}
        """;

    /// <summary>Two <c>update_todos</c> steps, the second carrying the plan as it ended.</summary>
    private static string RenderPlan(string payload) =>
        RetrospectiveTranscript.Render(
            [Planned(0, FirstPlanPayload), Planned(4, payload)],
            new RetrospectiveRequest("run-1") { StopReason = "Completed", Steps = 2 });

    private static StepRecord Planned(int step, string payload) =>
        new()
        {
            RunId = "run-1",
            TaskId = "desktop",
            StepIndex = step,
            Role = "worker",
            StartedAt = DateTimeOffset.UnixEpoch,
            Prompt = [],
            ToolCalls =
            [
                new ToolCallRecord(
                    $"call-{step}", "update_todos", null, "Succeeded", true, 1,
                    Result: payload, Error: null, Summary: "Plan updated."),
            ],
            ModelLatencyMs = 1,
            StepLatencyMs = 1,
            Outcome = "continued",
        };

    private static StepCritiqueRecord Critique(bool refuted, int refutingVotes, int respondingVotes) =>
        new("critic",
            refuted,
            Inconclusive: false,
            refutingVotes,
            respondingVotes,
            UnavailableVotes: 0,
            Votes:
            [
                new ReviewVoteRecord(false, 0.8, "The multiplication is right.", Available: true, "correctness"),
                new ReviewVoteRecord(false, 0.7, "No regression in the suite.", Available: true, "regression"),
                new ReviewVoteRecord(true, 0.9, "Compile and tests only - nothing ran the application.", Available: true, "evidence"),
            ]);

    private static string Render(StepCritiqueRecord critique) => Render(critique, false, false);

    private static string Render(bool unverified, bool noticed = false) => Render(null, unverified, noticed);

    private static string Render(StepCritiqueRecord? critique, bool unverified, bool noticed) =>
        RetrospectiveTranscript.Render(
            [
                new StepRecord
                {
                    RunId = "run-1",
                    TaskId = "desktop",
                    StepIndex = 0,
                    Role = "worker",
                    StartedAt = DateTimeOffset.UnixEpoch,
                    Prompt = [],
                    ToolCalls = [],
                    ModelLatencyMs = 1,
                    StepLatencyMs = 1,
                    Outcome = "continued",
                    Verification = new StepVerificationRecord(
                        Passed: true,
                        HighestRungReached: "UnitTests",
                        FailedRung: null,
                        DurationMs: 10,
                        Summary: "passed (4 tests)",
                        CritiqueCostUsd: 0m)
                    {
                        Critique = critique,
                        Unverified = unverified,
                        Noticed = noticed,
                    },
                },
            ],
            new RetrospectiveRequest("run-1") { StopReason = "Completed", Steps = 1 });
}
