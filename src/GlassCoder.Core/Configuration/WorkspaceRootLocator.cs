namespace GlassCoder.Core.Configuration;

/// <summary>
/// Finds the repository a desktop launch should work on.
/// <para>
/// <c>Workspace:RepoRoot</c> defaults to <c>"."</c>, which the path guard resolves against the
/// <em>process working directory</em>. That is the right contract for the console host, which is
/// run from the repository it should work on. It is the wrong one for a window somebody
/// double-clicks: the working directory is then the folder the executable lives in, so the agent
/// roots itself in <c>bin\Debug\...</c> and the workspace pane shows build output.
/// </para>
/// <para>
/// So the desktop app asks this instead, and only when nothing better was configured. Walking up
/// from the executable finds the repository in a normal development checkout and finds nothing at
/// all once the app is installed elsewhere - which is the honest answer, and leaves the saved
/// setting as the way to say where the work is.
/// </para>
/// </summary>
public static class WorkspaceRootLocator
{
    /// <summary>The configured value that means "nobody chose a workspace".</summary>
    public const string UnsetRepoRoot = ".";

    /// <summary>Whether a configured root is the placeholder rather than a real choice.</summary>
    public static bool IsUnset(string? repoRoot) =>
        string.IsNullOrWhiteSpace(repoRoot) || repoRoot.Trim() == UnsetRepoRoot;

    /// <summary>
    /// Walks up from <paramref name="startDirectory"/> looking for the marks of a repository
    /// root: a <c>.git</c> entry, or a solution file. Returns null when there is none, which
    /// leaves the configured value alone rather than guessing.
    /// </summary>
    /// <param name="startDirectory">Where to start. Defaults to the executable's directory.</param>
    public static string? Find(string? startDirectory = null)
    {
        DirectoryInfo? directory;
        try
        {
            directory = new DirectoryInfo(Path.GetFullPath(
                string.IsNullOrWhiteSpace(startDirectory) ? AppContext.BaseDirectory : startDirectory));
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException)
        {
            return null;
        }

        while (directory is not null)
        {
            if (IsRepositoryRoot(directory))
            {
                return Path.TrimEndingDirectorySeparator(directory.FullName);
            }

            directory = directory.Parent;
        }

        return null;
    }

    /// <summary>
    /// A <c>.git</c> that is a <em>file</em> counts too - that is what a worktree or a submodule
    /// checkout has where a plain clone has a directory.
    /// </summary>
    private static bool IsRepositoryRoot(DirectoryInfo directory)
    {
        try
        {
            string git = Path.Combine(directory.FullName, ".git");
            return Directory.Exists(git)
                || File.Exists(git)
                || directory.EnumerateFiles("*.sln").Any()
                || directory.EnumerateFiles("*.slnx").Any();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A directory that cannot be listed is not a root worth claiming; keep walking.
            return false;
        }
    }
}
