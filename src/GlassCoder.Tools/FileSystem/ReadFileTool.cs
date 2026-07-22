using System.ComponentModel;
using GlassCoder.Tools.Guardrails;
using GlassCoder.Tools.Registry;
using Microsoft.Extensions.Options;

namespace GlassCoder.Tools.FileSystem;

/// <summary>Result payload of <c>read_file</c>.</summary>
/// <param name="Path">Repo-relative path that was read.</param>
/// <param name="Content">The requested lines, joined with newlines.</param>
/// <param name="StartLine">1-based line number of the first returned line.</param>
/// <param name="EndLine">1-based line number of the last returned line.</param>
/// <param name="TotalLines">Total lines in the file, so the agent knows what it did not see.</param>
/// <param name="Truncated">Whether lines were withheld because of the line cap.</param>
public sealed record ReadFileResult(
    [property: Description("Repo-relative path that was read.")] string Path,
    [property: Description("The requested lines, joined with newlines.")] string Content,
    [property: Description("1-based line number of the first returned line.")] int StartLine,
    [property: Description("1-based line number of the last returned line.")] int EndLine,
    [property: Description("Total number of lines in the file.")] int TotalLines,
    [property: Description("True when lines were withheld because of the line cap.")] bool Truncated);

/// <summary>
/// <c>read_file</c> - one of the three Phase 0 read-only tools (CLAUDE.md §17, workplan task 9).
/// </summary>
public sealed class ReadFileTool : IToolSet
{
    private const string ToolName = "read_file";

    private readonly IPathGuard _guard;
    private readonly ToolsOptions _options;

    /// <summary>Creates the tool.</summary>
    public ReadFileTool(IPathGuard guard, IOptions<ToolsOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _guard = guard;
        _options = options.Value;
    }

    /// <summary>Reads a slice of a text file.</summary>
    [GlassCoderTool(ToolName, Order = 10)]
    [Description("Read a text file from the workspace and return it with 1-based line numbers. "
        + "Read before editing: an edit must quote an exact, unique string from the file.")]
    public ToolObservation<ReadFileResult> ReadFile(
        [Description("Path to the file, relative to the repository root, for example src/GlassCoder.Core/Agent/AgentLoop.cs.")]
        string path,
        [Description("1-based line number to start reading from. Use 1 for the beginning of the file.")]
        int startLine = 1,
        [Description("Maximum number of lines to return. Ask for a smaller window when you only need one region.")]
        int maxLines = 400)
    {
        PathGuardResult verdict = _guard.Resolve(path, PathAccess.Read);
        if (!verdict.Allowed || verdict.FullPath is null)
        {
            return Observation.Fail<ReadFileResult>(ToolName, ToolErrorCodes.PathNotAllowed, verdict.Reason!);
        }

        if (Directory.Exists(verdict.FullPath))
        {
            return Observation.Fail<ReadFileResult>(
                ToolName,
                ToolErrorCodes.InvalidArgument,
                $"'{verdict.RelativePath}' is a directory.",
                "Use glob to list a directory's files.");
        }

        FileInfo file = new(verdict.FullPath);
        if (!file.Exists)
        {
            return Observation.Fail<ReadFileResult>(
                ToolName,
                ToolErrorCodes.NotFound,
                $"'{verdict.RelativePath}' does not exist.",
                "Use glob to find the path you meant.");
        }

        if (file.Length > _options.MaxFileBytes)
        {
            return Observation.Fail<ReadFileResult>(
                ToolName,
                ToolErrorCodes.Unreadable,
                $"'{verdict.RelativePath}' is {file.Length} bytes, over the {_options.MaxFileBytes} byte limit.",
                "Use grep to find the region you need instead of reading the whole file.");
        }

        if (WorkspaceFiles.IsBinary(verdict.FullPath))
        {
            return Observation.Fail<ReadFileResult>(
                ToolName,
                ToolErrorCodes.Unreadable,
                $"'{verdict.RelativePath}' is not a text file.");
        }

        if (startLine < 1)
        {
            return Observation.Fail<ReadFileResult>(
                ToolName,
                ToolErrorCodes.InvalidArgument,
                $"startLine must be 1 or greater, got {startLine}.");
        }

        if (maxLines < 1)
        {
            return Observation.Fail<ReadFileResult>(
                ToolName,
                ToolErrorCodes.InvalidArgument,
                $"maxLines must be 1 or greater, got {maxLines}.");
        }

        string[] lines;
        try
        {
            lines = File.ReadAllLines(verdict.FullPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Observation.Fail<ReadFileResult>(ToolName, ToolErrorCodes.Unreadable, ex.Message);
        }

        int effectiveMax = Math.Min(maxLines, _options.MaxLinesPerRead);
        int firstIndex = Math.Min(startLine - 1, Math.Max(lines.Length - 1, 0));
        int count = Math.Min(effectiveMax, Math.Max(lines.Length - firstIndex, 0));
        bool truncated = firstIndex + count < lines.Length;

        string content = string.Join(
            Environment.NewLine,
            lines.Skip(firstIndex).Take(count).Select(line => WorkspaceFiles.Clip(line, _options.MaxLineLength)));

        ReadFileResult result = new(
            verdict.RelativePath!,
            content,
            lines.Length == 0 ? 0 : firstIndex + 1,
            firstIndex + count,
            lines.Length,
            truncated);

        string summary = truncated
            ? $"Read lines {result.StartLine}-{result.EndLine} of {lines.Length} from {result.Path} (truncated)."
            : $"Read lines {result.StartLine}-{result.EndLine} of {lines.Length} from {result.Path}.";

        return Observation.Ok(ToolName, result, summary);
    }
}
