namespace GlassCoder.Tools.Verification;

/// <summary>
/// Settings for the compiler-feedback rungs (CLAUDE.md §8, workplan tasks 14-15).
/// </summary>
public sealed class VerificationOptions
{
    /// <summary>Configuration section these options bind from.</summary>
    public const string SectionName = "GlassCoder:Verification";

    /// <summary>Entries the summariser shows the model before it stops listing.</summary>
    public int MaxSummarisedDiagnostics { get; set; } = 10;

    /// <summary>Whether the summary may include warnings once there are no errors to report.</summary>
    public bool IncludeWarningsInSummary { get; set; } = true;

    /// <summary>Source files one in-memory compilation will parse before giving up.</summary>
    public int MaxCompileFiles { get; set; } = 4000;

    /// <summary>
    /// Extra directories scanned for reference assemblies. The in-memory rung otherwise compiles
    /// against the harness's own runtime, which is approximate by construction.
    /// </summary>
    public IList<string> ExtraReferenceDirectories { get; } = [];

    /// <summary>Whether <c>edit_file</c> compiles the project in memory before persisting a change.</summary>
    public bool VerifyEditsBeforeWrite { get; set; } = true;

    /// <summary>
    /// Whether an edit that introduces a <em>new</em> compile error is refused rather than
    /// written. Pre-existing errors never block an edit - the agent is usually editing
    /// precisely because the project is broken.
    /// </summary>
    public bool RejectEditsThatBreakTheBuild { get; set; } = true;

    /// <summary>
    /// The identical attempt at one file on which the gate stands aside: that attempt is written
    /// with a warning and the build tool adjudicates. Zero or less keeps refusing without limit.
    /// <para>
    /// Run 5c071f37 refused the same WPF code-behind ten times with the same five errors while
    /// every build in between stayed green, and the run spent itself to the token limit. The
    /// analyzer's blind spot that day is fixed, but the next blind spot will present the same
    /// way - so the gate concedes a fixed argument. It concedes <em>on</em> this attempt rather
    /// than after it because run a408b61b, promised "after 3 the write will be allowed",
    /// reasonably never resubmitted a thrice-refused file and shipped no tests.
    /// </para>
    /// </summary>
    public int MaxIdenticalRefusals { get; set; } = 3;
}
