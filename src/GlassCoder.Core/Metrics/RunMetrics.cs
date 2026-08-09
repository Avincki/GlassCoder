namespace GlassCoder.Core.Metrics;

/// <summary>
/// The Section 11 performance indicators for one run (CLAUDE.md §11, workplan task 20).
/// <para>
/// This record <em>is</em> the deliverable. "Measure before you believe" only means anything if
/// the measurements are written down in a comparable shape, so every field here is defined once,
/// emitted as JSONL, and compared across runs and across ablation arms.
/// </para>
/// </summary>
public sealed record RunMetrics
{
    /// <summary>Run identifier, matching the transcript.</summary>
    public required string RunId { get; init; }

    /// <summary>Task identifier - the unit that is comparable across runs.</summary>
    public required string TaskId { get; init; }

    /// <summary>Served role that drove the run.</summary>
    public required string Role { get; init; }

    /// <summary>
    /// Which attempt at the task this run was. pass@1 is the rate over attempt 1, so a store
    /// that omits this cannot separate a first solve from a second one.
    /// </summary>
    public int Attempt { get; init; } = 1;

    /// <summary>The critic role the run was submitted with, when one was named.</summary>
    public string? CriticRole { get; init; }

    /// <summary>Who recorded this: the loop itself, or a checkpoint or ablation arm above it.</summary>
    public required string Source { get; init; }

    /// <summary>When the record was written.</summary>
    public required DateTimeOffset RecordedAt { get; init; }

    /// <summary>Why the loop stopped.</summary>
    public required string StopReason { get; init; }

    /// <summary>
    /// Whether the task's oracle test passed. Null when nothing graded the run - the loop cannot
    /// know, only a task suite can (CLAUDE.md §16).
    /// </summary>
    public bool? OraclePassed { get; init; }

    /// <summary>
    /// pass@1: the oracle went green on the first completed run. Null until an oracle has run.
    /// </summary>
    public bool? PassAtOne => OraclePassed;

    /// <summary>Steps-to-solve.</summary>
    public required int Steps { get; init; }

    /// <summary>Prompt tokens.</summary>
    public required long InputTokens { get; init; }

    /// <summary>Completion tokens.</summary>
    public required long OutputTokens { get; init; }

    /// <summary>Tokens-to-solve.</summary>
    public required long TotalTokens { get; init; }

    /// <summary>Wall-clock for the run. The local cost function - always read next to pass@1.</summary>
    public required double WallClockMs { get; init; }

    /// <summary>Estimated spend.</summary>
    public required decimal CostUsd { get; init; }

    /// <summary>Tool calls the model issued.</summary>
    public required int ToolCallsTotal { get; init; }

    /// <summary>Tool calls that parsed and executed.</summary>
    public required int ToolCallsValid { get; init; }

    /// <summary>Tool-call validity rate - the best early diagnostic for a weak model.</summary>
    public double ToolCallValidityRate => ToolCallsTotal == 0 ? 1d : (double)ToolCallsValid / ToolCallsTotal;

    /// <summary>
    /// Retrieval calls the policy admitted (workplan task 61).
    /// <para>
    /// These three are what make a retrieval arm readable. Without them the comparison is pass@1
    /// against pass@1, which cannot distinguish an arm whose tool was never called from one whose
    /// answers did not help - and on a greenfield scaffold, where nothing external is in question,
    /// blocked should greatly exceed allowed and a large allowed count is itself the finding.
    /// </para>
    /// </summary>
    public int RetrievalCallsAllowed { get; init; }

    /// <summary>Retrieval calls the policy refused, by <c>ToolErrorCodes</c> value.</summary>
    public IReadOnlyDictionary<string, int> RetrievalCallsBlocked { get; init; } =
        new Dictionary<string, int>(StringComparer.Ordinal);

    /// <summary>Characters retrieval put into the conversation, which is the context it cost.</summary>
    public int RetrievalCharsReturned { get; init; }

    /// <summary>Edits applied.</summary>
    public required int Edits { get; init; }

    /// <summary>Edits followed by a failing build.</summary>
    public required int EditsWithCompileErrors { get; init; }

    /// <summary>Compile-error rate per edit - the sharpest cheap quality signal.</summary>
    public double CompileErrorRatePerEdit => Edits == 0 ? 0d : (double)EditsWithCompileErrors / Edits;

    /// <summary>Builds run.</summary>
    public required int Builds { get; init; }

    /// <summary>Builds that failed.</summary>
    public required int BuildFailures { get; init; }

    /// <summary>Test runs.</summary>
    public required int TestRuns { get; init; }

    /// <summary>Test runs that were red.</summary>
    public required int TestFailures { get; init; }

    /// <summary>Edits taken to get back to a compiling state after the most recent break.</summary>
    public required int EditsToGreen { get; init; }

    /// <summary>Times the agent was in a failing state and could have recovered.</summary>
    public required int RecoveryOpportunities { get; init; }

    /// <summary>Times it actually did.</summary>
    public required int Recoveries { get; init; }

    /// <summary>Recovery rate - did the agent recover after a failing test or a bad edit.</summary>
    public double RecoveryRate =>
        RecoveryOpportunities == 0 ? 1d : (double)Recoveries / RecoveryOpportunities;

    /// <summary>Diagnostics the compiler reported.</summary>
    public required int DiagnosticsReported { get; init; }

    /// <summary>Diagnostics the summariser actually showed the model.</summary>
    public required int DiagnosticsShown { get; init; }

    /// <summary>Cascade ratio - errors reported against root causes shown. Validates the summariser.</summary>
    public double CascadeRatio => DiagnosticsShown == 0 ? 0d : (double)DiagnosticsReported / DiagnosticsShown;

    /// <summary>Wall-clock per solved task. Null when the task was not solved.</summary>
    public double? WallClockPerSolvedTaskMs => OraclePassed == true ? WallClockMs : null;

    /// <summary>Cost per solved task. Null when the task was not solved.</summary>
    public decimal? CostPerSolvedTask => OraclePassed == true ? CostUsd : null;

    /// <summary>Commit the run was taken against, for provenance (workplan task 35).</summary>
    public string? RepoCommit { get; init; }

    /// <summary>Hash of the effective configuration - what identifies an ablation arm.</summary>
    public string? ConfigHash { get; init; }

    /// <summary>
    /// Whether the always-loaded context was at least as new as the code. The Phase 6 watch
    /// metric is pass@1 split on exactly this flag.
    /// </summary>
    public bool? ContextFresh { get; init; }

    /// <summary>Name of the ablation arm, when the run was part of one.</summary>
    public string? Arm { get; init; }
}
