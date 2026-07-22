using Microsoft.Extensions.FileSystemGlobbing;
using Microsoft.Extensions.Options;

namespace GlassCoder.Tools.Guardrails;

/// <summary>
/// Default <see cref="IPathGuard"/> (workplan task 8).
/// <para>
/// A path is allowed only if, after full normalisation, it sits under one of the roots
/// configured for the requested access, is not excluded by a denied glob, and does not reach
/// its destination through a symbolic link that leaves the allowed set. Normalisation happens
/// before every check, so <c>..</c> traversal and mixed separators are collapsed rather than
/// pattern-matched away.
/// </para>
/// </summary>
public sealed class PathGuard : IPathGuard
{
    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    private readonly string[] _readableRoots;
    private readonly string[] _writableRoots;
    private readonly Matcher? _deniedMatcher;
    private readonly bool _followSymbolicLinks;

    /// <summary>Creates the guard from bound workspace configuration.</summary>
    public PathGuard(IOptions<WorkspaceOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        WorkspaceOptions workspace = options.Value;

        RepoRoot = Normalise(Path.GetFullPath(
            string.IsNullOrWhiteSpace(workspace.RepoRoot) ? "." : workspace.RepoRoot));

        _readableRoots = workspace.ReadablePaths.Count == 0
            ? [RepoRoot]
            : [.. workspace.ReadablePaths.Select(ResolveRoot).Distinct(StringComparer.Ordinal)];

        _writableRoots = [.. workspace.WritablePaths.Select(ResolveRoot).Distinct(StringComparer.Ordinal)];
        _followSymbolicLinks = workspace.FollowSymbolicLinks;

        if (workspace.DeniedGlobs.Count > 0)
        {
            _deniedMatcher = new Matcher(
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
            _deniedMatcher.AddIncludePatterns(workspace.DeniedGlobs);
        }
    }

    /// <inheritdoc />
    public string RepoRoot { get; }

    /// <inheritdoc />
    public bool HasWritablePaths => _writableRoots.Length > 0;

    /// <inheritdoc />
    public PathGuardResult Resolve(string? path, PathAccess access)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return PathGuardResult.Deny("Path is required.");
        }

        string full;
        try
        {
            full = Normalise(Path.GetFullPath(path, RepoRoot));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return PathGuardResult.Deny($"Path '{path}' is not a usable filesystem path: {ex.Message}");
        }

        string[] roots = access == PathAccess.Write ? _writableRoots : _readableRoots;
        if (roots.Length == 0)
        {
            return PathGuardResult.Deny(
                "No writable paths are configured, so every write is rejected. Configure GlassCoder:Workspace:WritablePaths to opt in.");
        }

        if (!IsUnderAnyRoot(full, roots))
        {
            return PathGuardResult.Deny(
                $"Path '{path}' resolves to '{full}', which is outside the {Describe(access)} set: {string.Join(", ", roots)}.");
        }

        string relative = ToRelativePath(full);
        if (_deniedMatcher is not null && _deniedMatcher.Match(relative).HasMatches)
        {
            return PathGuardResult.Deny($"Path '{relative}' is excluded by the workspace deny list.");
        }

        if (!_followSymbolicLinks)
        {
            string? escaped = FindEscapingLink(full, roots);
            if (escaped is not null)
            {
                return PathGuardResult.Deny(
                    $"Path '{path}' reaches '{escaped}' through a symbolic link that leaves the {Describe(access)} set.");
            }
        }

        return PathGuardResult.Allow(full, relative);
    }

    /// <inheritdoc />
    public string ToRelativePath(string fullPath)
    {
        string relative = Path.GetRelativePath(RepoRoot, fullPath);
        return relative.Replace(Path.DirectorySeparatorChar, '/').Replace(Path.AltDirectorySeparatorChar, '/');
    }

    private static string Describe(PathAccess access) => access == PathAccess.Write ? "writable" : "readable";

    private string ResolveRoot(string configured) =>
        Normalise(Path.GetFullPath(configured, RepoRoot));

    private static string Normalise(string fullPath) =>
        Path.TrimEndingDirectorySeparator(fullPath);

    private static bool IsRoot(string candidate, IReadOnlyList<string> roots)
    {
        foreach (string root in roots)
        {
            if (candidate.Equals(root, PathComparison))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsUnderAnyRoot(string full, IReadOnlyList<string> roots)
    {
        foreach (string root in roots)
        {
            if (full.Equals(root, PathComparison))
            {
                return true;
            }

            // The separator is what stops "C:\repo-other" from passing as "C:\repo".
            if (full.Length > root.Length &&
                full.StartsWith(root, PathComparison) &&
                (full[root.Length] == Path.DirectorySeparatorChar || full[root.Length] == Path.AltDirectorySeparatorChar))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Walks the path and its ancestors looking for a link whose final target leaves the
    /// allowed roots. Returns the offending target, or null when the path is clean.
    /// </summary>
    /// <remarks>
    /// The walk stops at the allowed root. A root that is itself a link is a deliberate
    /// configuration choice, not an escape attempt - and checking above it would make the
    /// guard depend on how the machine happens to mount the repository.
    /// </remarks>
    private static string? FindEscapingLink(string full, IReadOnlyList<string> roots)
    {
        string? current = full;
        while (!string.IsNullOrEmpty(current) && !IsRoot(current, roots))
        {
            try
            {
                FileSystemInfo? target = File.Exists(current)
                    ? new FileInfo(current).ResolveLinkTarget(returnFinalTarget: true)
                    : Directory.Exists(current)
                        ? new DirectoryInfo(current).ResolveLinkTarget(returnFinalTarget: true)
                        : null;

                if (target is not null && !IsUnderAnyRoot(Normalise(target.FullName), roots))
                {
                    return target.FullName;
                }
            }
            catch (IOException)
            {
                // A broken or unreadable link cannot be proven safe, so treat it as escaping.
                return current;
            }
            catch (UnauthorizedAccessException)
            {
                return current;
            }

            current = Path.GetDirectoryName(current);
        }

        return null;
    }
}
