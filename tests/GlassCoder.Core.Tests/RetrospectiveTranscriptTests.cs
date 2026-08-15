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

        digest.ShouldContain("verification: passed (0 tests) at UnitTests");
        digest.ShouldNotContain("verification: passed at UnitTests");
    }

    [Fact]
    public void A_pass_with_a_notice_carries_it()
    {
        Render(unverified: false, noticed: true).ShouldContain("passed (with a notice)");
    }

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
