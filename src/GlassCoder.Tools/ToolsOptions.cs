namespace GlassCoder.Tools;

/// <summary>
/// Limits on what a single tool call may return (CLAUDE.md §12, §13).
/// <para>
/// These are context-budget controls, not safety controls. A tool that hands back a 40k-line
/// file costs wall-clock on <em>every</em> later loop step, so each tool truncates and says so
/// rather than letting the window fill up silently.
/// </para>
/// </summary>
public sealed class ToolsOptions
{
    /// <summary>Configuration section these options bind from.</summary>
    public const string SectionName = "GlassCoder:Tools";

    /// <summary>Largest file <c>read_file</c> and <c>grep</c> will open.</summary>
    public int MaxFileBytes { get; set; } = 512 * 1024;

    /// <summary>Hard cap on lines returned by one <c>read_file</c> call.</summary>
    public int MaxLinesPerRead { get; set; } = 2000;

    /// <summary>Longest single line returned before it is clipped.</summary>
    public int MaxLineLength { get; set; } = 2000;

    /// <summary>Hard cap on matches returned by one <c>grep</c> call.</summary>
    public int MaxGrepMatches { get; set; } = 200;

    /// <summary>Hard cap on paths returned by one <c>glob</c> call.</summary>
    public int MaxGlobResults { get; set; } = 500;

    /// <summary>Hard cap on files visited by one search, so a wide glob cannot stall the loop.</summary>
    public int MaxFilesSearched { get; set; } = 20_000;

    /// <summary>Per-file regex timeout. Guards the loop against a catastrophically backtracking pattern.</summary>
    public int RegexTimeoutMilliseconds { get; set; } = 2000;
}
