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
/// <para>
/// The failure rule is per-signature, not per-consecutive-step. Run 5c071f37 interleaved every
/// refused write with a green build or a re-read - rational checking, not noise - so a
/// consecutive counter never reached its threshold in ten refusals. Failures are keyed by tool
/// and the <em>first line</em> of the error, because the later lines legitimately vary between
/// identical failures (the refusal's own strike countdown, a wobbling diagnostics total):
/// prose that synthesis writes must never be prose that detection keys on. Counts accumulate
/// until a change is applied - the one event that honestly resets the argument.
/// </para>
/// </summary>
internal sealed class RunProgressSentry
{
    /// <summary>Identical failures before the model is told it is repeating itself.</summary>
    private const int NudgeAfterIdenticalFailures = 3;

    /// <summary>Consecutive stalled steps before the same telling-off.</summary>
    private const int NudgeAfterStalledSteps = 3;

    /// <summary>Reads of one file, with nothing applied between them, before the model is told
    /// the file has not changed. Past this count they stop counting as novel work.</summary>
    private const int NudgeAfterSamePathReads = 4;

    /// <summary>The tools whose 'path' argument names a file whose content they return. Only
    /// these feed the same-path counter: a directory-shaped path re-queried with new patterns
    /// (grep, glob) is exploration, not a loop.</summary>
    private static readonly HashSet<string> PathReadTools = new(StringComparer.Ordinal) { "read_file" };

    private readonly HashSet<string> _seenCalls = new(StringComparer.Ordinal);

    private readonly Dictionary<string, int> _failureCounts = new(StringComparer.Ordinal);
    private readonly HashSet<string> _nudgedFailures = new(StringComparer.Ordinal);
    private string? _failureToNudge;

    private readonly Dictionary<string, int> _pathReads = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _nudgedPathReads = new(StringComparer.OrdinalIgnoreCase);
    private string? _pathReadToNudge;

    /// <summary>Identical failing test results before the model is told its edits are not landing.</summary>
    private const int NudgeAfterRepeatedTestFailures = 3;

    private readonly Dictionary<string, (string Line, int Count)> _testOutcomes = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _nudgedTestOutcomes = new(StringComparer.OrdinalIgnoreCase);
    private string? _testFailureToNudge;

    private int _stalledSteps;
    private string? _lastRepeatedCall;
    private bool _nudgedAboutStall;

    private bool _lastVerificationFailed;
    private string? _lastFailedRung;
    private bool _completionChallenged;
    private bool _noticeOutstanding;
    private bool _noticeChallenged;

    /// <summary>Feeds one step's tool calls and whether the step applied any change.</summary>
    public void ObserveStep(IReadOnlyList<ToolInvocation> invocations, bool changesApplied)
    {
        _failureToNudge = null;
        _pathReadToNudge = null;
        _testFailureToNudge = null;

        // Tracked before the applied-change reset, and deliberately not reset by it: runs
        // ea9a1f66 and 216360bf edited between every one of their identical "N of M tests
        // failed" results, and each edit honestly reset every other counter while fixing
        // nothing. A test outcome is only superseded by a different test outcome for the same
        // target - a green run, or a different failure.
        ObserveTestOutcomes(invocations);

        if (changesApplied)
        {
            _seenCalls.Clear();
            _stalledSteps = 0;
            _nudgedAboutStall = false;
            _failureCounts.Clear();
            _nudgedFailures.Clear();
            _pathReads.Clear();
            _nudgedPathReads.Clear();
            return;
        }

        foreach (ToolInvocation invocation in invocations)
        {
            // A Succeeded call whose outcome failed - a relayed dotnet command that exited
            // non-zero - is a failure here, whatever the status says. Run 4b562c91 sent the
            // same misshapen add_to_solution five times because every relay read as success.
            if ((invocation.Status == ToolCallStatus.Succeeded && invocation.OutcomeOk) ||
                FailureKey(invocation) is not { } key)
            {
                continue;
            }

            int count = _failureCounts[key] = _failureCounts.GetValueOrDefault(key) + 1;
            if (count == NudgeAfterIdenticalFailures && _nudgedFailures.Add(key))
            {
                _failureToNudge = key;
            }
        }

        bool anySucceeded = false;
        bool anyNovel = false;
        foreach (ToolInvocation invocation in invocations)
        {
            if (invocation.Status != ToolCallStatus.Succeeded || !invocation.OutcomeOk)
            {
                continue;
            }

            anySucceeded = true;

            // The same file re-read with a wobbling window is one loop, not many novel calls.
            // Run c5eb67f6 read one test file thirteen times - offset 70, 75, 76, maxLines 20,
            // 25, 30 - and every variation minted a fresh fingerprint, so the stall counter
            // never armed while the run read itself to the token limit. Reads of one unchanged
            // path are counted as themselves, whatever the window; past the nudge they stop
            // counting as novelty.
            bool wornPath = false;
            if (PathReadTools.Contains(invocation.ToolName) && PathOf(invocation) is { } path)
            {
                string pathKey = $"{invocation.ToolName}:{path}";
                int reads = _pathReads[pathKey] = _pathReads.GetValueOrDefault(pathKey) + 1;
                wornPath = reads > NudgeAfterSamePathReads;
                if (reads == NudgeAfterSamePathReads && _nudgedPathReads.Add(pathKey))
                {
                    _pathReadToNudge = path;
                }
            }

            string fingerprint = DescribeCall(invocation);
            if (_seenCalls.Add(fingerprint) && !wornPath)
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
    public void ObserveVerification(bool passed, string? failedRung, bool noticed = false)
    {
        _lastVerificationFailed = !passed;
        _lastFailedRung = failedRung;
        if (passed)
        {
            // A tree that went green earns back the right to be challenged if it goes red again.
            _completionChallenged = false;
        }

        // A notice is not cleared by the next green climb - it is cleared by the next climb that
        // has nothing to say. Otherwise the one rung that raised it is outvoted by every rung
        // after it, which is how it came to move nothing in run 4c7de12b.
        _noticeOutstanding = noticed;
    }

    /// <summary>The identical-failure nudge, exactly once per failure signature.</summary>
    public string? FailureNudge() =>
        _failureToNudge is not null
            ? $"That call has now failed the same way {NudgeAfterIdenticalFailures} times, counting attempts " +
              $"on either side of other work: {_failureToNudge}. Repeating it will not work. Change approach - " +
              "read the file again and quote from what it returns, use create_file with overwrite: true to " +
              "replace the whole file, or work on something else."
            : null;

    /// <summary>Feeds the test outcomes of one step into the repeated-failure tracker.</summary>
    private void ObserveTestOutcomes(IReadOnlyList<ToolInvocation> invocations)
    {
        foreach (ToolInvocation invocation in invocations)
        {
            if (invocation.Status != ToolCallStatus.Succeeded ||
                !string.Equals(invocation.ToolName, "run_tests", StringComparison.Ordinal) ||
                invocation.Summary is not { } summary)
            {
                continue;
            }

            string key = PathOf(invocation) ?? "(default)";

            // A green run ends the streak and re-arms the nudge for a later, different fight.
            if (invocation.OutcomeOk)
            {
                _testOutcomes.Remove(key);
                _nudgedTestOutcomes.Remove(key);
                continue;
            }

            int end = summary.IndexOf('\n');
            string line = (end < 0 ? summary : summary[..end]).TrimEnd('\r');

            int count = _testOutcomes.TryGetValue(key, out (string Line, int Count) seen) &&
                string.Equals(seen.Line, line, StringComparison.Ordinal)
                ? seen.Count + 1
                : 1;
            _testOutcomes[key] = (line, count);

            if (count == NudgeAfterRepeatedTestFailures && _nudgedTestOutcomes.Add(key))
            {
                _testFailureToNudge = line;
            }
        }
    }

    /// <summary>The repeated failing-test nudge, exactly once per streak.</summary>
    public string? TestFailureNudge() =>
        _testFailureToNudge is not null
            ? $"run_tests has returned the same failing result {NudgeAfterRepeatedTestFailures} times in a " +
              $"row despite your edits in between: {_testFailureToNudge} Your changes are not reaching the " +
              "failure. Read the failing test and the code it exercises before editing again, rewrite the " +
              "file whole with create_file overwrite: true, or delete the failing approach and solve it " +
              "another way."
            : null;

    /// <summary>The same-path read nudge, exactly once per worn path.</summary>
    public string? PathReadNudge() =>
        _pathReadToNudge is not null
            ? $"You have now read {_pathReadToNudge} {NudgeAfterSamePathReads} times without changing " +
              "anything. The file has not changed between reads, so the answers cannot differ. Read " +
              "the exact region you need in one call (startLine and maxLines, or outline: true for " +
              "a C# file's shape), or act on what you already know - for a file this size, " +
              "create_file with overwrite: true and the full corrected content is often the shortest path."
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
        if (limits.MaxIdenticalToolFailures > 0 && _failureCounts.Count > 0)
        {
            KeyValuePair<string, int> worst = _failureCounts.MaxBy(pair => pair.Value);
            if (worst.Value >= limits.MaxIdenticalToolFailures)
            {
                return (AgentStopReason.RepeatedToolFailure,
                    $"The same call failed {worst.Value} times with no change applied in between: {worst.Key}");
            }
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
    /// The push-back when the model stops over a green suite that verified the wrong thing -
    /// returned once, on the same terms as <see cref="ChallengeCompletion"/>.
    /// <para>
    /// Deliberately not a gate. This repository has paid twice for gates that would not concede
    /// (<c>5c071f37</c>, <c>a408b61b</c>), and a suite notice is weaker evidence than a red tree:
    /// it says the tests may be testing the wrong thing, which the model is entitled to disagree
    /// with. One sentence it has to answer, then the run finishes either way and the record says
    /// what happened.
    /// </para>
    /// </summary>
    public string? ChallengeNotice()
    {
        if (!_noticeOutstanding || _noticeChallenged || _lastVerificationFailed)
        {
            return null;
        }

        _noticeChallenged = true;
        return "Before you stop: the last test run passed but the verification raised a notice about " +
            "what it actually covered - re-read it above. Either address it, or say in your summary " +
            "why the suite is adequate as it stands.";
    }

    /// <summary>What the run record should say when a completion goes through over a live notice.</summary>
    public string? NoticeCaveat() =>
        _noticeOutstanding ? "Completed over an unanswered test-suite notice." : null;

    /// <summary>
    /// A failure as the counter sees it: the tool and the first line of its error. The first
    /// line only, because that is the stable core - the later lines legitimately vary between
    /// identical failures (the verification refusal appends its strike countdown, and a
    /// diagnostics total can wobble while the refusal stays the same refusal), and keying on
    /// them made every repeat look novel.
    /// </summary>
    private static string? FailureKey(ToolInvocation invocation)
    {
        // A soft failure has no ErrorMessage - the summary is its account of the refusal, and
        // its first line ("dotnet add_reference failed with exit 1.") is stable the same way.
        string? message = invocation.ErrorMessage
            ?? (!invocation.OutcomeOk ? invocation.Summary : null);
        if (message is null)
        {
            return null;
        }

        int end = message.IndexOf('\n');
        return $"{invocation.ToolName}: {(end < 0 ? message : message[..end]).TrimEnd('\r')}";
    }

    /// <summary>The 'path' argument as a string, when the call carried one.</summary>
    private static string? PathOf(ToolInvocation invocation) =>
        invocation.Arguments is { } arguments &&
        arguments.TryGetValue("path", out object? value)
            ? value?.ToString()
            : null;

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
