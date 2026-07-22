using GlassCoder.Tools.Guardrails;
using Microsoft.Extensions.FileSystemGlobbing;
using Microsoft.Extensions.FileSystemGlobbing.Abstractions;

namespace GlassCoder.Tools.FileSystem;

/// <summary>
/// Shared filesystem plumbing for the read-only tools: glob enumeration that runs every hit
/// past the path guard, and the binary/text decision.
/// </summary>
internal static class WorkspaceFiles
{
    /// <summary>
    /// Enumerates files under <paramref name="rootFullPath"/> matching <paramref name="glob"/>,
    /// skipping anything the guard denies. Stops after <paramref name="maxFiles"/> visits.
    /// </summary>
    public static IEnumerable<string> Enumerate(
        IPathGuard guard,
        string rootFullPath,
        string glob,
        int maxFiles,
        CancellationToken cancellationToken)
    {
        Matcher matcher = new(OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        matcher.AddInclude(string.IsNullOrWhiteSpace(glob) ? "**/*" : glob);

        DirectoryInfoWrapper wrapper = new(new DirectoryInfo(rootFullPath));
        int visited = 0;

        foreach (FilePatternMatch match in matcher.Execute(wrapper).Files)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (++visited > maxFiles)
            {
                yield break;
            }

            string candidate = Path.GetFullPath(Path.Combine(rootFullPath, match.Path));
            PathGuardResult verdict = guard.Resolve(candidate, PathAccess.Read);
            if (verdict.Allowed && verdict.FullPath is not null)
            {
                yield return verdict.FullPath;
            }
        }
    }

    /// <summary>
    /// Whether a file looks like binary content. A NUL byte in the first block is the same
    /// heuristic <c>git</c> uses, and it is right often enough to keep binary noise out of the
    /// context window.
    /// </summary>
    public static bool IsBinary(string fullPath)
    {
        try
        {
            using FileStream stream = File.OpenRead(fullPath);
            Span<byte> buffer = stackalloc byte[8000];
            int read = stream.Read(buffer);
            return buffer[..read].IndexOf((byte)0) >= 0;
        }
        catch (IOException)
        {
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }

    /// <summary>Clips a line to <paramref name="maxLength"/>, marking that it was clipped.</summary>
    public static string Clip(string line, int maxLength) =>
        line.Length <= maxLength ? line : string.Concat(line.AsSpan(0, maxLength), " … [line truncated]");
}
