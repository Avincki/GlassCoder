using System.Text.Json;
using GlassCoder.Core.Agent;
using GlassCoder.Core.Metrics;
using GlassCoder.TestSupport;
using GlassCoder.Tools;
using GlassCoder.Tools.Registry;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace GlassCoder.Core.Tests;

/// <summary>
/// Performance-indicator recording (workplan task 20). The acceptance criterion is that a run
/// produces a complete metrics JSONL record - so these tests check the definitions and then
/// check the file.
/// </summary>
public sealed class MetricsTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "glasscoder-tests", Guid.NewGuid().ToString("n"));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public void A_run_produces_a_complete_metrics_record_on_disk()
    {
        JsonlMetricsRecorder recorder = new(Options.Create(new MetricsOptions { Directory = _directory }));

        recorder.Record(Sample());

        string[] lines = File.ReadAllLines(recorder.FilePath);
        JsonElement record = JsonDocument.Parse(lines.ShouldHaveSingleItem()).RootElement;

        // Every Section 11 indicator must be present, including the derived ones - a reader
        // should never have to reimplement a definition.
        foreach (string field in new[]
        {
            "runId", "taskId", "stopReason", "steps", "totalTokens", "wallClockMs", "costUsd",
            "toolCallsTotal", "toolCallsValid", "toolCallValidityRate", "edits",
            "editsWithCompileErrors", "compileErrorRatePerEdit", "editsToGreen",
            "recoveryRate", "cascadeRatio", "passAtOne",
        })
        {
            record.TryGetProperty(field, out _).ShouldBeTrue($"metrics record is missing '{field}'");
        }

        record.GetProperty("passAtOne").GetBoolean().ShouldBeTrue();
        record.GetProperty("costPerSolvedTask").GetDecimal().ShouldBe(0.25m);
    }

    [Fact]
    public void Appending_is_safe_to_repeat()
    {
        JsonlMetricsRecorder recorder = new(Options.Create(new MetricsOptions { Directory = _directory }));

        recorder.Record(Sample());
        recorder.Record(Sample() with { RunId = "run-2" });

        File.ReadAllLines(recorder.FilePath).Length.ShouldBe(2);
    }

    [Fact]
    public void Recording_can_be_switched_off()
    {
        JsonlMetricsRecorder recorder = new(
            Options.Create(new MetricsOptions { Directory = _directory, Enabled = false }));

        recorder.Record(Sample());

        File.Exists(recorder.FilePath).ShouldBeFalse();
    }

    [Fact]
    public void An_unsolved_task_has_no_cost_per_solved_task()
    {
        RunMetrics unsolved = Sample() with { OraclePassed = false };

        unsolved.CostPerSolvedTask.ShouldBeNull();
        unsolved.WallClockPerSolvedTaskMs.ShouldBeNull();
        unsolved.PassAtOne.ShouldBe(false);
    }

    [Fact]
    public void An_ungraded_run_reports_pass_at_one_as_unknown_rather_than_false()
    {
        // The loop cannot know whether the task was solved. Recording "false" would quietly
        // poison every pass@1 average taken over these rows.
        (Sample() with { OraclePassed = null }).PassAtOne.ShouldBeNull();
    }

    [Fact]
    public void The_collector_reads_the_indicators_out_of_the_observations()
    {
        RunMetricsCollector collector = new();

        collector.Observe(Invocation("edit_file", """{"ok":true,"tool":"edit_file","data":{"path":"a.cs"}}"""));
        collector.Observe(Invocation("build", """{"ok":true,"tool":"build","data":{"succeeded":false,"totalErrors":7,"diagnostics":"7 error(s)\n  a.cs(1,1): error CS0103: x"}}"""));
        collector.Observe(Invocation("edit_file", """{"ok":true,"tool":"edit_file","data":{"path":"a.cs"}}"""));
        collector.Observe(Invocation("build", """{"ok":true,"tool":"build","data":{"succeeded":true,"totalErrors":0,"diagnostics":"No diagnostics."}}"""));

        RunMetrics metrics = collector.Build(Result(), "test", oraclePassed: true, DateTimeOffset.UnixEpoch);

        metrics.Edits.ShouldBe(2);
        metrics.Builds.ShouldBe(2);
        metrics.BuildFailures.ShouldBe(1);
        metrics.EditsWithCompileErrors.ShouldBe(1);
        metrics.CompileErrorRatePerEdit.ShouldBe(0.5d);
        metrics.RecoveryOpportunities.ShouldBe(1);
        metrics.Recoveries.ShouldBe(1);
        metrics.EditsToGreen.ShouldBe(1);
        metrics.DiagnosticsReported.ShouldBe(7);
        metrics.CascadeRatio.ShouldBe(7d);
    }

    [Fact]
    public void An_invalid_tool_call_contributes_nothing_but_the_validity_rate()
    {
        RunMetricsCollector collector = new();

        collector.Observe(new ToolInvocation
        {
            CallId = "c1",
            ToolName = "edit_file",
            Status = ToolCallStatus.InvalidArguments,
            Result = null,
            Duration = TimeSpan.Zero,
        });

        collector.Edits.ShouldBe(0);
    }

    [Fact]
    public void A_red_test_run_is_a_recovery_opportunity()
    {
        RunMetricsCollector collector = new();

        collector.Observe(Invocation("run_tests", """{"ok":true,"tool":"run_tests","data":{"ok":false,"failed":2}}"""));
        collector.Observe(Invocation("run_tests", """{"ok":true,"tool":"run_tests","data":{"ok":true,"failed":0}}"""));

        RunMetrics metrics = collector.Build(Result(), "test", null, DateTimeOffset.UnixEpoch);

        metrics.TestRuns.ShouldBe(2);
        metrics.TestFailures.ShouldBe(1);
        metrics.RecoveryOpportunities.ShouldBe(1);
        metrics.Recoveries.ShouldBe(1);
        metrics.RecoveryRate.ShouldBe(1d);
    }

    [Fact]
    public async Task The_loop_records_metrics_and_carries_them_on_the_result()
    {
        RecordingMetricsRecorder recorder = new();
        AgentLoop loop = new(
            new FakeChatClientFactory(new FakeChatClient(FakeChatClient.Text("done"))),
            new ToolRegistry(Array.Empty<IToolSet>()),
            new RecordingStepLogger(),
            TestContextAssembler.Create(),
            recorder,
            Options.Create(new AgentOptions()));

        AgentRunResult result = await loop.RunAsync(new AgentRunRequest { TaskId = "t1", Goal = "do it" });

        RunMetrics recorded = recorder.Records.ShouldHaveSingleItem();
        recorded.RunId.ShouldBe(result.RunId);
        recorded.Source.ShouldBe("loop");
        recorded.OraclePassed.ShouldBeNull();
        result.Metrics.ShouldNotBeNull();
        result.Metrics!.TotalTokens.ShouldBe(result.TotalTokens);
    }

    private static ToolInvocation Invocation(string tool, string resultJson) => new()
    {
        CallId = "c",
        ToolName = tool,
        Status = ToolCallStatus.Succeeded,
        Result = JsonDocument.Parse(resultJson).RootElement,
        Duration = TimeSpan.FromMilliseconds(5),
    };

    private static AgentRunResult Result() => new()
    {
        RunId = "run-1",
        TaskId = "task-1",
        StopReason = AgentStopReason.Completed,
        Steps = 4,
        Elapsed = TimeSpan.FromSeconds(12),
        Messages = Array.Empty<ChatMessage>(),
        TotalTokens = 4200,
        EstimatedCostUsd = 0.25m,
        ToolCallsTotal = 4,
        ToolCallsValid = 4,
    };

    private static RunMetrics Sample() =>
        new RunMetricsCollector().Build(Result(), "test", oraclePassed: true, DateTimeOffset.UnixEpoch);
}
