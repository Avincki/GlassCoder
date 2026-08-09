using System.Windows;
using System.Windows.Threading;
using GlassCoder.Core.Verification;
using GlassCoder.Wpf.ViewModels;
using GlassCoder.Wpf.Views;

namespace GlassCoder.Wpf.Tests;

/// <summary>
/// The retrospective surface (workplan task 67).
/// <para>
/// Three things here are worth a test and none of them is the review. That the button explains
/// itself when it cannot be pressed - a greyed control with no reason is a bug report waiting to
/// happen, and "no CLI", "no run yet" and "a run is in flight" have three different fixes. That
/// the proposals window opens when a retrospective finishes and <em>not</em> when one is read
/// back off disk, because a window arriving at startup answers a question nobody asked. And that
/// the feed survives arriving from a background thread, which is the defect task 65 paid for.
/// </para>
/// </summary>
public sealed class RetrospectiveSurfaceTests
{
    [Fact]
    public void Before_any_run_the_button_says_why_it_cannot_be_pressed()
    {
        (bool CanRun, string Tooltip) state = UiThread.Run(dispatcher =>
        {
            RetrospectiveViewModel model = Model(dispatcher, new StubReviewer());
            Settle(dispatcher, model);
            return (model.CanRun, model.Tooltip);
        });

        state.CanRun.ShouldBeFalse();
        state.Tooltip.ShouldContain("Finish a run first");
    }

    [Fact]
    public void A_missing_cli_greys_the_button_with_the_reason_the_probe_gave()
    {
        (bool CanRun, string Tooltip) state = UiThread.Run(dispatcher =>
        {
            StubReviewer reviewer = new()
            {
                Availability = ReviewerAvailability.Unavailable("Could not launch 'claude'. Install Claude Code."),
            };

            RetrospectiveViewModel model = Model(dispatcher, reviewer);
            model.OfferRun(Run());
            Settle(dispatcher, model);
            return (model.CanRun, model.Tooltip);
        });

        state.CanRun.ShouldBeFalse();
        state.Tooltip.ShouldContain("Install Claude Code");
    }

    [Fact]
    public void It_stands_down_while_the_agent_is_running()
    {
        // The run this surface holds is the last finished one, not the one in flight. Judging a
        // tree the agent is still writing would review a moving target.
        (bool CanRun, string Tooltip) state = UiThread.Run(dispatcher =>
        {
            RetrospectiveViewModel model = Model(dispatcher, new StubReviewer());
            model.OfferRun(Run());
            Settle(dispatcher, model);

            model.IsAgentRunning = true;
            return (model.CanRun, model.Tooltip);
        });

        state.CanRun.ShouldBeFalse();
        state.Tooltip.ShouldContain("run is in flight");
    }

    [Fact]
    public void A_finished_retrospective_opens_the_proposals_window_once()
    {
        (int Opened, int Stages, int Recommendations, int Ticked) after = UiThread.Run(dispatcher =>
        {
            StubReviewer reviewer = new() { Result = Result() };
            RecordingDialog dialog = new();
            RetrospectiveViewModel model = Model(dispatcher, reviewer, dialog);

            model.OfferRun(Run());
            Settle(dispatcher, model);

            model.RunCommand.Execute(null);
            UiThread.Pump(dispatcher, () => !model.IsRunning);

            return (dialog.Opened, model.Stages.Count, model.Recommendations.Count,
                model.Recommendations.Count(r => r.IsAccepted));
        });

        after.Opened.ShouldBe(1);
        after.Stages.ShouldBe(3);
        after.Recommendations.ShouldBe(2);

        // Defects start ticked and nothing else does, as the file viewer's actions do: "yes to
        // the bugs, let me read the rest" is the common press.
        after.Ticked.ShouldBe(1);
    }

    [Fact]
    public void Reading_one_back_off_disk_shows_it_without_opening_a_window()
    {
        // Rehydration happens when a run is offered, which on a restart is before the operator
        // has pressed anything. A window arriving then is not an answer to a question they asked.
        (int Opened, int Stages, string Status) after = UiThread.Run(dispatcher =>
        {
            StubReviewer reviewer = new() { OnDisk = Result() };
            RecordingDialog dialog = new();
            RetrospectiveViewModel model = Model(dispatcher, reviewer, dialog);

            model.OfferRun(Run());
            Settle(dispatcher, model);

            return (dialog.Opened, model.Stages.Count, model.Status);
        });

        after.Opened.ShouldBe(0);
        after.Stages.ShouldBe(3, "the reports are there to read");
        after.Status.ShouldContain("on disk");
    }

    [Fact]
    public void The_live_feed_fills_from_the_stage_that_is_running()
    {
        IReadOnlyList<string> feed = UiThread.Run(dispatcher =>
        {
            // The view model marshals its continuations onto whatever context started them,
            // which on the real UI thread is the dispatcher's.
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));

            StubReviewer reviewer = new()
            {
                Result = Result(),
                Narration =
                [
                    new RetrospectiveActivity(RetrospectiveStageKind.Code, ClaudeCliEventKind.Started, "Session started"),
                    new RetrospectiveActivity(RetrospectiveStageKind.Code, ClaudeCliEventKind.ToolCall, "Read src/A.cs"),
                    new RetrospectiveActivity(RetrospectiveStageKind.Harness, ClaudeCliEventKind.Note, "no stream-json"),
                ],
            };

            RetrospectiveViewModel model = Model(dispatcher, reviewer);
            model.OfferRun(Run());
            Settle(dispatcher, model);

            model.RunCommand.Execute(null);

            // Waited for by the feed rather than by IsRunning. Progress<T> posts to the
            // dispatcher, and a stub that answers instantly finishes the retrospective before
            // those posts are drained - which is exactly what the real thing does not do, since
            // a stage takes minutes. Pumping on the feed is what this test is about anyway.
            UiThread.Pump(dispatcher, () => model.Activity.Count >= 3).ShouldBeTrue(
                "the narration should reach the surface");

            return (IReadOnlyList<string>)[.. model.Activity];
        });

        feed.Count.ShouldBe(3);
        feed[1].ShouldBe("· Read src/A.cs");
        feed[2].ShouldBe("! no stream-json");
    }

    [Fact]
    public void A_stage_reaches_the_surface_as_it_finishes_rather_than_at_the_end()
    {
        // The point of the whole streamed path: three sessions take minutes each, and until this
        // worked the empty state stayed on screen with two finished reports sitting behind it.
        (bool HasStages, bool HasResult, int Stages, string First) after = UiThread.Run(dispatcher =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));

            TaskCompletionSource gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
            StubReviewer reviewer = new()
            {
                Result = Result(),
                Gate = gate,
                Narration =
                [
                    new RetrospectiveActivity(RetrospectiveStageKind.Code, ClaudeCliEventKind.ToolCall, "Read src/A.cs"),
                    Finished(RetrospectiveStageKind.Code, "The multiply logic is untested."),
                ],
            };

            RetrospectiveViewModel model = Model(dispatcher, reviewer);
            model.OfferRun(Run());
            Settle(dispatcher, model);

            model.RunCommand.Execute(null);

            // Held mid-retrospective, which is the state being tested: one stage answered, the
            // other two still running, no result at all.
            UiThread.Pump(dispatcher, () => model.Stages.Count > 0).ShouldBeTrue(
                "the finished stage should reach the surface");

            (bool, bool, int, string) observed =
                (model.HasStages, model.HasResult, model.Stages.Count, model.Activity[^1]);

            gate.SetResult();
            UiThread.Pump(dispatcher, () => !model.IsRunning);
            return observed;
        });

        after.HasStages.ShouldBeTrue("the empty state must stand down for the first report");
        after.HasResult.ShouldBeFalse("nothing has finished yet - that is the whole point");
        after.Stages.ShouldBe(1, "only the stage that has actually finished");
        after.First.ShouldStartWith("✓ ", customMessage: "a finished stage is not a warning");
    }

    [Fact]
    public void The_newest_stage_opens_and_the_one_before_it_folds()
    {
        (bool CodeOpen, bool ProcessOpen) after = UiThread.Run(dispatcher =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));

            TaskCompletionSource gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
            StubReviewer reviewer = new()
            {
                Result = Result(),
                Gate = gate,
                Narration =
                [
                    Finished(RetrospectiveStageKind.Code, "The multiply logic is untested."),
                    Finished(RetrospectiveStageKind.Process, "Steps 18 to 39 were spent on layout tests."),
                ],
            };

            RetrospectiveViewModel model = Model(dispatcher, reviewer);
            model.OfferRun(Run());
            Settle(dispatcher, model);

            model.RunCommand.Execute(null);
            UiThread.Pump(dispatcher, () => model.Stages.Count >= 2).ShouldBeTrue();

            (bool, bool) observed = (model.Stages[0].IsExpanded, model.Stages[1].IsExpanded);

            gate.SetResult();
            UiThread.Pump(dispatcher, () => !model.IsRunning);
            return observed;
        });

        after.ProcessOpen.ShouldBeTrue("the one that just landed is what the operator is waiting for");
        after.CodeOpen.ShouldBeFalse("two open reports and a live log is more than the pane holds");
    }

    [Fact]
    public void Finishing_keeps_open_whatever_the_operator_was_reading()
    {
        // Apply replaces the streamed stages with the result's own - the harness stage's proposals
        // are ranked and capped by then, so they are not the same objects. Collapsing a report
        // somebody is mid-way through, at the moment the thing they waited for arrives, is its own
        // small insult.
        bool codeStillOpen = UiThread.Run(dispatcher =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));

            TaskCompletionSource gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
            StubReviewer reviewer = new()
            {
                Result = Result(),
                Gate = gate,
                Narration = [Finished(RetrospectiveStageKind.Code, "The multiply logic is untested.")],
            };

            RetrospectiveViewModel model = Model(dispatcher, reviewer);
            model.OfferRun(Run());
            Settle(dispatcher, model);

            model.RunCommand.Execute(null);
            UiThread.Pump(dispatcher, () => model.Stages.Count > 0).ShouldBeTrue();

            // What the operator is reading while stage two runs.
            model.Stages[0].IsExpanded = true;

            gate.SetResult();
            UiThread.Pump(dispatcher, () => !model.IsRunning && model.HasResult).ShouldBeTrue();

            model.Stages.Count.ShouldBe(3, "the finished result replaces the streamed one");
            return model.Stages.Single(s => s.Stage.Kind == RetrospectiveStageKind.Code).IsExpanded;
        });

        codeStillOpen.ShouldBeTrue();
    }

    [Fact]
    public void The_work_order_waits_for_a_tick_and_for_somewhere_to_put_it()
    {
        (bool Before, bool AfterUnticking, string Tooltip) state = UiThread.Run(dispatcher =>
        {
            StubReviewer reviewer = new() { Result = Result() };
            RetrospectiveViewModel model = Model(dispatcher, reviewer, writer: new StubWriter());

            model.OfferRun(Run());
            Settle(dispatcher, model);
            model.RunCommand.Execute(null);
            UiThread.Pump(dispatcher, () => !model.IsRunning);

            bool before = model.CanWriteWorkOrder;
            foreach (ReviewActionViewModel item in model.Recommendations)
            {
                item.IsAccepted = false;
            }

            return (before, model.CanWriteWorkOrder, model.WorkOrderTooltip);
        });

        state.Before.ShouldBeTrue("the High-priority item starts ticked");
        state.AfterUnticking.ShouldBeFalse("there is nothing to write");
        state.Tooltip.ShouldNotContain("HarnessRepoPath");
    }

    [Fact]
    public void Without_a_harness_repository_the_button_names_the_setting()
    {
        string tooltip = UiThread.Run(dispatcher =>
        {
            StubReviewer reviewer = new() { Result = Result() };
            StubWriter writer = new() { UnavailableReason = "Set GlassCoder:Retrospective:HarnessRepoPath to …" };
            RetrospectiveViewModel model = Model(dispatcher, reviewer, writer: writer);

            model.OfferRun(Run());
            Settle(dispatcher, model);
            model.RunCommand.Execute(null);
            UiThread.Pump(dispatcher, () => !model.IsRunning);

            model.CanWriteWorkOrder.ShouldBeFalse();
            return model.WorkOrderTooltip;
        });

        tooltip.ShouldContain("HarnessRepoPath");
    }

    [Fact]
    public void The_surface_and_its_window_lay_out_over_a_real_retrospective()
    {
        // XAML is not checked by the compiler. A missing StaticResource key, a converter that is
        // not where the markup says, or a binding to an attached property that does not exist all
        // build cleanly and throw when the control is first constructed - in front of the operator.
        // Laid out rather than merely constructed, because the item templates - the stage
        // expanders and the recommendation rows - are only instantiated when something asks the
        // panel for its size.
        (int Stages, int Rows) built = UiThread.Run(dispatcher =>
        {
            TestApplication.Ensure();

            StubReviewer reviewer = new() { OnDisk = Result() };
            RetrospectiveViewModel model = Model(dispatcher, reviewer);
            model.OfferRun(Run());
            Settle(dispatcher, model);

            RetrospectiveView view = new() { DataContext = model };
            view.Measure(new Size(1000, 800));
            view.Arrange(new Rect(0, 0, 1000, 800));
            view.UpdateLayout();

            RetrospectiveResultWindow window = new(model);
            window.Measure(new Size(880, 640));
            window.Arrange(new Rect(0, 0, 880, 640));
            window.UpdateLayout();

            return (model.Stages.Count, model.Recommendations.Count);
        });

        built.Stages.ShouldBe(3);
        built.Rows.ShouldBe(2);
    }

    /// <summary>Pumps until the availability probe has been applied to the tooltip.</summary>
    private static void Settle(Dispatcher dispatcher, RetrospectiveViewModel model) =>
        UiThread.Pump(dispatcher, () => !model.Tooltip.StartsWith("Checking", StringComparison.Ordinal));

    private static RetrospectiveViewModel Model(
        Dispatcher dispatcher,
        IRetrospectiveReviewer reviewer,
        IRetrospectiveResultDialog? dialog = null,
        IRetrospectiveWriter? writer = null) =>
        new(reviewer, writer ?? new StubWriter(), dispatcher, dialog);

    private static RetrospectiveRequest Run() => new("216360bf")
    {
        Goal = "Build a desktop app that multiplies two numbers.",
        StopReason = "Completed",
        Steps = 44,
        TotalTokens = 640_519,
    };

    private static Retrospective Result() => new()
    {
        RunId = "216360bf",
        TakenAt = DateTimeOffset.UnixEpoch,
        Stages =
        [
            Stage(RetrospectiveStageKind.Code, "The multiply logic is untested."),
            Stage(RetrospectiveStageKind.Process, "Steps 18 to 39 were spent on layout tests."),
            Stage(RetrospectiveStageKind.Harness, "Nothing judges the screen."),
        ],
        Recommendations =
        [
            new ReviewAction("screen-oracle", "Judge the screen", "nothing does", ReviewActionPriority.High),
            new ReviewAction("rename-probe", "Rename the probe", "taste", ReviewActionPriority.Optional),
        ],
        Directory = "C:/workspace/.glasscoder/retrospectives/216360bf",
    };

    /// <summary>The one activity that carries a finished stage, as the reviewer reports it.</summary>
    private static RetrospectiveActivity Finished(RetrospectiveStageKind kind, string report) =>
        new(kind, ClaudeCliEventKind.Note, "done") { Completed = Stage(kind, report) };

    private static RetrospectiveStage Stage(RetrospectiveStageKind kind, string report) => new()
    {
        Kind = kind,
        Reviewed = true,
        Report = report,
        Model = "claude-opus-5",
        DurationMs = 41_000,
        CostUsd = 0.5m,
    };

    /// <summary>An <see cref="IRetrospectiveReviewer"/> that answers immediately from a script.</summary>
    private sealed class StubReviewer : IRetrospectiveReviewer
    {
        public bool Enabled { get; init; } = true;

        public ReviewerAvailability Availability { get; init; } = ReviewerAvailability.Available("test");

        public Retrospective? Result { get; init; }

        public Retrospective? OnDisk { get; init; }

        public IReadOnlyList<RetrospectiveActivity> Narration { get; init; } = [];

        /// <summary>
        /// Held between the narration and the result, for tests about the surface mid-run.
        /// <para>
        /// Without it a stub finishes before <see cref="Progress{T}"/>'s posts are drained, so
        /// the final result lands first and there is no "two stages done, one to go" to observe -
        /// which is the one state the real reviewer, three sessions at minutes each, spends
        /// almost all of its time in.
        /// </para>
        /// </summary>
        public TaskCompletionSource? Gate { get; init; }

        public Task<ReviewerAvailability> ProbeAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Availability);

        public async Task<Retrospective> ReviewAsync(
            RetrospectiveRequest request,
            IProgress<RetrospectiveActivity>? progress = null,
            CancellationToken cancellationToken = default)
        {
            foreach (RetrospectiveActivity activity in Narration)
            {
                progress?.Report(activity);
            }

            if (Gate is not null)
            {
                await Gate.Task.ConfigureAwait(false);
            }

            return Result ?? Retrospective.NotTaken(request.RunId, "no script", DateTimeOffset.UnixEpoch);
        }

        public Retrospective? Load(string runId) => OnDisk;
    }

    /// <summary>Counts how often the proposals window was asked for.</summary>
    private sealed class RecordingDialog : IRetrospectiveResultDialog
    {
        public int Opened { get; private set; }

        public void Show(RetrospectiveViewModel model) => Opened++;
    }

    /// <summary>A writer that can or cannot write, and records what it was given.</summary>
    private sealed class StubWriter : IRetrospectiveWriter
    {
        public string? UnavailableReason { get; init; }

        public bool CanWrite => UnavailableReason is null;

        public ReviewActionPlan? Written { get; private set; }

        public string Write(ReviewActionPlan plan)
        {
            Written = plan;
            return "C:/glasscoder/docs/retrospectives/retro-216360bf-19700101-000000.md";
        }
    }
}
