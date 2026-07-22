using System.ComponentModel;
using GlassCoder.Tools.Guardrails;
using GlassCoder.Tools.Registry;
using Microsoft.Extensions.Options;

namespace GlassCoder.Tools.FileSystem;

/// <summary>Result payload of <c>glob</c>.</summary>
/// <param name="Paths">Matching repo-relative paths, sorted.</param>
/// <param name="TotalMatches">Number of paths returned.</param>
/// <param name="Truncated">Whether the result cap cut the list short.</param>
public sealed record GlobResult(
    [property: Description("Matching repo-relative paths, sorted alphabetically.")] IReadOnlyList<string> Paths,
    [property: Description("Number of paths returned.")] int TotalMatches,
    [property: Description("True when more paths matched than were returned.")] bool Truncated);

/// <summary>
/// <c>glob</c> - one of the three Phase 0 read-only tools (CLAUDE.md §17, workplan task 9).
/// </summary>
public sealed class GlobTool : IToolSet
{
    private const string ToolName = "glob";

    private readonly IPathGuard _guard;
    private readonly ToolsOptions _options;

    /// <summary>Creates the tool.</summary>
    public GlobTool(IPathGuard guard, IOptions<ToolsOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _guard = guard;
        _options = options.Value;
    }

    /// <summary>Lists workspace files matching a glob.</summary>
    [GlassCoderTool(ToolName, Order = 30)]
    [Description("List workspace files matching a glob pattern. Use this to find where something lives "
        + "before reading or grepping it.")]
    public ToolObservation<GlobResult> Glob(
        [Description("Glob pattern, for example '**/*.cs' or 'src/**/Agent*.cs'.")]
        string pattern,
        [Description("Directory to search from, relative to the repository root. Use '.' for the whole repository.")]
        string path = ".",
        [Description("Maximum number of paths to return.")]
        int maxResults = 200,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(pattern))
        {
            return Observation.Fail<GlobResult>(ToolName, ToolErrorCodes.InvalidArgument, "pattern is required.");
        }

        PathGuardResult verdict = _guard.Resolve(path, PathAccess.Read);
        if (!verdict.Allowed || verdict.FullPath is null)
        {
            return Observation.Fail<GlobResult>(ToolName, ToolErrorCodes.PathNotAllowed, verdict.Reason!);
        }

        if (!Directory.Exists(verdict.FullPath))
        {
            return Observation.Fail<GlobResult>(
                ToolName,
                ToolErrorCodes.NotFound,
                $"'{verdict.RelativePath}' is not a directory.");
        }

        int limit = Math.Clamp(maxResults, 1, _options.MaxGlobResults);
        List<string> paths = [];
        bool truncated = false;

        try
        {
            foreach (string file in WorkspaceFiles.Enumerate(
                         _guard, verdict.FullPath, pattern, _options.MaxFilesSearched, cancellationToken))
            {
                if (paths.Count >= limit)
                {
                    truncated = true;
                    break;
                }

                paths.Add(_guard.ToRelativePath(file));
            }
        }
        catch (OperationCanceledException)
        {
            return Observation.Fail<GlobResult>(ToolName, ToolErrorCodes.Timeout, "The search was cancelled.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Observation.Fail<GlobResult>(ToolName, ToolErrorCodes.Unreadable, ex.Message);
        }

        paths.Sort(StringComparer.Ordinal);
        GlobResult result = new(paths, paths.Count, truncated);
        string summary = truncated
            ? $"{paths.Count} paths matched '{pattern}' (capped at {limit})."
            : $"{paths.Count} paths matched '{pattern}'.";

        return Observation.Ok(ToolName, result, summary);
    }
}
