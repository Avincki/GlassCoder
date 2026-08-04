using System.Xml;
using System.Xml.Linq;

namespace GlassCoder.Tools.Verification;

/// <summary>
/// What a .NET repository is shaped like: where its projects are, which one owns a file, and what
/// <c>dotnet build</c> should be pointed at.
/// <para>
/// This exists because "build the workspace root" is only correct for repositories that keep a
/// solution at the root. A tree whose projects live under <c>src/</c> and has no solution answers
/// <c>MSB1003: Specify a project or solution file</c> to every build - in 300 ms, before any code
/// is compiled - and a verification rung that reports that as a failure is telling the agent its
/// edit broke something when nothing of the sort happened.
/// </para>
/// </summary>
public static class ProjectLocator
{
    /// <summary>Project file extensions this understands.</summary>
    private static readonly string[] ProjectPatterns = ["*.csproj", "*.fsproj", "*.vbproj"];

    /// <summary>Solution file extensions, newest format first.</summary>
    private static readonly string[] SolutionPatterns = ["*.slnx", "*.sln"];

    /// <summary>
    /// The nearest project file at or above <paramref name="fullPath"/>, or null when there is
    /// none. Walks past directories that do not exist yet, because a file being created may not
    /// have one.
    /// </summary>
    public static string? FindProjectFile(string fullPath)
    {
        ArgumentNullException.ThrowIfNull(fullPath);

        DirectoryInfo? directory = Directory.Exists(fullPath)
            ? new DirectoryInfo(fullPath)
            : new FileInfo(fullPath).Directory;

        while (directory is not null)
        {
            if (directory.Exists && EnumerateProjects(directory.FullName).FirstOrDefault() is { } project)
            {
                return project;
            }

            directory = directory.Parent;
        }

        return null;
    }

    /// <summary>The solution file directly in <paramref name="directory"/>, if there is one.</summary>
    public static string? FindSolutionFile(string directory)
    {
        ArgumentNullException.ThrowIfNull(directory);

        if (!Directory.Exists(directory))
        {
            return null;
        }

        foreach (string pattern in SolutionPatterns)
        {
            try
            {
                if (Directory.EnumerateFiles(directory, pattern, SearchOption.TopDirectoryOnly).FirstOrDefault() is { } solution)
                {
                    return solution;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // An unreadable directory simply has no solution as far as this is concerned.
            }
        }

        return null;
    }

    /// <summary>Project files directly in <paramref name="directory"/>.</summary>
    public static IEnumerable<string> EnumerateProjects(string directory)
    {
        ArgumentNullException.ThrowIfNull(directory);

        foreach (string pattern in ProjectPatterns)
        {
            string[] found;
            try
            {
                found = Directory.GetFiles(directory, pattern, SearchOption.TopDirectoryOnly);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (string file in found)
            {
                yield return file;
            }
        }
    }

    /// <summary>
    /// What to hand <c>dotnet build</c> so it covers <paramref name="changedFullPaths"/>, as a
    /// path relative to <paramref name="repoRoot"/>. Null when the tree holds nothing buildable,
    /// which is a reason to skip the rung rather than to fail it.
    /// <para>
    /// The order is deliberate. One project covers a single-project change exactly and is the
    /// fastest thing that can be correct. A root solution covers everything and is what a
    /// multi-project change needs. Falling back to the root directory is last, and only when
    /// something there is actually buildable - that fallback is where MSB1003 came from.
    /// </para>
    /// </summary>
    public static string? ResolveBuildTarget(string repoRoot, IEnumerable<string>? changedFullPaths = null)
    {
        ArgumentNullException.ThrowIfNull(repoRoot);

        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(repoRoot));

        // One project owning every changed file is the tightest correct target.
        if (changedFullPaths is not null)
        {
            HashSet<string> projects = new(StringComparer.OrdinalIgnoreCase);
            bool any = false;

            foreach (string changed in changedFullPaths)
            {
                any = true;
                if (FindProjectFile(changed) is not { } project)
                {
                    projects.Clear();
                    break;
                }

                projects.Add(project);
            }

            if (any && projects.Count == 1)
            {
                return Relative(root, projects.Single());
            }
        }

        if (FindSolutionFile(root) is { } solution)
        {
            return Relative(root, solution);
        }

        if (EnumerateProjects(root).FirstOrDefault() is { } rootProject)
        {
            return Relative(root, rootProject);
        }

        // Nothing at the root to build. Look for exactly one project in the tree before giving
        // up - a repository with a single project under src/ is the common shape here, and
        // building it is unambiguous.
        List<string> all = [.. FindAllProjects(root).Take(2)];
        return all.Count == 1 ? Relative(root, all[0]) : null;
    }

    /// <summary>Every project file under <paramref name="root"/>, skipping build output.</summary>
    public static IEnumerable<string> FindAllProjects(string root)
    {
        ArgumentNullException.ThrowIfNull(root);

        if (!Directory.Exists(root))
        {
            yield break;
        }

        foreach (string pattern in ProjectPatterns)
        {
            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(root, pattern, SearchOption.AllDirectories);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (string file in files)
            {
                if (!IsBuildOutput(file))
                {
                    yield return file;
                }
            }
        }
    }

    /// <summary>
    /// The references a project declares, as the assembly names they would produce.
    /// <para>
    /// Read straight from the XML rather than evaluated through MSBuild. That is enough for the
    /// question this answers - "is the reference set I just assembled complete?" - and it does
    /// not need the SDK, a restore, or a second of startup.
    /// </para>
    /// </summary>
    public static ProjectReferences ReadReferences(string projectFile)
    {
        ArgumentNullException.ThrowIfNull(projectFile);

        List<string> projects = [];
        List<string> packages = [];

        try
        {
            XDocument document = XDocument.Load(projectFile);
            foreach (XElement element in document.Descendants())
            {
                string? include = element.Attribute("Include")?.Value;
                if (string.IsNullOrWhiteSpace(include))
                {
                    continue;
                }

                if (element.Name.LocalName.Equals("ProjectReference", StringComparison.OrdinalIgnoreCase))
                {
                    projects.Add(Path.GetFileNameWithoutExtension(include.Replace('\\', Path.DirectorySeparatorChar)));
                }
                else if (element.Name.LocalName.Equals("PackageReference", StringComparison.OrdinalIgnoreCase))
                {
                    packages.Add(include.Trim());
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or XmlException)
        {
            // A malformed project file tells us nothing about its references, which is the same
            // answer as having none: the caller falls back to trusting the compile.
        }

        return new ProjectReferences(projects, packages);
    }

    /// <summary>What a project targets, as written in its project file, or null when it does not say.</summary>
    public static string? ReadTargetFrameworks(string projectFile)
    {
        ArgumentNullException.ThrowIfNull(projectFile);

        try
        {
            foreach (XElement element in XDocument.Load(projectFile).Descendants())
            {
                if (element.Name.LocalName is "TargetFramework" or "TargetFrameworks")
                {
                    string value = element.Value.Trim();
                    if (value.Length > 0)
                    {
                        return value;
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or XmlException)
        {
            // A project file that will not parse tells us nothing, which is what null means.
        }

        return null;
    }

    /// <summary>
    /// Whether <paramref name="projectFile"/> sits above another project, so the SDK's default
    /// glob pulls that project's sources into this one.
    /// <para>
    /// A real and quiet hazard: <c>src/A.csproj</c> with <c>src/A.Tests/</c> beneath it compiles
    /// the tests into A, which then needs the test framework it does not reference. The errors
    /// that follow point at the test files and say nothing about the nesting that caused them.
    /// </para>
    /// </summary>
    public static bool ContainsNestedProject(string projectFile)
    {
        ArgumentNullException.ThrowIfNull(projectFile);

        string? directory = Path.GetDirectoryName(Path.GetFullPath(projectFile));
        if (directory is null)
        {
            return false;
        }

        foreach (string other in FindAllProjects(directory))
        {
            if (!string.Equals(Path.GetFullPath(other), Path.GetFullPath(projectFile), StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsBuildOutput(string path)
    {
        string normalised = path.Replace('\\', '/');
        return normalised.Contains("/bin/", StringComparison.OrdinalIgnoreCase) ||
               normalised.Contains("/obj/", StringComparison.OrdinalIgnoreCase);
    }

    private static string Relative(string root, string fullPath)
    {
        string relative = Path.GetRelativePath(root, fullPath);
        return relative.Replace('\\', '/');
    }
}

/// <summary>What a project file says it depends on.</summary>
/// <param name="Projects">Assembly names of referenced projects.</param>
/// <param name="Packages">Package ids.</param>
public sealed record ProjectReferences(IReadOnlyList<string> Projects, IReadOnlyList<string> Packages)
{
    /// <summary>Whether the project declares any dependency at all.</summary>
    public bool Any => Projects.Count > 0 || Packages.Count > 0;
}
