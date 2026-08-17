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
using GlassCoder.Tools.Build;
using GlassCoder.Tools.Changes;
using GlassCoder.Tools.Guardrails;
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
    private readonly ICriticPanel? _critics;
    private readonly VerificationLadderOptions _verification;
    private readonly AgentOptions _defaults;
    private readonly TimeProvider _time;
    private readonly ILogger<AgentLoop> _logger;
    private readonly ILimitExtensionGate? _limitGate;
    private readonly RuntimeEvidence? _runtime;
    private readonly AbandonedIntents? _intents;
    private readonly AdvisoryNotices? _notices;
    private readonly WorkspaceOptions? _workspace;
    private readonly GlassCoder.Tools.Retrieval.IRetrievalPolicy? _retrieval;

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
        IOptions<VerificationLadderOptions>? verificationOptions = null,
        ICriticPanel? critics = null,
        ILimitExtensionGate? limitGate = null,
        RuntimeEvidence? runtime = null,
        AbandonedIntents? intents = null,
        AdvisoryNotices? notices = null,
        IOptions<WorkspaceOptions>? workspace = null,
        GlassCoder.Tools.Retrieval.IRetrievalPolicy? retrieval = null)
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
        _critics = critics;
        _verification = verificationOptions?.Value ?? new VerificationLadderOptions();
        _defaults = options.Value;
        _time = timeProvider ?? TimeProvider.System;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<AgentLoop>.Instance;
        _limitGate = limitGate;
        _runtime = runtime;
        _intents = intents;
        _notices = notices;
        _workspace = workspace?.Value;
        _retrieval = retrieval;
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

        // The critique panel speaks at most twice per run, at completion claims, judging them
        // against the latest ladder summary. Once proved gameable: run f4ed50e0 answered a
        // refutation by adding UI-test packages, wrote no test that used them, and completed on
        // the spent critique - so the recovery now gets judged too. Twice is a hard ceiling on
        // purpose: unbounded refutation once drove a worker into a revert loop (run 4b582162),
        // so a second refutation completes with a recorded caveat instead of a third argument.
        const int MaxCritiquePanels = 2;
        int critiquePanels = 0;
        string? critiqueCaveat = null;

        // The whole record, not just its summary: the completion panel's advisory wording names
        // what actually verified the change, and a summary string cannot say whether the tests
        // rung ran, ran and found nothing, or was never reached.
        StepVerificationRecord? lastVerification = null;

        // What the first panel concluded, so the second can be told whether anything it asked for
        // arrived (workplan task 72).
        CritiqueHistory critiqueHistory = new();

        // Everything about not-making-progress - repeated failures, stalled read loops, and
        // stopping over a red tree - lives in the sentry, so the loop body stays the cycle.
        RunProgressSentry sentry = new();

        while (true)
        {
            if (budget.Exhausted() is { } exhausted)
            {
                // The operator may buy the run one more allotment of the tripped ceiling - a
                // run that dies three steps from done used to restart from zero. Asked again
                // each time the extended ceiling trips; a run nobody answers for stops exactly
                // as before, and only steps and tokens are extendable.
                if (exhausted is AgentStopReason.StepLimit or AgentStopReason.TokenLimit &&
                    await RequestExtensionAsync(exhausted, budget, cancellationToken).ConfigureAwait(false))
                {
                    budget.Extend(exhausted);
                    _logger.LogInformation(
                        "Run {RunId}: {Reason} reached and extended by the operator; ceilings now " +
                        "{MaxSteps} steps, {MaxTokens} tokens",
                        request.RunId, exhausted, budget.MaxSteps, budget.MaxTotalTokens);
                    continue;
                }

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
                // "ModelError" names the stop, not the cause, and `ex.Message` alone names the
                // symptom without the endpoint it happened against. What a reader needs to act -
                // which role, which endpoint, and which of the half-dozen distinct failures this
                // was - is spread across the exception chain and the role's settings, so it is
                // assembled here rather than left for whoever reads the transcript to reconstruct.
                TimeSpan failedAfter = Stopwatch.GetElapsedTime(modelStart);
                stopReason = AgentStopReason.ModelError;
                error = ModelCallFailure.Describe(role, roleOptions, ex, failedAfter).Message;
                _logger.LogError(ex, "Model call failed on step {StepIndex}: {Failure}", budget.Steps, error);
                LogStep(step, messages, response: null, [], failedAfter, stopReason.ToString(), error);
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
                // The red-tree challenge first, then the suite notice: a failing rung is the more
                // urgent thing to be told, and only one push-back is spent per stop attempt.
                if ((sentry.ChallengeCompletion() ?? sentry.ChallengeNotice()) is { } challenge)
                {
                    budget.CountStep();
                    messages.Add(new ChatMessage(ChatRole.User, challenge));
                    LogStep(step with { Prompt = prompt }, messages, response, [], modelLatency, "continued", null);
                    continue;
                }

                // The critique boundary. The panel used to sit on the ladder and judge every
                // applied change against the whole run goal - a question no intermediate step
                // can answer, so it refuted 14 of 14 changes in run 4b582162 and its prose
                // drove the worker into a revert loop until the run was cancelled. "The goal
                // is met" is the one claim the refutation prompt was built for; it is judged
                // at most twice, and the second verdict is final either way.
                StepVerification? critique = null;
                if (critiquePanels < MaxCritiquePanels && _critics is not null)
                {
                    critiquePanels++;
                    critique = await CritiqueCompletionAsync(
                        request, response.Text, lastVerification, budget, critiqueHistory, cancellationToken)
                        .ConfigureAwait(false);

                    if (critique?.Record.Critique is { } panel)
                    {
                        metrics.ObserveCompletionCritique(panel.Refuted);
                    }

                    if (critique?.Message is { } review)
                    {
                        if (critiquePanels < MaxCritiquePanels)
                        {
                            budget.CountStep();
                            messages.Add(new ChatMessage(ChatRole.User, review));
                            LogStep(
                                step with { Prompt = prompt },
                                messages, response, [], modelLatency, "continued", null, critique.Record);
                            continue;
                        }

                        // A second refutation ends the argument rather than extending it: the
                        // run completes, and the record says the panel was never convinced -
                        // in advisory mode too. Run 216360bf finished as "Completed" while the
                        // review banner read REFUTED, and a record that disagrees with its own
                        // review is the kind of green that defers the real fix. The caveat is
                        // information, not a gate; finishing as-is stays allowed.
                        critiqueCaveat = Cap(
                            $"Completed despite a second critique refutation. {review}",
                            MaxCritiqueFeedbackCharacters);
                        metrics.ObserveCompletionOverRefutation();
                    }
                }

                stopReason = AgentStopReason.Completed;
                finalText = response.Text;
                List<string> caveats = [];
                if (critiqueCaveat is not null)
                {
                    caveats.Add(critiqueCaveat);
                }

                if (sentry.CompletionCaveat() is { } caveat)
                {
                    caveats.Add(caveat);
                }

                if (sentry.NoticeCaveat() is { } noticeCaveat)
                {
                    caveats.Add(noticeCaveat);
                }

                // On the record as well as in front of the panel: the run that shipped without a
                // root solution finished green, and the fact that it had asked for one and been
                // told how to get it existed nowhere a later reader would look.
                if (_intents?.Summary() is { } abandonedCaveat)
                {
                    caveats.Add(abandonedCaveat);
                }

                if (_notices?.Summary() is { } unansweredCaveat)
                {
                    caveats.Add(unansweredCaveat);
                }

                if (EmptySolutions() is { } emptyCaveat)
                {
                    caveats.Add(emptyCaveat);
                }

                if (caveats.Count > 0)
                {
                    error = string.Join(" ", caveats);
                    _logger.LogWarning("Run {RunId}: {Caveat}", request.RunId, error);
                }

                budget.CountStep();
                LogStep(
                    step with { Prompt = prompt },
                    messages, response, [], modelLatency, stopReason.ToString(), error, critique?.Record);
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

            // What was wanted and refused, and whether it was ever achieved. Every observation,
            // not just the failures: a success is what closes an entry, and step 19 of run
            // dd11ef7c - refused, then repaired on the next call - must leave no trace at all.
            if (_intents is not null)
            {
                foreach (ToolInvocation invocation in invocations)
                {
                    _intents.Observe(
                        invocation.ToolName,
                        Operation(invocation),
                        invocation.Status == ToolCallStatus.Succeeded && invocation.OutcomeOk,
                        budget.Steps);
                }
            }

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
                sentry.ObserveVerification(
                    verification.Record.Passed,
                    verification.Record.FailedRung,
                    verification.Record.Noticed,
                    verification.Tests);
                lastVerification = verification.Record;
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

            if (sentry.PathReadNudge() is { } pathReadNudge)
            {
                messages.Add(new ChatMessage(ChatRole.User, pathReadNudge));
            }

            if (sentry.TestFailureNudge() is { } testFailureNudge)
            {
                messages.Add(new ChatMessage(ChatRole.User, testFailureNudge));
            }

            if (sentry.RedundantVerificationNudge() is { } cachedVerificationNudge)
            {
                messages.Add(new ChatMessage(ChatRole.User, cachedVerificationNudge));
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

        // What retrieval spent, read once at the end (workplan task 61). Without it a retrieval
        // arm is pass@1 against pass@1, which cannot tell an arm whose tool was never called from
        // one whose answers did not help.
        metrics.Retrieval = _retrieval?.Stats;

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

        // The closing line carries the failure detail with it: a reader who tails the log sees the
        // stop and its cause together, rather than the stop here and the reason forty stack-trace
        // lines earlier.
        _logger.LogInformation(
            "Run {RunId} stopped: {StopReason} after {Steps} steps, {TotalTokens} tokens, {Elapsed:F1}s, tool-call validity {Validity:P0}{Failure}",
            request.RunId, stopReason, budget.Steps, budget.TotalTokens, result.Elapsed.TotalSeconds, result.ToolCallValidityRate,
            error is null ? string.Empty : $" · {error}");

        return result;
    }

    /// <summary>
    /// Asks the gate whether the tripped ceiling may grow. A gate that is absent, declines,
    /// or fails answers no - an extension is a favour, never a dependency the run can crash on.
    /// </summary>
    private async Task<bool> RequestExtensionAsync(
        AgentStopReason exhausted, RunBudget budget, CancellationToken cancellationToken)
    {
        if (_limitGate is null)
        {
            return false;
        }

        RunLimitReached limit = exhausted == AgentStopReason.StepLimit
            ? new RunLimitReached(exhausted, budget.Steps, budget.MaxSteps, budget.StepAllotment)
            : new RunLimitReached(exhausted, budget.TotalTokens, budget.MaxTotalTokens, budget.TokenAllotment);

        try
        {
            return await _limitGate.RequestExtensionAsync(limit, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "The limit-extension gate failed; stopping at the limit as configured");
            return false;
        }
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
                    // Deliberately absent, which parks the critique rung: a panel judging one
                    // step's diff against the whole run goal refuted everything it saw (run
                    // 4b582162). The panel now speaks at the completion claim instead.
                    ChangeDescription: null,
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
            VerificationVerdict.Describe(report.Passed, report.Unverified, report.Noticed),
            report.FailedRung ?? report.HighestRungReached,
            report.DurationMs);

        // A failure right after a deletion gets one extra sentence: run d21eb210 deleted the
        // only copy of its deliverable, and when the build then missed the file, "fixed" it by
        // removing the reference too - the goal quietly went with it. The recovery that keeps
        // the work has to be named at the moment the wrong one looks easier.
        bool deleted = applied.Any(c => c.BeforeText.Length > 0 && c.AfterText.Length == 0);

        // The header the model reads used to say "passed" whatever the body said underneath, while
        // the logger one call up already distinguished "passed (0 tests)" for the operator. Run
        // 4c7de12b got "passed" four times over "the test run exited cleanly but ran 0 tests -
        // nothing was verified", which is the one place a reader stops if the first line is
        // reassuring. Same condition, same words, both audiences.
        string message = report.Passed
            ? report.Unverified
                ? $"Automatic verification of your change reached {report.HighestRungReached}, " +
                  $"which verified nothing.\n{report.Summary}"
                : $"Automatic verification of your change passed (reached {report.HighestRungReached}).\n{report.Summary}"
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
                report.Critique?.EstimatedCostUsd ?? 0m)
            {
                Critique = report.Critique is { } rungCritique ? Record(rungCritique) : null,
                Unverified = report.Unverified,
                Noticed = report.Noticed,
            },
            message,

            // The rung that ran the tests, for the sentry. The record above carries the climb's
            // verdict and its prose; what a repeated-failure counter needs is which suite this was
            // and what its first line said, and neither survives the flattening into a summary.
            report.TestRun);
    }

    /// <summary>
    /// Ceiling on the critique text handed back to the worker. The full verdicts go to the
    /// transcript; the worker gets the tally and the leading reasons. Three full paragraphs of
    /// critic prose per step is most of what turned run 4b582162's context into critique.
    /// </summary>
    private const int MaxCritiqueFeedbackCharacters = 800;

    /// <summary>
    /// Ceiling on a tool result replayed into a step's prompt record.
    /// <para>
    /// Much tighter than <see cref="LoggingOptions.MaxLoggedTextLength"/>, because every step logs
    /// the <em>whole</em> conversation: one result is written once per step for the rest of the
    /// run, so recording these at the 16,000-character limit would grow the transcript with the
    /// square of the step count - a thirty-step run could log nine megabytes of the same tool
    /// output. The authoritative full copy is written once, in
    /// <see cref="ToolCallRecord.Result"/> on the step that made the call; this is the replay, and
    /// it only has to be enough to read the conversation by.
    /// </para>
    /// </summary>
    private const int MaxReplayedResultCharacters = 2_000;

    /// <summary>
    /// One critique of the finished work, at the moment the model first claims the goal is met.
    /// <para>
    /// Null when there was nothing to judge - no panel, no applied changes, a panel that could
    /// not be reached. A non-null result with a null <see cref="StepVerification.Message"/> is
    /// an acceptance: recorded, but not worth a message the model would have to answer.
    /// </para>
    /// <para>
    /// The refutation is worded as advisory unless critique gates: the compiler and tests have
    /// already had their say, and a small worker treats critic prose as instructions whatever
    /// the flag says - so the wording, the cap and the single shot are the actual guardrails.
    /// </para>
    /// </summary>
    private async Task<StepVerification?> CritiqueCompletionAsync(
        AgentRunRequest request,
        string claim,
        StepVerificationRecord? lastVerification,
        RunBudget budget,
        CritiqueHistory history,
        CancellationToken cancellationToken)
    {
        string? evidence = lastVerification?.Summary;

        if (_critics is null || _changes is null || !_critics.CanCritique(request.CriticRole))
        {
            return null;
        }

        IReadOnlyList<CodeChange> applied = [.. _changes.All()
            .Where(c => string.Equals(c.RunId, request.RunId, StringComparison.Ordinal) &&
                        c.Status == ChangeStatus.Applied)];
        if (applied.Count == 0)
        {
            // A run that changed nothing made no refutable claim - it answered a question.
            return null;
        }

        // What the panel is judging, and whether it is the same thing the last panel refused
        // (workplan task 72). The fingerprint is over the *evidence*, not the diff: run d5edbc59's
        // second panel saw two changed XAML attributes and an identical verification set, and a
        // diff-based fingerprint would have called that new evidence and said nothing.
        // Runtime evidence reaches the panel that asked for it (workplan task 71). A tool
        // observation stops at the model and the transcript; the critique reads this string, so
        // without the last step a launch would answer the refutation everywhere except where the
        // refutation is made.
        string? runtime = _runtime?.Latest;
        if (runtime is not null)
        {
            evidence = string.IsNullOrWhiteSpace(evidence) ? runtime : $"{evidence}\n{runtime}";

            // The sibling absence. A launch happened, so the line below never fires - and the
            // panel's gate is effectively "did a launch happen", which is how run 31983adb closed
            // 3/3 on a window nobody had typed into, over a goal whose verb was "press Multiply".
            // The fact was in the launch summary's own hedge and three critics and the worker all
            // read past it; an absence has to be stated to be judged, which is the whole argument
            // the line below was built on.
            if (_runtime!.WindowWentUntouched)
            {
                const string untouched = "Runtime: a window was drawn, but nothing was ever typed into it and " +
                                         "nothing in it was pressed - so this is the window at rest, not what it " +
                                         "does when it is used.";
                evidence = $"{evidence}\n{untouched}";
            }
        }
        else if (BuiltSomethingRunnable())
        {
            // An absence, stated. The panel's prompt already treats a missing launch as grounds to
            // refute for a goal about a running application, and it works: eleven seconds after
            // this panel accepted run dbaa0580 3/3, the post-run reviewer - same critic role, same
            // three lenses, same work - refuted 3/3 with every lens naming the missing launch. The
            // only difference was that one of them could see the absence and the other had to
            // infer it from a line that was not there. This is the line.
            const string never = "Runtime: the application was never launched in this run, " +
                                 "so nothing observed what it does when it opens.";
            evidence = string.IsNullOrWhiteSpace(evidence) ? never : $"{evidence}\n{never}";
        }

        // And what the run asked for, was refused, and never came back to. The panel is judging
        // whether the goal was met; a build that was never made and a solution that was never
        // created are exactly the kind of absence a green climb cannot show it.
        if (_intents?.Summary() is { } abandoned)
        {
            evidence = string.IsNullOrWhiteSpace(evidence) ? abandoned : $"{evidence}\n{abandoned}";
        }

        // And what the harness kept saying that nothing acted on. The refusal ledger above is the
        // failure half of the same question; run 31983adb spent a third of its steps carrying an
        // item the harness disowned on all five touches, with no organ counting that it had.
        if (_notices?.Summary() is { } unanswered)
        {
            evidence = string.IsNullOrWhiteSpace(evidence) ? unanswered : $"{evidence}\n{unanswered}";
        }

        if (EmptySolutions() is { } empty)
        {
            evidence = string.IsNullOrWhiteSpace(evidence) ? empty : $"{evidence}\n{empty}";
        }

        string fingerprint = Fingerprint(evidence);
        bool unchanged = history.Refutation is not null &&
                         string.Equals(history.Fingerprint, fingerprint, StringComparison.Ordinal);

        string context = unchanged
            ? "\n\nA previous panel in this run refused this work, and the verification evidence " +
              "is unchanged since then. It refused because: " +
              $"{Cap(history.Refutation!, MaxCritiqueFeedbackCharacters)}\n" +
              "This is context, not an instruction - judge the work in front of you. But if you " +
              "accept now, say what changed your mind, because nothing in the evidence did."
            : string.Empty;

        long start = Stopwatch.GetTimestamp();
        CritiqueResult critique;
        try
        {
            critique = await _critics.CritiqueAsync(
                request.Goal,
                DescribeChanges(applied),
                $"{evidence ?? "No automatic verification ran."}{context}",
                request.CriticRole,
                string.IsNullOrWhiteSpace(claim) ? null : claim,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The panel failing is not the model failing; finish rather than block on a critic.
            _logger.LogWarning(ex, "Completion critique could not run; finishing without it");
            return null;
        }

        budget.AddCriticSpend(critique.EstimatedCostUsd);
        _logger.LogInformation(
            "Completion critique for run {RunId}: {Outcome} - {Summary}",
            request.RunId,
            critique.Inconclusive ? "inconclusive" : critique.Refuted ? "REFUTED" : "accepted",
            critique.Summary);

        string? message = null;
        if (critique.Refuted)
        {
            // The recovery instruction is concrete because run f4ed50e0's was not: told the
            // evidence was thin, it added UI-test packages, wrote no test that used them, and
            // resubmitted - motion shaped like work. The screen sentence is run 216360bf's
            // scar: refuted over UI visibility, it wrote XAML-parsing layout tests that can
            // never pass in a plain test process, burned ~28 steps, and deleted them.
            //
            // And it is conditional, because the harness had learned to stop *saying* satisfiable
            // things and not yet to stop *prescribing* them. Run ae72c5ad had already launched at
            // step 12; its result was inside the very evidence this panel had just refuted; the
            // sentence told the model to launch, so at step 15 it did, and the identical string
            // came back. A remedy the harness can see is already spent is worth one step and
            // nothing else - the same correction 2026-08-11 made to the orphan-type notice.
            string screen = runtime is null
                ? "If the refutation concerns what is visible on screen, fix the layout in the XAML " +
                  "(SizeToContent, Height, margins) - tests that parse XAML text prove nothing about " +
                  "rendering. Then call launch_app: it starts the application and reports whether it " +
                  "came up and drew a window, which is the evidence a refutation like that is asking " +
                  "for. "
                : "The application has already been launched this run and the panel read that result " +
                  "before refusing, so launching it again unchanged answers nothing. What is still " +
                  "missing is what the window does: give launch_app a probe (Box=value types into a " +
                  "control, Btn! clicks it, Out? reads one back) so the answer is what the window " +
                  "showed for a given input, or change the code the refutation is about. ";
            string reasons = Cap(critique.Summary, MaxCritiqueFeedbackCharacters);
            message = _verification.CritiqueGates
                ? $"A critique panel refuted the finished work: {reasons}\n" +
                  "Address the refutation with substantive work - new or changed code, and tests that " +
                  "exercise it; adding packages without tests that use them addresses nothing. " +
                  screen + "Then reply with your final summary to finish."
                : $"Advisory review of the finished work - {Authority(lastVerification)}, and you may " +
                  $"finish as-is if you disagree: {reasons}\n" +
                  "Address only what you agree with - with code and tests, not package references " +
                  "alone. " + screen + "Then reply with your final summary to finish.";
        }

        // Remembered for the next panel, whichever way this one voted.
        history.Fingerprint = fingerprint;
        history.Refutation = critique.Refuted ? critique.Summary : history.Refutation;

        if (unchanged && !critique.Refuted)
        {
            // Not a veto - the run proceeds exactly as it would have. Logged so that "accepted on
            // unchanged evidence" is greppable across runs rather than reconstructed from two
            // transcripts by hand.
            _logger.LogInformation(
                "Completion critique for run {RunId} accepted on evidence unchanged since the previous refutation",
                request.RunId);
        }

        return new StepVerification(
            new StepVerificationRecord(
                !critique.Refuted || !_verification.CritiqueGates,
                nameof(VerificationRung.Critique),
                critique.Refuted && _verification.CritiqueGates ? nameof(VerificationRung.Critique) : null,
                Stopwatch.GetElapsedTime(start).TotalMilliseconds,
                critique.Summary,
                critique.EstimatedCostUsd)
            {
                Critique = Record(critique) with { EvidenceUnchanged = unchanged },
            },
            message);
    }

    /// <summary>
    /// The solutions this run leaves behind with no projects in them, or null when there are none.
    /// <para>
    /// A fact the harness has been able to compute for weeks and could only deliver if the model
    /// chose to ask for it. Run <c>29356042</c> is what that costs: refused a solution below the
    /// root at step 1, created one at the root at step 2, never added a project to it, and shipped
    /// a repository whose <c>dotnet test</c> runs zero tests and exits 0. <c>AbandonedIntents</c>
    /// could not catch it either - it keys on tool and operation, so the successful
    /// <c>new_solution</c> closed the refused one's entry, and <c>add_to_solution</c> never opened
    /// an entry because it was never called. Key the mechanism on the fact, not on the event that
    /// revealed it.
    /// </para>
    /// <para>
    /// A notice, never a gate, on the contract <see cref="AbandonedIntents"/> ships under.
    /// </para>
    /// </summary>
    private string? EmptySolutions()
    {
        if (_workspace?.RepoRoot is not { Length: > 0 } root)
        {
            return null;
        }

        List<string> empty =
        [
            .. GlassCoder.Tools.Verification.ProjectLocator.FindEmptySolutions(root)
                .Select(s => GlassCoder.Tools.Verification.ProjectLocator.EmptySolutionMessage(
                    Path.GetRelativePath(root, s).Replace('\\', '/'))),
        ];

        return empty.Count == 0 ? null : string.Join(" ", empty);
    }

    /// <summary>
    /// Whether this workspace holds something that can be run at all.
    /// <para>
    /// The launch-absence line is worth saying about a desktop or console application and is noise
    /// about a library, where there is nothing to launch and a critic told otherwise would be
    /// refusing work for want of evidence nobody could produce - the deadlock this panel's wording
    /// has been shaped twice to avoid.
    /// </para>
    /// </summary>
    private bool BuiltSomethingRunnable() =>
        _workspace?.RepoRoot is { Length: > 0 } root &&
        GlassCoder.Tools.Verification.ProjectLocator.AnyExecutableProject(root);

    /// <summary>
    /// The operation a call was for, where its tool has operations.
    /// <para>
    /// <c>dotnet_project new_solution</c> and <c>dotnet_project add_reference</c> are two different
    /// intents behind one tool name, and a ledger keyed on the name alone would let the second
    /// close the first's entry. Read off the arguments the model actually sent rather than from a
    /// list of which tools have operations, which would be a second place to keep in step.
    /// </para>
    /// </summary>
    private static string? Operation(ToolInvocation invocation) =>
        invocation.Arguments is not null &&
        invocation.Arguments.TryGetValue("operation", out object? operation)
            ? operation?.ToString()
            : null;

    /// <summary>
    /// What the advisory concession is allowed to call the authority: only what actually ran.
    /// <para>
    /// The clause used to say "the compiler and test results above remain the authority" whatever
    /// the climb had done. In run <c>ae72c5ad</c> the test result above was a UnitTests rung that
    /// found no test - <see cref="StepVerificationRecord.Unverified"/> was set, and the loop's own
    /// message three hundred lines earlier already said the climb verified nothing. The sentence
    /// pointed the model at the weakest evidence in the run and offered it the exit, and the model
    /// took it. This is the same correction 2026-08-09 made to the model-facing verdict; this was
    /// the one remaining place that still overstated what a climb had established.
    /// </para>
    /// </summary>
    private static string Authority(StepVerificationRecord? verification) => verification switch
    {
        // Nothing climbed at all. There is no authority to defer to, and inventing one would be
        // the same lie in the other direction.
        null => "nothing automatic verified this change",

        // The tests rung ran and found nothing to run. Naming the compiler is honest; naming
        // "test results" points at a rung that established nothing.
        { Unverified: true } => "the compiler above remains the authority - no test verified this change",

        // The climb stopped below the tests. Do not mention tests at all.
        { } reached when !ReachedTests(reached) => "the compiler above remains the authority",

        _ => "the compiler and test results above remain the authority",
    };

    /// <summary>Whether the climb got as far as a rung that runs tests.</summary>
    private static bool ReachedTests(StepVerificationRecord verification) =>
        Enum.TryParse(verification.HighestRungReached, out VerificationRung rung) &&
        rung is VerificationRung.UnitTests or VerificationRung.FullSuite;

    /// <summary>
    /// One stable string for the evidence a panel judged (workplan task 72).
    /// <para>
    /// Over the verification summary rather than the diff, deliberately. The question is not
    /// "did the code move" - it always does between panels - but "did anything the refutation
    /// asked for arrive". Run <c>d5edbc59</c>'s second panel had two changed XAML attributes and
    /// the identical set of rung results, which is precisely the case worth naming.
    /// </para>
    /// </summary>
    private static string Fingerprint(string? evidence) =>
        Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(
                Encoding.UTF8.GetBytes(evidence ?? string.Empty)));

    /// <summary>
    /// What the previous critique panel in this run concluded, carried to the next one.
    /// Mutable and run-scoped: it exists for the length of one <c>RunAsync</c>.
    /// </summary>
    private sealed class CritiqueHistory
    {
        /// <summary>The evidence the last panel judged.</summary>
        public string? Fingerprint { get; set; }

        /// <summary>Why the last refusing panel refused, when one did.</summary>
        public string? Refutation { get; set; }
    }

    /// <summary>
    /// The panel's verdict vote by vote, for the transcript. The tally alone hid the dissent:
    /// run 05e1bedb was accepted 2/3 and the one refuting reason - the line a human would
    /// actually read - was discarded with the votes.
    /// </summary>
    private static StepCritiqueRecord Record(CritiqueResult critique) => new(
        critique.Role,
        critique.Refuted,
        critique.Inconclusive,
        critique.RefutingVotes,
        critique.RespondingVotes,
        critique.UnavailableVotes,
        [.. critique.Votes.Select(v => new ReviewVoteRecord(v.Refuted, v.Confidence, v.Reason, v.Available, v.Lens))]);

    private static string Cap(string text, int limit) =>
        text.Length <= limit ? text : text[..limit] + " [...]";

    /// <summary>Renders the run's edits as diffs for the critique - "it edited Pager.cs" is not refutable.</summary>
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

    /// <summary>
    /// One message, reduced to what a transcript needs.
    /// <para>
    /// <see cref="ChatMessage.Text"/> concatenates the <see cref="TextContent"/> parts and nothing
    /// else - which for two of the four roles is nothing at all. An assistant turn that only called
    /// a tool carries a <see cref="FunctionCallContent"/>, and the tool's answer comes back as a
    /// <see cref="FunctionResultContent"/>; reading only <c>Text</c> recorded both as empty, so the
    /// replayed conversation was a column of blank <c>[assistant]</c> and <c>[tool]</c> lines and
    /// the run could not be reconstructed as the model saw it.
    /// </para>
    /// <para>
    /// Redaction still happens in <see cref="StepLogger"/>. This only decides what there is to
    /// redact.
    /// </para>
    /// </summary>
    private static TranscriptMessage Describe(ChatMessage message)
    {
        List<string>? toolCalls = null;
        List<string>? results = null;

        foreach (AIContent content in message.Contents)
        {
            switch (content)
            {
                case FunctionCallContent call:
                    (toolCalls ??= []).Add(call.Name);
                    break;

                case FunctionResultContent result when Serialise(result.Result) is { Length: > 0 } text:
                    (results ??= []).Add(SecretRedactor.Truncate(text, MaxReplayedResultCharacters)!);
                    break;

                default:
                    break;
            }
        }

        // The message's own text wins wherever it has any; a tool result only stands in where
        // there was none, which is precisely the case that recorded as blank.
        string? described = !string.IsNullOrEmpty(message.Text)
            ? message.Text
            : results is null ? null : string.Join(Environment.NewLine, results);

        return new TranscriptMessage(message.Role.Value, described, toolCalls);
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
            invocation.Summary,
            invocation.Hint)
        {
            OutcomeOk = invocation.OutcomeOk,
        };

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

    /// <summary>One climb's outcome: what to log, what - if anything - to tell the model, and the
    /// test rung itself when one ran, which the sentry counts and the record cannot carry.</summary>
    private sealed record StepVerification(
        StepVerificationRecord Record,
        string? Message,
        RungResult? Tests = null);

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
