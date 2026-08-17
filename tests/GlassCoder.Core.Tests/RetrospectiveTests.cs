using System.Text.Json;
using GlassCoder.Core.Diagnostics;
using GlassCoder.Core.Verification;
using GlassCoder.TestSupport;
using GlassCoder.Tools.Changes;
using GlassCoder.Tools.Processes;
using Microsoft.Extensions.Time.Testing;

namespace GlassCoder.Core.Tests;

/// <summary>
/// The run retrospective (workplan task 67): three staged headless sessions over one finished run.
/// <para>
/// What is worth defending here is not the reviews - they are a model's opinion and no test can
/// check them. It is the staging. Stage two must actually receive stage one's answer, or the
/// second question is not the question this was built to ask; a stage that fails must not cost
/// the stages that succeeded; and the whole thing must never throw, because it is reached from a
/// button on a surface (CLAUDE.md §7).
/// </para>
/// </summary>
public sealed class RetrospectiveTests
{
    private const string Version = "2.1.221 (Claude Code)";

    [Fact]
    public async Task Three_stages_run_in_order_over_the_right_material()
    {
        using TempWorkspace workspace = new();
        FakeProcessRunner runner = Probed()
            .Enqueue(0, Report("The multiply logic is untested."))
            .Enqueue(0, Report("Steps 18 to 39 were spent on layout tests."))
            .Enqueue(0, Recommendations("The harness has no oracle for the screen."));

        Retrospective result = await Reviewer(runner, workspace).ReviewAsync(Request());

        result.Stages.Select(s => s.Kind).ShouldBe(
        [
            RetrospectiveStageKind.Code,
            RetrospectiveStageKind.Process,
            RetrospectiveStageKind.Harness,
        ]);

        result.Complete.ShouldBeTrue();
        result.Stages[0].Report.ShouldContain("multiply logic is untested");
        result.Recommendations.Select(r => r.Id).ShouldBe(["screen-oracle"]);

        // Three launches after the probe, all read-only, all in the workspace.
        runner.Requests.Count.ShouldBe(4);
        foreach (ProcessRunRequest launch in runner.Requests.Skip(1))
        {
            List<string> arguments = [.. launch.Arguments];
            arguments[arguments.IndexOf("--allowedTools") + 1].Split(',').ShouldBe(["Read", "Grep", "Glob"]);
            arguments[arguments.IndexOf("--permission-mode") + 1].ShouldBe("plan");
            launch.WorkingDirectory.ShouldBe(workspace.Guard().RepoRoot);
        }
    }

    [Fact]
    public async Task The_process_stage_is_handed_the_code_review_and_the_transcript()
    {
        // The whole reason the stages are staged. A process review that cannot see what the run
        // produced is answering a different, smaller question.
        using TempWorkspace workspace = new();
        FakeProcessRunner runner = Probed()
            .Enqueue(0, Report("The click handler holds the arithmetic, so no test can reach it."))
            .Enqueue(0, Report("Fine."))
            .Enqueue(0, Recommendations());

        RecordingTranscript transcript = new(
        [
            Step(0, "list_projects", "no projects"),
            Step(1, "edit_file", "the quoted lines do not exist", parsed: false),
        ]);

        await Reviewer(runner, workspace, transcript: transcript).ReviewAsync(Request());

        string directive = runner.Requests[2].StandardInput.ShouldNotBeNull();
        directive.ShouldContain("no test can reach it", Case.Sensitive);
        directive.ShouldContain("edit_file");
        directive.ShouldContain("the quoted lines do not exist");
        directive.ShouldContain("Step 1");
    }

    /// <summary>
    /// The digest is of the session, not of the run that happens to name the reports.
    /// <para>
    /// An operator rarely gets there in one go: they run, read what came out, sharpen the goal and
    /// run again. The digest selected the retrospective's own run id out of the session and dropped
    /// everything before it, so a review of three runs' work was written from the last of them - and
    /// a process reviewer that cannot see the earlier runs cannot report that the session took three
    /// attempts, which is the single most useful thing it had to say.
    /// </para>
    /// </summary>
    [Fact]
    public async Task The_process_stage_is_handed_every_run_of_the_session()
    {
        using TempWorkspace workspace = new();
        FakeProcessRunner runner = Probed().Enqueue(0, Report()).Enqueue(0, Report()).Enqueue(0, Recommendations());

        RecordingTranscript transcript = new(
            [
                Step(0, "dotnet_project", "scaffolded the wrong shape", runId: "run-0"),
                Step(0, "edit_file", "put the arithmetic in the view model"),
            ],
            [Run("run-0", "Build a temperature converter."), Run("run-1")]);

        Retrospective result = await Reviewer(runner, workspace, transcript: transcript).ReviewAsync(Request());

        // The earlier run reaches the reviewer, headed by its own goal rather than by the last
        // run's, and both runs are numbered so a claim can name which one it is about.
        string directive = runner.Requests[2].StandardInput.ShouldNotBeNull();
        directive.ShouldContain("scaffolded the wrong shape");
        directive.ShouldContain("Build a temperature converter.");
        directive.ShouldContain("Run 1 of 2");
        directive.ShouldContain("Run 2 of 2");

        // And the file beside the reports says the same, because that is what a person reads.
        string digest = File.ReadAllText(Path.Combine(result.Directory.ShouldNotBeNull(), "transcript.md"));
        digest.ShouldContain("Runs in this session: 2");
        digest.ShouldContain("scaffolded the wrong shape");
        digest.ShouldContain("put the arithmetic in the view model");
    }

    [Fact]
    public async Task The_harness_stage_gets_the_source_tree_as_an_extra_root()
    {
        using TempWorkspace workspace = new();
        using TempWorkspace harness = new();
        FakeProcessRunner runner = Probed().Enqueue(0, Report()).Enqueue(0, Report()).Enqueue(0, Recommendations());

        await Reviewer(runner, workspace, Options(harness.Root)).ReviewAsync(Request());

        IReadOnlyList<string> arguments = runner.Requests[3].Arguments;
        arguments[^2].ShouldBe("--add-dir", "the flag is variadic, so it has to be last");
        arguments[^1].ShouldBe(harness.Root);

        // The two stages before it read the workspace only.
        runner.Requests[1].Arguments.ShouldNotContain("--add-dir");
        runner.Requests[2].Arguments.ShouldNotContain("--add-dir");
    }

    [Fact]
    public async Task Without_a_source_tree_the_harness_stage_is_told_it_is_working_blind()
    {
        // It still runs - the two reviews are real evidence on their own - but a stage that
        // pretended to have read code it never saw would produce confident nonsense.
        using TempWorkspace workspace = new();
        FakeProcessRunner runner = Probed().Enqueue(0, Report()).Enqueue(0, Report()).Enqueue(0, Recommendations());

        await Reviewer(runner, workspace).ReviewAsync(Request());

        runner.Requests[3].Arguments.ShouldNotContain("--add-dir");
        runner.Requests[3].StandardInput.ShouldNotBeNull().ShouldContain("NOT made available");
    }

    [Fact]
    public async Task A_failed_stage_keeps_the_stages_that_worked()
    {
        using TempWorkspace workspace = new();
        FakeProcessRunner runner = Probed()
            .Enqueue(0, Report("The code is sound."))
            .Enqueue(1, standardError: "credit balance too low")
            .Enqueue(0, Recommendations());

        Retrospective result = await Reviewer(runner, workspace).ReviewAsync(Request());

        result.Stages.Count.ShouldBe(3);
        result.Stages[0].Reviewed.ShouldBeTrue();
        result.Stages[1].Reviewed.ShouldBeFalse();
        result.Stages[1].Failure.ShouldContain("credit balance too low");
        result.Complete.ShouldBeFalse();

        // And the stage after it still ran, told plainly that its second input is missing.
        result.Stages[2].Reviewed.ShouldBeTrue();
        runner.Requests[3].StandardInput.ShouldNotBeNull().ShouldContain("did not complete");
    }

    /// <summary>
    /// Recommending happens once, in the stage that can check (workplan task 75).
    /// <para>
    /// Stage 2 has the transcript and the code review and nothing else - no `WORKPLAN.md`, no
    /// `HISTORY.md`, no `--add-dir` on the harness. Asked for improvements anyway, the 2026-08-08
    /// stage 2 listed five and three were already shipped.
    /// </para>
    /// </summary>
    [Fact]
    public async Task The_process_stage_is_told_to_diagnose_rather_than_prescribe()
    {
        using TempWorkspace workspace = new();
        FakeProcessRunner runner = Probed()
            .Enqueue(0, Report("The code is sound."))
            .Enqueue(0, Report("Steps 9 to 12 were edit thrash."))
            .Enqueue(0, Recommendations());

        await Reviewer(runner, workspace).ReviewAsync(Request());

        string stageTwo = runner.Requests[2].StandardInput.ShouldNotBeNull();
        stageTwo.ShouldContain("Do not recommend changes to GlassCoder");
        stageTwo.ShouldContain("does the recommending");

        // Stage 3 is the one that still asks for them, and it is the one with the files.
        runner.Requests[3].StandardInput.ShouldNotBeNull().ShouldContain("`recommendations` are concrete improvements");
    }

    /// <summary>
    /// The defect this task was written from (workplan task 68). On 2026-08-08 stage 3 answered in
    /// full and was thrown away, because the CLI exits non-zero when it stops itself at
    /// <c>--max-budget-usd</c> and the session read only the exit code. A stage that answered and
    /// was then cut off is a reviewed stage with an asterisk, not a stage that did not happen.
    /// </summary>
    [Fact]
    public async Task A_stage_stopped_at_its_ceiling_keeps_its_report_and_says_it_was_capped()
    {
        using TempWorkspace workspace = new();
        FakeProcessRunner runner = Probed()
            .Enqueue(0, Report("The code is sound."))
            .Enqueue(0, Report("The run was efficient."))
            .Enqueue(1, Capped(Recommendations("What the harness should learn.")));

        Retrospective result = await Reviewer(runner, workspace).ReviewAsync(Request());

        RetrospectiveStage harness = result.Stages[2];
        harness.Reviewed.ShouldBeTrue("the report was produced and paid for before the ceiling was reached");
        harness.Report.ShouldContain("What the harness should learn");
        harness.Failure.ShouldNotBeNull().ShouldContain("Reached maximum budget");

        // The tickable list survives with it, which is the half a work order is written from.
        result.Recommendations.Select(r => r.Id).ShouldBe(["screen-oracle"]);

        // And it is on disk like any other finished stage, so a restart still shows it.
        File.Exists(Path.Combine(result.Directory, "3-harness.md")).ShouldBeTrue();
        File.Exists(Path.Combine(result.Directory, "recommendations.json")).ShouldBeTrue();
    }

    [Fact]
    public async Task Cancelling_keeps_whatever_finished()
    {
        using TempWorkspace workspace = new();
        using CancellationTokenSource cancellation = new();

        CancellingProcessRunner runner = new(cancellation, cancelAfter: 2);
        runner.Enqueue(0, Version);
        runner.Enqueue(0, Report("The code is sound."));

        Retrospective result = await Reviewer(runner, workspace)
            .ReviewAsync(Request(), progress: null, cancellation.Token);

        result.Stages.Count.ShouldBe(1, "the stage that finished before the cancellation is kept");
        result.Stages[0].Kind.ShouldBe(RetrospectiveStageKind.Code);
        result.Complete.ShouldBeFalse();
    }

    [Fact]
    public async Task Costs_and_durations_sum_across_the_stages()
    {
        using TempWorkspace workspace = new();
        FakeProcessRunner runner = Probed()
            .Enqueue(0, Report(cost: 0.5m))
            .Enqueue(0, Report(cost: 0.25m))
            .Enqueue(0, Recommendations(cost: 1.25m));

        Retrospective result = await Reviewer(runner, workspace).ReviewAsync(Request());

        result.TotalCostUsd.ShouldBe(2.00m);
    }

    [Fact]
    public async Task Recommendations_come_back_ranked_and_capped()
    {
        using TempWorkspace workspace = new();
        FakeProcessRunner runner = Probed()
            .Enqueue(0, Report())
            .Enqueue(0, Report())
            .Enqueue(0, Recommendations(items: """
                {"id":"tidy","title":"Rename the probe","detail":"","priority":"Optional"},
                {"id":"oracle","title":"Judge the screen","detail":"nothing does","priority":"High"},
                {"id":"cover","title":"Notice empty suites","detail":"","priority":"Medium"}
                """));

        Retrospective result = await Reviewer(
            runner, workspace, Options(maxRecommendations: 2)).ReviewAsync(Request());

        result.Recommendations.Select(r => r.Id).ShouldBe(["oracle", "cover"]);
        result.Recommendations[0].Priority.ShouldBe(ReviewActionPriority.High);
    }

    [Fact]
    public async Task A_report_with_nothing_to_tick_says_so_rather_than_looking_empty()
    {
        // --json-schema is version-dependent, so a stage that answers in prose has to degrade to
        // "here is what it said" rather than to a blank checklist that reads as "no findings".
        using TempWorkspace workspace = new();
        FakeProcessRunner runner = Probed()
            .Enqueue(0, Report())
            .Enqueue(0, Report())
            .Enqueue(0, """{"is_error":false,"result":"GlassCoder needs an oracle for the screen."}""");

        Retrospective result = await Reviewer(runner, workspace).ReviewAsync(Request());

        result.Recommendations.ShouldBeEmpty();
        result.Stages[^1].Reviewed.ShouldBeTrue();
        result.Stages[^1].Report.ShouldContain("needs an oracle");
        result.Stages[^1].Failure.ShouldNotBeNull().ShouldContain("proposed nothing to tick");
    }

    [Fact]
    public async Task Every_stage_is_written_beside_the_run_and_reads_back()
    {
        using TempWorkspace workspace = new();
        FakeProcessRunner runner = Probed()
            .Enqueue(0, Report("The code is sound."))
            .Enqueue(0, Report("The run was efficient."))
            .Enqueue(0, Recommendations("Judge the screen."));

        FakeTimeProvider time = new(new DateTimeOffset(2026, 8, 11, 11, 6, 0, TimeSpan.Zero));
        ClaudeCodeRetrospectiveReviewer reviewer = Reviewer(runner, workspace, time: time);
        await reviewer.ReviewAsync(Request());

        // Rehydration: a restart finds the same three reports and the same tickable list. The
        // folder is a timestamp now, so this can only work by reading the run id back out of the
        // reports - which is the whole reason it is written into their front matter.
        Retrospective? loaded = reviewer.Load("run-1");

        loaded.ShouldNotBeNull();
        loaded.Stages.Count.ShouldBe(3);
        loaded.Stages[0].Report.ShouldContain("The code is sound.");
        loaded.Recommendations.Select(r => r.Id).ShouldBe(["screen-oracle"]);

        // Named for when it was taken, the way the work order beside it is. The digest is on disk
        // too, so the CLI never needs a root under %LocalAppData%.
        string directory = Path.Combine(workspace.Root, ".glasscoder", "retrospectives", "20260811-110600");
        File.Exists(Path.Combine(directory, "transcript.md")).ShouldBeTrue();
        File.ReadAllText(Path.Combine(directory, "1-code.md")).ShouldContain("runId: run-1");
    }

    [Fact]
    public async Task A_second_look_at_the_same_run_no_longer_overwrites_the_first()
    {
        // A run id could only ever name one folder, so re-taking a retrospective destroyed the
        // one before it. Two timestamps are two folders, and Load answers with the newer.
        using TempWorkspace workspace = new();
        FakeTimeProvider time = new(new DateTimeOffset(2026, 8, 11, 11, 6, 0, TimeSpan.Zero));

        await Reviewer(Probed().Enqueue(0, Report("First look.")), workspace, time: time)
            .ReviewAsync(Request());

        time.Advance(TimeSpan.FromHours(2));

        ClaudeCodeRetrospectiveReviewer second = Reviewer(
            Probed().Enqueue(0, Report("Second look.")), workspace, time: time);
        await second.ReviewAsync(Request());

        System.IO.Directory
            .GetDirectories(Path.Combine(workspace.Root, ".glasscoder", "retrospectives"))
            .Select(Path.GetFileName)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ShouldBe(["20260811-110600", "20260811-130600"]);

        second.Load("run-1").ShouldNotBeNull().Stages[0].Report.ShouldContain("Second look.");
    }

    [Fact]
    public async Task A_retrospective_written_under_the_old_run_id_layout_still_loads()
    {
        // Four of these existed when the naming changed. A rehydration that only understands
        // folders written after the change silently loses the history somebody kept.
        using TempWorkspace workspace = new();
        string legacy = Path.Combine(workspace.Root, ".glasscoder", "retrospectives", "run-1");
        System.IO.Directory.CreateDirectory(legacy);
        await File.WriteAllTextAsync(
            Path.Combine(legacy, "1-code.md"),
            "---\nglasscoder: retrospective\nstage: Code\n---\n\n# The code\n\nWritten before the rename.");

        Retrospective? loaded = Reviewer(new FakeProcessRunner(), workspace).Load("run-1");

        loaded.ShouldNotBeNull();
        loaded.Stages[0].Report.ShouldContain("Written before the rename.");
    }

    [Fact]
    public Task Nothing_is_on_disk_for_a_run_that_was_never_reviewed()
    {
        using TempWorkspace workspace = new();
        Reviewer(new FakeProcessRunner(), workspace).Load("never-happened").ShouldBeNull();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Switching_the_feature_off_launches_nothing()
    {
        using TempWorkspace workspace = new();
        FakeProcessRunner runner = new();

        Retrospective result = await Reviewer(runner, workspace, Options(enabled: false)).ReviewAsync(Request());

        result.Stages.ShouldBeEmpty();
        result.Failure.ShouldNotBeNull();
        runner.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task Each_stage_lands_in_the_transcript_against_the_run_it_judged()
    {
        // The precedent the file review and the operator rating set: a paid model call that left
        // no trace would be the one thing here that cannot be reconstructed afterwards.
        using TempWorkspace workspace = new();
        FakeProcessRunner runner = Probed().Enqueue(0, Report()).Enqueue(0, Report()).Enqueue(0, Recommendations());
        RecordingStepLogger steps = new();

        await Reviewer(runner, workspace, steps: steps).ReviewAsync(Request());

        steps.Steps.Count.ShouldBe(3);
        steps.Steps.ShouldAllBe(s => s.RunId == "run-1");
        steps.Steps.ShouldAllBe(s => s.Role == "human");
        steps.Steps.SelectMany(s => s.ToolCalls).Select(c => c.Name).ShouldBe(
            ["retrospective_code", "retrospective_process", "retrospective_harness"]);
    }

    private static RetrospectiveRequest Request() => new("run-1")
    {
        Goal = "Build a desktop app that multiplies two numbers.",
        StopReason = "Completed",
        Steps = 44,
        TotalTokens = 640_519,
    };

    private static RetrospectiveOptions Options(
        string? harnessRepoPath = null, int maxRecommendations = 12, bool enabled = true) =>
        new()
        {
            Enabled = enabled,
            HarnessRepoPath = harnessRepoPath ?? string.Empty,
            MaxRecommendations = maxRecommendations,
        };

    private static ClaudeCodeRetrospectiveReviewer Reviewer(
        IProcessRunner runner,
        TempWorkspace workspace,
        RetrospectiveOptions? options = null,
        ITranscriptBus? transcript = null,
        IStepLogger? steps = null,
        TimeProvider? time = null) =>
        new(runner,
            workspace.Guard(),
            new ChangeLog(),
            TempWorkspace.Wrap(options ?? Options()),
            logger: null,
            transcript,
            steps,
            time);

    /// <summary>A runner whose first scripted answer is the version probe.</summary>
    private static FakeProcessRunner Probed() => new FakeProcessRunner().Enqueue(0, Version);

    private static StepRecord Step(
        int index, string tool, string summary, bool parsed = true, string runId = "run-1") => new()
    {
        RunId = runId,
        TaskId = "desktop",
        StepIndex = index,
        Role = "worker",
        StartedAt = DateTimeOffset.UnixEpoch,
        Prompt = [],
        ToolCalls = [new ToolCallRecord("c", tool, null, parsed ? "Succeeded" : "Failed", parsed, 1, null, null, summary)],
        ModelLatencyMs = 1,
        StepLatencyMs = 1,
        Outcome = "continued",
    };

    private static RunRecord Run(string runId, string? goal = null) => new()
    {
        RunId = runId,
        TaskId = "desktop",
        Role = "worker",
        Goal = goal,
        StartedAt = DateTimeOffset.UnixEpoch,
        CompletedAt = DateTimeOffset.UnixEpoch.AddMinutes(5),
        StopReason = "Completed",
        Steps = 4,
        InputTokens = 100,
        OutputTokens = 20,
        TotalTokens = 120,
        EstimatedCostUsd = 0m,
        ElapsedMs = 1000,
        ToolCallsTotal = 4,
        ToolCallsValid = 4,
    };

    /// <summary>The CLI's envelope carrying a prose report, as stages 1 and 2 answer.</summary>
    private static string Report(string report = "Nothing of note.", decimal cost = 0m) =>
        "{\"type\":\"result\",\"is_error\":false,\"session_id\":\"sess\",\"total_cost_usd\":"
        + cost.ToString(System.Globalization.CultureInfo.InvariantCulture)
        + ",\"result\":\"see structured output\",\"structured_output\":{\"report\":"
        + JsonSerializer.Serialize(report) + "}}";

    /// <summary>The CLI's envelope carrying a report and a tickable list, as stage 3 answers.</summary>
    private static string Recommendations(
        string report = "The harness could do better.",
        decimal cost = 0m,
        string items = """{"id":"screen-oracle","title":"Judge the screen","detail":"nothing does","priority":"High"}""") =>
        "{\"type\":\"result\",\"is_error\":false,\"session_id\":\"sess\",\"total_cost_usd\":"
        + cost.ToString(System.Globalization.CultureInfo.InvariantCulture)
        + ",\"result\":\"see structured output\",\"structured_output\":{\"report\":"
        + JsonSerializer.Serialize(report) + ",\"recommendations\":[" + items + "]}}";

    /// <summary>
    /// The same envelope as the CLI writes it when it stops itself at its spend ceiling: the answer
    /// is present and complete, <c>is_error</c> is set, and the reason lives in <c>errors</c> and
    /// <c>subtype</c> rather than in <c>result</c> - which is why the original failure text was
    /// empty.
    /// </summary>
    private static string Capped(string envelope) =>
        envelope.Replace(
            "\"is_error\":false",
            "\"is_error\":true,\"subtype\":\"error_max_budget_usd\",\"terminal_reason\":\"budget_exhausted\"," +
            "\"errors\":[\"Reached maximum budget ($2)\"]",
            StringComparison.Ordinal);

    /// <summary>An <see cref="ITranscriptBus"/> holding a scripted session, and nothing else.</summary>
    private sealed class RecordingTranscript : ITranscriptBus
    {
        public RecordingTranscript(IReadOnlyList<StepRecord> steps, IReadOnlyList<RunRecord>? runs = null)
        {
            Steps = steps;
            Runs = runs ?? [];
        }

        public IReadOnlyList<StepRecord> Steps { get; }

        public IReadOnlyList<ReviewRecord> Reviews => [];

        public IReadOnlyList<RunRecord> Runs { get; }

        public event EventHandler<StepRecord>? StepRecorded { add { } remove { } }

        public event EventHandler<RunRecord>? RunRecorded { add { } remove { } }

        public event EventHandler<ReviewRecord>? ReviewRecorded { add { } remove { } }

        public int NextStepIndex(string runId) => Steps.Count;

        public void Clear()
        {
        }
    }

    /// <summary>Cancels the retrospective partway through, from inside the runner.</summary>
    private sealed class CancellingProcessRunner : IProcessRunner
    {
        private readonly Queue<ProcessRunResult> _scripted = new();
        private readonly CancellationTokenSource _cancellation;
        private readonly int _cancelAfter;
        private int _calls;

        public CancellingProcessRunner(CancellationTokenSource cancellation, int cancelAfter)
        {
            _cancellation = cancellation;
            _cancelAfter = cancelAfter;
        }

        public void Enqueue(int exitCode, string standardOutput) =>
            _scripted.Enqueue(new ProcessRunResult(exitCode, standardOutput, string.Empty, TimeSpan.Zero, false));

        public Task<ProcessRunResult> RunAsync(ProcessRunRequest request, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (++_calls >= _cancelAfter)
            {
                _cancellation.Cancel();
            }

            return Task.FromResult(_scripted.Count > 0
                ? _scripted.Dequeue()
                : new ProcessRunResult(0, string.Empty, string.Empty, TimeSpan.Zero, false));
        }
    }
}
