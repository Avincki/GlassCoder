using GlassCoder.Core.Agent;
using GlassCoder.Core.Metrics;
using GlassCoder.Core.Planning;
using GlassCoder.Core.Verification;
using GlassCoder.TestSupport;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace GlassCoder.Core.Tests;

/// <summary>
/// Executing a workplan (workplan task 79).
/// <para>
/// The property under test throughout is that the checkbox records an <em>oracle outcome</em> and
/// never the model's opinion. An agent that says it finished is offering a belief; a named test
/// that passes is offering a fact, and every assertion here is about keeping those apart.
/// </para>
/// </summary>
public sealed class WorkplanRunnerTests
{
    [Fact]
    public async Task A_passing_task_is_ticked_in_the_file()
    {
        using Fixture fixture = new(Plan());

        await fixture.RunAsync();

        fixture.Task("first").IsComplete.ShouldBeTrue();
    }

    [Fact]
    public async Task A_task_the_model_believes_it_finished_is_not_ticked_when_the_oracle_fails()
    {
        // The whole reason this runner exists. The loop reports Completed either way.
        using Fixture fixture = new(Plan());
        fixture.Ladder.Passed = false;

        WorkplanRunReport report = await fixture.RunAsync();

        fixture.Task("first").IsComplete.ShouldBeFalse();
        report.Outcomes.ShouldHaveSingleItem().Status.ShouldBe(WorkplanTaskStatus.Failed);
    }

    [Fact]
    public async Task The_run_stops_at_the_first_failure()
    {
        // The tasks are dependency-ordered, so everything after a failed prerequisite measures the
        // prerequisite rather than itself.
        using Fixture fixture = new(Plan());
        fixture.Ladder.Passed = false;

        await fixture.RunAsync();

        fixture.Loop.Requests.ShouldHaveSingleItem().TaskId.ShouldBe("first");
        fixture.Task("second").IsComplete.ShouldBeFalse();
    }

    [Fact]
    public async Task Every_task_runs_when_every_oracle_passes()
    {
        using Fixture fixture = new(Plan());

        WorkplanRunReport report = await fixture.RunAsync();

        fixture.Loop.Requests.Select(r => r.TaskId).ShouldBe(["first", "second"]);
        report.Complete.ShouldBeTrue();
        report.Remaining.ShouldBe(0);
    }

    [Fact]
    public async Task An_already_ticked_task_is_not_run_again()
    {
        // Re-invocation resumes at the first unticked task, because the file on disk is the state.
        using Fixture fixture = new(Plan(firstComplete: true));

        await fixture.RunAsync();

        fixture.Loop.Requests.ShouldHaveSingleItem().TaskId.ShouldBe("second");
    }

    [Fact]
    public async Task The_run_is_issued_under_the_tasks_slug_and_nothing_else()
    {
        // The one rule that cannot be got wrong: GlassContext joins run outcomes onto plan tasks
        // by slug. A runner passing a position would attach this run to whatever is second next
        // week.
        using Fixture fixture = new(Plan());

        await fixture.RunAsync();

        fixture.Loop.Requests[0].TaskId.ShouldBe("first");
        fixture.Metrics.Recorded[0].TaskId.ShouldBe("first");
    }

    [Fact]
    public async Task A_task_with_no_slug_runs_under_the_slug_derived_from_its_title()
    {
        using Fixture fixture = new("""
            # Workplan

            ## 1. Set up the solution

            - [ ] **Estimated time:** 1h

            **Oracle:** `dotnet test --filter SetupTests`

            Body.
            """);

        await fixture.RunAsync();

        fixture.Loop.Requests.ShouldHaveSingleItem().TaskId.ShouldBe("set-up-the-solution");
    }

    [Fact]
    public async Task The_goal_carries_the_title_the_body_the_targets_and_the_oracle()
    {
        // An agent told which tests decide the task can aim at them; one that is not is guessing
        // at the target it will be measured against.
        using Fixture fixture = new(Plan());

        await fixture.RunAsync();

        string goal = fixture.Loop.Requests[0].Goal;
        goal.ShouldContain("Do the first thing");
        goal.ShouldContain("The body of the first task.");
        goal.ShouldContain("src/First.cs");
        goal.ShouldContain("dotnet test --filter FirstTests");
    }

    [Fact]
    public async Task Nothing_but_the_checkbox_changes_in_the_plan()
    {
        using Fixture fixture = new(Plan());
        string before = fixture.Text;

        await fixture.RunAsync();

        // Untick both boxes again and the file is byte-identical to the one that went in.
        fixture.Text.Replace("- [x]", "- [ ]", StringComparison.Ordinal).ShouldBe(before);
    }

    // ── Limits ──

    [Fact]
    public async Task A_limit_stops_the_plan_and_leaves_the_box_unticked()
    {
        using Fixture fixture = new(Plan());
        fixture.Loop.StopReason = AgentStopReason.StepLimit;

        WorkplanRunReport report = await fixture.RunAsync();

        report.Outcomes.ShouldHaveSingleItem().Status.ShouldBe(WorkplanTaskStatus.LimitStopped);
        fixture.Task("first").IsComplete.ShouldBeFalse();

        // The oracle is never asked: the run did not finish, so the tests would be judging
        // whatever half-done state it stopped in.
        fixture.Ladder.Calls.ShouldBe(0);
    }

    // ── Attempts ──

    [Fact]
    public async Task A_first_attempt_is_recorded_as_attempt_one()
    {
        using Fixture fixture = new(Plan());

        await fixture.RunAsync();

        fixture.Metrics.Recorded[0].Attempt.ShouldBe(1);
    }

    [Fact]
    public async Task A_retry_after_an_interruption_is_recorded_as_the_next_attempt()
    {
        // Attempt numbers that restarted at 1 on every invocation would report every task as
        // solved first try, which is the opposite of the truth they exist to record. The runner
        // stops at the first failure, so the retry is always a fresh invocation - the count has to
        // come off the metrics file.
        using Fixture fixture = new(Plan());
        fixture.Ladder.Passed = false;

        await fixture.RunAsync();
        await fixture.RunAsync();
        await fixture.RunAsync();

        fixture.Metrics.Recorded.Select(m => m.Attempt).ShouldBe([1, 2, 3]);
    }

    // ── Metrics ──

    [Fact]
    public async Task One_run_writes_one_metrics_row()
    {
        // Two rows for one run is not merely redundant: GlassContext's importer sums steps and
        // tokens per task, so a duplicate doubles both.
        using Fixture fixture = new(Plan());

        await fixture.RunAsync();

        fixture.Metrics.Recorded.Count.ShouldBe(2);
        fixture.Loop.Requests.ShouldAllBe(r => !r.RecordMetrics);
    }

    [Fact]
    public async Task The_recorded_row_carries_the_oracles_verdict()
    {
        using Fixture fixture = new(Plan());
        fixture.Ladder.Passed = false;

        await fixture.RunAsync();

        fixture.Metrics.Recorded[0].OraclePassed.ShouldBe(false);
        fixture.Metrics.Recorded[0].Source.ShouldBe("workplan");
    }

    // ── The plan is optional ──

    [Fact]
    public void An_ordinary_run_still_records_its_own_metrics()
    {
        // The constraint this whole feature is held to: a workplan is a way to drive GlassCoder,
        // never a thing it requires. Anything reaching the loop without one behaves as before.
        new AgentRunRequest { TaskId = "adhoc", Goal = "Do a thing" }.RecordMetrics.ShouldBeTrue();
    }

    private static string Plan(bool firstComplete = false) =>
        $$"""
        # Workplan

        ## 1. Do the first thing

        <!-- task:first -->

        - [{{(firstComplete ? "x" : " ")}}] **Estimated time:** 1h

        **Target files:** `src/First.cs`

        **Oracle:** `dotnet test --filter FirstTests`

        The body of the first task.

        ## 2. Do the second thing

        <!-- task:second -->

        - [ ] **Estimated time:** 1h

        **Oracle:** `dotnet test --filter SecondTests`

        The body of the second task.

        """.ReplaceLineEndings("\n");

    /// <summary>The runner over a plan in a throwaway directory, with the model and oracle faked.</summary>
    internal sealed class Fixture : IDisposable
    {
        private readonly TempWorkspace _workspace = new();

        public Fixture(string plan)
        {
            PlanPath = Path.Combine(_workspace.Root, "WORKPLAN.md");
            File.WriteAllText(PlanPath, plan);

            // Under the workspace, so prior attempts are read back from a real file the way they
            // would be in a repository, and no test can see another's history.
            MetricsPath = Path.Combine(_workspace.Root, "metrics.jsonl");
            Metrics = new CapturingRecorder(MetricsPath);

            Runner = new WorkplanRunner(
                Loop,
                Ladder,
                Metrics,
                Options.Create(new MetricsOptions
                {
                    Directory = _workspace.Root,
                    FileName = "metrics.jsonl",
                }));
        }

        public string PlanPath { get; }

        public string MetricsPath { get; }

        public ScriptedLoop Loop { get; } = new();

        public ScriptedLadder Ladder { get; } = new();

        public CapturingRecorder Metrics { get; }

        public WorkplanRunner Runner { get; }

        public string Text => File.ReadAllText(PlanPath);

        public Task<WorkplanRunReport> RunAsync() => Runner.RunAsync(new WorkplanRunRequest(PlanPath));

        public WorkplanTask Task(string slug) =>
            Workplan.Parse(Text).Tasks.Single(t => t.EffectiveSlug == slug);

        public void Dispose() => _workspace.Dispose();
    }

    /// <summary>A loop that never calls a model and reports whatever it was told to.</summary>
    internal sealed class ScriptedLoop : IAgentLoop
    {
        public List<AgentRunRequest> Requests { get; } = [];

        public AgentStopReason StopReason { get; set; } = AgentStopReason.Completed;

        public Task<AgentRunResult> RunAsync(AgentRunRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);

            return System.Threading.Tasks.Task.FromResult(new AgentRunResult
            {
                RunId = $"run-{Requests.Count}",
                TaskId = request.TaskId,
                Attempt = request.Attempt,
                StopReason = StopReason,
                Steps = 3,
                Elapsed = TimeSpan.FromSeconds(1),
                Messages = [],
                FinalText = "I believe I have finished.",
                Metrics = new RunMetrics
                {
                    RunId = $"run-{Requests.Count}",
                    TaskId = request.TaskId,
                    Role = "worker",
                    Source = "loop",
                    Attempt = request.Attempt,
                    RecordedAt = DateTimeOffset.UnixEpoch,
                    StopReason = StopReason.ToString(),
                    Steps = 3,
                    InputTokens = 10,
                    OutputTokens = 5,
                    TotalTokens = 15,
                    WallClockMs = 1000,
                    CostUsd = 0m,
                    ToolCallsTotal = 2,
                    ToolCallsValid = 2,
                    Edits = 1,
                    EditsWithCompileErrors = 0,
                    Builds = 1,
                    BuildFailures = 0,
                    TestRuns = 1,
                    TestFailures = 0,
                    EditsToGreen = 1,
                    RecoveryOpportunities = 0,
                    Recoveries = 0,
                    DiagnosticsReported = 0,
                    DiagnosticsShown = 0,
                },
            });
        }
    }

    /// <summary>A ladder that reports a unit-test rung exactly as it was told to.</summary>
    internal sealed class ScriptedLadder : IVerificationLadder
    {
        public bool Passed { get; set; } = true;

        public bool Unverified { get; set; }

        public bool SkipTests { get; set; }

        /// <summary>Fails a rung above the tests, so the climb is red while the oracle is green.</summary>
        public bool RefuteCritique { get; set; }

        public int Calls { get; private set; }

        public List<string?> Filters { get; } = [];

        public Task<VerificationReport> VerifyAsync(
            VerificationRequest request, CancellationToken cancellationToken = default)
        {
            Calls++;
            Filters.Add(request.TestFilter);

            RungResult tests = new(
                VerificationRung.UnitTests,
                Passed,
                Unverified ? "The test run exited cleanly but ran 0 tests - nothing was verified." : "4 tests passed.",
                DurationMs: 10,
                Skipped: SkipTests)
            {
                Unverified = Unverified,
            };

            if (!RefuteCritique)
            {
                return System.Threading.Tasks.Task.FromResult(new VerificationReport(
                    Passed,
                    VerificationRung.UnitTests,
                    Passed ? null : VerificationRung.UnitTests,
                    [tests],
                    DurationMs: 10));
            }

            RungResult critique = new(
                VerificationRung.Critique, false, "2/3 critics refuted the change.", DurationMs: 10);

            return System.Threading.Tasks.Task.FromResult(new VerificationReport(
                false,
                VerificationRung.Critique,
                VerificationRung.Critique,
                [tests, critique],
                DurationMs: 20));
        }
    }

    /// <summary>Records to a real file, because prior attempts are read back off disk.</summary>
    internal sealed class CapturingRecorder : IMetricsRecorder
    {
        private readonly JsonlMetricsRecorder _inner;

        public CapturingRecorder(string path) =>
            _inner = new JsonlMetricsRecorder(Options.Create(new MetricsOptions
            {
                Directory = Path.GetDirectoryName(path)!,
                FileName = Path.GetFileName(path),
            }));

        public List<RunMetrics> Recorded { get; } = [];

        public void Record(RunMetrics metrics)
        {
            Recorded.Add(metrics);
            _inner.Record(metrics);
        }
    }
}
