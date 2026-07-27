namespace GlassCoder.Core.Diagnostics;

/// <summary>One critic's verdict, as it was recorded (workplan task 37).</summary>
/// <param name="Refuted">Whether this critic thought the change was wrong.</param>
/// <param name="Confidence">How sure it was, 0 to 1.</param>
/// <param name="Reason">Why, in the critic's own words.</param>
/// <param name="Available">Whether the critic actually judged. False is a failure to judge, not an acceptance.</param>
public sealed record ReviewVoteRecord(
    bool Refuted,
    double Confidence,
    string Reason,
    bool Available);

/// <summary>
/// The post-run review as a transcript record (workplan task 37).
/// <para>
/// <see cref="RunRecord"/> closes before the review runs - the review judges the finished run,
/// so it cannot be a field on the record of the run it judges. It is its own record, appended
/// after the run's, carrying the critique verbatim: an opinion that shaped a retry and left no
/// trace would be the one thing on that surface that cannot be reconstructed.
/// </para>
/// </summary>
public sealed record ReviewRecord
{
    /// <summary>The run that was reviewed.</summary>
    public required string RunId { get; init; }

    /// <summary>Task identifier, for cross-run comparison.</summary>
    public required string TaskId { get; init; }

    /// <summary>Which attempt at the task the reviewed run was.</summary>
    public int Attempt { get; init; } = 1;

    /// <summary>The critic role that judged, so the transcript records which oracle spoke.</summary>
    public required string CriticRole { get; init; }

    /// <summary>Whether the panel refuted the run's work.</summary>
    public required bool Refuted { get; init; }

    /// <summary>Whether too little of the panel voted to conclude anything.</summary>
    public required bool Inconclusive { get; init; }

    /// <summary>What the panel said, as shown to the human.</summary>
    public required string Summary { get; init; }

    /// <summary>Every verdict, including the minority and the critics that never arrived.</summary>
    public required IReadOnlyList<ReviewVoteRecord> Votes { get; init; }

    /// <summary>How many critics actually judged.</summary>
    public int RespondingVotes { get; init; }

    /// <summary>How many critics could not be reached.</summary>
    public int UnavailableVotes { get; init; }

    /// <summary>Prompt tokens across the panel.</summary>
    public long InputTokens { get; init; }

    /// <summary>Completion tokens across the panel.</summary>
    public long OutputTokens { get; init; }

    /// <summary>What the second opinion cost, at the critic role's own prices.</summary>
    public decimal EstimatedCostUsd { get; init; }

    /// <summary>Wall-clock for the review.</summary>
    public double DurationMs { get; init; }

    /// <summary>When the review was recorded.</summary>
    public required DateTimeOffset RecordedAt { get; init; }
}
