using System.ComponentModel;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.Options;

namespace GlassCoder.Tools.Verification;

/// <summary>What the model is told about a failed rung.</summary>
/// <param name="TotalErrors">True total of errors, however few are listed.</param>
/// <param name="TotalWarnings">True total of warnings.</param>
/// <param name="Entries">The diagnostics that were kept, sorted by file position.</param>
/// <param name="Truncated">Whether entries were withheld.</param>
/// <param name="FilesAffected">Distinct files with at least one error.</param>
/// <param name="Text">The rendering handed to the model.</param>
public sealed record DiagnosticSummary(
    [property: Description("Total number of errors, including any not listed here.")] int TotalErrors,
    [property: Description("Total number of warnings, including any not listed here.")] int TotalWarnings,
    [property: Description("The diagnostics that were kept, earliest file position first.")] IReadOnlyList<CodeDiagnostic> Entries,
    [property: Description("True when more diagnostics exist than are listed.")] bool Truncated,
    [property: Description("Number of distinct files with at least one error.")] int FilesAffected,
    [property: Description("Human- and model-readable rendering of this summary.")] string Text)
{
    /// <summary>Whether the underlying rung passed.</summary>
    public bool Ok => TotalErrors == 0;

    /// <summary>
    /// Cascade ratio: diagnostics reported per diagnostic shown (CLAUDE.md §11). A high ratio is
    /// the summariser earning its place; a ratio near one means the cascade was real.
    /// </summary>
    public double CascadeRatio => Entries.Count == 0 ? 0d : (double)TotalErrors / Entries.Count;
}

/// <summary>
/// Turns a pile of compiler diagnostics into something a model can act on
/// (CLAUDE.md §8.2, workplan task 15).
/// <para>
/// <b>Mandatory before any diagnostic reaches a model.</b> One bad edit can produce hundreds of
/// errors that are all the same error; handing those over raw consumes the entire context window
/// and buries the root cause. So: first error per file, deduplicated by code, capped, sorted by
/// position - earliest first, because the earliest error is usually the cause of the rest - and
/// the true total always reported so the agent knows the real scale of what it broke.
/// </para>
/// </summary>
public sealed class DiagnosticSummarizer
{
    private readonly VerificationOptions _options;

    /// <summary>Creates the summariser.</summary>
    public DiagnosticSummarizer(IOptions<VerificationOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
    }

    /// <summary>Summarises a rung's report.</summary>
    public DiagnosticSummary Summarise(DiagnosticReport report, string? title = null)
    {
        ArgumentNullException.ThrowIfNull(report);
        return Summarise(report.Diagnostics, title, report.FailureReason);
    }

    /// <summary>Summarises a diagnostic list.</summary>
    public DiagnosticSummary Summarise(
        IReadOnlyList<CodeDiagnostic> diagnostics,
        string? title = null,
        string? failureReason = null)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);

        int totalErrors = 0;
        int totalWarnings = 0;
        foreach (CodeDiagnostic diagnostic in diagnostics)
        {
            if (diagnostic.Severity == CodeSeverity.Error)
            {
                totalErrors++;
            }
            else if (diagnostic.Severity == CodeSeverity.Warning)
            {
                totalWarnings++;
            }
        }

        // Errors first; warnings only fill the remaining space, and only once nothing is broken.
        List<CodeDiagnostic> candidates = [.. Select(diagnostics, CodeSeverity.Error)];
        if (candidates.Count == 0 && _options.IncludeWarningsInSummary)
        {
            candidates.AddRange(Select(diagnostics, CodeSeverity.Warning));
        }

        int cap = Math.Max(1, _options.MaxSummarisedDiagnostics);
        bool truncated = candidates.Count > cap;
        List<CodeDiagnostic> entries = [.. candidates.Take(cap)];

        int filesAffected = diagnostics
            .Where(d => d.Severity == CodeSeverity.Error && d.FilePath is not null)
            .Select(d => d.FilePath!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();

        string text = Render(title, failureReason, totalErrors, totalWarnings, entries, truncated, filesAffected);
        return new DiagnosticSummary(totalErrors, totalWarnings, entries, truncated, filesAffected, text);
    }

    /// <summary>
    /// Keeps the first diagnostic per file, then deduplicates by code across files, then sorts
    /// by position. The order matters: dropping duplicates before picking per-file firsts would
    /// hide a real second occurrence of a common error in another file.
    /// </summary>
    private static IEnumerable<CodeDiagnostic> Select(IReadOnlyList<CodeDiagnostic> diagnostics, CodeSeverity severity)
    {
        IEnumerable<CodeDiagnostic> ofSeverity = diagnostics.Where(d => d.Severity == severity);

        IEnumerable<CodeDiagnostic> firstPerFile = ofSeverity
            .GroupBy(d => d.FilePath ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderBy(d => d.Line).ThenBy(d => d.Column).First());

        return firstPerFile
            .GroupBy(d => d.Id, StringComparer.Ordinal)
            .Select(group => group.OrderBy(d => d.FilePath, StringComparer.OrdinalIgnoreCase)
                                  .ThenBy(d => d.Line)
                                  .First())
            .OrderBy(d => d.FilePath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(d => d.Line)
            .ThenBy(d => d.Column);
    }

    private static string Render(
        string? title,
        string? failureReason,
        int totalErrors,
        int totalWarnings,
        List<CodeDiagnostic> entries,
        bool truncated,
        int filesAffected)
    {
        CultureInfo culture = CultureInfo.InvariantCulture;
        StringBuilder text = new();

        if (!string.IsNullOrWhiteSpace(title))
        {
            text.AppendLine(title);
        }

        if (!string.IsNullOrWhiteSpace(failureReason))
        {
            text.AppendLine(culture, $"Could not run: {failureReason}");
            return text.ToString().TrimEnd();
        }

        if (totalErrors == 0 && totalWarnings == 0)
        {
            text.AppendLine("No diagnostics.");
            return text.ToString().TrimEnd();
        }

        text.AppendLine(culture,
            $"{totalErrors} error(s), {totalWarnings} warning(s) across {filesAffected} file(s).");

        foreach (CodeDiagnostic entry in entries)
        {
            text.AppendLine(culture, $"  {entry}");
        }

        if (truncated)
        {
            text.AppendLine(culture,
                $"  … more diagnostics were withheld. Fix the {entries.Count} above first - later errors are usually consequences of the earliest one.");
        }

        return text.ToString().TrimEnd();
    }
}
