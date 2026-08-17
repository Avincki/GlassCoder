using GlassCoder.Core.Diagnostics;
using GlassCoder.Core.Verification;
using GlassCoder.TestSupport;

namespace GlassCoder.Core.Tests;

/// <summary>
/// The digest is of a session, not of a run.
/// <para>
/// An operator rarely gets there in one go: they run, read what came out, sharpen the goal and run
/// again. The digest selected the retrospective's own run id out of the session and dropped
/// everything before it, so a review of three runs' work was written from the last of them - and the
/// earlier runs, which are where the decisions that shaped the last one were taken, were invisible
/// to the one reviewer whose entire subject is how the work went. Worse than lossy: a process
/// reviewer that cannot see a run cannot report that the session took three attempts, so the digest
/// answered "how did this go" with the most flattering slice of it.
/// </para>
/// </summary>
public sealed class RetrospectiveSessionTests
{
    [Fact]
    public void Every_run_of_the_session_is_in_the_digest()
    {
        string digest = RetrospectiveTranscript.Render(
            [Step("run-1", 0, "listed the projects"), Step("run-2", 0, "wrote the converter"), Step("run-3", 0, "fixed the binding")],
            Request("run-3"),
            runs: [Run("run-1"), Run("run-2"), Run("run-3")]);

        digest.ShouldContain("Run 1 of 3");
        digest.ShouldContain("Run 2 of 3");
        digest.ShouldContain("Run 3 of 3");

        // And the steps themselves, which is the half that was being thrown away.
        digest.ShouldContain("listed the projects");
        digest.ShouldContain("wrote the converter");
        digest.ShouldContain("fixed the binding");
    }

    [Fact]
    public void The_runs_are_in_the_order_they_happened()
    {
        string digest = RetrospectiveTranscript.Render(
            [Step("run-1", 0, "the first thing"), Step("run-2", 0, "the second thing")],
            Request("run-2"),
            runs: [Run("run-1"), Run("run-2")]);

        digest.IndexOf("the first thing", StringComparison.Ordinal)
            .ShouldBeLessThan(digest.IndexOf("the second thing", StringComparison.Ordinal));
    }

    [Fact]
    public void The_run_the_retrospective_was_taken_on_is_findable_among_the_others()
    {
        // It names the reports, the directory and the work order, so a reader has to be able to
        // pick it out of three runs that otherwise look alike.
        string digest = RetrospectiveTranscript.Render(
            [Step("run-1", 0, "earlier"), Step("run-2", 0, "later")],
            Request("run-2"),
            runs: [Run("run-1"), Run("run-2")]);

        digest.ShouldContain("The retrospective was taken on run `run-2`, which is run 2 of 2.");
        digest.ShouldContain("This is the run the retrospective was taken on.");
    }

    [Fact]
    public void An_earlier_run_is_headed_by_its_own_goal_and_its_own_ending()
    {
        // The whole reason a session is worth reading: run 2's goal is usually a repair of what run
        // 1 did, and the pair is the evidence. One goal for three runs hides that entirely.
        string digest = RetrospectiveTranscript.Render(
            [Step("run-1", 0, "earlier"), Step("run-2", 0, "later")],
            Request("run-2", goal: "The converter does not round. Fix it."),
            runs:
            [
                Run("run-1", goal: "Build a temperature converter.", stopReason: "StepLimit", steps: 25),
                Run("run-2", goal: "The converter does not round. Fix it.", steps: 6),
            ]);

        digest.ShouldContain("Build a temperature converter.");
        digest.ShouldContain("The converter does not round. Fix it.");

        // Each run's own ending, not the last one's applied to all of them. A run that hit the step
        // limit and a run that completed are the same session and different facts.
        digest.ShouldContain("Stopped: StepLimit");
        digest.ShouldContain("Stopped: Completed");
    }

    [Fact]
    public void A_run_that_never_wrote_a_record_still_gets_its_steps_and_says_what_is_missing()
    {
        // A run that crashed or was cancelled has steps and no run record. Dropping it would hide
        // exactly the run a process review most wants, and rendering a blank ending as "unknown"
        // would read as a gap in the digest rather than a fact about the run.
        string digest = RetrospectiveTranscript.Render(
            [Step("run-1", 0, "it got this far"), Step("run-2", 0, "later")],
            Request("run-2"),
            runs: [Run("run-2")]);

        digest.ShouldContain("it got this far");
        digest.ShouldContain("no ending recorded");
    }

    [Fact]
    public void Steps_taken_outside_any_run_are_not_dressed_up_as_one()
    {
        // A commit or a rating made between runs carries the placeholder run id. It belongs in the
        // session - it is something the operator did, and stage 2 is reviewing what the operator
        // had to do - but calling it "Run 2 of 3" would invent a run that never happened.
        string digest = RetrospectiveTranscript.Render(
            [Step("run-1", 0, "the run"), Step("no-run", 0, "the operator committed"), Step("run-2", 0, "the next run")],
            Request("run-2"),
            runs: [Run("run-1"), Run("run-2")]);

        digest.ShouldContain("Runs in this session: 2");
        digest.ShouldContain("## Work outside any run");
        digest.ShouldContain("the operator committed");
        digest.ShouldNotContain("Run 3 of");

        // And it is not asked the questions a run is asked. "No plan was recorded" over a commit
        // reports an absence in something that was never meant to plan.
        digest.Split("No plan was recorded").Length.ShouldBe(3, "one per run, and none for the operator");
    }

    [Fact]
    public void A_run_offered_out_of_an_earlier_session_says_it_has_no_steps_here()
    {
        // A cold start offers the last run in yesterday's log, and this session holds none of it.
        // "No steps were recorded" is the answer to that; an empty file is not.
        string digest = RetrospectiveTranscript.Render([], Request("run-9"));

        digest.ShouldContain("run-9");
        digest.ShouldContain("No steps were recorded for this run in this session.");
    }

    [Fact]
    public void One_run_reads_as_a_session_of_one()
    {
        string digest = RetrospectiveTranscript.Render([Step("run-1", 0, "did the thing")], Request("run-1"));

        digest.ShouldContain("Runs in this session: 1");
        digest.ShouldContain("Run 1 of 1");

        // The instruction to read across runs is for a reader who has more than one.
        digest.ShouldNotContain("a later run picks up what an earlier one left");
    }

    [Fact]
    public void The_cap_is_a_budget_over_the_session_rather_than_over_the_last_run()
    {
        // The failure this guards: with a per-run cap, three runs at 40,000 characters each is a
        // 120,000-character digest and the process stage's window is spent on it. With the cap kept
        // where it was and the runs sharing it, no run may eat the budget the others need - and
        // every drop is still declared, so a reader knows the run was longer than the digest.
        List<StepRecord> steps =
        [
            .. Enumerable.Range(0, 20).Select(i => Step("run-1", i, new string('a', 200))),
            .. Enumerable.Range(0, 20).Select(i => Step("run-2", i, new string('b', 200))),
        ];

        string digest = RetrospectiveTranscript.Render(
            steps, Request("run-2"), maxCharacters: 4000, runs: [Run("run-1"), Run("run-2")]);

        digest.ShouldContain("Run 1 of 2");
        digest.ShouldContain("Run 2 of 2");

        // Both runs kept a head and a tail, and both said what they dropped.
        digest.Split("steps omitted here to fit the digest").Length.ShouldBe(3);
        digest.ShouldContain("aaaa");
        digest.ShouldContain("bbbb");

        // Headers are written whatever happens, so the cap is a target rather than a hard ceiling -
        // but it is a target, and a per-run cap would have put this near 8,000.
        digest.Length.ShouldBeLessThan(6000);
    }

    // ── The bus, which is where the session's runs had nowhere to be kept ──

    [Fact]
    public void The_bus_keeps_every_run_of_the_session()
    {
        // It announced them and remembered nothing, so the retrospective - which subscribes to
        // nothing and reads the bus after the fact - could not name a run it had not been present
        // for. Steps and reviews were already kept; this is the third of the three.
        TranscriptBus bus = new(new RecordingStepLogger());
        bus.LogRun(Run("run-1"));
        bus.LogRun(Run("run-2"));

        bus.Runs.Select(r => r.RunId).ShouldBe(["run-1", "run-2"]);
    }

    [Fact]
    public void Clearing_the_session_drops_its_runs_with_its_steps()
    {
        TranscriptBus bus = new(new RecordingStepLogger());
        bus.LogRun(Run("run-1"));

        bus.Clear();

        bus.Runs.ShouldBeEmpty("Clear starts a new session, and the old session's runs are not in it");
    }

    /// <summary>
    /// The run the retrospective was taken on. Its own header comes from here rather than from the
    /// run record, because this is what the surface was told and what names the reports.
    /// </summary>
    private static RetrospectiveRequest Request(string runId, string? goal = null) => new(runId)
    {
        Goal = goal ?? "Build a desktop app that multiplies two numbers.",
        StopReason = "Completed",
        Steps = 6,
        TotalTokens = 12_000,
    };

    private static StepRecord Step(string runId, int index, string summary) => new()
    {
        RunId = runId,
        TaskId = "desktop",
        StepIndex = index,
        Role = "worker",
        StartedAt = DateTimeOffset.UnixEpoch,
        Prompt = [],
        ToolCalls = [new ToolCallRecord("c", "build", null, "Succeeded", true, 1, null, null, summary)],
        ModelLatencyMs = 1,
        StepLatencyMs = 1,
        Outcome = "continued",
    };

    private static RunRecord Run(
        string runId,
        string? goal = null,
        string stopReason = "Completed",
        int steps = 4) =>
        new()
        {
            RunId = runId,
            TaskId = "desktop",
            Role = "worker",
            Goal = goal,
            StartedAt = DateTimeOffset.UnixEpoch,
            CompletedAt = DateTimeOffset.UnixEpoch.AddMinutes(5),
            StopReason = stopReason,
            Steps = steps,
            InputTokens = 100,
            OutputTokens = 20,
            TotalTokens = 120,
            EstimatedCostUsd = 0m,
            ElapsedMs = 1000,
            ToolCallsTotal = 4,
            ToolCallsValid = 4,
        };
}
