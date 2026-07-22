namespace GlassCoder.Core.Diagnostics;

/// <summary>
/// Logging and redaction settings (CLAUDE.md §9, §13; workplan task 5).
/// </summary>
public sealed class LoggingOptions
{
    /// <summary>Configuration section these options bind from.</summary>
    public const string SectionName = "GlassCoder:Logging";

    /// <summary>Directory for log files, relative to the working directory unless absolute.</summary>
    public string Directory { get; set; } = "logs";

    /// <summary>
    /// Machine-readable sink. One JSON object per line; the trailing dash is where Serilog
    /// inserts the rolling date.
    /// </summary>
    public string JsonlFileName { get; set; } = "glasscoder-.jsonl";

    /// <summary>Human-readable sink.</summary>
    public string TextFileName { get; set; } = "glasscoder-.log";

    /// <summary>Whether to also write the human-readable view to the console.</summary>
    public bool Console { get; set; } = true;

    /// <summary>Minimum level: Verbose, Debug, Information, Warning, Error or Fatal.</summary>
    public string MinimumLevel { get; set; } = "Information";

    /// <summary>Rolling files to keep.</summary>
    public int RetainedFileCountLimit { get; set; } = 31;

    /// <summary>
    /// Whether prompts, responses, and tool results may be written to the log store. Turning
    /// this off keeps the step skeleton - indexes, tool names, statuses, tokens, latencies -
    /// and drops every piece of source content (CLAUDE.md §9).
    /// </summary>
    public bool LogSourceContent { get; set; } = true;

    /// <summary>Longest single logged text value before it is truncated.</summary>
    public int MaxLoggedTextLength { get; set; } = 16_000;

    /// <summary>
    /// Property names whose values are always replaced with a redaction marker. Matched as
    /// whole names, case-insensitively - substring matching would swallow legitimate
    /// properties such as <c>TotalTokens</c>.
    /// </summary>
    public IList<string> RedactedPropertyNames { get; } =
    [
        "apikey",
        "api_key",
        "authorization",
        "password",
        "secret",
        "token",
        "credential",
    ];
}
