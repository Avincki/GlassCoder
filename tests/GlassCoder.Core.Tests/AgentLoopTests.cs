using System.ComponentModel;
using GlassCoder.Core.Agent;
using GlassCoder.Models.Configuration;
using GlassCoder.TestSupport;
using GlassCoder.Tools;
using GlassCoder.Tools.Registry;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace GlassCoder.Core.Tests;

/// <summary>
/// The controller loop (workplan task 10). Every limit must have a graceful give-up path, and
/// the loop - not a framework auto-invoker - must be the thing that executes the tools.
/// </summary>
public sealed class AgentLoopTests
{
    [Fact]
    public async Task A_response_without_a_tool_call_completes_the_run()
    {
        Harness harness = new(FakeChatClient.Text("All done."));

        AgentRunResult result = await harness.RunAsync();

        result.StopReason.ShouldBe(AgentStopReason.Completed);
        result.RanToCompletion.ShouldBeTrue();
        result.FinalText.ShouldBe("All done.");
        result.Steps.ShouldBe(1);
        harness.Client.CallCount.ShouldBe(1);
    }

    [Fact]
    public async Task The_loop_executes_the_tool_and_feeds_the_observation_back()
    {
        Harness harness = new(
            FakeChatClient.ToolCall("echo", new Dictionary<string, object?> { ["text"] = "hello" }),
            FakeChatClient.Text("Echoed."));

        AgentRunResult result = await harness.RunAsync();

        result.StopReason.ShouldBe(AgentStopReason.Completed);
        result.Steps.ShouldBe(2);
        result.ToolCallsTotal.ShouldBe(1);
        result.ToolCallsValid.ShouldBe(1);

        // The observation must be visible to the model on the next turn - that is the "Result"
        // leg of Observe → Think → Act → Result.
        IReadOnlyList<ChatMessage> secondPrompt = harness.Client.Requests[1].Messages;
        secondPrompt.ShouldContain(m => m.Role == ChatRole.Tool);
        secondPrompt[^1].Contents.OfType<FunctionResultContent>().ShouldHaveSingleItem();
    }

    [Fact]
    public async Task Tools_are_advertised_on_every_request()
    {
        Harness harness = new(FakeChatClient.Text("done"));

        await harness.RunAsync();

        harness.Client.Requests[0].Options!.Tools!.Select(t => t.Name).ToList().ShouldBe(["echo", "fails"]);
    }

    [Fact]
    public async Task The_step_limit_stops_the_run()
    {
        Harness harness = new(new AgentOptions { MaxSteps = 3 }, FakeChatClient.ToolCall("echo"));

        AgentRunResult result = await harness.RunAsync();

        result.StopReason.ShouldBe(AgentStopReason.StepLimit);
        result.Steps.ShouldBe(3);
        harness.Client.CallCount.ShouldBe(3);
    }

    [Fact]
    public async Task The_token_limit_stops_the_run()
    {
        // Each scripted response reports 120 tokens.
        Harness harness = new(new AgentOptions { MaxSteps = 100, MaxTotalTokens = 200 }, FakeChatClient.ToolCall("echo"));

        AgentRunResult result = await harness.RunAsync();

        result.StopReason.ShouldBe(AgentStopReason.TokenLimit);
        result.TotalTokens.ShouldBe(240);
        result.Steps.ShouldBe(2);
    }

    [Fact]
    public async Task The_wall_clock_limit_stops_the_run()
    {
        FakeTimeProvider time = new();
        Harness harness = new(
            new AgentOptions { MaxSteps = 100, MaxWallClockSeconds = 60 },
            FakeChatClient.ToolCall("echo"))
        {
            TimeProvider = time,
        };
        harness.Client.OnRequest = _ => time.Advance(TimeSpan.FromSeconds(30));

        AgentRunResult result = await harness.RunAsync();

        result.StopReason.ShouldBe(AgentStopReason.TimeLimit);
        result.Steps.ShouldBe(2);
    }

    [Fact]
    public async Task The_cost_limit_stops_the_run()
    {
        Harness harness = new(
            new AgentOptions { MaxSteps = 100, MaxCostUsd = 0.15m },
            FakeChatClient.ToolCall("echo"))
        {
            RoleOptions = new ModelRoleOptions
            {
                Endpoint = "http://localhost/v1",
                ModelAlias = "worker",
                InputCostPerMillionTokens = 1000m,
                OutputCostPerMillionTokens = 0m,
            },
        };

        AgentRunResult result = await harness.RunAsync();

        result.StopReason.ShouldBe(AgentStopReason.CostLimit);
        result.EstimatedCostUsd.ShouldBeGreaterThan(0.15m);
        result.Steps.ShouldBe(2);
    }

    [Fact]
    public async Task Repeated_invalid_tool_calls_stop_the_run()
    {
        Harness harness = new(
            new AgentOptions { MaxSteps = 100, MaxConsecutiveInvalidToolCalls = 2 },
            FakeChatClient.ToolCall("no_such_tool"));

        AgentRunResult result = await harness.RunAsync();

        result.StopReason.ShouldBe(AgentStopReason.ToolFailureLimit);
        result.ToolCallsTotal.ShouldBe(2);
        result.ToolCallsValid.ShouldBe(0);
        result.ToolCallValidityRate.ShouldBe(0d);
    }

    [Fact]
    public async Task A_failing_model_call_stops_the_run_without_throwing()
    {
        Harness harness = new(FakeChatClient.Text("unused"));
        harness.Client.ThrowOnNextCall = new HttpRequestException("connection refused");

        AgentRunResult result = await harness.RunAsync();

        result.StopReason.ShouldBe(AgentStopReason.ModelError);
        result.Error.ShouldContain("connection refused");
    }

    [Fact]
    public async Task Cancellation_stops_the_run_gracefully()
    {
        Harness harness = new(FakeChatClient.ToolCall("echo"));
        using CancellationTokenSource cancellation = new();
        await cancellation.CancelAsync();

        AgentRunResult result = await harness.RunAsync(cancellation.Token);

        result.StopReason.ShouldBe(AgentStopReason.Cancelled);
        result.Steps.ShouldBe(0);
    }

    [Fact]
    public async Task A_handled_tool_failure_is_a_valid_call_and_the_run_carries_on()
    {
        Harness harness = new(FakeChatClient.ToolCall("fails"), FakeChatClient.Text("Recovered."));

        AgentRunResult result = await harness.RunAsync();

        result.StopReason.ShouldBe(AgentStopReason.Completed);
        result.ToolCallsTotal.ShouldBe(1);
        result.ToolCallsValid.ShouldBe(1);
    }

    [Fact]
    public async Task Every_step_is_recorded_with_the_transcript_schema()
    {
        Harness harness = new(
            FakeChatClient.ToolCall("echo", new Dictionary<string, object?> { ["text"] = "hello" }),
            FakeChatClient.Text("Echoed."));

        AgentRunResult result = await harness.RunAsync();

        harness.StepLogger.Steps.Count.ShouldBe(2);

        StepRecordAssertions.ShouldDescribeAToolStep(harness.StepLogger.Steps[0], result.RunId);
        harness.StepLogger.Steps[1].Outcome.ShouldBe(nameof(AgentStopReason.Completed));
        harness.StepLogger.Steps[1].ResponseText.ShouldBe("Echoed.");
        harness.StepLogger.Steps.Select(s => s.StepIndex).ShouldBe([0, 1]);
    }

    [Fact]
    public async Task The_run_result_carries_the_full_message_history()
    {
        Harness harness = new(FakeChatClient.ToolCall("echo"), FakeChatClient.Text("done"));

        AgentRunResult result = await harness.RunAsync();

        result.Messages[0].Role.ShouldBe(ChatRole.System);
        result.Messages[1].Role.ShouldBe(ChatRole.User);
        result.Messages.ShouldContain(m => m.Role == ChatRole.Tool);
    }

    private static class StepRecordAssertions
    {
        public static void ShouldDescribeAToolStep(Core.Diagnostics.StepRecord record, string runId)
        {
            record.RunId.ShouldBe(runId);
            record.TaskId.ShouldBe("task-1");
            record.StepIndex.ShouldBe(0);
            record.Role.ShouldBe("worker");
            record.Outcome.ShouldBe("continued");
            record.Prompt.Count.ShouldBe(2);
            record.Prompt[0].Role.ShouldBe("system");
            record.ToolCalls.ShouldHaveSingleItem();
            record.ToolCalls[0].Name.ShouldBe("echo");
            record.ToolCalls[0].Parsed.ShouldBeTrue();
            record.ToolCalls[0].Status.ShouldBe(nameof(ToolCallStatus.Succeeded));
            record.ToolCalls[0].Result.ShouldContain("hello");
            record.TotalTokens.ShouldBe(120);
            record.ModelLatencyMs.ShouldBeGreaterThanOrEqualTo(0);
        }
    }

    /// <summary>Wires a loop over a scripted client, a real registry and a recording step logger.</summary>
    private sealed class Harness
    {
        private readonly AgentOptions _options;

        public Harness(params ChatResponse[] responses)
            : this(new AgentOptions(), responses)
        {
        }

        public Harness(AgentOptions options, params ChatResponse[] responses)
        {
            _options = options;
            Client = new FakeChatClient(responses);
        }

        public FakeChatClient Client { get; }

        public RecordingStepLogger StepLogger { get; } = new();

        public RecordingMetricsRecorder Metrics { get; } = new();

        public TimeProvider TimeProvider { get; init; } = TimeProvider.System;

        public ModelRoleOptions RoleOptions { get; init; } =
            new() { Endpoint = "http://localhost/v1", ModelAlias = "worker" };

        public Task<AgentRunResult> RunAsync(CancellationToken cancellationToken = default)
        {
            AgentLoop loop = new(
                new FakeChatClientFactory(Client, RoleOptions),
                new ToolRegistry([new TestTools()]),
                StepLogger,
                TestContextAssembler.Create(),
                Metrics,
                Options.Create(_options),
                TimeProvider);

            return loop.RunAsync(
                new AgentRunRequest { TaskId = "task-1", Goal = "Do the thing." },
                cancellationToken);
        }
    }

    private sealed class TestTools : IToolSet
    {
        [GlassCoderTool("echo", Order = 1)]
        [Description("Echoes text back, for tests.")]
        public ToolObservation<EchoData> Echo([Description("Text to echo back.")] string text = "hello") =>
            Observation.Ok("echo", new EchoData(text), "echoed");

        [GlassCoderTool("fails", Order = 2)]
        [Description("Always reports a handled failure, for tests.")]
        public ToolObservation<EchoData> Fails() =>
            Observation.Fail<EchoData>("fails", ToolErrorCodes.NotFound, "nothing here");
    }

    public sealed record EchoData([property: Description("The echoed text.")] string Value);
}
