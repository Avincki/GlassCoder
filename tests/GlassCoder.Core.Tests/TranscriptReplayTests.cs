using GlassCoder.Core.Agent;
using GlassCoder.Core.Diagnostics;
using GlassCoder.TestSupport;
using GlassCoder.Tools.Registry;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Serilog;

namespace GlassCoder.Core.Tests;

/// <summary>
/// Workplan task 11's acceptance criterion, taken literally: run the loop through the real
/// Serilog pipeline, then read the JSONL back and check the whole run is there.
/// <para>
/// This is the test that makes "every run is fully reconstructable from the logs alone"
/// falsifiable. Without it that claim is a comment.
/// </para>
/// </summary>
public sealed class TranscriptReplayTests : IDisposable
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
    public async Task A_completed_run_replays_from_its_jsonl_into_a_complete_transcript()
    {
        AgentRunResult result = await RunThroughSerilogAsync();

        IReadOnlyList<RunTranscript> transcripts = TranscriptReader.ReadDirectory(_directory);

        RunTranscript transcript = transcripts.ShouldHaveSingleItem();
        transcript.RunId.ShouldBe(result.RunId);
        transcript.TaskId.ShouldBe("replay-task");
        transcript.IsComplete.ShouldBeTrue();
        transcript.Steps.Count.ShouldBe(result.Steps);
    }

    [Fact]
    public async Task The_replayed_transcript_carries_tokens_and_latencies_per_call()
    {
        await RunThroughSerilogAsync();

        RunTranscript transcript = TranscriptReader.ReadDirectory(_directory).ShouldHaveSingleItem();

        foreach (StepRecord step in transcript.Steps)
        {
            step.TotalTokens.ShouldNotBeNull();
            step.ModelLatencyMs.ShouldBeGreaterThanOrEqualTo(0);
            step.StepLatencyMs.ShouldBeGreaterThanOrEqualTo(0);
        }

        transcript.TotalTokens.ShouldBe(180);   // 120 for the tool step, 60 for the closing step
        transcript.Run!.TotalTokens.ShouldBe(180);
    }

    [Fact]
    public async Task The_replayed_transcript_carries_the_tool_calls_and_their_results()
    {
        await RunThroughSerilogAsync();

        RunTranscript transcript = TranscriptReader.ReadDirectory(_directory).ShouldHaveSingleItem();

        ToolCallRecord call = transcript.ToolCalls.ShouldHaveSingleItem();
        call.Name.ShouldBe("echo");
        call.Status.ShouldBe(nameof(ToolCallStatus.Succeeded));
        call.Parsed.ShouldBeTrue();
        call.Result.ShouldContain("hello");
        call.Arguments!["text"].ToString().ShouldBe("hello");
    }

    [Fact]
    public async Task The_replayed_transcript_renders_as_readable_text()
    {
        await RunThroughSerilogAsync();

        string text = TranscriptReader.ReadDirectory(_directory).ShouldHaveSingleItem().ToText();

        text.ShouldContain("replay-task");
        text.ShouldContain("step 0");
        text.ShouldContain("tool echo");
        text.ShouldContain("Completed");
    }

    [Fact]
    public void A_truncated_line_does_not_cost_the_rest_of_the_transcript()
    {
        // Log files are append-only streams; a crash can cut one mid-write.
        string[] lines =
        [
            """{"@t":"2026-07-22T10:00:00Z","@mt":"glasscoder.step {@Step}","Step":{"RunId":"r1","TaskId":"t1","StepIndex":0,"Role":"worker","StartedAt":"2026-07-22T10:00:00Z","Prompt":[],"ToolCalls":[],"ModelLatencyMs":1,"StepLatencyMs":2,"Outcome":"continued"}}""",
            """{"@t":"2026-07-22T10:00:01Z","@mt":"glasscoder.step {@Step}","Step":{"RunId":"r1","Task""",
            """{"@t":"2026-07-22T10:00:02Z","@mt":"glasscoder.run {@Run}","Run":{"RunId":"r1","TaskId":"t1","Role":"worker","StartedAt":"2026-07-22T10:00:00Z","CompletedAt":"2026-07-22T10:00:03Z","StopReason":"Completed","Steps":1,"InputTokens":1,"OutputTokens":2,"TotalTokens":3,"EstimatedCostUsd":0,"ElapsedMs":3000,"ToolCallsTotal":0,"ToolCallsValid":0}}""",
        ];

        RunTranscript transcript = TranscriptReader.Read(lines).ShouldHaveSingleItem();

        transcript.Steps.ShouldHaveSingleItem();
        transcript.Run.ShouldNotBeNull();
        transcript.Run!.StopReason.ShouldBe("Completed");
    }

    private async Task<AgentRunResult> RunThroughSerilogAsync()
    {
        LoggingOptions logging = new() { Directory = _directory, Console = false };
        using Serilog.Core.Logger serilog = SerilogBootstrap.CreateLogger(logging);
        using ILoggerFactory factory = LoggerFactory.Create(builder => builder.AddSerilog(serilog));

        StepLogger stepLogger = new(factory.CreateLogger<StepLogger>(), Options.Create(logging));

        AgentLoop loop = new(
            new FakeChatClientFactory(new FakeChatClient(
                FakeChatClient.ToolCall("echo", new Dictionary<string, object?> { ["text"] = "hello" }),
                FakeChatClient.Text("Done."))),
            new ToolRegistry([new EchoTools()]),
            stepLogger,
            TestContextAssembler.Create(),
            new RecordingMetricsRecorder(),
            Options.Create(new AgentOptions()));

        AgentRunResult result = await loop.RunAsync(
            new AgentRunRequest { TaskId = "replay-task", Goal = "Echo hello." });

        // Flush the sinks before reading the files back.
        serilog.Dispose();
        return result;
    }

    private sealed class EchoTools : IToolSet
    {
        [GlassCoderTool("echo")]
        [System.ComponentModel.Description("Echoes text back, for tests.")]
        public Tools.ToolObservation<EchoData> Echo(
            [System.ComponentModel.Description("Text to echo back.")] string text = "hello") =>
            Tools.Observation.Ok("echo", new EchoData(text), "echoed");
    }

    public sealed record EchoData([property: System.ComponentModel.Description("The echoed text.")] string Value);
}
