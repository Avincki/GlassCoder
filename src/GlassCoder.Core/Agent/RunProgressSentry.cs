using GlassCoder.Tools.Registry;

namespace GlassCoder.Core.Agent;

/// <summary>
/// Watches a run for the shapes of not-making-progress the budgets cannot see, and owns what to
/// say about them (workplan task 45; runs e8f9186a, d18c0e57, 21f25fea).
/// <para>
/// Three shapes, one class: the same call <em>failing</em> the same way over and over (17
/// consecutive steps on one edit while validity read 100%); whole steps made of verbatim
/// <em>successful</em> repeats of earlier calls (25 read-only steps of byte-identical answers);
/// and a model declaring itself done over a tree whose last verification failed (a run that
/// "Completed" with eleven red builds). They lived as loose flags in the loop body until there
/// were ten of them - this class is the same logic with a name, so the loop reads as observe →
/// think → act → verify → sentry.
/// </para>
/// <para>
/// The stall rule is deliberately per-step, not per-call: a step counts as stalled only when
/// <em>every</em> successful call in it repeats an earlier one and nothing was applied. A
/// habitual list_changes beside a novel read is not a stall, and neither is re-reading an
/// unchanged file after compaction dropped it - both were false positives of a per-call count.
/// Memory clears whenever a change is applied, because a changed workspace can legitimately be
/// re-inspected.
/// </para>
/// </summary>
internal sealed class RunProgressSentry
{
    /// <summary>Identical failures before the model is told it is repeating itself.</summary>
    private const int NudgeAfterIdenticalFailures = 3;

    /// <summary>Consecutive stalled steps before the same telling-off.</summary>
    private const int NudgeAfterStalledSteps = 3;

    private readonly HashSet<string> _seenCalls = new(StringComparer.Ordinal);

    private string? _lastFailure;
    private int _identicalFailures;

    private int _stalledSteps;
    private string? _lastRepeatedCall;
    private bool _nudgedAboutStall;

    private bool _lastVerificationFailed;
    private string? _lastFailedRung;
    private bool _completionChallenged;

    /// <summary>Feeds one step's tool calls and whether the step applied any change.</summary>
    public void ObserveStep(IReadOnlyList<ToolInvocation> invocations, bool changesApplied)
    {
        string? failure = IdenticalFailure(invocations);
        _identicalFailures = failure is not null && failure == _lastFailure
            ? _identicalFailures + 1
            : failure is null ? 0 : 1;
        _lastFailure = failure;

        if (changesApplied)
        {
            _seenCalls.Clear();
            _stalledSteps = 0;
            _nudgedAboutStall = false;
            return;
        }

        bool anySucceeded = false;
        bool anyNovel = false;
        foreach (ToolInvocation invocation in invocations)
        {
            if (invocation.Status != ToolCallStatus.Succeeded)
            {
                continue;
            }

            anySucceeded = true;
            string fingerprint = DescribeCall(invocation);
            if (_seenCalls.Add(fingerprint))
            {
                anyNovel = true;
            }
            else
            {
                _lastRepeatedCall = fingerprint;
            }
        }

        // A step of pure failures leaves the stall count alone: the failure counter owns those,
        // and a run alternating one repeated read with one failing edit is still going nowhere.
        if (anySucceeded)
        {
            _stalledSteps = anyNovel ? 0 : _stalledSteps + 1;
        }
    }

    /// <summary>Feeds the outcome of a post-step verification climb.</summary>
    public void ObserveVerification(bool passed, string? failedRung)
    {
        _lastVerificationFailed = !passed;
        _lastFailedRung = failedRung;
        if (passed)
        {
            // A tree that went green earns back the right to be challenged if it goes red again.
            _completionChallenged = false;
        }
    }

    /// <summary>The identical-failure nudge, exactly once per threshold crossing.</summary>
    public string? FailureNudge() =>
        _identicalFailures == NudgeAfterIdenticalFailures
            ? $"That call has now failed the same way {_identicalFailures} times: {_lastFailure}. Repeating it " +
              "will not work. Change approach - read the file again and quote from what it returns, use " +
              "create_file with overwrite: true to replace the whole file, or work on something else."
            : null;

    /// <summary>The stall nudge, once per stretch of no-progress.</summary>
    public string? StallNudge()
    {
        if (_nudgedAboutStall || _stalledSteps < NudgeAfterStalledSteps)
        {
            return null;
        }

        _nudgedAboutStall = true;
        return $"For the last {_stalledSteps} steps, every call you made repeated an earlier call verbatim and " +
            $"received the identical answer (latest: {_lastRepeatedCall}). Asking again cannot add information - " +
            "the answer will not change until you change the workspace. Act on what you already know and take a " +
            "concrete step toward the goal, inside a writable root.";
    }

    /// <summary>
    /// Whether the run should stop for lack of progress, and how to describe it. Null while the
    /// run is still moving or the limits are switched off.
    /// </summary>
    public (AgentStopReason Reason, string Error)? StopVerdict(AgentOptions limits)
    {
        if (limits.MaxIdenticalToolFailures > 0 && _identicalFailures >= limits.MaxIdenticalToolFailures)
        {
            return (AgentStopReason.RepeatedToolFailure,
                $"The same call failed {_identicalFailures} times running: {_lastFailure}");
        }

        if (limits.MaxStalledSteps > 0 && _stalledSteps >= limits.MaxStalledSteps)
        {
            return (AgentStopReason.Stalled,
                $"The run stalled: for {_stalledSteps} consecutive steps every call repeated an earlier one " +
                $"with the identical answer (latest: {_lastRepeatedCall}).");
        }

        return null;
    }

    /// <summary>
    /// The push-back when the model stops talking over a red tree - returned once; a second
    /// attempt to stop is allowed through and <see cref="CompletionCaveat"/> records it.
    /// </summary>
    public string? ChallengeCompletion()
    {
        if (!_lastVerificationFailed || _completionChallenged)
        {
            return null;
        }

        _completionChallenged = true;
        return $"Do not stop yet: the last automatic verification FAILED at {_lastFailedRung ?? "an early rung"}, " +
            "so the tree does not verify. Fix the reported problems and confirm with a build, or state " +
            "explicitly what is still broken and why it cannot be fixed in this run.";
    }

    /// <summary>What the run record should say when a completion goes through over a red tree.</summary>
    public string? CompletionCaveat() =>
        _lastVerificationFailed
            ? $"Completed while the last verification was still failing at {_lastFailedRung ?? "an early rung"}."
            : null;

    /// <summary>
    /// The way this step failed, when it failed one way and made no progress at all. Null the
    /// moment anything succeeds, because a step that achieved something is not a step stuck in
    /// a loop - even if one of its other calls failed.
    /// </summary>
    private static string? IdenticalFailure(IReadOnlyList<ToolInvocation> invocations)
    {
        if (invocations.Count == 0 || invocations.Any(i => i.Status == ToolCallStatus.Succeeded))
        {
            return null;
        }

        string? first = Key(invocations[0]);
        return first is not null && invocations.All(i => Key(i) == first) ? first : null;

        static string? Key(ToolInvocation invocation) =>
            invocation.ErrorMessage is { } message ? $"{invocation.ToolName}: {message}" : null;
    }

    /// <summary>
    /// A call as the repeat tracker sees it: tool, arguments and answer, all of it. The answer
    /// is part of the identity on purpose - the same question against a changed workspace gets
    /// a different summary and reads as novel.
    /// </summary>
    private static string DescribeCall(ToolInvocation invocation)
    {
        string arguments = invocation.Arguments is null
            ? string.Empty
            : string.Join(", ", invocation.Arguments
                .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => $"{pair.Key}={pair.Value}"));

        return $"{invocation.ToolName}({arguments}) => {invocation.Summary ?? invocation.ErrorMessage ?? "(no summary)"}";
    }
}
