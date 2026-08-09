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
/// It refuses to overwrite <em>unless asked to</em>. Creation and modification stayed separate
/// verbs for a long time, so that "replace one unique string" was the only way an existing file
/// could change - but that left no way at all to replace a generated stub, and a run once spent
/// seventeen consecutive steps failing to edit its way to the same result (workplan task 45).
/// <c>overwrite</c> is explicit, defaults to false, and goes through the same change log,
/// pre-write check and approval gate as everything else, so it is a named verb rather than the
/// hole an upserting create tool would have been.
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
    private readonly VerificationRefusalTracker _refusals;
    private readonly ILogger<CreateFileTool> _logger;

    /// <summary>Creates the tool.</summary>
    public CreateFileTool(
        IPathGuard guard,
        ICodeAnalyzer analyzer,
        DiagnosticSummarizer summarizer,
        IOptions<VerificationOptions> options,
        IChangeLog? changes = null,
        IApprovalGate? approval = null,
        ILogger<CreateFileTool>? logger = null,
        VerificationRefusalTracker? refusals = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        _guard = guard;
        _analyzer = analyzer;
        _summarizer = summarizer;
        _changes = changes ?? new ChangeLog();
        _approval = approval ?? new AutoApprovalGate(Options.Create(new ApprovalOptions()));
        _options = options.Value;
        _refusals = refusals ?? new VerificationRefusalTracker();
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<CreateFileTool>.Instance;
    }

    /// <summary>Creates a new file and writes its full contents.</summary>
    [GlassCoderTool(ToolName, Order = 35)]
    [Description("Write a file's complete contents, creating parent directories. With overwrite: true it "
        + "replaces an existing file. Compile-checked before it is written.")]
    public async Task<ToolObservation<CreateFileResult>> CreateFileAsync(
        [Description("Repo-relative path for the file.")]
        string path,
        [Description("The complete contents of the file.")]
        string content,
        [Description("Replace the file if it exists. False refuses instead.")]
        bool overwrite = false,
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

        bool exists = File.Exists(verdict.FullPath);
        if (exists && !overwrite)
        {
            return Observation.Fail<CreateFileResult>(
                ToolName,
                ToolErrorCodes.AlreadyExists,
                $"'{verdict.RelativePath}' already exists.",
                "Use edit_file to change part of it, or call this again with overwrite: true to replace it.");
        }

        if (Directory.Exists(verdict.FullPath))
        {
            return Observation.Fail<CreateFileResult>(
                ToolName,
                ToolErrorCodes.AlreadyExists,
                $"'{verdict.RelativePath}' is a directory.");
        }

        string before = string.Empty;
        if (exists)
        {
            try
            {
                before = await File.ReadAllTextAsync(verdict.FullPath, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return Observation.Fail<CreateFileResult>(ToolName, ToolErrorCodes.Unreadable, ex.Message);
            }

            // Keep the file's own line endings rather than imposing the model's, so replacing a
            // file wholesale does not leave it half CRLF and half LF.
            content = TextFile.WithNewLine(content, TextFile.DominantNewLine(before));
        }

        // Recorded before it is attempted, so a refused write is as visible in the UI as one that
        // landed. An empty "before" is what makes a creation render as pure addition, while a
        // replacement renders as the diff it actually is.
        CodeChange change = _changes.Propose(verdict.RelativePath!, ToolName, before, content);

        (bool rejected, string? diagnostics, bool verified) =
            await VerifyAsync(verdict.FullPath, verdict.RelativePath!, content, cancellationToken).ConfigureAwait(false);

        if (rejected)
        {
            _changes.Update(change.Id, ChangeStatus.Rejected, "Verification refused the edit.", diagnostics);
            return Observation.Fail<CreateFileResult>(
                ToolName,
                ToolErrorCodes.VerificationFailed,
                $"'{verdict.RelativePath}' was not written: it would not compile.\n{diagnostics}",
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

        _logger.LogInformation(
            "{Verb} {Path}: {Lines} lines", exists ? "Replaced" : "Created", verdict.RelativePath, lines);

        CreateFileResult result = new(verdict.RelativePath!, lines, verified, diagnostics, change.Id);
        return Observation.Ok(
            ToolName,
            result,
            $"{(exists ? "Replaced" : "Created")} {verdict.RelativePath} ({lines} lines)." +
            $"{OrphanNotice(verdict.FullPath)}{XamlNotices.Describe(verdict.FullPath!, content)}");
    }

    /// <summary>
    /// Says, in the observation itself, when a compilable file lands outside every project.
    /// <para>
    /// The harness always knew - "Nothing buildable found" went to the log at the exact moment
    /// run d21eb210 created its class at the workspace root - but the model never heard it, and
    /// three sessions in a row began with a source file the eventual project would have to be
    /// contorted around. The warning has to be in the message the model is already reading.
    /// </para>
    /// </summary>
    private string OrphanNotice(string fullPath)
    {
        if (!_analyzer.Handles(fullPath))
        {
            return string.Empty;
        }

        string? project;
        try
        {
            project = ProjectLocator.FindProjectFile(fullPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return string.Empty;
        }

        // A project above the repo root is someone else's; inside the workspace it must sit.
        bool owned = project is not null &&
            project.StartsWith(_guard.RepoRoot, StringComparison.OrdinalIgnoreCase);

        return owned
            ? string.Empty
            : " No project contains this file, so nothing will compile or test it. Scaffold a project first " +
              "with dotnet_project (new) and create source files inside its directory - a file wired in from " +
              "outside is how name collisions and duplicate-compile errors start.";
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
            _refusals.Forget(fullPath);
            return (false, after.FailureReason ?? before.FailureReason, false);
        }

        IReadOnlyList<CodeDiagnostic> introduced = Introduced(before, after);
        if (introduced.Count == 0)
        {
            _refusals.Forget(fullPath);
            return (false, null, true);
        }

        DiagnosticSummary introducedSummary = _summarizer.Summarise(
            introduced,
            $"This file would introduce {introduced.Count} new compile error(s).");

        // The refusal carries the diagnosis, not just the error: where a missing type actually
        // lives, and the reference or using that reaches it (runs 05e1bedb, a408b61b).
        string hints = SymbolHints.Describe(
            introduced, fullPath, _guard.RepoRoot,
            identifiers => _analyzer.LocateInReferences(fullPath, identifiers));

        if (!_options.RejectEditsThatBreakTheBuild)
        {
            return (false, introducedSummary.Text + hints, true);
        }

        // The loop-breaker (run 5c071f37): an in-memory check that keeps refusing the same file
        // with the same errors may be blind to something - generated code, most likely - and it
        // has no way to learn. Conceding one attempt past the limit proved one too many: run
        // a408b61b was promised "after 3 the write will be allowed", reasonably never resubmitted
        // a thrice-refused file, and shipped no tests - so the limit itself is now the attempt
        // that goes through. Only this rung stands aside; a syntax error above is in the file
        // itself and never a blind spot.
        int strikes = _refusals.RecordRefusal(fullPath, VerificationRefusalTracker.FingerprintOf(introduced));
        if (_options.MaxIdenticalRefusals > 0 && strikes >= _options.MaxIdenticalRefusals)
        {
            _refusals.Forget(fullPath);
            return (false,
                $"Written on identical attempt {strikes}: the in-memory check keeps reporting the same " +
                "errors, which can mean it is blind to generated code rather than that the content is " +
                "wrong. The build tool is the authoritative gate - run it next.\n" +
                introducedSummary.Text + hints,
                true);
        }

        string strikeNote = _options.MaxIdenticalRefusals > 0 && strikes > 1
            ? $"\nThis exact refusal has now happened {strikes} times. Identical attempt " +
              $"{_options.MaxIdenticalRefusals} will be written as-is and judged by the build tool."
            : string.Empty;

        return (true, introducedSummary.Text + hints + strikeNote, true);
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
