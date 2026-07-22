namespace GlassCoder.Tools.Verification;

/// <summary>
/// Rungs 1 and 2 of the verification ladder for C# targets (CLAUDE.md §8, workplan task 14).
/// <para>
/// Both run in-process. That is the whole point: a per-document syntax check has to be fast
/// enough to run after <em>every</em> edit, and a rung that takes ten seconds is a rung the
/// loop will be tempted to skip.
/// </para>
/// </summary>
public interface ICodeAnalyzer
{
    /// <summary>Whether this analyzer handles the given file.</summary>
    bool Handles(string filePath);

    /// <summary>
    /// Rung 1: parse one file and report syntax errors. No project, no references, no I/O.
    /// </summary>
    DiagnosticReport CheckSyntax(string filePath, string text);

    /// <summary>
    /// Rung 2: compile the project that owns <paramref name="filePath"/> in memory, with the
    /// file's content replaced by <paramref name="proposedText"/>.
    /// <para>
    /// This is what lets a bad edit be refused <em>before</em> it reaches disk: the diagnostics
    /// come back from a compilation that never touched the working tree.
    /// </para>
    /// </summary>
    Task<DiagnosticReport> CheckEditAsync(string filePath, string proposedText, CancellationToken cancellationToken = default);

    /// <summary>Rung 2: compile a directory of C# sources in memory as one project.</summary>
    Task<DiagnosticReport> CompileAsync(string projectDirectory, CancellationToken cancellationToken = default);
}
