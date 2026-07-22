namespace GlassCoder.Tools.Guardrails;

/// <summary>Kind of access a tool is asking for.</summary>
public enum PathAccess
{
    /// <summary>Reading a file or listing a directory.</summary>
    Read,

    /// <summary>Creating, modifying or deleting a file.</summary>
    Write,
}

/// <summary>Verdict on one path, plus the normalised form the tool should use.</summary>
/// <param name="Allowed">Whether the access is permitted.</param>
/// <param name="FullPath">Absolute, normalised path. Null when denied.</param>
/// <param name="RelativePath">Repo-relative path with forward slashes, for observations and logs.</param>
/// <param name="Reason">Why the access was denied. Null when allowed.</param>
public sealed record PathGuardResult(bool Allowed, string? FullPath, string? RelativePath, string? Reason)
{
    /// <summary>Builds an allowing verdict.</summary>
    public static PathGuardResult Allow(string fullPath, string relativePath) =>
        new(true, fullPath, relativePath, null);

    /// <summary>Builds a denying verdict.</summary>
    public static PathGuardResult Deny(string reason) => new(false, null, null, reason);
}

/// <summary>
/// The path allow-list guardrail (CLAUDE.md §7, workplan task 8). Every tool that touches the
/// filesystem asks this first; there is no other way in.
/// </summary>
public interface IPathGuard
{
    /// <summary>Absolute, normalised repository root.</summary>
    string RepoRoot { get; }

    /// <summary>Whether any path at all may be written.</summary>
    bool HasWritablePaths { get; }

    /// <summary>Resolves and authorises a path for the requested access.</summary>
    PathGuardResult Resolve(string? path, PathAccess access);

    /// <summary>Repo-relative, forward-slashed form of an absolute path, for logs and observations.</summary>
    string ToRelativePath(string fullPath);
}
