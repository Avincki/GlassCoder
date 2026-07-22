using System.Diagnostics;
using System.Text.Json;
using GlassCoder.Core.Context;
using GlassCoder.Core.Diagnostics;
using GlassCoder.Core.Metrics;
using GlassCoder.Models;
using GlassCoder.Models.Configuration;
using GlassCoder.Tools.Registry;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GlassCoder.Core.Agent;

/// <summary>
/// The controller loop: Observe → Think → Act → Result, repeated until the goal is met or a
/// limit trips (CLAUDE.md §3.1, workplan task 10).
/// <para>
/// <b>The loop is the agent.</b> It is deliberately small and deliberately hand-written. A
/// framework auto-invoker (<c>UseFunctionInvocation()</c>) would run this same cycle out of
/// reach: not interruptible, not budgetable, not loggable at the granularity the transcript
/// needs. Intelligence belongs in the tools and the verifier, not here.
/// </para>
/// </summary>
public sealed class AgentLoop : IAgentLoop
{
    private readonly IChatClientFactory _clients;
    private readonly IToolRegistry _tools;
    private readonly IStepLogger _stepLogger;
    private readonly IContextAssembler _context;
    private readonly IMetricsRecorder _metrics;
    private readonly AgentOptions _defaults;
    private readonly TimeProvider _time;
    private readonly ILogger<AgentLoop> _logger;

    /// <summary>Creates the loop.</summary>
    public AgentLoop(
        IChatClientFactory clients,
        IToolRegistry tools,
        IStepLogger stepLogger,
        IContextAssembler context,
        IMetricsRecorder metrics,
        IOptions<AgentOptions> options,
        TimeProvider? timeProvider = null,
        ILogger<AgentLoop>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        _clients = clients;
        _tools = tools;
        _stepLogger = stepLogger;
        _context = context;
        _metrics = metrics;
        _defaults = options.Value;
        _time = timeProvider ?? TimeProvider.System;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<AgentLoop>.Instance;
    }

    /// <inheritdoc />
    public async Task<AgentRunResult> RunAsync(AgentRunRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        AgentOptions limits = request.Limits ?? _defaults;
        string role = request.Role ?? limits.Role;
        IChatClient client = _clients.GetClient(role);
        ModelRoleOptions roleOptions = _clients.GetRoleOptions(role);
        ChatOptions chatOptions = new() { Tools = [.. _tools.Tools], ToolMode = ChatToolMode.Auto };

        // `messages` is the complete history and stays complete - it is the transcript. What
        // goes over the wire each step is the assembled window, which may be compacted.
        List<ChatMessage> messages =
            [.. _context.CreateInitialMessages(request.SystemPrompt ?? limits.SystemPrompt, request.Goal)];

        RunBudget budget = new(limits, roleOptions, _time);
        RunMetricsCollector metrics = new();
        IReadOnlyDictionary<string, object?>? requestProperties = DescribeRequestProperties(client, chatOptions);
        DateTimeOffset startedAt = _time.GetUtcNow();

        using Activity? runActivity = GlassCoderActivity.Source.StartActivity("glasscoder.run");
        runActivity?.SetTag("glasscoder.run_id", request.RunId);
        runActivity?.SetTag("glasscoder.task_id", request.TaskId);
        runActivity?.SetTag("glasscoder.role", role);

        _logger.LogInformation(
            "Run {RunId} started for task {TaskId} on role {Role} with {ToolCount} tools",
            request.RunId, request.TaskId, role, _tools.Functions.Count);

        AgentStopReason stopReason;
        string? finalText = null;
        string? error = null;

        while (true)
        {
            if (budget.Exhausted() is { } exhausted)
            {
                stopReason = exhausted;
                break;
            }

            // Observe: assemble the leanest window that still contains what the agent needs.
            AssembledContext window = _context.Assemble(messages);
            StepContext step = new(request, role, budget.Steps, _time.GetUtcNow(), requestProperties)
            {
                Context = window,
            };

            // Think.
            ChatResponse response;
            long modelStart = Stopwatch.GetTimestamp();
            try
            {
                response = await client.GetResponseAsync(window.Messages, chatOptions, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                stopReason = AgentStopReason.Cancelled;
                LogStep(step, messages, response: null, [], Stopwatch.GetElapsedTime(modelStart), stopReason.ToString(), null);
                break;
            }
            catch (Exception ex)
            {
                stopReason = AgentStopReason.ModelError;
                error = ex.Message;
                _logger.LogError(ex, "Model call failed on step {StepIndex}", budget.Steps);
                LogStep(step, messages, response: null, [], Stopwatch.GetElapsedTime(modelStart), stopReason.ToString(), error);
                break;
            }

            TimeSpan modelLatency = Stopwatch.GetElapsedTime(modelStart);
            budget.AddUsage(response.Usage);

            // The prompt recorded in the transcript is the window that was actually sent, and it
            // must be a snapshot: when the window is not compacted it *is* the history list, and
            // the next lines are about to append to that list.
            IReadOnlyList<ChatMessage> prompt = [.. window.Messages];
            messages.AddMessages(response);

            // Act: exactly the calls the model asked for, no more.
            List<FunctionCallContent> calls =
                [.. response.Messages.SelectMany(m => m.Contents).OfType<FunctionCallContent>()];

            if (calls.Count == 0)
            {
                stopReason = AgentStopReason.Completed;
                finalText = response.Text;
                budget.CountStep();
                LogStep(step with { Prompt = prompt }, messages, response, [], modelLatency, stopReason.ToString(), null);
                break;
            }

            // Result: every observation goes back to the model, successes and failures alike.
            List<ToolInvocation> invocations =
                await ExecuteAsync(calls, budget, metrics, cancellationToken).ConfigureAwait(false);
            messages.Add(new ChatMessage(
                ChatRole.Tool,
                [.. invocations.Select(i => (AIContent)new FunctionResultContent(i.CallId, i.Result))]));

            budget.CountStep();
            LogStep(step with { Prompt = prompt }, messages, response, invocations, modelLatency, "continued", null);
        }

        AgentRunResult result = new()
        {
            RunId = request.RunId,
            TaskId = request.TaskId,
            StopReason = stopReason,
            Steps = budget.Steps,
            FinalText = finalText,
            InputTokens = budget.InputTokens,
            OutputTokens = budget.OutputTokens,
            TotalTokens = budget.TotalTokens,
            EstimatedCostUsd = budget.EstimatedCostUsd,
            Elapsed = budget.Elapsed,
            ToolCallsTotal = budget.ToolCallsTotal,
            ToolCallsValid = budget.ToolCallsValid,
            Messages = messages,
            Error = error,
        };

        // The run record closes the transcript: the steps say what happened, this says what the
        // run was and how it ended (workplan task 11).
        _stepLogger.LogRun(new RunRecord
        {
            RunId = request.RunId,
            TaskId = request.TaskId,
            Role = role,
            Goal = request.Goal,
            SystemPrompt = request.SystemPrompt ?? limits.SystemPrompt,
            StartedAt = startedAt,
            CompletedAt = _time.GetUtcNow(),
            StopReason = stopReason.ToString(),
            Steps = budget.Steps,
            FinalText = finalText,
            InputTokens = budget.InputTokens,
            OutputTokens = budget.OutputTokens,
            TotalTokens = budget.TotalTokens,
            EstimatedCostUsd = budget.EstimatedCostUsd,
            ElapsedMs = budget.Elapsed.TotalMilliseconds,
            ToolCallsTotal = budget.ToolCallsTotal,
            ToolCallsValid = budget.ToolCallsValid,
            Error = error,
        });

        // Performance indicators, per run, in a shape that is comparable across runs and
        // across ablation arms (CLAUDE.md §11, workplan task 20).
        RunMetrics runMetrics = metrics.Build(result, source: "loop", oraclePassed: null, recordedAt: _time.GetUtcNow());
        result = result with { Metrics = runMetrics };
        _metrics.Record(runMetrics);

        runActivity?.SetTag("glasscoder.stop_reason", stopReason.ToString());
        runActivity?.SetTag("glasscoder.steps", budget.Steps);
        runActivity?.SetTag("glasscoder.total_tokens", budget.TotalTokens);

        _logger.LogInformation(
            "Run {RunId} stopped: {StopReason} after {Steps} steps, {TotalTokens} tokens, {Elapsed:F1}s, tool-call validity {Validity:P0}",
            request.RunId, stopReason, budget.Steps, budget.TotalTokens, result.Elapsed.TotalSeconds, result.ToolCallValidityRate);

        return result;
    }

    private async Task<List<ToolInvocation>> ExecuteAsync(
        List<FunctionCallContent> calls,
        RunBudget budget,
        RunMetricsCollector metrics,
        CancellationToken cancellationToken)
    {
        List<ToolInvocation> invocations = new(calls.Count);

        foreach (FunctionCallContent call in calls)
        {
            using Activity? activity = GlassCoderActivity.Source.StartActivity("glasscoder.tool");
            activity?.SetTag("glasscoder.tool", call.Name);

            ToolInvocation invocation = await _tools.InvokeAsync(call, cancellationToken).ConfigureAwait(false);
            budget.CountToolCall(invocation.IsValid);
            metrics.Observe(invocation);
            activity?.SetTag("glasscoder.tool_status", invocation.Status.ToString());
            invocations.Add(invocation);
        }

        return invocations;
    }

    /// <summary>
    /// Asks the pipeline what constrained decoding will actually attach to a request, so the
    /// transcript records the arm's decoding settings rather than the caller's intent.
    /// </summary>
    private static Dictionary<string, object?>? DescribeRequestProperties(IChatClient client, ChatOptions options)
    {
        ConstrainedDecodingChatClient? stage = client.GetService<ConstrainedDecodingChatClient>();
        AdditionalPropertiesDictionary? properties = stage?.Constrain(options)?.AdditionalProperties;
        return properties is null ? null : new Dictionary<string, object?>(properties, StringComparer.Ordinal);
    }

    private void LogStep(
        StepContext step,
        IReadOnlyList<ChatMessage> messages,
        ChatResponse? response,
        IReadOnlyList<ToolInvocation> invocations,
        TimeSpan modelLatency,
        string outcome,
        string? error) =>
        _stepLogger.LogStep(new StepRecord
        {
            RunId = step.Request.RunId,
            TaskId = step.Request.TaskId,
            StepIndex = step.Index,
            Role = step.Role,
            ModelId = response?.ModelId,
            StartedAt = step.StartedAt,
            Prompt = [.. (step.Prompt ?? messages).Select(Describe)],
            ResponseText = response?.Text,
            ToolCalls = [.. invocations.Select(Describe)],
            RequestProperties = step.RequestProperties,
            InputTokens = response?.Usage?.InputTokenCount,
            OutputTokens = response?.Usage?.OutputTokenCount,
            TotalTokens = response?.Usage?.TotalTokenCount,
            ModelLatencyMs = modelLatency.TotalMilliseconds,
            StepLatencyMs = (_time.GetUtcNow() - step.StartedAt).TotalMilliseconds,
            FinishReason = response?.FinishReason?.Value,
            EstimatedContextTokens = step.Context?.EstimatedTokens,
            ContextCompacted = step.Context?.Compacted ?? false,
            Outcome = outcome,
            Error = error,
        });

    private static TranscriptMessage Describe(ChatMessage message)
    {
        List<string>? toolCalls = null;
        foreach (AIContent content in message.Contents)
        {
            if (content is FunctionCallContent call)
            {
                (toolCalls ??= []).Add(call.Name);
            }
        }

        return new TranscriptMessage(message.Role.Value, message.Text, toolCalls);
    }

    private static ToolCallRecord Describe(ToolInvocation invocation) =>
        new(
            invocation.CallId,
            invocation.ToolName,
            invocation.Arguments,
            invocation.Status.ToString(),
            invocation.IsValid,
            invocation.Duration.TotalMilliseconds,
            Serialise(invocation.Result),
            invocation.ErrorMessage);

    private static string? Serialise(object? result)
    {
        switch (result)
        {
            case null:
                return null;

            case JsonElement element:
                return element.GetRawText();

            default:
                try
                {
                    return JsonSerializer.Serialize(result, ToolFunctionFactory.SerializerOptions);
                }
                catch (NotSupportedException)
                {
                    return result.ToString();
                }
        }
    }

    /// <summary>Per-step scratch state, kept out of the loop body so it stays readable.</summary>
    private sealed record StepContext(
        AgentRunRequest Request,
        string Role,
        int Index,
        DateTimeOffset StartedAt,
        IReadOnlyDictionary<string, object?>? RequestProperties)
    {
        public IReadOnlyList<ChatMessage>? Prompt { get; init; }

        public AssembledContext? Context { get; init; }
    }
}
