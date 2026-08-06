using System.ComponentModel;
using System.Text;
using GlassCoder.Tools.Changes;
using GlassCoder.Tools.Guardrails;
using GlassCoder.Tools.Registry;
using GlassCoder.Tools.Verification;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GlassCoder.Tools.FileSystem;

/// <summary>One replacement in one file.</summary>
/// <param name="Path">Repo-relative file to change.</param>
/// <param name="OldText">Exact text to replace, unique in the file as it stands.</param>
/// <param name="NewText">What to put there instead.</param>
/// <param name="ReplaceAll">Replace every occurrence instead of requiring exactly one.</param>
/// <remarks>
/// No <c>[Description]</c> on any of them, deliberately: the tool declares parameters of the
/// same names immediately above this, and describing them twice put 350 characters on every
/// request to say the same thing in two places.
/// </remarks>
public sealed record FileEdit(string Path, string OldText, string NewText, bool ReplaceAll = false);

/// <summary>What happened to one file.</summary>
/// <param name="Path">Repo-relative file.</param>
/// <param name="Applied">Whether it was written.</param>
/// <param name="Edits">How many replacements were made in it.</param>
/// <param name="StartLine">1-based first line the change touched.</param>
/// <param name="EndLine">1-based last line it touched.</param>
/// <param name="LinesBefore">Lines in the file before.</param>
/// <param name="LinesAfter">Lines in the file after.</param>
/// <param name="Verified">Whether it was compile-checked in memory before being written.</param>
/// <param name="Diagnostics">Summarised diagnostics from that check, when it ran.</param>
/// <param name="ChangeId">Identifier of the change in the change log.</param>
/// <param name="Error">Why nothing was written, when nothing was.</param>
public sealed record FileEditResult(
    [property: Description("Repo-relative path.")] string Path,
    [property: Description("True when the file was written.")] bool Applied,
    [property: Description("How many replacements were made in this file.")] int Edits,
    [property: Description("1-based first line the change touched.")] int StartLine,
    [property: Description("1-based last line the change touched.")] int EndLine,
    [property: Description("Lines in the file before.")] int LinesBefore,
    [property: Description("Lines in the file after.")] int LinesAfter,
    [property: Description("True when the change was compile-checked before being written.")] bool Verified,
    [property: Description("Diagnostics from the pre-write check, if it ran.")] string? Diagnostics = null,
    [property: Description("Identifier of this change in the change log.")] string? ChangeId = null,
    [property: Description("Why this file was not changed, when it was not.")] string? Error = null);

/// <summary>Result payload of <c>edit_file</c>.</summary>
/// <param name="Files">One entry per file, in the order the edits first named them.</param>
/// <param name="FilesChanged">How many files were written.</param>
/// <param name="EditsApplied">How many replacements were made in all.</param>
public sealed record EditFileResult(
    [property: Description("One entry per file named by the edits, changed or not.")] IReadOnlyList<FileEditResult> Files,
    [property: Description("How many files were written.")] int FilesChanged,
    [property: Description("How many replacements were made in all.")] int EditsApplied);

/// <summary>
/// <c>edit_file</c> - the tool that changes things (CLAUDE.md §7, workplan tasks 16 and 46).
/// <para>
/// Each edit replaces one <em>exact, unique</em> string. Not a line range, not a regex, not a
/// fuzzy match: an edit that can silently land in the wrong place is worse than an edit that
/// fails, because the loop will not notice. Absent target and ambiguous target are both errors,
/// and both are observations the agent can act on. The one sanctioned exception is
/// <c>replaceAll</c>: five byte-identical call sites cost one run five separate steps, each
/// quoting a whole method to satisfy the uniqueness rule, when the actual request was "change
/// every occurrence" - which is safe precisely because it is explicit.
/// </para>
/// <para>
/// <strong>Two shapes, and the flat one is primary.</strong> <c>edit_file(path, oldText,
/// newText)</c> is what a model reaches for unprompted; <c>edit_file(edits: [...])</c> does
/// several replacements, across several files, in one call. Edits to the same file are applied in
/// order and that file is verified and written once - three edits in one file cost one in-memory
/// compile instead of three.
/// </para>
/// <para>
/// The list was briefly the <em>only</em> shape, and one run says why it must not be. Asked to
/// write tests, the model spent eight consecutive steps on this tool and landed nothing: three
/// calls in the flat shape it had never been shown, two with the edits' <c>path</c> left at the
/// top level, one well-formed. Tool-call validity fell from 1.00 - where it had sat for eleven
/// runs - to 0.86, and the run was cancelled. The schema saving was about 150 tokens a request;
/// it cost forty thousand tokens on one task.
/// </para>
/// <para>
/// So this meets the model where it is, which is the same lesson line-ending tolerance taught
/// (task 45): a shape the model does not reliably produce is a contract the harness should not
/// insist on. A <c>path</c> given at the top level fills in for edits that omit it, because that
/// is exactly what the run did, and a malformed call says which shape to use rather than
/// reporting a permission problem it does not have.
/// </para>
/// <para>
/// <strong>Atomic per file, deliberately not across files.</strong> Every edit to a file lands or
/// none does. Cross-file atomicity would need a rollback path over the working tree, and a
/// partly-applied change that says exactly which files landed is more useful to the model than
/// one that silently undoes correct work.
/// </para>
/// <para>
/// Two gates stand before each write. The path allow-list decides whether the file may be
/// touched at all (task 8), and the in-memory Roslyn check decides whether the result would still
/// compile (task 14) - so a broken edit is refused before it reaches the working tree.
/// </para>
/// </summary>
public sealed class EditFileTool : IToolSet
{
    private const string ToolName = "edit_file";

    /// <summary>
    /// Both shapes, spelled out. Attached to every malformed call, because the run this exists for
    /// never worked out what was wrong with its arguments from an error that did not say.
    /// </summary>
    private const string ShapeHint =
        "Call it either way: edit_file(path, oldText, newText) for one replacement, or "
        + "edit_file(edits: [{path, oldText, newText}, ...]) for several.";

    /// <summary>Shared by both match paths: a unique target that is absent, and a replace-all with no hits.</summary>
    private const string NotFoundHint =
        "Line endings are already matched flexibly, so the difference is in the characters "
        + "themselves - indentation, most often. Read the file again and copy the target from "
        + "what it returns. To replace the whole file instead, use create_file with overwrite: true.";

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

    /// <summary>Applies one replacement, or an ordered list of them grouped by file.</summary>
    [GlassCoderTool(ToolName, Order = 40)]
    [Description("Replace an exact, unique string in a workspace file. Read it first and quote enough "
        + "surrounding text to make the target unique. Compile-checked before writing.")]
    public async Task<ToolObservation<EditFileResult>> EditFileAsync(
        [Description("Repo-relative path to the file.")]
        string? path = null,
        [Description("Text to replace. Must appear exactly once, indentation included; line endings are "
            + "matched flexibly.")]
        string? oldText = null,
        [Description("The replacement text.")]
        string? newText = null,
        [Description("Replace every occurrence of oldText instead of exactly one.")]
        bool replaceAll = false,
        [Description("Several replacements at once, instead of the flat shape.")]
        IReadOnlyList<FileEdit>? edits = null,
        CancellationToken cancellationToken = default)
    {
        (List<FileEdit> planned, string? complaint) = Plan(path, oldText, newText, replaceAll, edits);
        if (complaint is not null)
        {
            return Observation.Fail<EditFileResult>(ToolName, ToolErrorCodes.InvalidArgument, complaint, ShapeHint);
        }

        List<FileOutcome> outcomes = [];
        foreach ((PathGuardResult Verdict, string Path, List<FileEdit> Hunks) group in Group(planned))
        {
            outcomes.Add(await ApplyAsync(group.Verdict, group.Path, group.Hunks, cancellationToken)
                .ConfigureAwait(false));
        }

        EditFileResult result = new(
            [.. outcomes.Select(o => o.Result)],
            outcomes.Count(o => o.Result.Applied),
            outcomes.Sum(o => o.Result.Applied ? o.Result.Edits : 0));

        // Nothing landed, so this is a failed call rather than a partial one. Said as a failure
        // because the loop counts those: a model repeating an edit whose target is not there has
        // to be able to trip the repeated-failure guard, and an "ok" with an error inside it
        // would let it loop to the step limit instead.
        if (result.FilesChanged == 0)
        {
            FileOutcome first = outcomes[0];
            return Observation.Fail<EditFileResult>(
                ToolName,
                first.Code ?? ToolErrorCodes.InvalidArgument,
                Describe(outcomes),
                first.Hint);
        }

        return Observation.Ok(ToolName, result, Summarise(result, outcomes));
    }

    /// <summary>Applies a list of replacements - the batch shape, without the flat parameters.</summary>
    public Task<ToolObservation<EditFileResult>> EditFilesAsync(
        IReadOnlyList<FileEdit> edits,
        CancellationToken cancellationToken = default) =>
        EditFileAsync(edits: edits, cancellationToken: cancellationToken);

    /// <summary>
    /// Works out what was actually asked for, and says which shape to use when it cannot tell.
    /// <para>
    /// The path fallback is not politeness. A run sent the file's path at the top level and left
    /// it out of each edit five times running; the information was there and the harness refused
    /// on a technicality, then reported it as <c>path_not_allowed</c> - which sent the model to
    /// look at the writable set rather than at its own arguments, so it never recovered.
    /// </para>
    /// </summary>
    private static (List<FileEdit> Edits, string? Complaint) Plan(
        string? path, string? oldText, string? newText, bool replaceAll, IReadOnlyList<FileEdit>? edits)
    {
        if (edits is { Count: > 0 })
        {
            List<FileEdit> planned = [];
            for (int i = 0; i < edits.Count; i++)
            {
                if (edits[i] is not { } edit)
                {
                    return ([], $"Edit {i + 1} of {edits.Count} is empty.");
                }

                string? where = string.IsNullOrWhiteSpace(edit.Path) ? path : edit.Path;
                if (string.IsNullOrWhiteSpace(where))
                {
                    return ([], $"Edit {i + 1} of {edits.Count} names no path, and the call has no "
                        + "top-level path to fall back on.");
                }

                // A top-level replaceAll spreads to the edits for the same reason the path does:
                // when the intent arrived at the wrong level, refusing it is a technicality.
                planned.Add(edit with { Path = where, ReplaceAll = edit.ReplaceAll || replaceAll });
            }

            return (planned, null);
        }

        if (string.IsNullOrWhiteSpace(path) && string.IsNullOrEmpty(oldText))
        {
            return ([], "Nothing to do.");
        }

        if (string.IsNullOrWhiteSpace(path))
        {
            return ([], "path is required.");
        }

        if (string.IsNullOrEmpty(oldText))
        {
            return ([], $"oldText is required: it is the text to replace in '{path}'.");
        }

        return ([new FileEdit(path, oldText, newText ?? string.Empty, replaceAll)], null);
    }

    /// <summary>
    /// Groups the edits by the file they name, keeping the order they were first named in.
    /// <para>
    /// Grouped on the guard's own spelling of the path rather than the model's, so
    /// <c>./src/A.cs</c> and <c>src/A.cs</c> are one file. They were two groups when this was
    /// keyed on the raw string, and the second would then read the text the first had already
    /// replaced and fail to find its target.
    /// </para>
    /// </summary>
    private List<(PathGuardResult Verdict, string Path, List<FileEdit> Hunks)> Group(IReadOnlyList<FileEdit> edits)
    {
        Dictionary<string, int> index = new(StringComparer.OrdinalIgnoreCase);
        List<(PathGuardResult Verdict, string Path, List<FileEdit> Hunks)> groups = [];

        // Plan has already filled in every path, so a verdict here is about the workspace's rules
        // rather than about the arguments - which is what lets PathNotAllowed below mean what it
        // says. It reached the model as the answer to a missing path once, and cost five steps.
        foreach (FileEdit edit in edits)
        {
            string raw = edit.Path;
            PathGuardResult verdict = _guard.Resolve(raw, PathAccess.Write);
            string key = verdict.RelativePath ?? raw;

            if (!index.TryGetValue(key, out int at))
            {
                at = groups.Count;
                index[key] = at;
                groups.Add((verdict, key, []));
            }

            groups[at].Hunks.Add(edit!);
        }

        return groups;
    }

    /// <summary>
    /// Applies every edit for one file, or none of them. The verification and the approval happen
    /// once, on the finished text, which is both cheaper and the only correct thing to check -
    /// an intermediate state between two hunks of a rename does not compile and was never meant to.
    /// </summary>
    private async Task<FileOutcome> ApplyAsync(
        PathGuardResult verdict,
        string path,
        List<FileEdit> hunks,
        CancellationToken cancellationToken)
    {
        if (!verdict.Allowed || verdict.FullPath is null)
        {
            return Refused(path, ToolErrorCodes.PathNotAllowed, verdict.Reason!);
        }

        if (!File.Exists(verdict.FullPath))
        {
            return Refused(path, ToolErrorCodes.NotFound, $"'{path}' does not exist.");
        }

        string original;
        try
        {
            original = await File.ReadAllTextAsync(verdict.FullPath, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Refused(path, ToolErrorCodes.Unreadable, ex.Message);
        }

        string newLine = TextFile.DominantNewLine(original);
        string updated = original;
        int replacements = 0;

        for (int i = 0; i < hunks.Count; i++)
        {
            HunkResult step = Replace(updated, hunks[i], newLine, Where(i, hunks.Count), path);
            if (step.Text is null)
            {
                return Refused(path, step.Code!, step.Message!, step.Hint);
            }

            updated = step.Text;
            replacements += step.Replacements;
        }

        // Every change is recorded before it is applied, so a change that was refused is as
        // visible in the UI as one that landed (CLAUDE.md §10).
        CodeChange change = _changes.Propose(path, ToolName, original, updated);

        // Gate 1: would this still parse, and would it still compile? Refuse before writing.
        (bool rejected, string? diagnostics, bool verified) = await VerifyAsync(
            verdict.FullPath, path, original, updated, cancellationToken).ConfigureAwait(false);

        if (rejected)
        {
            _changes.Update(change.Id, ChangeStatus.Rejected, "Verification refused the edit.", diagnostics);
            return Refused(
                path,
                ToolErrorCodes.VerificationFailed,
                $"The edit was refused: it would break '{path}'.\n{diagnostics}",
                "Fix the problem in your replacement text and try again. Nothing has been written.");
        }

        // Gate 2: does a human have to say yes? Asked per file, not per batch, because the prompt
        // shows a diff and a reviewer must see what they are approving - and refusing one file of
        // a change still lets the rest land.
        ApprovalDecision decision = await _approval.RequestAsync(change, cancellationToken).ConfigureAwait(false);
        if (!decision.Approved)
        {
            _changes.Update(change.Id, ChangeStatus.Rejected, decision.Reason ?? "A human rejected the change.");
            return Refused(
                path,
                ToolErrorCodes.ApprovalRefused,
                decision.Reason ?? $"A human rejected the change to '{path}'.",
                "Nothing has been written. Take the feedback into account before trying again.");
        }

        try
        {
            await File.WriteAllTextAsync(verdict.FullPath, updated, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _changes.Update(change.Id, ChangeStatus.Rejected, ex.Message);
            return Refused(path, ToolErrorCodes.Unreadable, ex.Message);
        }

        _changes.Update(change.Id, ChangeStatus.Applied, verificationSummary: diagnostics);

        // The touched range comes from the finished diff rather than from offset bookkeeping
        // across hunks, which is both simpler and the only version that stays right when two
        // hunks overlap in effect.
        (int Start, int End)? range = change.Range();

        _logger.LogInformation(
            "Edited {Path}: {Edits} replacement(s), {LinesBefore} → {LinesAfter} lines",
            path, replacements, CountLines(original) + 1, CountLines(updated) + 1);

        return new FileOutcome(
            new FileEditResult(
                path,
                Applied: true,
                replacements,
                range?.Start ?? 1,
                range?.End ?? 1,
                CountLines(original) + 1,
                CountLines(updated) + 1,
                verified,
                diagnostics,
                change.Id),
            null,
            null);
    }

    /// <summary>Applies one hunk to the text as it now stands, or says why it could not.</summary>
    private static HunkResult Replace(string text, FileEdit hunk, string newLine, string which, string path)
    {
        if (string.IsNullOrEmpty(hunk?.OldText))
        {
            return HunkResult.Refused(
                ToolErrorCodes.InvalidArgument,
                $"{which}oldText is required.",
                "To create a file, use create_file.");
        }

        if (string.Equals(hunk.OldText, hunk.NewText, StringComparison.Ordinal))
        {
            return HunkResult.Refused(
                ToolErrorCodes.InvalidArgument,
                $"{which}oldText and newText are identical, so this edit would do nothing.");
        }

        // Line endings are matched flexibly. The model emits \n; a file from dotnet new on
        // Windows holds \r\n; and demanding the two agree byte for byte is a contract no model
        // reliably honours - it cost one run seventeen consecutive failures on a seven-line file.
        if (hunk.ReplaceAll)
        {
            return ReplaceEverywhere(text, hunk, newLine, which, path);
        }

        TextFile.Match? found = TextFile.Find(text, hunk.OldText, out int occurrences);

        if (found is null && occurrences > 1)
        {
            // The line numbers cost nothing here and save the model a full re-read: told only
            // "appears 6 times", run 48a7af6a burned two steps and a re-read finding out where.
            IReadOnlyList<TextFile.Match> sites = TextFile.FindAll(text, hunk.OldText);
            string lines = string.Join(", ", sites.Take(8).Select(m => TextFile.LineNumberAt(text, m.Start)));
            if (sites.Count > 8)
            {
                lines += ", …";
            }

            return HunkResult.Refused(
                ToolErrorCodes.AmbiguousTarget,
                $"{which}the text to replace appears {occurrences} times in '{path}', at lines {lines}.",
                "Pass replaceAll: true to change every occurrence, or include more surrounding "
                    + "context so the target is unique.");
        }

        if (found is not { } match)
        {
            return HunkResult.Refused(
                ToolErrorCodes.NotFound,
                $"{which}the text to replace was not found in '{path}'. {Nearest(text, hunk.OldText)}",
                NotFoundHint);
        }

        return new HunkResult(
            string.Concat(
                text.AsSpan(0, match.Start),
                TextFile.WithNewLine(hunk.NewText, newLine),
                text.AsSpan(match.Start + match.Length)),
            null,
            null,
            null);
    }

    /// <summary>
    /// Replaces every occurrence - the request the ambiguity guard exists to refuse, made safe by
    /// being explicit. Five identical call sites cost one run five separate steps, each quoting a
    /// whole method to be unique; asked for as "all of them", they are one hunk and one step.
    /// </summary>
    private static HunkResult ReplaceEverywhere(string text, FileEdit hunk, string newLine, string which, string path)
    {
        IReadOnlyList<TextFile.Match> matches = TextFile.FindAll(text, hunk.OldText);
        if (matches.Count == 0)
        {
            return HunkResult.Refused(
                ToolErrorCodes.NotFound,
                $"{which}the text to replace was not found in '{path}'. {Nearest(text, hunk.OldText)}",
                NotFoundHint);
        }

        StringBuilder updated = new(text.Length);
        string replacement = TextFile.WithNewLine(hunk.NewText, newLine);
        int consumed = 0;

        foreach (TextFile.Match match in matches)
        {
            updated.Append(text, consumed, match.Start - consumed).Append(replacement);
            consumed = match.Start + match.Length;
        }

        updated.Append(text, consumed, text.Length - consumed);
        return new HunkResult(updated.ToString(), null, null, null, matches.Count);
    }

    /// <summary>Names the hunk when there is more than one, so a failure says which edit stopped.</summary>
    private static string Where(int index, int count) => count == 1 ? string.Empty : $"Edit {index + 1} of {count}: ";

    private static FileOutcome Refused(string path, string code, string message, string? hint = null) =>
        new(
            new FileEditResult(path, Applied: false, 0, 0, 0, 0, 0, Verified: false, Error: message),
            code,
            hint);

    /// <summary>Every file that did not change, and why - the "says exactly which" half of the contract.</summary>
    private static string Describe(List<FileOutcome> outcomes) =>
        string.Join("\n", outcomes.Where(o => !o.Result.Applied).Select(o => o.Result.Error));

    private static string Summarise(EditFileResult result, List<FileOutcome> outcomes)
    {
        string summary = result.FilesChanged == 1 && result.EditsApplied == 1
            ? $"Edited {result.Files.First(f => f.Applied).Path} at line {result.Files.First(f => f.Applied).StartLine}."
            : $"{result.EditsApplied} edit(s) across {result.FilesChanged} file(s).";

        int refused = outcomes.Count - result.FilesChanged;
        return refused == 0 ? summary : $"{summary} {refused} file(s) unchanged:\n{Describe(outcomes)}";
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

    /// <summary>
    /// A lead to follow when the target was not found.
    /// <para>
    /// "Not found" on its own sends the model back to re-read a file it has often already read
    /// correctly. Saying which part of its target <em>did</em> match points at the line that
    /// differs, which is usually one indentation level.
    /// </para>
    /// </summary>
    private static string Nearest(string original, string oldText)
    {
        string[] lines = oldText.ReplaceLineEndings(TextFile.Lf).Split(TextFile.Lf);
        if (lines.Length <= 1)
        {
            return string.Empty;
        }

        (string normalised, _) = TextFile.Normalise(original);

        int matched = 0;
        foreach (string line in lines)
        {
            if (line.Trim().Length > 0 && !normalised.Contains(line, StringComparison.Ordinal))
            {
                break;
            }

            matched++;
        }

        return matched == 0
            ? "None of its lines appear in the file."
            : $"Its first {matched} line(s) appear in the file, but line {matched + 1} does not: "
                + $"\"{Trim(lines[matched])}\".";
    }

    private static string Trim(string line) =>
        line.Length <= 80 ? line : string.Concat(line.AsSpan(0, 80), "…");

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

    /// <summary>A file's result plus the error code and hint that belong to the observation, not the payload.</summary>
    private sealed record FileOutcome(FileEditResult Result, string? Code, string? Hint);

    /// <summary>The text after one hunk, or the reason there is none.</summary>
    /// <remarks><c>Replacements</c> is 1 for a unique match and the occurrence count for replace-all.</remarks>
    private readonly record struct HunkResult(string? Text, string? Code, string? Message, string? Hint, int Replacements = 1)
    {
        public static HunkResult Refused(string code, string message, string? hint = null) =>
            new(null, code, message, hint);
    }
}
