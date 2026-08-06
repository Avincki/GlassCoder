using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using GlassCoder.Core.Context;
using GlassCoder.Core.Diagnostics;
using GlassCoder.Core.Metrics;
using GlassCoder.Core.Provenance;
using GlassCoder.Core.Verification;
using GlassCoder.Models;
using GlassCoder.Models.Configuration;
using GlassCoder.Tools.Changes;
using GlassCoder.Tools.Planning;
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
    private readonly ITodoList _todos;
    private readonly IProvenanceStamper? _provenance;
    private readonly IVerificationLadder? _verifier;
    private readonly IChangeLog? _changes;
    private readonly VerificationLadderOptions _verification;
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
        ITodoList? todos = null,
        IProvenanceStamper? provenance = null,
        TimeProvider? timeProvider = null,
        ILogger<AgentLoop>? logger = null,
        IVerificationLadder? verifier = null,
        IChangeLog? changes = null,
        IOptions<VerificationLadderOptions>? verificationOptions = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        _clients = clients;
        _tools = tools;
        _stepLogger = stepLogger;
        _context = context;
        _metrics = metrics;
        _todos = todos ?? new TodoList();
        _provenance = provenance;
        _verifier = verifier;
        _changes = changes;
        _verification = verificationOptions?.Value ?? new VerificationLadderOptions();
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

        // Tools need to know which run they are serving without it being threaded through every
        // signature, and each run starts from a blank plan.
        RunContext.Set(new RunContext(request.RunId, request.TaskId));
        _todos.Clear();
        ProvenanceStamp? stamp = _provenance?.Stamp();
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

        // Cursor into this run's slice of the change log, so each step verifies only the
        // changes it applied itself (workplan task 36).
        int changesSeen = 0;

        // The step-budget warning is sent once. Repeating it every step would spend the very
        // budget it is warning about.
        bool warnedAboutSteps = false;

        // Everything about not-making-progress - repeated failures, stalled read loops, and
        // stopping over a red tree - lives in the sentry, so the loop body stays the cycle.
        RunProgressSentry sentry = new();

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
                // Once, not every time: a model that maintains "done" over a red tree after
                // being told is stuck, and looping the challenge would spend the rest of the
                // budget restating it.
                if (sentry.ChallengeCompletion() is { } challenge)
                {
                    budget.CountStep();
                    messages.Add(new ChatMessage(ChatRole.User, challenge));
                    LogStep(step with { Prompt = prompt }, messages, response, [], modelLatency, "continued", null);
                    continue;
                }

                stopReason = AgentStopReason.Completed;
                finalText = response.Text;
                if (sentry.CompletionCaveat() is { } caveat)
                {
                    error = caveat;
                    _logger.LogWarning("Run {RunId}: {Caveat}", request.RunId, caveat);
                }

                budget.CountStep();
                LogStep(step with { Prompt = prompt }, messages, response, [], modelLatency, stopReason.ToString(), error);
                break;
            }

            // Result: every observation goes back to the model, successes and failures alike.
            List<ToolInvocation> invocations =
                await ExecuteAsync(calls, budget, metrics, cancellationToken).ConfigureAwait(false);

            // One snapshot of what this step changed, shared by everyone who cares: the sentry
            // (progress is measured against it) and the verifier (it climbs over exactly this
            // slice). Two mechanisms once kept two cursors over the same log; this is the one
            // read per step that both derive from.
            IReadOnlyList<CodeChange> runChanges = _changes is null
                ? []
                : [.. _changes.All().Where(c => string.Equals(c.RunId, request.RunId, StringComparison.Ordinal))];
            IReadOnlyList<CodeChange> newlyApplied =
                [.. runChanges.Skip(changesSeen).Where(c => c.Status == ChangeStatus.Applied)];
            changesSeen = runChanges.Count;

            // Not-making-progress is the sentry's department: repeated identical failures,
            // whole steps of verbatim repeats, and completions over a red tree.
            sentry.ObserveStep(invocations, newlyApplied.Count > 0);

            messages.Add(new ChatMessage(
                ChatRole.Tool,
                [.. invocations.Select(i => (AIContent)new FunctionResultContent(i.CallId, i.Result))]));

            // Verify: when the step applied changes, climb the ladder before the next thought,
            // so the model learns immediately whether its change survives the cheap oracles
            // (CLAUDE.md §8, workplan task 36).
            StepVerification? verification = null;
            if (_verifier is not null && _verification.VerifyAppliedChanges && newlyApplied.Count > 0)
            {
                verification = await VerifyChangesAsync(request, newlyApplied, budget, metrics, cancellationToken)
                    .ConfigureAwait(false);
            }

            if (verification is not null)
            {
                // The report goes back as an observation, not a verdict the loop acts on
                // itself: the failure policy is that the model corrects, the same way it
                // corrects after a failing tool call. Rejected-at-the-gate is the write
                // tools' job; reverting applied work is a human's.
                messages.Add(new ChatMessage(ChatRole.User, verification.Message));
                sentry.ObserveVerification(verification.Record.Passed, verification.Record.FailedRung);
            }

            budget.CountStep();

            // Nudged well before the limits, and told what to do differently. A model repeating
            // an unsatisfiable call is usually missing an option, not stuck.
            if (sentry.FailureNudge() is { } failureNudge)
            {
                messages.Add(new ChatMessage(ChatRole.User, failureNudge));
            }

            if (sentry.StallNudge() is { } stallNudge)
            {
                messages.Add(new ChatMessage(ChatRole.User, stallNudge));
            }

            // Told once, when it starts to matter. A run that spends its last steps re-checking
            // finished work rather than finishing it is the common way a step limit is reached,
            // and the agent cannot pace itself against a ceiling it cannot see.
            if (!warnedAboutSteps && budget.IsRunningOutOfSteps)
            {
                warnedAboutSteps = true;
                messages.Add(new ChatMessage(
                    ChatRole.User,
                    $"Budget: {budget.StepsRemaining} of {budget.MaxSteps} steps remain. Finish the highest-value " +
                    "work now and stop. Do not re-run a build or test whose result you already have, and do not " +
                    "start anything you cannot complete in the steps left."));
            }

            LogStep(
                step with { Prompt = prompt },
                messages,
                response,
                invocations,
                modelLatency,
                "continued",
                null,
                verification?.Record);

            // Stopping is kinder than the alternative, which is spending the rest of a finite
            // budget on calls whose answers will not change.
            if (sentry.StopVerdict(limits) is { } verdict)
            {
                stopReason = verdict.Reason;
                error = verdict.Error;
                _logger.LogWarning("Run {RunId} stopped by the progress sentry: {Error}", request.RunId, verdict.Error);
                break;
            }
        }

        AgentRunResult result = new()
        {
            RunId = request.RunId,
            TaskId = request.TaskId,
            Goal = request.Goal,
            CriticRole = request.CriticRole,
            Attempt = request.Attempt,
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
            CriticRole = request.CriticRole,
            Attempt = request.Attempt,
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
            Provenance = stamp,
            Todos = _todos.Items.Count == 0 ? null : _todos.Items,
        });

        // Performance indicators, per run, in a shape that is comparable across runs and
        // across ablation arms (CLAUDE.md §11, workplan task 20).
        RunMetrics runMetrics = metrics.Build(result, "loop", oraclePassed: null, recordedAt: _time.GetUtcNow())
            with
            {
                RepoCommit = stamp?.RepoCommit,
                ConfigHash = stamp?.ConfigHash,
                ContextFresh = stamp?.ContextFresh,
            };
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
    /// Climbs the verification ladder over the changes one step applied (workplan task 36).
    /// <para>
    /// The failure policy is correction, not rejection: the report goes back to the model as an
    /// observation and the loop carries on. The write tools already refuse changes their
    /// in-memory check can prove broken; what the ladder catches here - a red test, a break in
    /// another project - is applied work, and silently reverting applied work would leave the
    /// model reasoning about a working tree that no longer matches what it was told.
    /// </para>
    /// </summary>
    private async Task<StepVerification?> VerifyChangesAsync(
        AgentRunRequest request,
        IReadOnlyList<CodeChange> applied,
        RunBudget budget,
        RunMetricsCollector metrics,
        CancellationToken cancellationToken)
    {
        // A single-file step gets the syntax rung on exactly what changed; a multi-file step
        // starts at the compile rung, which covers every file at once.
        CodeChange? single = applied.Count == 1 ? applied[0] : null;

        VerificationReport report;
        try
        {
            report = await _verifier!.VerifyAsync(
                new VerificationRequest(
                    FilePath: single?.Path,
                    FileText: single?.AfterText,
                    TestFilter: _verification.TestFilter,
                    RunFullSuite: _verification.RunFullSuite,
                    Goal: request.Goal,
                    ChangeDescription: DescribeChanges(applied),
                    CriticRole: request.CriticRole)
                {
                    // What the step touched, so the ladder can build the project that owns it
                    // rather than guessing at the workspace root.
                    ChangedPaths = [.. applied.Select(c => c.Path)],
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The harness failing to verify is not the model failing to code. Log it and
            // continue unverified rather than handing the model an error it cannot act on.
            _logger.LogWarning(ex, "Verification could not run after step; continuing unverified");
            return null;
        }

        if (report.Results.All(r => r.Skipped))
        {
            // Nothing could judge this change - not a C# file, no sandbox. Silence is more
            // honest than a hollow "verified".
            return null;
        }

        if (report.Critique is { } critique)
        {
            // Rung 6 spends the critic role's tokens; bill them at the critic role's prices.
            budget.AddCriticSpend(critique.EstimatedCostUsd);
        }

        metrics.ObserveVerification(report);

        foreach (CodeChange change in applied)
        {
            // Tie the outcome to the change that produced it (CLAUDE.md §10).
            _changes!.Update(change.Id, change.Status, verificationSummary: report.Summary);
        }

        _logger.LogInformation(
            "Verification after step: {Outcome} at rung {Rung} in {Duration:F0} ms",
            report.Passed ? "passed" : "FAILED",
            report.FailedRung ?? report.HighestRungReached,
            report.DurationMs);

        // A failure right after a deletion gets one extra sentence: run d21eb210 deleted the
        // only copy of its deliverable, and when the build then missed the file, "fixed" it by
        // removing the reference too - the goal quietly went with it. The recovery that keeps
        // the work has to be named at the moment the wrong one looks easier.
        bool deleted = applied.Any(c => c.BeforeText.Length > 0 && c.AfterText.Length == 0);
        string message = report.Passed
            ? $"Automatic verification of your change passed (reached {report.HighestRungReached}).\n{report.Summary}"
            : $"Automatic verification of your change FAILED at {report.FailedRung}.\n{report.Summary}\n" +
              "The change is written but does not verify. Fix the reported problems before continuing." +
              (deleted
                  ? " This step deleted a file; if the failure is a missing source or symbol, restore the " +
                    "file (its content is in the change log) instead of removing whatever refers to it."
                  : string.Empty);

        return new StepVerification(
            new StepVerificationRecord(
                report.Passed,
                report.HighestRungReached.ToString(),
                report.FailedRung?.ToString(),
                report.DurationMs,
                report.Summary,
                report.Critique?.EstimatedCostUsd ?? 0m),
            message);
    }

    /// <summary>Renders the step's edits as diffs for the critique rung - "it edited Pager.cs" is not refutable.</summary>
    private string DescribeChanges(IReadOnlyList<CodeChange> applied)
    {
        StringBuilder text = new();
        foreach (CodeChange change in applied)
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"--- {change.Path}");
            foreach (DiffLine line in change.Diff())
            {
                text.AppendLine(line.ToString());
            }

            if (text.Length >= _verification.MaxChangeCharacters)
            {
                text.AppendLine(CultureInfo.InvariantCulture,
                    $"[truncated at {_verification.MaxChangeCharacters} characters]");
                break;
            }
        }

        return text.ToString();
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
        string? error,
        StepVerificationRecord? verification = null) =>
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
            Verification = verification,
            Todos = _todos.Items.Count == 0 ? null : _todos.Items,
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
            invocation.ErrorMessage,
            invocation.Summary);

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

    /// <summary>One climb's outcome: what to log, and what to tell the model.</summary>
    private sealed record StepVerification(StepVerificationRecord Record, string Message);

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
