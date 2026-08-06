using GlassCoder.Core.Agent;
using GlassCoder.Core.Diagnostics;
using GlassCoder.Core.Verification;
using GlassCoder.TestSupport;
using GlassCoder.Tools.Changes;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace GlassCoder.Core.Tests;

/// <summary>
/// The post-run review: the same critic as rung 6, asked after the run instead of during it, and
/// answering to you rather than to the agent.
/// <para>
/// The property worth defending is what it does <em>not</em> do. It never starts a run. A retry
/// the reviewer triggered would be a second attempt granted by a model, and pass@1 measured over
/// attempts a critic decided to allow is not pass@1 (CLAUDE.md §11).
/// </para>
/// </summary>
public sealed class RunReviewerTests
{
    [Fact]
    public async Task A_run_that_hit_a_limit_is_not_reviewed()
    {
        // StepLimit already says why the run stopped; paying a critic to paraphrase it is spend
        // for nothing.
        StubCriticPanel critics = new();
        RunReviewer reviewer = Reviewer(critics, out ChangeLog changes);
        Change(changes, "run-1");

        RunReview review = await reviewer.ReviewAsync(Result("run-1", AgentStopReason.StepLimit));

        review.Reviewed.ShouldBeFalse();
        review.Summary.ShouldContain("StepLimit");
        critics.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task A_run_that_changed_nothing_is_not_reviewed()
    {
        StubCriticPanel critics = new();
        RunReviewer reviewer = Reviewer(critics, out _);

        RunReview review = await reviewer.ReviewAsync(Result("run-1", AgentStopReason.Completed));

        review.Reviewed.ShouldBeFalse();
        review.Summary.ShouldContain("changed no files");
        critics.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task A_completed_run_with_changes_is_reviewed_by_the_critic_the_run_asked_for()
    {
        StubCriticPanel critics = new();
        RunReviewer reviewer = Reviewer(critics, out ChangeLog changes);
        Change(changes, "run-1");

        RunReview review = await reviewer.ReviewAsync(
            Result("run-1", AgentStopReason.Completed, criticRole: "critic-remote"));

        review.Reviewed.ShouldBeTrue();
        critics.Calls.ShouldBe(1);
        critics.LastRole.ShouldBe("critic-remote");
        critics.LastChange.ShouldContain("+    return values;", Case.Sensitive);
        critics.LastEvidence.ShouldContain("Completed");
    }

    [Fact]
    public async Task A_refuted_review_offers_a_retry_and_the_retry_carries_the_findings()
    {
        StubCriticPanel critics = new()
        {
            Verdict = new CritiqueResult(
                true,
                [new CritiqueVerdict(true, 0.9, "The input array is sorted in place.")],
                1,
                "1/1 critics refuted the change")
            { RespondingVotes = 1 },
        };

        RunReviewer reviewer = Reviewer(critics, out ChangeLog changes);
        Change(changes, "run-1");

        RunReview review = await reviewer.ReviewAsync(Result("run-1", AgentStopReason.Completed));

        review.Refuted.ShouldBeTrue();
        review.SuggestsRetry.ShouldBeTrue();

        string retry = RunReviewer.ComposeRetryGoal("Sort the values ascending.", review);
        retry.ShouldStartWith("Sort the values ascending.");
        retry.ShouldContain("The input array is sorted in place.");
        retry.ShouldContain("refuted");
    }

    [Fact]
    public async Task An_accepted_review_offers_no_retry_and_leaves_the_goal_alone()
    {
        StubCriticPanel critics = new();
        RunReviewer reviewer = Reviewer(critics, out ChangeLog changes);
        Change(changes, "run-1");

        RunReview review = await reviewer.ReviewAsync(Result("run-1", AgentStopReason.Completed));

        review.Refuted.ShouldBeFalse();
        review.SuggestsRetry.ShouldBeFalse();
        RunReviewer.ComposeRetryGoal("Sort the values ascending.", review)
            .ShouldBe("Sort the values ascending.");
    }

    [Fact]
    public async Task An_inconclusive_panel_is_not_a_review_and_offers_no_retry()
    {
        // "The critics could not be reached" must not read as "the critics had nothing to say".
        StubCriticPanel critics = new()
        {
            Verdict = new CritiqueResult(false, [], 0, "Critique inconclusive: only 0 of 3 critics could be reached")
            { Inconclusive = true },
        };

        RunReviewer reviewer = Reviewer(critics, out ChangeLog changes);
        Change(changes, "run-1");

        RunReview review = await reviewer.ReviewAsync(Result("run-1", AgentStopReason.Completed));

        review.Reviewed.ShouldBeFalse();
        review.Inconclusive.ShouldBeTrue();
        review.SuggestsRetry.ShouldBeFalse();
    }

    [Fact]
    public async Task Changes_from_another_run_are_not_reviewed_as_this_ones()
    {
        StubCriticPanel critics = new();
        RunReviewer reviewer = Reviewer(critics, out ChangeLog changes);
        Change(changes, "run-other");

        RunReview review = await reviewer.ReviewAsync(Result("run-1", AgentStopReason.Completed));

        review.Reviewed.ShouldBeFalse();
        review.Summary.ShouldContain("changed no files");
    }

    [Fact]
    public async Task A_review_is_persisted_to_the_transcript_verbatim()
    {
        // The opinion that shaped a decision must leave a trace (workplan task 37): the full
        // critique - votes, reasons, cost - goes into the run's transcript, not just the screen.
        StubCriticPanel critics = new()
        {
            Verdict = new CritiqueResult(
                true,
                [
                    new CritiqueVerdict(true, 0.9, "The input array is sorted in place."),
                    new CritiqueVerdict(false, 0.6, "Looks right to me."),
                    new CritiqueVerdict(false, 0d, "Critic unavailable: timeout", Available: false),
                ],
                1,
                "1/2 critics refuted the change")
            {
                RespondingVotes = 2,
                UnavailableVotes = 1,
                InputTokens = 1200,
                OutputTokens = 90,
                EstimatedCostUsd = 0.0123m,
            },
        };

        RunReviewer reviewer = Reviewer(critics, out ChangeLog changes, out RecordingStepLogger transcript);
        Change(changes, "run-1");

        await reviewer.ReviewAsync(Result("run-1", AgentStopReason.Completed, criticRole: "critic-remote"));

        ReviewRecord record = transcript.Reviews.ShouldHaveSingleItem();
        record.RunId.ShouldBe("run-1");
        record.TaskId.ShouldBe("task-1");
        record.CriticRole.ShouldBe("critic-remote");
        record.Refuted.ShouldBeTrue();
        record.Votes.Count.ShouldBe(3);
        record.Votes[0].Reason.ShouldBe("The input array is sorted in place.");
        record.Votes[2].Available.ShouldBeFalse("an unreachable critic is recorded as such, never as an acceptance");
        record.RespondingVotes.ShouldBe(2);
        record.UnavailableVotes.ShouldBe(1);
        record.InputTokens.ShouldBe(1200);
        record.EstimatedCostUsd.ShouldBe(0.0123m);
    }

    [Fact]
    public async Task A_run_that_was_not_reviewed_leaves_no_review_record()
    {
        // No critique ran, so there is no opinion to persist - an empty record would claim one.
        StubCriticPanel critics = new();
        RunReviewer reviewer = Reviewer(critics, out ChangeLog changes, out RecordingStepLogger transcript);
        Change(changes, "run-1");

        await reviewer.ReviewAsync(Result("run-1", AgentStopReason.StepLimit));

        transcript.Reviews.ShouldBeEmpty();
    }

    [Fact]
    public async Task An_inconclusive_panel_is_persisted_as_inconclusive()
    {
        // "Could not be reached" is a fact worth keeping too - a transcript with a silent gap
        // where the review should be reads as "nobody asked".
        StubCriticPanel critics = new()
        {
            Verdict = new CritiqueResult(false, [], 0, "Critique inconclusive: only 0 of 3 critics could be reached")
            { Inconclusive = true },
        };

        RunReviewer reviewer = Reviewer(critics, out ChangeLog changes, out RecordingStepLogger transcript);
        Change(changes, "run-1");

        await reviewer.ReviewAsync(Result("run-1", AgentStopReason.Completed));

        ReviewRecord record = transcript.Reviews.ShouldHaveSingleItem();
        record.Inconclusive.ShouldBeTrue();
        record.Refuted.ShouldBeFalse();
    }

    [Fact]
    public async Task The_retry_is_composed_from_the_findings_that_were_persisted()
    {
        // Acceptance for workplan task 37: the retry references the persisted findings - what
        // went into the transcript is what the next attempt is built from, not a paraphrase.
        StubCriticPanel critics = new()
        {
            Verdict = new CritiqueResult(
                true,
                [new CritiqueVerdict(true, 0.9, "The input array is sorted in place.")],
                1,
                "1/1 critics refuted the change")
            { RespondingVotes = 1 },
        };

        RunReviewer reviewer = Reviewer(critics, out ChangeLog changes, out RecordingStepLogger transcript);
        Change(changes, "run-1");

        RunReview review = await reviewer.ReviewAsync(Result("run-1", AgentStopReason.Completed));

        string persisted = transcript.Reviews.ShouldHaveSingleItem().Votes.ShouldHaveSingleItem().Reason;
        RunReviewer.ComposeRetryGoal("Sort the values ascending.", review).ShouldContain(persisted);
    }

    // ── What the panel is shown (run ff74b2d4) ──
    //
    // That run wrote a test with a wrong expected value, watched it fail, fixed it, and finished
    // green - and the review refuted it 3/3 at full confidence, quoting the "ran 0 tests" line
    // from before the tests existed and the failure the run had already fixed. The panel judged
    // exactly what it was shown; what it was shown was the journey.

    [Fact]
    public async Task The_evidence_is_the_final_climb_not_the_journey()
    {
        StubCriticPanel critics = new();
        RunReviewer reviewer = Reviewer(critics, out ChangeLog changes);

        RunContext.Set(new RunContext("run-1", "task-1"));
        CodeChange early = changes.Propose("src/Sorter.cs", "create_file", string.Empty, "public class Sorter { }\n");
        changes.Update(early.Id, ChangeStatus.Applied,
            verificationSummary: "The test run exited cleanly but ran 0 tests - nothing was verified.");
        CodeChange late = changes.Propose("tests/SorterTests.cs", "create_file", string.Empty, "public class SorterTests { }\n");
        changes.Update(late.Id, ChangeStatus.Applied, verificationSummary: "3 tests passed.");

        await reviewer.ReviewAsync(Result("run-1", AgentStopReason.Completed));

        critics.LastEvidence.ShouldNotBeNull();
        critics.LastEvidence.ShouldContain("3 tests passed.");
        critics.LastEvidence.ShouldNotContain("ran 0 tests",
            customMessage: "a summary from before the tests existed reads as a contradiction of the finished state");
        critics.LastEvidence.ShouldContain("Judge the state the run finished in");
    }

    [Fact]
    public async Task The_change_under_review_is_the_net_diff_not_the_journey()
    {
        StubCriticPanel critics = new();
        RunReviewer reviewer = Reviewer(critics, out ChangeLog changes);

        RunContext.Set(new RunContext("run-1", "task-1"));
        CodeChange wrong = changes.Propose("tests/SorterTests.cs", "create_file",
            string.Empty, "int expected = old_expectation;\n");
        changes.Update(wrong.Id, ChangeStatus.Applied);
        CodeChange fixedUp = changes.Propose("tests/SorterTests.cs", "edit_file",
            "int expected = old_expectation;\n", "int expected = fixed_expectation;\n");
        changes.Update(fixedUp.Id, ChangeStatus.Applied, verificationSummary: "3 tests passed.");

        await reviewer.ReviewAsync(Result("run-1", AgentStopReason.Completed));

        critics.LastChange.ShouldNotBeNull();
        critics.LastChange.ShouldContain("fixed_expectation");
        critics.LastChange.ShouldNotContain("old_expectation",
            customMessage: "a mistake the run already fixed is not part of the claim under judgment");
    }

    [Fact]
    public async Task A_file_edited_and_put_back_reviews_as_no_net_change()
    {
        StubCriticPanel critics = new();
        RunReviewer reviewer = Reviewer(critics, out ChangeLog changes);

        RunContext.Set(new RunContext("run-1", "task-1"));
        CodeChange there = changes.Propose("src/Sorter.cs", "edit_file", "int x = 1;\n", "int x = 2;\n");
        changes.Update(there.Id, ChangeStatus.Applied);
        CodeChange back = changes.Propose("src/Sorter.cs", "edit_file", "int x = 2;\n", "int x = 1;\n");
        changes.Update(back.Id, ChangeStatus.Applied, verificationSummary: "3 tests passed.");

        await reviewer.ReviewAsync(Result("run-1", AgentStopReason.Completed));

        critics.LastChange.ShouldNotBeNull();
        critics.LastChange.ShouldContain("no net change");
        critics.LastChange.ShouldNotContain("int x = 2;", customMessage: "an empty diff dressed as an edit invites a verdict on nothing");
    }

    private static RunReviewer Reviewer(StubCriticPanel critics, out ChangeLog changes)
    {
        changes = new ChangeLog();
        return new RunReviewer(critics, changes, Options.Create(new RunReviewOptions()));
    }

    private static RunReviewer Reviewer(
        StubCriticPanel critics,
        out ChangeLog changes,
        out RecordingStepLogger transcript)
    {
        changes = new ChangeLog();
        transcript = new RecordingStepLogger();
        return new RunReviewer(critics, changes, Options.Create(new RunReviewOptions()), transcript: transcript);
    }

    private static void Change(ChangeLog changes, string runId)
    {
        RunContext.Set(new RunContext(runId, "task-1"));
        CodeChange change = changes.Propose(
            "src/DoubleSorter.cs",
            "edit_file",
            "public static double[] Ascending(double[] values)\n{\n}\n",
            "public static double[] Ascending(double[] values)\n{\n    Array.Sort(values);\n    return values;\n}\n");

        changes.Update(change.Id, ChangeStatus.Applied, verificationSummary: "3 tests passed.");
    }

    private static AgentRunResult Result(
        string runId,
        AgentStopReason stopReason,
        string? criticRole = null) =>
        new()
        {
            RunId = runId,
            TaskId = "task-1",
            Goal = "Sort the values ascending.",
            CriticRole = criticRole,
            StopReason = stopReason,
            Steps = 4,
            FinalText = "Done - the sorter now returns ascending values.",
            Elapsed = TimeSpan.FromSeconds(12),
            ToolCallsTotal = 6,
            ToolCallsValid = 6,
            Messages = [new ChatMessage(ChatRole.Assistant, "done")],
        };

    /// <summary>A panel that records what it was asked and returns whatever the test wants.</summary>
    private sealed class StubCriticPanel : ICriticPanel
    {
        public int Calls { get; private set; }

        public string? LastRole { get; private set; }

        public string? LastChange { get; private set; }

        public string? LastEvidence { get; private set; }

        public CritiqueResult Verdict { get; set; } =
            new(false, [new CritiqueVerdict(false, 0.8, "Looks right.")], 0, "1/1 critics accepted the change.")
            { RespondingVotes = 1 };

        public bool Enabled => true;

        public bool CanCritique(string? role) => true;

        public string ResolveRole(string? role) => role ?? "critic";

        public Task<CritiqueResult> CritiqueAsync(
            string goal,
            string change,
            string evidence,
            string? role = null,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            LastRole = role;
            LastChange = change;
            LastEvidence = evidence;
            return Task.FromResult(Verdict with { Role = ResolveRole(role) });
        }
    }
}
