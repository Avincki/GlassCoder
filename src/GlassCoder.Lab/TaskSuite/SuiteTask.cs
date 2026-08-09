namespace GlassCoder.Lab.TaskSuite;

/// <summary>One task in the acceptance suite (CLAUDE.md §16, workplan task 21).</summary>
/// <param name="Id">Stable identifier, used as the task id in metrics and transcripts.</param>
/// <param name="Order">Position in the suite. The order is the point: it separates skills.</param>
/// <param name="Title">Short human name.</param>
/// <param name="Stresses">The one skill this task is meant to exercise.</param>
/// <param name="Goal">The goal handed to the agent.</param>
/// <param name="Files">The fixture repository, as relative path to content.</param>
/// <param name="StartsGreen">
/// Whether the fixture's oracle passes before the agent touches anything.
/// <para>
/// Almost every task starts red - a task whose oracle already passes measures nothing. The
/// exception is the refactoring task, whose oracle is precisely "the suite <em>stays</em> green",
/// and which would be meaningless if it started broken. Recording the expectation makes the
/// difference deliberate rather than an accident nobody noticed.
/// </para>
/// </param>
public sealed record SuiteTask(
    string Id,
    int Order,
    string Title,
    string Stresses,
    string Goal,
    IReadOnlyDictionary<string, string> Files,
    bool StartsGreen = false)
{
    /// <summary>
    /// Whether the answer is outside the repository (workplan task 60).
    /// <para>
    /// Eight of the nine fixtures are self-contained, which is what makes them hermetic - and also
    /// what makes them silent about retrieval: a perfect retrieval tool scores exactly zero on a
    /// suite whose every answer is already in the tree, and every arm in task 61 would return the
    /// same number for the same reason. This flag marks the one fixture built to need an outside
    /// answer, and the run declares it through <c>DiagnosticRetrievalSignals.Require</c> instead
    /// of waiting for a compiler to raise the right error.
    /// </para>
    /// </summary>
    public bool RequiresExternalDocs { get; init; }
}

/// <summary>The outcome of running one suite task.</summary>
/// <param name="Task">Which task.</param>
/// <param name="Passed">Whether the oracle went green.</param>
/// <param name="OracleOutput">What the oracle said, for diagnosis.</param>
/// <param name="DurationMs">Wall-clock for the oracle run.</param>
public sealed record OracleResult(SuiteTask Task, bool Passed, string OracleOutput, double DurationMs);
