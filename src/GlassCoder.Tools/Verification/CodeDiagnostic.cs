using System.ComponentModel;

namespace GlassCoder.Tools.Verification;

/// <summary>Severity of a compiler diagnostic, independent of any one compiler's enum.</summary>
public enum CodeSeverity
{
    /// <summary>Not surfaced to the agent.</summary>
    Hidden,

    /// <summary>Informational.</summary>
    Info,

    /// <summary>A warning. Rung 3 of the ladder: reported, never a gate (CLAUDE.md §8).</summary>
    Warning,

    /// <summary>An error. This gates.</summary>
    Error,
}

/// <summary>
/// One typed compiler diagnostic (CLAUDE.md §8.1, workplan task 14).
/// <para>
/// Typed, never scraped. Every field here comes from a <c>Microsoft.CodeAnalysis.Diagnostic</c>
/// or from the structured fields of an MSBuild diagnostic line - regexing over prose compiler
/// output is how a harness ends up reporting a "fix" for an error the compiler never raised.
/// </para>
/// </summary>
/// <param name="Id">Compiler error code, for example <c>CS0103</c>.</param>
/// <param name="Severity">How serious it is.</param>
/// <param name="Message">The compiler's message.</param>
/// <param name="FilePath">Repo-relative file, when the diagnostic has a location.</param>
/// <param name="Line">1-based line, or 0 when there is no location.</param>
/// <param name="Column">1-based column, or 0 when there is no location.</param>
public sealed record CodeDiagnostic(
    [property: Description("Compiler error code, for example CS0103.")] string Id,
    [property: Description("Severity: Error, Warning, Info or Hidden.")] CodeSeverity Severity,
    [property: Description("The compiler's message.")] string Message,
    [property: Description("Repo-relative file the diagnostic points at.")] string? FilePath = null,
    [property: Description("1-based line number, or 0 when the diagnostic has no location.")] int Line = 0,
    [property: Description("1-based column number, or 0 when the diagnostic has no location.")] int Column = 0)
{
    /// <summary>Whether this diagnostic gates the ladder.</summary>
    public bool IsError => Severity == CodeSeverity.Error;

    /// <summary>Renders the diagnostic the way a compiler would, for the model to read.</summary>
    public override string ToString() =>
        FilePath is null
            ? $"{Severity.ToString().ToLowerInvariant()} {Id}: {Message}"
            : $"{FilePath}({Line},{Column}): {Severity.ToString().ToLowerInvariant()} {Id}: {Message}";
}

/// <summary>The result of running one verification rung.</summary>
/// <param name="Ok">Whether the rung passed - that is, produced no errors.</param>
/// <param name="Diagnostics">Diagnostics produced, unsummarised.</param>
/// <param name="ErrorCount">True total of errors, however many are listed.</param>
/// <param name="WarningCount">True total of warnings.</param>
/// <param name="DurationMs">Wall-clock the rung took.</param>
/// <param name="FailureReason">Why the rung could not run at all, when that happened.</param>
public sealed record DiagnosticReport(
    bool Ok,
    IReadOnlyList<CodeDiagnostic> Diagnostics,
    int ErrorCount,
    int WarningCount,
    double DurationMs,
    string? FailureReason = null)
{
    /// <summary>An empty, passing report.</summary>
    public static DiagnosticReport Success(double durationMs = 0) => new(true, [], 0, 0, durationMs);

    /// <summary>A report for a rung that could not run.</summary>
    public static DiagnosticReport Inconclusive(string reason, double durationMs = 0) =>
        new(false, [], 0, 0, durationMs, reason);

    /// <summary>Builds a report from a diagnostic list, counting the true totals.</summary>
    public static DiagnosticReport FromDiagnostics(IReadOnlyList<CodeDiagnostic> diagnostics, double durationMs)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);

        int errors = 0;
        int warnings = 0;
        foreach (CodeDiagnostic diagnostic in diagnostics)
        {
            if (diagnostic.Severity == CodeSeverity.Error)
            {
                errors++;
            }
            else if (diagnostic.Severity == CodeSeverity.Warning)
            {
                warnings++;
            }
        }

        return new DiagnosticReport(errors == 0, diagnostics, errors, warnings, durationMs);
    }
}
