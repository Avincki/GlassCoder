using System.ComponentModel;
using GlassCoder.Tools.Changes;
using GlassCoder.Tools.Guardrails;
using GlassCoder.Tools.Registry;
using GlassCoder.Tools.Verification;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GlassCoder.Tools.FileSystem;

/// <summary>Result payload of <c>create_file</c>.</summary>
/// <param name="Path">Repo-relative file that was created.</param>
/// <param name="Lines">Lines in the new file.</param>
/// <param name="Verified">Whether the content was compile-checked in memory before being written.</param>
/// <param name="Diagnostics">Summarised diagnostics from that pre-write check, when it ran.</param>
/// <param name="ChangeId">Identifier of this change in the change log, for the UI to link to.</param>
public sealed record CreateFileResult(
    [property: Description("Repo-relative path that was created.")] string Path,
    [property: Description("Number of lines in the new file.")] int Lines,
    [property: Description("True when the content was compile-checked in memory before being written.")] bool Verified,
    [property: Description("Summary of diagnostics from the pre-write check, if it ran.")] string? Diagnostics,
    [property: Description("Identifier of this change in the change log.")] string? ChangeId = null);

/// <summary>
/// <c>create_file</c> - the only way to add a file to the workspace (CLAUDE.md §7, §10).
/// <para>
/// <see cref="EditFileTool"/> can only change what already exists, which left new files with no
/// route in at all: the suite's own "add a feature spanning three files" task had to cram a new
/// type into an existing file, measuring the tool set rather than the model. This closes that
/// gap without weakening the property that makes <c>edit_file</c> safe.
/// </para>
/// <para>
/// It <em>refuses to overwrite</em>. Creation and modification stay separate verbs, so
/// "replace one exact, unique string" remains the only way an existing file can change - an
/// upserting create tool would be a hole straight through that guarantee.
/// </para>
/// <para>
/// The path allow-list, the change log, the pre-write compile check and the approval gate all
/// apply, in that order and for the same reasons they apply to an edit. A tool that wrote around
/// the change log would make the change surface lie about what the agent did, which is precisely
/// why <c>bash</c> is not the answer here.
/// </para>
/// </summary>
public sealed class CreateFileTool : IToolSet
{
    private const string ToolName = "create_file";

    private readonly IPathGuard _guard;
    private readonly ICodeAnalyzer _analyzer;
    private readonly DiagnosticSummarizer _summarizer;
    private readonly IChangeLog _changes;
    private readonly IApprovalGate _approval;
    private readonly VerificationOptions _options;
    private readonly ILogger<CreateFileTool> _logger;

    /// <summary>Creates the tool.</summary>
    public CreateFileTool(
        IPathGuard guard,
        ICodeAnalyzer analyzer,
        DiagnosticSummarizer summarizer,
        IOptions<VerificationOptions> options,
        IChangeLog? changes = null,
        IApprovalGate? approval = null,
        ILogger<CreateFileTool>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        _guard = guard;
        _analyzer = analyzer;
        _summarizer = summarizer;
        _changes = changes ?? new ChangeLog();
        _approval = approval ?? new AutoApprovalGate(Options.Create(new ApprovalOptions()));
        _options = options.Value;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<CreateFileTool>.Instance;
    }

    /// <summary>Creates a new file and writes its full contents.</summary>
    [GlassCoderTool(ToolName, Order = 35)]
    [Description("Create a new file and write its complete contents. Fails if anything already exists at "
        + "that path - use edit_file to change a file that is already there. Missing parent directories are "
        + "created. The content is syntax- and compile-checked before it is written.")]
    public async Task<ToolObservation<CreateFileResult>> CreateFileAsync(
        [Description("Path for the new file, relative to the repository root.")]
        string path,
        [Description("The complete contents of the new file.")]
        string content,
        CancellationToken cancellationToken = default)
    {
        if (content is null)
        {
            return Observation.Fail<CreateFileResult>(
                ToolName,
                ToolErrorCodes.InvalidArgument,
                "content is required.",
                "Pass the file's full text. An empty string is allowed; omitting the argument is not.");
        }

        PathGuardResult verdict = _guard.Resolve(path, PathAccess.Write);
        if (!verdict.Allowed || verdict.FullPath is null)
        {
            return Observation.Fail<CreateFileResult>(ToolName, ToolErrorCodes.PathNotAllowed, verdict.Reason!);
        }

        if (File.Exists(verdict.FullPath))
        {
            return Observation.Fail<CreateFileResult>(
                ToolName,
                ToolErrorCodes.AlreadyExists,
                $"'{verdict.RelativePath}' already exists.",
                "Use edit_file to change it. This tool will not overwrite a file.");
        }

        if (Directory.Exists(verdict.FullPath))
        {
            return Observation.Fail<CreateFileResult>(
                ToolName,
                ToolErrorCodes.AlreadyExists,
                $"'{verdict.RelativePath}' is a directory.");
        }

        // Recorded before it is attempted, so a refused creation is as visible in the UI as one
        // that landed. An empty "before" is what makes the diff render as pure addition.
        CodeChange change = _changes.Propose(verdict.RelativePath!, ToolName, string.Empty, content);

        (bool rejected, string? diagnostics, bool verified) =
            await VerifyAsync(verdict.FullPath, verdict.RelativePath!, content, cancellationToken).ConfigureAwait(false);

        if (rejected)
        {
            _changes.Update(change.Id, ChangeStatus.Rejected, "Verification refused the edit.", diagnostics);
            return Observation.Fail<CreateFileResult>(
                ToolName,
                ToolErrorCodes.VerificationFailed,
                $"The file was not created: '{verdict.RelativePath}' would not compile.\n{diagnostics}",
                "Fix the problem in the content and try again. Nothing has been written.");
        }

        ApprovalDecision decision = await _approval.RequestAsync(change, cancellationToken).ConfigureAwait(false);
        if (!decision.Approved)
        {
            _changes.Update(change.Id, ChangeStatus.Rejected, decision.Reason ?? "A human rejected the change.");
            return Observation.Fail<CreateFileResult>(
                ToolName,
                ToolErrorCodes.ApprovalRefused,
                decision.Reason ?? $"A human rejected the creation of '{verdict.RelativePath}'.",
                "Nothing has been written. Take the feedback into account before trying again.");
        }

        try
        {
            string? directory = Path.GetDirectoryName(verdict.FullPath);
            if (!string.IsNullOrEmpty(directory))
            {
                // Safe by construction: the guard resolved the full path into the writable set,
                // so every directory between that set and the file is inside it too.
                Directory.CreateDirectory(directory);
            }

            await File.WriteAllTextAsync(verdict.FullPath, content, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _changes.Update(change.Id, ChangeStatus.Rejected, ex.Message);
            return Observation.Fail<CreateFileResult>(ToolName, ToolErrorCodes.Unreadable, ex.Message);
        }

        _changes.Update(change.Id, ChangeStatus.Applied, verificationSummary: diagnostics);
        int lines = content.Length == 0 ? 0 : CountLines(content) + 1;

        _logger.LogInformation("Created {Path}: {Lines} lines", verdict.RelativePath, lines);

        CreateFileResult result = new(verdict.RelativePath!, lines, verified, diagnostics, change.Id);
        return Observation.Ok(ToolName, result, $"Created {verdict.RelativePath} ({lines} lines).");
    }

    /// <summary>
    /// Runs the pre-write rungs against a file that is not on disk yet.
    /// <para>
    /// The "before" state is the project without this file, which is what an empty override text
    /// models: the analyzer adds a tree for a path it did not enumerate, and an empty tree
    /// declares nothing. So, exactly as for an edit, only errors this file itself introduces are
    /// grounds for refusal - a project that was already broken stays creatable.
    /// </para>
    /// </summary>
    private async Task<(bool Rejected, string? Diagnostics, bool Verified)> VerifyAsync(
        string fullPath,
        string relativePath,
        string content,
        CancellationToken cancellationToken)
    {
        if (!_options.VerifyEditsBeforeWrite || !_analyzer.Handles(fullPath))
        {
            return (false, null, false);
        }

        DiagnosticReport syntax = _analyzer.CheckSyntax(relativePath, content);
        if (!syntax.Ok)
        {
            DiagnosticSummary summary = _summarizer.Summarise(syntax, "Syntax check of the new file failed.");
            return (_options.RejectEditsThatBreakTheBuild, summary.Text, true);
        }

        DiagnosticReport before = await _analyzer
            .CheckEditAsync(fullPath, string.Empty, cancellationToken).ConfigureAwait(false);
        DiagnosticReport after = await _analyzer
            .CheckEditAsync(fullPath, content, cancellationToken).ConfigureAwait(false);

        if (before.FailureReason is not null || after.FailureReason is not null)
        {
            // An inconclusive compile is not a failed compile - a file created outside any
            // project, say. Say so and let it through; build remains the authoritative gate.
            return (false, after.FailureReason ?? before.FailureReason, false);
        }

        IReadOnlyList<CodeDiagnostic> introduced = Introduced(before, after);
        if (introduced.Count == 0)
        {
            return (false, null, true);
        }

        DiagnosticSummary introducedSummary = _summarizer.Summarise(
            introduced,
            $"This file would introduce {introduced.Count} new compile error(s).");

        return (_options.RejectEditsThatBreakTheBuild, introducedSummary.Text, true);
    }

    private static IReadOnlyList<CodeDiagnostic> Introduced(DiagnosticReport before, DiagnosticReport after)
    {
        HashSet<string> existing = new(
            before.Diagnostics.Where(d => d.IsError).Select(Fingerprint),
            StringComparer.Ordinal);

        return [.. after.Diagnostics.Where(d => d.IsError && !existing.Contains(Fingerprint(d)))];
    }

    private static string Fingerprint(CodeDiagnostic diagnostic) =>
        $"{diagnostic.Id}|{diagnostic.FilePath}|{diagnostic.Message}";

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
