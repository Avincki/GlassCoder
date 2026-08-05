using System.Windows.Threading;
using GlassCoder.Core.Diagnostics;
using GlassCoder.TestSupport;
using GlassCoder.Wpf.ViewModels;

namespace GlassCoder.Wpf.Tests;

/// <summary>
/// The transcript's elapsed column: where each step sits on the run's clock, read at the step's
/// end - as opposed to the latency column beside it, which is how long that one step took.
/// <para>
/// Every case here seeds the bus before the view model is built, so the rows are produced by the
/// constructor rather than by the <c>StepRecorded</c> handler. That handler posts through
/// <c>Dispatcher.BeginInvoke</c>, and a test that relied on it would have to pump the queue to
/// see a row - timing this assertion has nothing to do with.
/// </para>
/// </summary>
public sealed class TranscriptElapsedTests
{
    private static readonly DateTimeOffset Origin = new(2026, 8, 3, 9, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// The first step's start is the origin, and each row reads the clock at its own step's end.
    /// A row appears when its step completes, so a clock stopped at the step's start would lag
    /// the run by exactly the action it just watched - the first row of a run that has been
    /// working for thirteen seconds must say so, not say 0:00.
    /// </summary>
    [Fact]
    public void Elapsed_reads_the_clock_at_the_end_of_each_step()
    {
        IReadOnlyList<string> elapsed = ElapsedFor(
            Step("run-1", 0, Origin, stepLatencyMs: 13_000),
            Step("run-1", 1, Origin.AddSeconds(15), stepLatencyMs: 8_000),
            Step("run-1", 2, Origin.AddSeconds(90), stepLatencyMs: 500));

        elapsed.ShouldBe(["0:13", "0:23", "1:30"]);
    }

    /// <summary>
    /// The bus is session-scoped, so a second run lands in the same list as the first. Its clock
    /// has to restart - otherwise every step of run two reads as hours in, measured from a run
    /// that already finished.
    /// </summary>
    [Fact]
    public void A_second_run_in_the_same_session_restarts_the_clock()
    {
        IReadOnlyList<string> elapsed = ElapsedFor(
            Step("run-1", 0, Origin),
            Step("run-1", 1, Origin.AddSeconds(30)),
            Step("run-2", 0, Origin.AddMinutes(10)),
            Step("run-2", 1, Origin.AddMinutes(10).AddSeconds(12)));

        elapsed.ShouldBe(["0:00", "0:30", "0:00", "0:12"]);
    }

    /// <summary>Past an hour the minute count would be ambiguous, so the hour is shown.</summary>
    [Fact]
    public void Elapsed_grows_an_hour_field_once_the_run_passes_one()
    {
        IReadOnlyList<string> elapsed = ElapsedFor(
            Step("run-1", 0, Origin),
            Step("run-1", 1, Origin.AddMinutes(59).AddSeconds(59)),
            Step("run-1", 2, Origin.AddHours(1).AddMinutes(4).AddSeconds(12)));

        elapsed.ShouldBe(["0:00", "59:59", "1:04:12"]);
    }

    /// <summary>
    /// Steps carry the wall clock they started on, and a wall clock can go backwards - an NTP
    /// correction mid-run is enough. Reading "-0:03" into a run would be worse than reading zero.
    /// </summary>
    [Fact]
    public void A_clock_that_moved_backwards_reads_as_zero_rather_than_negative()
    {
        IReadOnlyList<string> elapsed = ElapsedFor(
            Step("run-1", 0, Origin),
            Step("run-1", 1, Origin.AddSeconds(-3)));

        elapsed.ShouldBe(["0:00", "0:00"]);
    }

    /// <summary>
    /// Publishes <paramref name="steps"/> through a real bus, builds the transcript over it, and
    /// returns the elapsed cell of every row in order.
    /// </summary>
    private static IReadOnlyList<string> ElapsedFor(params StepRecord[] steps) =>
        UiThread.Run<IReadOnlyList<string>>(dispatcher =>
        {
            TranscriptBus bus = new(new RecordingStepLogger());
            foreach (StepRecord step in steps)
            {
                bus.LogStep(step);
            }

            TranscriptViewModel transcript = new(bus, dispatcher);
            return [.. transcript.Steps.Select(row => row.Elapsed)];
        });

    private static StepRecord Step(
        string runId, int index, DateTimeOffset startedAt, double stepLatencyMs = 120) => new()
    {
        RunId = runId,
        TaskId = "task-1",
        StepIndex = index,
        Role = "worker",
        StartedAt = startedAt,
        Prompt = [],
        ToolCalls = [],
        ModelLatencyMs = 100,
        StepLatencyMs = stepLatencyMs,
        Outcome = "continued",
    };
}
