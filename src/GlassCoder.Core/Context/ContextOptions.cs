namespace GlassCoder.Core.Context;

/// <summary>
/// Context-window policy (CLAUDE.md §12, workplan task 12).
/// <para>
/// The governing fact: effective context lags advertised context, and dilution costs
/// wall-clock on <em>every</em> loop step, not just the one that added the tokens. So the
/// window is assembled from a lean always-loaded root plus whatever the agent retrieves on
/// demand - never a dump of the doc tree.
/// </para>
/// </summary>
public sealed class ContextOptions
{
    /// <summary>Configuration section these options bind from.</summary>
    public const string SectionName = "GlassCoder:Context";

    /// <summary>
    /// Token ceiling for the assembled window. Deliberately below any served model's advertised
    /// window: the last tokens of a long window are the least reliable.
    /// </summary>
    public int MaxContextTokens { get; set; } = 48_000;

    /// <summary>Fraction of <see cref="MaxContextTokens"/> at which compaction starts.</summary>
    public double CompactionThreshold { get; set; } = 0.8;

    /// <summary>
    /// Files always loaded into the window, resolved through the path guard. Keep this short -
    /// this is the "lean root", not a documentation index.
    /// </summary>
    public IList<string> RootContextFiles { get; } = [];

    /// <summary>Token ceiling for the root context, after which it is truncated.</summary>
    public int MaxRootContextTokens { get; set; } = 6_000;

    /// <summary>Whether older turns are compacted when the budget is exceeded.</summary>
    public bool EnableCompaction { get; set; } = true;

    /// <summary>
    /// Turns kept verbatim at the tail. The most recent exchanges are the ones the model is
    /// actually reasoning over; everything before them can be summarised.
    /// </summary>
    public int KeepRecentTurns { get; set; } = 6;

    /// <summary>Characters per token used by the heuristic estimator.</summary>
    public double CharactersPerToken { get; set; } = 4.0;
}
