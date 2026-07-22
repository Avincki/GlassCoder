using System.ComponentModel;
using GlassCoder.Tools.Changes;
using GlassCoder.Tools.Guardrails;
using GlassCoder.Tools.Registry;
using GlassCoder.Tools.Verification;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GlassCoder.Tools.FileSystem;

/// <summary>Result payload of <c>edit_file</c>.</summary>
/// <param name="Path">Repo-relative file that was changed.</param>
/// <param name="StartLine">1-based first line of the replaced region.</param>
/// <param name="EndLine">1-based last line of the replaced region, after the edit.</param>
/// <param name="LinesBefore">Lines in the file before the edit.</param>
/// <param name="LinesAfter">Lines in the file after the edit.</param>
/// <param name="Verified">Whether the edit was compile-checked in memory before being written.</param>
/// <param name="Diagnostics">Summarised diagnostics from that pre-write check, when it ran.</param>
/// <param name="ChangeId">Identifier of this change in the change log, for the UI to link to.</param>
public sealed record EditFileResult(
    [property: Description("Repo-relative path that was edited.")] string Path,
    [property: Description("1-based first line of the replaced region.")] int StartLine,
    [property: Description("1-based last line of the replaced region after the edit.")] int EndLine,
    [property: Description("Number of lines in the file before the edit.")] int LinesBefore,
    [property: Description("Number of lines in the file after the edit.")] int LinesAfter,
    [property: Description("True when the edit was compile-checked in memory before being written.")] bool Verified,
    [property: Description("Summary of diagnostics from the pre-write check, if it ran.")] string? Diagnostics,
    [property: Description("Identifier of this change in the change log.")] string? ChangeId = null);

/// <summary>
/// <c>edit_file</c> - the first tool that changes anything (CLAUDE.md §7, workplan task 16).
/// <para>
/// It replaces one <em>exact, unique</em> string. Not a line range, not a regex, not a fuzzy
/// match: an edit that can silently land in the wrong place is worse than an edit that fails,
/// because the loop will not notice. Absent target and ambiguous target are both errors, and
/// both are observations the agent can act on.
/// </para>
/// <para>
/// Two gates stand before the write. The path allow-list decides whether this file may be
/// touched at all (task 8), and the in-memory Roslyn check decides whether the result would
/// still compile (task 14) - so a broken edit is refused before it reaches the working tree,
/// not after.
/// </para>
/// </summary>
public sealed class EditFileTool : IToolSet
{
    private const string ToolName = "edit_file";

    private readonly IPathGuard _guard;
    private readonly ICodeAnalyzer _analyzer;
    private readonly DiagnosticSummarizer _summarizer;
    private readonly IChangeLog _changes;
    private readonly IApprovalGate _approval;
    private readonly VerificationOptions _options;
    private readonly ILogger<EditFileTool> _logger;

    /// <summary>Creates the tool.</summary>
    public EditFileTool(
        IPathGuard guard,
        ICodeAnalyzer analyzer,
        DiagnosticSummarizer summarizer,
        IOptions<VerificationOptions> options,
        IChangeLog? changes = null,
        IApprovalGate? approval = null,
        ILogger<EditFileTool>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        _guard = guard;
        _analyzer = analyzer;
        _summarizer = summarizer;
        _changes = changes ?? new ChangeLog();
        _approval = approval ?? new AutoApprovalGate(Options.Create(new ApprovalOptions()));
        _options = options.Value;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<EditFileTool>.Instance;
    }

    /// <summary>Replaces an exact, unique string in a file.</summary>
    [GlassCoderTool(ToolName, Order = 40)]
    [Description("Replace an exact, unique string in a workspace file. Read the file first and quote enough "
        + "surrounding text to make the target unique - the edit fails if the text is missing or appears more "
        + "than once. The change is syntax- and compile-checked before it is written.")]
    public async Task<ToolObservation<EditFileResult>> EditFileAsync(
        [Description("Path to the file, relative to the repository root.")]
        string path,
        [Description("The exact text to replace. Must appear exactly once in the file, whitespace included.")]
        string oldText,
        [Description("The replacement text.")]
        string newText,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(oldText))
        {
            return Observation.Fail<EditFileResult>(
                ToolName,
                ToolErrorCodes.InvalidArgument,
                "oldText is required.",
                "To create a file, use an empty file and edit into it, or ask for a file-creation tool.");
        }

        if (string.Equals(oldText, newText, StringComparison.Ordinal))
        {
            return Observation.Fail<EditFileResult>(
                ToolName,
                ToolErrorCodes.InvalidArgument,
                "oldText and newText are identical, so this edit would do nothing.");
        }

        PathGuardResult verdict = _guard.Resolve(path, PathAccess.Write);
        if (!verdict.Allowed || verdict.FullPath is null)
        {
            return Observation.Fail<EditFileResult>(ToolName, ToolErrorCodes.PathNotAllowed, verdict.Reason!);
        }

        if (!File.Exists(verdict.FullPath))
        {
            return Observation.Fail<EditFileResult>(
                ToolName,
                ToolErrorCodes.NotFound,
                $"'{verdict.RelativePath}' does not exist.");
        }

        string original;
        try
        {
            original = await File.ReadAllTextAsync(verdict.FullPath, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Observation.Fail<EditFileResult>(ToolName, ToolErrorCodes.Unreadable, ex.Message);
        }

        int first = original.IndexOf(oldText, StringComparison.Ordinal);
        if (first < 0)
        {
            return Observation.Fail<EditFileResult>(
                ToolName,
                ToolErrorCodes.NotFound,
                $"The text to replace was not found in '{verdict.RelativePath}'.",
                "Read the file again and copy the target exactly, including indentation and line endings.");
        }

        int second = original.IndexOf(oldText, first + 1, StringComparison.Ordinal);
        if (second >= 0)
        {
            int occurrences = Occurrences(original, oldText);
            return Observation.Fail<EditFileResult>(
                ToolName,
                ToolErrorCodes.AmbiguousTarget,
                $"The text to replace appears {occurrences} times in '{verdict.RelativePath}'.",
                "Include more surrounding context so the target is unique.");
        }

        string updated = string.Concat(original.AsSpan(0, first), newText, original.AsSpan(first + oldText.Length));

        // Every change is recorded before it is applied, so a change that was refused is as
        // visible in the UI as one that landed (CLAUDE.md §10).
        CodeChange change = _changes.Propose(verdict.RelativePath!, ToolName, original, updated);

        // Gate 1: would this still parse, and would it still compile? Refuse before writing.
        (bool rejected, string? diagnostics, bool verified) = await VerifyAsync(
            verdict.FullPath, verdict.RelativePath!, original, updated, cancellationToken).ConfigureAwait(false);

        if (rejected)
        {
            _changes.Update(change.Id, ChangeStatus.Rejected, "Verification refused the edit.", diagnostics);
            return Observation.Fail<EditFileResult>(
                ToolName,
                ToolErrorCodes.VerificationFailed,
                $"The edit was refused: it would break '{verdict.RelativePath}'.\n{diagnostics}",
                "Fix the problem in your replacement text and try again. Nothing has been written.");
        }

        // Gate 2: does a human have to say yes? The permission prompt is a guardrail before
        // write, so it runs after verification and before anything touches the working tree.
        ApprovalDecision decision = await _approval.RequestAsync(change, cancellationToken).ConfigureAwait(false);
        if (!decision.Approved)
        {
            _changes.Update(change.Id, ChangeStatus.Rejected, decision.Reason ?? "A human rejected the change.");
            return Observation.Fail<EditFileResult>(
                ToolName,
                ToolErrorCodes.ApprovalRefused,
                decision.Reason ?? $"A human rejected the change to '{verdict.RelativePath}'.",
                "Nothing has been written. Take the feedback into account before trying again.");
        }

        try
        {
            await File.WriteAllTextAsync(verdict.FullPath, updated, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _changes.Update(change.Id, ChangeStatus.Rejected, ex.Message);
            return Observation.Fail<EditFileResult>(ToolName, ToolErrorCodes.Unreadable, ex.Message);
        }

        _changes.Update(change.Id, ChangeStatus.Applied, verificationSummary: diagnostics);
        int startLine = CountLines(original.AsSpan(0, first)) + 1;
        int linesBefore = CountLines(original) + 1;
        int linesAfter = CountLines(updated) + 1;
        int endLine = startLine + CountLines(newText);

        _logger.LogInformation(
            "Edited {Path}: lines {StartLine}-{EndLine}, {LinesBefore} → {LinesAfter} lines",
            verdict.RelativePath, startLine, endLine, linesBefore, linesAfter);

        EditFileResult result = new(
            verdict.RelativePath!,
            startLine,
            endLine,
            linesBefore,
            linesAfter,
            verified,
            diagnostics,
            change.Id);

        return Observation.Ok(ToolName, result, $"Edited {verdict.RelativePath} at line {startLine}.");
    }

    /// <summary>
    /// Runs the pre-write rungs. Pre-existing errors never block an edit: the agent is usually
    /// editing <em>because</em> the project is broken, and refusing to let it start would be a
    /// deadlock. Only errors the edit itself introduces are grounds for refusal.
    /// </summary>
    private async Task<(bool Rejected, string? Diagnostics, bool Verified)> VerifyAsync(
        string fullPath,
        string relativePath,
        string original,
        string updated,
        CancellationToken cancellationToken)
    {
        if (!_options.VerifyEditsBeforeWrite || !_analyzer.Handles(fullPath))
        {
            return (false, null, false);
        }

        DiagnosticReport syntax = _analyzer.CheckSyntax(relativePath, updated);
        if (!syntax.Ok)
        {
            DiagnosticSummary summary = _summarizer.Summarise(syntax, "Syntax check of the edited file failed.");
            return (_options.RejectEditsThatBreakTheBuild, summary.Text, true);
        }

        DiagnosticReport before = await _analyzer.CheckEditAsync(fullPath, original, cancellationToken).ConfigureAwait(false);
        DiagnosticReport after = await _analyzer.CheckEditAsync(fullPath, updated, cancellationToken).ConfigureAwait(false);

        if (before.FailureReason is not null || after.FailureReason is not null)
        {
            // An inconclusive compile is not a failed compile. Say so and let the edit through;
            // the build tool is the authoritative gate anyway.
            return (false, after.FailureReason ?? before.FailureReason, false);
        }

        IReadOnlyList<CodeDiagnostic> introduced = Introduced(before, after);
        if (introduced.Count == 0)
        {
            return (false, null, true);
        }

        DiagnosticSummary introducedSummary = _summarizer.Summarise(
            introduced,
            $"This edit would introduce {introduced.Count} new compile error(s).");

        return (_options.RejectEditsThatBreakTheBuild, introducedSummary.Text, true);
    }

    private static IReadOnlyList<CodeDiagnostic> Introduced(DiagnosticReport before, DiagnosticReport after)
    {
        HashSet<string> existing = new(
            before.Diagnostics.Where(d => d.IsError).Select(Fingerprint),
            StringComparer.Ordinal);

        return [.. after.Diagnostics.Where(d => d.IsError && !existing.Contains(Fingerprint(d)))];
    }

    // Line numbers shift when text is inserted, so identity is the code, the file and the
    // message - not the position.
    private static string Fingerprint(CodeDiagnostic diagnostic) =>
        $"{diagnostic.Id}|{diagnostic.FilePath}|{diagnostic.Message}";

    private static int Occurrences(string text, string value)
    {
        int count = 0;
        int index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }

    private static int CountLines(ReadOnlySpan<char> text)
    {
        int lines = 0;
        foreach (char character in text)
        {
            if (character == '\n')
            {
                lines++;
            }
        }

        return lines;
    }
}
