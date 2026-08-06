using System.ComponentModel;
using System.Text.Json.Serialization;
using GlassCoder.Tools.Changes;
using GlassCoder.Tools.Guardrails;
using GlassCoder.Tools.Registry;
using Microsoft.Extensions.Logging;

namespace GlassCoder.Tools.FileSystem;

/// <summary>What <c>file_operation</c> is being asked to do.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<FileOperation>))]
public enum FileOperation
{
    /// <summary>Remove a file from the workspace.</summary>
    Delete,

    /// <summary>Move or rename a file, contents unchanged.</summary>
    Move,

    /// <summary>Put a file back the way this run first found it.</summary>
    Revert,
}

/// <summary>Result payload of <c>file_operation</c>.</summary>
/// <param name="Operation">What was done.</param>
/// <param name="Path">The file it was done to.</param>
/// <param name="Destination">Where it went, for a move.</param>
/// <param name="Lines">Lines in the file the operation acted on.</param>
/// <param name="ChangeId">Identifier of this change in the change log, for the UI to link to.</param>
public sealed record FileOperationResult(
    [property: Description("The operation that ran.")] string Operation,
    [property: Description("Repo-relative path it applied to.")] string Path,
    [property: Description("Repo-relative destination, for a move.")] string? Destination,
    [property: Description("Lines in the file the operation acted on.")] int Lines,
    [property: Description("Identifier of this change in the change log.")] string? ChangeId);

/// <summary>
/// <c>file_operation</c> - remove, relocate and undo (workplan tasks 49 and 50).
/// <para>
/// Until now the agent could create a file and change one, and nothing else. That is a real gap
/// rather than a theoretical one: <c>list_projects</c> reports a project nested inside another -
/// where the SDK glob compiles the inner sources into the outer and every resulting error points
/// at the wrong file - and the agent could diagnose that and not fix it, because fixing it means
/// moving a file.
/// </para>
/// <para>
/// Three verbs on one tool rather than three tools, following <c>dotnet_project</c>. Tool schemas
/// are re-sent on every call and measure about 300 tokens each against a step-0 request of
/// roughly 3,200 - so a new top-level name costs ten per cent of every request for the whole run,
/// whether or not the model ever calls it. Capability belongs on the tools that exist.
/// </para>
/// </summary>
public sealed class FileOperationTool : IToolSet
{
    private const string ToolName = "file_operation";

    private readonly IPathGuard _guard;
    private readonly IChangeLog _changes;
    private readonly IApprovalGate _approval;
    private readonly ILogger<FileOperationTool> _logger;

    /// <summary>Creates the tool.</summary>
    public FileOperationTool(
        IPathGuard guard,
        IChangeLog? changes = null,
        IApprovalGate? approval = null,
        ILogger<FileOperationTool>? logger = null)
    {
        _guard = guard;
        _changes = changes ?? new ChangeLog();
        _approval = approval ?? new AutoApprovalGate(
            Microsoft.Extensions.Options.Options.Create(new ApprovalOptions()));
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<FileOperationTool>.Instance;
    }

    /// <summary>Deletes, moves or reverts a file.</summary>
    [GlassCoderTool(ToolName, Order = 41)]
    [Description("Delete a file, move or rename one, or revert one to how this run found it.")]
    public async Task<ToolObservation<FileOperationResult>> RunAsync(
        [Description("What to do.")]
        FileOperation operation,
        [Description("Repo-relative file to act on.")]
        string path,
        [Description("Repo-relative destination. Move only.")]
        string? destination = null,
        CancellationToken cancellationToken = default)
    {
        PathGuardResult source = _guard.Resolve(path, PathAccess.Write);
        if (!source.Allowed || source.FullPath is null)
        {
            return Fail(ToolErrorCodes.PathNotAllowed, source.Reason!);
        }

        if (Directory.Exists(source.FullPath))
        {
            // A tool that can empty bin/ is a tool that can empty something else. Directories are
            // refused outright rather than recursed.
            return Fail(
                ToolErrorCodes.InvalidArgument,
                $"'{source.RelativePath}' is a directory.",
                "This tool acts on one file at a time.");
        }

        return operation switch
        {
            FileOperation.Delete => await DeleteAsync(source, cancellationToken).ConfigureAwait(false),
            FileOperation.Move => await MoveAsync(source, destination, cancellationToken).ConfigureAwait(false),
            _ => await RevertAsync(source, cancellationToken).ConfigureAwait(false),
        };
    }

    private async Task<ToolObservation<FileOperationResult>> DeleteAsync(
        PathGuardResult source, CancellationToken cancellationToken)
    {
        if (!File.Exists(source.FullPath))
        {
            return Fail(ToolErrorCodes.NotFound, $"'{source.RelativePath}' does not exist.");
        }

        string? before = await ReadAsync(source.FullPath!).ConfigureAwait(false);
        if (before is null)
        {
            return Fail(ToolErrorCodes.Unreadable, $"'{source.RelativePath}' could not be read.");
        }

        // Recorded before it happens, and as before-text to nothing, so the Changes surface shows
        // a deletion as the removal it is rather than as an absence nobody can account for.
        CodeChange change = _changes.Propose(source.RelativePath!, ToolName, before, string.Empty);

        ApprovalDecision decision = await _approval.RequestAsync(change, cancellationToken).ConfigureAwait(false);
        if (!decision.Approved)
        {
            _changes.Update(change.Id, ChangeStatus.Rejected, decision.Reason ?? "A human refused the deletion.");
            return Fail(
                ToolErrorCodes.ApprovalRefused,
                decision.Reason ?? $"A human refused to delete '{source.RelativePath}'.",
                "Nothing has been removed.");
        }

        try
        {
            File.Delete(source.FullPath!);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _changes.Update(change.Id, ChangeStatus.Rejected, ex.Message);
            return Fail(ToolErrorCodes.Unreadable, ex.Message);
        }

        _changes.Update(change.Id, ChangeStatus.Applied);
        int lines = Lines(before);
        _logger.LogInformation("Deleted {Path}: {Lines} lines", source.RelativePath, lines);

        // The change log holds the deleted text, and the model is told so here: run d21eb210
        // deleted the only copy of its deliverable and, when the build then missed it, removed
        // the reference instead of the mistake - not knowing that restoring was one call away.
        return Observation.Ok(
            ToolName,
            new FileOperationResult("delete", source.RelativePath!, null, lines, change.Id),
            $"Deleted {source.RelativePath} ({lines} lines). Its content is preserved in the change log; " +
            "if this delete turns out to be wrong, re-create the file rather than removing references to it.");
    }

    private async Task<ToolObservation<FileOperationResult>> MoveAsync(
        PathGuardResult source, string? destination, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(destination))
        {
            return Fail(ToolErrorCodes.InvalidArgument, "move needs a destination.");
        }

        PathGuardResult target = _guard.Resolve(destination, PathAccess.Write);
        if (!target.Allowed || target.FullPath is null)
        {
            return Fail(ToolErrorCodes.PathNotAllowed, target.Reason!);
        }

        if (!File.Exists(source.FullPath))
        {
            return Fail(ToolErrorCodes.NotFound, $"'{source.RelativePath}' does not exist.");
        }

        if (File.Exists(target.FullPath) || Directory.Exists(target.FullPath))
        {
            return Fail(
                ToolErrorCodes.AlreadyExists,
                $"'{target.RelativePath}' already exists.",
                "Delete it first, or move to a path that is free.");
        }

        string? content = await ReadAsync(source.FullPath!).ConfigureAwait(false);
        if (content is null)
        {
            return Fail(ToolErrorCodes.Unreadable, $"'{source.RelativePath}' could not be read.");
        }

        // Two entries, because CodeChange.Path is singular - and because a removal and an addition
        // is how a reviewer wants to read a move anyway.
        CodeChange removal = _changes.Propose(source.RelativePath!, ToolName, content, string.Empty);

        // Approval is asked once, on the half that puts something at risk. The addition is a
        // consequence of saying yes to the removal, and prompting twice for one decision is how
        // an approval gate teaches people to click through it.
        ApprovalDecision decision = await _approval.RequestAsync(removal, cancellationToken).ConfigureAwait(false);
        if (!decision.Approved)
        {
            _changes.Update(removal.Id, ChangeStatus.Rejected, decision.Reason ?? "A human refused the move.");
            return Fail(
                ToolErrorCodes.ApprovalRefused,
                decision.Reason ?? $"A human refused to move '{source.RelativePath}'.",
                "Nothing has been moved.");
        }

        CodeChange addition = _changes.Propose(target.RelativePath!, ToolName, string.Empty, content);

        try
        {
            string? directory = Path.GetDirectoryName(target.FullPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.Move(source.FullPath!, target.FullPath!);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _changes.Update(removal.Id, ChangeStatus.Rejected, ex.Message);
            _changes.Update(addition.Id, ChangeStatus.Rejected, ex.Message);
            return Fail(ToolErrorCodes.Unreadable, ex.Message);
        }

        _changes.Update(removal.Id, ChangeStatus.Applied);
        _changes.Update(addition.Id, ChangeStatus.Applied);

        int lines = Lines(content);
        _logger.LogInformation("Moved {From} to {To}: {Lines} lines", source.RelativePath, target.RelativePath, lines);

        return Observation.Ok(
            ToolName,
            new FileOperationResult("move", source.RelativePath!, target.RelativePath, lines, addition.Id),
            $"Moved {source.RelativePath} to {target.RelativePath}.");
    }

    /// <summary>
    /// Puts a file back the way this run first found it.
    /// <para>
    /// Bounded to this run's own changes on purpose. This is a way for the agent to undo its own
    /// work cheaply, not a general undo of the working tree - it must never be able to discard
    /// something the operator did by hand.
    /// </para>
    /// </summary>
    private async Task<ToolObservation<FileOperationResult>> RevertAsync(
        PathGuardResult source, CancellationToken cancellationToken)
    {
        string runId = RunContext.Current.RunId;

        // The earliest applied change is the one holding what the file looked like before this
        // run touched it. Any later one would only undo the most recent edit.
        CodeChange? first = _changes.All()
            .FirstOrDefault(c =>
                string.Equals(c.RunId, runId, StringComparison.Ordinal) &&
                string.Equals(c.Path, source.RelativePath, StringComparison.OrdinalIgnoreCase) &&
                c.Status == ChangeStatus.Applied);

        if (first is null)
        {
            return Fail(
                ToolErrorCodes.NotFound,
                $"This run has not changed '{source.RelativePath}', so there is nothing to revert.",
                "Use list_changes to see what this run has changed.");
        }

        string current = File.Exists(source.FullPath)
            ? await ReadAsync(source.FullPath!).ConfigureAwait(false) ?? string.Empty
            : string.Empty;

        if (string.Equals(current, first.BeforeText, StringComparison.Ordinal))
        {
            return Fail(
                ToolErrorCodes.InvalidArgument,
                $"'{source.RelativePath}' is already as this run found it.");
        }

        CodeChange change = _changes.Propose(source.RelativePath!, ToolName, current, first.BeforeText);

        ApprovalDecision decision = await _approval.RequestAsync(change, cancellationToken).ConfigureAwait(false);
        if (!decision.Approved)
        {
            _changes.Update(change.Id, ChangeStatus.Rejected, decision.Reason ?? "A human refused the revert.");
            return Fail(
                ToolErrorCodes.ApprovalRefused,
                decision.Reason ?? $"A human refused to revert '{source.RelativePath}'.",
                "Nothing has been changed.");
        }

        try
        {
            if (first.BeforeText.Length == 0)
            {
                // The run created this file, so putting it back means it should not exist.
                if (File.Exists(source.FullPath))
                {
                    File.Delete(source.FullPath!);
                }
            }
            else
            {
                string? directory = Path.GetDirectoryName(source.FullPath);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                await File.WriteAllTextAsync(source.FullPath!, first.BeforeText, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _changes.Update(change.Id, ChangeStatus.Rejected, ex.Message);
            return Fail(ToolErrorCodes.Unreadable, ex.Message);
        }

        _changes.Update(change.Id, ChangeStatus.Applied);
        int lines = Lines(first.BeforeText);
        _logger.LogInformation("Reverted {Path} to how this run found it", source.RelativePath);

        return Observation.Ok(
            ToolName,
            new FileOperationResult("revert", source.RelativePath!, null, lines, change.Id),
            first.BeforeText.Length == 0
                ? $"Reverted {source.RelativePath}: this run created it, so it has been removed."
                : $"Reverted {source.RelativePath} to how this run found it ({lines} lines).");
    }

    private static async Task<string?> ReadAsync(string fullPath)
    {
        try
        {
            return await File.ReadAllTextAsync(fullPath).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static int Lines(string text) =>
        text.Length == 0 ? 0 : text.ReplaceLineEndings(TextFile.Lf).TrimEnd('\n').Split(TextFile.Lf).Length;

    private static ToolObservation<FileOperationResult> Fail(string code, string message, string? hint = null) =>
        Observation.Fail<FileOperationResult>(ToolName, code, message, hint);
}
