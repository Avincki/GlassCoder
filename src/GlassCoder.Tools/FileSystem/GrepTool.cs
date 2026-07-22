using System.ComponentModel;
using System.Text.RegularExpressions;
using GlassCoder.Tools.Guardrails;
using GlassCoder.Tools.Registry;
using Microsoft.Extensions.Options;

namespace GlassCoder.Tools.FileSystem;

/// <summary>One line that matched a <c>grep</c> pattern.</summary>
/// <param name="Path">Repo-relative file path.</param>
/// <param name="Line">1-based line number.</param>
/// <param name="Column">1-based column of the match.</param>
/// <param name="Text">The matching line.</param>
public sealed record GrepMatch(
    [property: Description("Repo-relative path of the file containing the match.")] string Path,
    [property: Description("1-based line number of the match.")] int Line,
    [property: Description("1-based column of the match within the line.")] int Column,
    [property: Description("Text of the matching line.")] string Text);

/// <summary>Result payload of <c>grep</c>.</summary>
/// <param name="Matches">Matches, in file then line order.</param>
/// <param name="TotalMatches">Matches returned.</param>
/// <param name="FilesSearched">Files actually opened.</param>
/// <param name="Truncated">Whether the match cap cut the list short.</param>
public sealed record GrepResult(
    [property: Description("Matches, ordered by file then line.")] IReadOnlyList<GrepMatch> Matches,
    [property: Description("Number of matches returned.")] int TotalMatches,
    [property: Description("Number of files searched.")] int FilesSearched,
    [property: Description("True when more matches exist than were returned.")] bool Truncated);

/// <summary>
/// <c>grep</c> - one of the three Phase 0 read-only tools (CLAUDE.md §17, workplan task 9).
/// </summary>
public sealed class GrepTool : IToolSet
{
    private const string ToolName = "grep";

    private readonly IPathGuard _guard;
    private readonly ToolsOptions _options;

    /// <summary>Creates the tool.</summary>
    public GrepTool(IPathGuard guard, IOptions<ToolsOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _guard = guard;
        _options = options.Value;
    }

    /// <summary>Searches workspace files for a regular expression.</summary>
    [GlassCoderTool(ToolName, Order = 20)]
    [Description("Search the workspace for a .NET regular expression and return matching lines with their file and line number. "
        + "This is the cheapest way to locate code - prefer it over reading whole files.")]
    public ToolObservation<GrepResult> Grep(
        [Description("The .NET regular expression to search for, for example 'class\\s+AgentLoop'.")]
        string pattern,
        [Description("Directory to search, relative to the repository root. Use '.' for the whole repository.")]
        string path = ".",
        [Description("Glob limiting which files are searched, for example '**/*.cs'.")]
        string glob = "**/*",
        [Description("Whether the search ignores case.")]
        bool ignoreCase = false,
        [Description("Maximum number of matches to return.")]
        int maxResults = 100,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(pattern))
        {
            return Observation.Fail<GrepResult>(ToolName, ToolErrorCodes.InvalidArgument, "pattern is required.");
        }

        PathGuardResult verdict = _guard.Resolve(path, PathAccess.Read);
        if (!verdict.Allowed || verdict.FullPath is null)
        {
            return Observation.Fail<GrepResult>(ToolName, ToolErrorCodes.PathNotAllowed, verdict.Reason!);
        }

        if (!Directory.Exists(verdict.FullPath))
        {
            return Observation.Fail<GrepResult>(
                ToolName,
                ToolErrorCodes.NotFound,
                $"'{verdict.RelativePath}' is not a directory.");
        }

        Regex regex;
        try
        {
            RegexOptions regexOptions = RegexOptions.None;
            if (ignoreCase)
            {
                regexOptions |= RegexOptions.IgnoreCase;
            }

            regex = new Regex(pattern, regexOptions, TimeSpan.FromMilliseconds(_options.RegexTimeoutMilliseconds));
        }
        catch (ArgumentException ex)
        {
            return Observation.Fail<GrepResult>(
                ToolName,
                ToolErrorCodes.InvalidArgument,
                $"'{pattern}' is not a valid .NET regular expression: {ex.Message}");
        }

        int limit = Math.Clamp(maxResults, 1, _options.MaxGrepMatches);
        List<GrepMatch> matches = [];
        int filesSearched = 0;
        bool truncated = false;

        try
        {
            foreach (string file in WorkspaceFiles.Enumerate(
                         _guard, verdict.FullPath, glob, _options.MaxFilesSearched, cancellationToken))
            {
                if (new FileInfo(file).Length > _options.MaxFileBytes || WorkspaceFiles.IsBinary(file))
                {
                    continue;
                }

                filesSearched++;
                string relative = _guard.ToRelativePath(file);
                int lineNumber = 0;

                foreach (string line in ReadLines(file))
                {
                    lineNumber++;
                    Match match = regex.Match(line);
                    if (!match.Success)
                    {
                        continue;
                    }

                    if (matches.Count >= limit)
                    {
                        truncated = true;
                        break;
                    }

                    matches.Add(new GrepMatch(
                        relative,
                        lineNumber,
                        match.Index + 1,
                        WorkspaceFiles.Clip(line, _options.MaxLineLength)));
                }

                if (truncated)
                {
                    break;
                }
            }
        }
        catch (RegexMatchTimeoutException)
        {
            return Observation.Fail<GrepResult>(
                ToolName,
                ToolErrorCodes.Timeout,
                $"Pattern '{pattern}' took longer than {_options.RegexTimeoutMilliseconds} ms on a single line.",
                "Simplify the pattern - nested quantifiers backtrack catastrophically.");
        }
        catch (OperationCanceledException)
        {
            return Observation.Fail<GrepResult>(ToolName, ToolErrorCodes.Timeout, "The search was cancelled.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Observation.Fail<GrepResult>(ToolName, ToolErrorCodes.Unreadable, ex.Message);
        }

        GrepResult result = new(matches, matches.Count, filesSearched, truncated);
        string summary = truncated
            ? $"{matches.Count} matches in {filesSearched} files (capped at {limit}; narrow the glob or pattern)."
            : $"{matches.Count} matches in {filesSearched} files.";

        return Observation.Ok(ToolName, result, summary);
    }

    private static IEnumerable<string> ReadLines(string file)
    {
        try
        {
            return File.ReadLines(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}
