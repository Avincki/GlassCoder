using System.ComponentModel;
using GlassCoder.Tools.Guardrails;
using GlassCoder.Tools.Registry;
using GlassCoder.Tools.Verification;
using Microsoft.Extensions.Options;

namespace GlassCoder.Tools.FileSystem;

/// <summary>Result payload of <c>read_file</c>.</summary>
/// <param name="Path">Repo-relative path that was read.</param>
/// <param name="Content">The requested lines, joined with newlines.</param>
/// <param name="StartLine">1-based line number of the first returned line.</param>
/// <param name="EndLine">1-based line number of the last returned line.</param>
/// <param name="TotalLines">Total lines in the file, so the agent knows what it did not see.</param>
/// <param name="Truncated">Whether lines were withheld because of the line cap.</param>
/// <param name="LineEndings">What the file uses: crlf, lf, mixed or none.</param>
/// <param name="ClippedLines">How many returned lines were too long to show whole.</param>
public sealed record ReadFileResult(
    [property: Description("Repo-relative path that was read.")] string Path,
    [property: Description("The requested lines, joined with the line ending the file itself uses.")] string Content,
    [property: Description("1-based line number of the first returned line.")] int StartLine,
    [property: Description("1-based line number of the last returned line.")] int EndLine,
    [property: Description("Total number of lines in the file.")] int TotalLines,
    [property: Description("True when lines were withheld because of the line cap.")] bool Truncated,
    [property: Description("How this file ends its lines: crlf, lf, mixed or none.")] string LineEndings,
    [property: Description("How many returned lines were too long and are shown truncated. Those lines cannot be quoted to edit_file.")] int ClippedLines);

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
    [Description("Read a text file. Read before editing: an edit must quote an exact, unique string from "
        + "the file. In a large C# file take the outline first, then the one range you need.")]
    public ToolObservation<ReadFileResult> ReadFile(
        [Description("Repo-relative path, e.g. src/Agent/AgentLoop.cs.")]
        string path,
        [Description("1-based line to start from.")]
        int startLine = 1,
        [Description("Maximum lines to return. Ask for less when you need one region.")]
        int maxLines = 400,
        [Description("For C#, return the declarations and their line numbers instead of the code.")]
        bool outline = false)
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

        string text;
        try
        {
            text = File.ReadAllText(verdict.FullPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Observation.Fail<ReadFileResult>(ToolName, ToolErrorCodes.Unreadable, ex.Message);
        }

        // Split without losing what the file actually is. The previous ReadAllLines/join through
        // Environment.NewLine handed the model a reconstruction: a file's real endings were
        // replaced by the platform's, so what it was shown was not what was on disk - while
        // edit_file went on to demand exactly what was on disk.
        string[] lines = text.ReplaceLineEndings(TextFile.Lf).Split(TextFile.Lf);
        if (lines.Length > 1 && lines[^1].Length == 0)
        {
            // A trailing newline terminates the last line rather than starting an empty one.
            lines = lines[..^1];
        }

        if (outline)
        {
            return Outline(verdict.RelativePath!, text, lines.Length);
        }

        int effectiveMax = Math.Min(maxLines, _options.MaxLinesPerRead);
        int firstIndex = Math.Min(startLine - 1, Math.Max(lines.Length - 1, 0));
        int count = Math.Min(effectiveMax, Math.Max(lines.Length - firstIndex, 0));
        bool truncated = firstIndex + count < lines.Length;

        string[] window = [.. lines.Skip(firstIndex).Take(count)];
        int clipped = window.Count(line => line.Length > _options.MaxLineLength);

        string content = string.Join(
            TextFile.DominantNewLine(text),
            window.Select(line => WorkspaceFiles.Clip(line, _options.MaxLineLength)));

        ReadFileResult result = new(
            verdict.RelativePath!,
            content,
            lines.Length == 0 ? 0 : firstIndex + 1,
            firstIndex + count,
            lines.Length,
            truncated,
            TextFile.DescribeEndings(text),
            clipped);

        // A truncated read names its own continuation. Run c5eb67f6 needed lines ~70-95, paged
        // with a parameter this tool does not have, and re-read the head thirteen times; a
        // summary that says how to get the rest turns the second attempt into the right one.
        string summary = truncated
            ? $"Read lines {result.StartLine}-{result.EndLine} of {lines.Length} from {result.Path} (truncated). " +
              $"Continue with startLine: {result.EndLine + 1}" +
              (Path.GetExtension(result.Path).Equals(".cs", StringComparison.OrdinalIgnoreCase)
                  ? ", or use outline: true for the file's shape with line numbers."
                  : ".")
            : $"Read lines {result.StartLine}-{result.EndLine} of {lines.Length} from {result.Path}.";

        if (clipped > 0)
        {
            // Said out loud, because a clipped line is the one thing here that cannot be quoted
            // back to edit_file - it is not what the file holds.
            summary += $" {clipped} line(s) were too long and are shown truncated; do not quote those to edit_file.";
        }

        return Observation.Ok(ToolName, result, summary);
    }

    /// <summary>
    /// The file's declarations and where they are, instead of its text (workplan task 47).
    /// <para>
    /// Orienting in an unfamiliar file used to cost the whole file. This costs its shape, and
    /// every entry carries the line number that turns the follow-up into one ranged read. It
    /// needs only the syntax tree, so it works on a file whose project has never been built.
    /// </para>
    /// </summary>
    private ToolObservation<ReadFileResult> Outline(string relativePath, string text, int totalLines)
    {
        if (!relativePath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
        {
            return Observation.Fail<ReadFileResult>(
                ToolName,
                ToolErrorCodes.InvalidArgument,
                $"'{relativePath}' is not C#, and an outline is read from the C# syntax tree.",
                "Read it without outline, or use grep to find the region you need.");
        }

        IReadOnlyList<SourceSymbol> symbols = CodeStructure.Outline(relativePath, text, _options.MaxLinesPerRead);
        if (symbols.Count == 0)
        {
            return Observation.Fail<ReadFileResult>(
                ToolName,
                ToolErrorCodes.NotFound,
                $"'{relativePath}' declares nothing an outline can show.",
                "Read it without outline.");
        }

        ReadFileResult result = new(
            relativePath,
            CodeStructure.Render(symbols),
            symbols[0].Line,
            symbols[^1].EndLine,
            totalLines,
            Truncated: false,
            TextFile.DescribeEndings(text),
            ClippedLines: 0);

        return Observation.Ok(
            ToolName,
            result,
            $"{symbols.Count} declaration(s) in {relativePath}. Read a line range for any body.");
    }
}
