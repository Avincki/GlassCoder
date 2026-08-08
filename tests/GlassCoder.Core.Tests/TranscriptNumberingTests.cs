using GlassCoder.Core.Diagnostics;
using GlassCoder.TestSupport;

namespace GlassCoder.Core.Tests;

/// <summary>
/// What number a step recorded outside the loop should carry.
/// <para>
/// A human action - a manual commit, a push, an operator's rating - is a step the loop never
/// numbered, and its caller cannot know what the run reached. Both callers counted from zero of
/// their own until this existed, so a rating given after step 25 was logged as step 0.
/// </para>
/// </summary>
public sealed class TranscriptNumberingTests
{
    [Fact]
    public void A_manual_step_is_numbered_one_past_the_run()
    {
        TranscriptBus bus = new(new RecordingStepLogger());
        for (int index = 0; index <= 18; index++)
        {
            bus.LogStep(Step("run-1", index));
        }

        bus.NextStepIndex("run-1").ShouldBe(19);
    }

    /// <summary>
    /// The collision this test exists for. The transcript numbers a post-run review "one past
    /// the run's last step", and a review is not a step - so without counting them, run
    /// eea444a6 gave its review row 19 and then handed 19 to the operator's rating as well.
    /// </summary>
    [Fact]
    public void A_review_occupies_a_number_even_though_it_is_not_a_step()
    {
        TranscriptBus bus = new(new RecordingStepLogger());
        for (int index = 0; index <= 18; index++)
        {
            bus.LogStep(Step("run-1", index));
        }

        bus.NextStepIndex("run-1").ShouldBe(19);

        bus.LogReview(Review("run-1"));

        bus.NextStepIndex("run-1").ShouldBe(20, "the review row already claims 19");
    }

    /// <summary>Another run's steps and reviews must not push this one's numbering along.</summary>
    [Fact]
    public void Numbering_is_per_run()
    {
        TranscriptBus bus = new(new RecordingStepLogger());
        bus.LogStep(Step("run-1", 4));
        bus.LogStep(Step("run-2", 99));
        bus.LogReview(Review("run-2"));

        bus.NextStepIndex("run-1").ShouldBe(5);
        bus.NextStepIndex("run-2").ShouldBe(101);
    }

    /// <summary>A run nobody has logged starts where the loop would have started.</summary>
    [Fact]
    public void A_run_with_no_steps_starts_at_zero()
    {
        new TranscriptBus(new RecordingStepLogger()).NextStepIndex("no-run").ShouldBe(0);
    }

    /// <summary>
    /// Two manual steps in a row do not collide: the first is itself a step, so it raises the
    /// high-water mark the second reads.
    /// </summary>
    [Fact]
    public void A_second_manual_step_follows_the_first()
    {
        TranscriptBus bus = new(new RecordingStepLogger());
        bus.LogStep(Step("run-1", 7));

        int first = bus.NextStepIndex("run-1");
        bus.LogStep(Step("run-1", first));

        bus.NextStepIndex("run-1").ShouldBe(first + 1);
    }

    private static StepRecord Step(string runId, int index) => new()
    {
        RunId = runId,
        TaskId = "task",
        StepIndex = index,
        Role = "worker",
        StartedAt = DateTimeOffset.UnixEpoch,
        Prompt = [],
        ToolCalls = [],
        ModelLatencyMs = 1,
        StepLatencyMs = 1,
        Outcome = "ok",
    };

    private static ReviewRecord Review(string runId) => new()
    {
        RunId = runId,
        TaskId = "task",
        CriticRole = "critic",
        RecordedAt = DateTimeOffset.UnixEpoch,
        Refuted = false,
        Inconclusive = false,
        Summary = "accepted",
        Votes = [],
    };
}
