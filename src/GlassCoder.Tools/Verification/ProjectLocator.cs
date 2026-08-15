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

    /// <summary>
    /// Whether this is MSBuild scratch rather than a project anyone wrote.
    /// <para>
    /// WPF's markup compile writes <c>&lt;project&gt;_&lt;hash&gt;_wpftmp.csproj</c> beside the
    /// real one and deletes it at the end - unless the build dies first, and then it stays. Run
    /// <c>4c7de12b</c> left one, and step 18's <c>build src/MultiplyApp</c> came back
    /// <c>MSB1011: this folder contains more than one project or solution file</c>, costing a step.
    /// This repository's own <c>.gitignore</c> has carried a rule for these since before the
    /// harness existed; nothing in the harness knew about them.
    /// </para>
    /// <para>
    /// Filtered rather than reported, because no caller of this class wants one: not the build
    /// target resolver, not the owning-project lookup, and not the analyzer deciding whether
    /// implicit usings are on - that last one would read a 31 KB fully-expanded scratch project
    /// to answer a question about the real one. Ordering saved run 4c7de12b from that by luck,
    /// <c>'.'</c> sorting before <c>'_'</c>.
    /// </para>
    /// </summary>
    public static bool IsScratch(string path) =>
        Path.GetFileNameWithoutExtension(path.AsSpan()).EndsWith("_wpftmp", StringComparison.OrdinalIgnoreCase);

    /// <summary>Project files directly in <paramref name="directory"/>, excluding MSBuild scratch.</summary>
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
                if (IsScratch(file))
                {
                    continue;
                }

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
                string owner = projects.Single();

                // A change confined to one project still breaks every project that references it
                // the moment it changes what the project exports - and the owner is exactly the
                // one project such a change cannot fail in. Building a dependent builds the owner
                // first, so the dependent at the top of the chain is both the tightest and the
                // most complete single target.
                (bool hasDependents, string? top) = Dependents(root, owner);
                if (top is not null)
                {
                    return Relative(root, top);
                }

                if (!hasDependents || FindSolutionFile(root) is null)
                {
                    return Relative(root, owner);
                }

                // Dependents exist, no single project covers them all, and the root solution
                // below does.
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

    /// <summary>
    /// The projects that transitively reference <paramref name="ownerProject"/>, and the one
    /// that covers them all when there is one.
    /// <para>
    /// From run <c>e8f9186a</c>: a library gained a parameter, the ladder built the library
    /// alone and reported green, and every call site in the test project had just stopped
    /// compiling - invisibly, because nothing ever built the project the change actually broke.
    /// </para>
    /// </summary>
    private static (bool HasDependents, string? CoveringTarget) Dependents(string root, string ownerProject)
    {
        List<string> all = [.. FindAllProjects(root)];
        if (all.Count <= 1)
        {
            return (false, null);
        }

        Dictionary<string, string> byName = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, IReadOnlyList<string>> referenced = new(StringComparer.OrdinalIgnoreCase);
        foreach (string project in all)
        {
            byName[Path.GetFileNameWithoutExtension(project)] = project;
            referenced[project] = [.. ReadProjectReferencePaths(project).Select(r => r.Name)];
        }

        // Walked by name rather than by resolved path, because the names are what the projects
        // themselves declare and a broken Include resolves to nothing anyway.
        bool Reaches(string project, string targetName)
        {
            HashSet<string> visited = new(StringComparer.OrdinalIgnoreCase);
            Stack<string> pending = new();
            pending.Push(project);
            while (pending.Count > 0)
            {
                foreach (string name in referenced[pending.Pop()])
                {
                    if (name.Equals(targetName, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }

                    if (byName.TryGetValue(name, out string? next) && visited.Add(next))
                    {
                        pending.Push(next);
                    }
                }
            }

            return false;
        }

        string ownerName = Path.GetFileNameWithoutExtension(ownerProject);
        List<string> dependents = [.. all.Where(project =>
            !string.Equals(Path.GetFullPath(project), Path.GetFullPath(ownerProject), StringComparison.OrdinalIgnoreCase) &&
            Reaches(project, ownerName))];

        if (dependents.Count == 0)
        {
            return (false, null);
        }

        List<string> tops = [.. dependents.Where(candidate => dependents.All(other =>
            string.Equals(other, candidate, StringComparison.OrdinalIgnoreCase) ||
            Reaches(candidate, Path.GetFileNameWithoutExtension(other))))];

        return (true, tops.Count == 1 ? tops[0] : null);
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
    /// Package ids that mean "this project holds tests". Here rather than in each caller, because
    /// three places were asking the same question from three copies of the same list.
    /// </summary>
    public static readonly string[] TestFrameworkPackages =
        ["xunit", "nunit", "MSTest", "Microsoft.NET.Test.Sdk"];

    /// <summary>
    /// Whether anything at or under this path builds something that can be run - a console or
    /// desktop application rather than a library.
    /// <para>
    /// Asked so the completion panel can be told that nothing ran it. Read straight from
    /// <c>OutputType</c>, like every other question this class answers, because the alternative is
    /// an SDK evaluation to learn one word.
    /// </para>
    /// </summary>
    public static bool AnyExecutableProject(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        try
        {
            string directory = Directory.Exists(path) ? path : Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".";
            return FindAllProjects(directory).Any(IsExecutableProject);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Whether this project produces an executable.</summary>
    public static bool IsExecutableProject(string projectFile)
    {
        ArgumentNullException.ThrowIfNull(projectFile);

        try
        {
            return XDocument.Load(projectFile).Descendants("OutputType")
                .Any(e => e.Value.Trim() is "Exe" or "WinExe");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException)
        {
            return false;
        }
    }

    /// <summary>Whether this project file references a test framework.</summary>
    public static bool IsTestProject(string projectFile)
    {
        ArgumentNullException.ThrowIfNull(projectFile);

        return ReadReferences(projectFile).Packages
            .Any(package => TestFrameworkPackages
                .Any(framework => package.Contains(framework, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>
    /// Whether anything at or under this path is a test project.
    /// <para>
    /// Asked before the test rung spends a process. Steps 3 to 8 of run <c>457867c7</c> each
    /// applied a scaffolding change, each climbed the ladder, and each paid a <c>dotnet test</c>
    /// launch to be told that a workspace with no test project in it ran no tests - six times, for
    /// an answer sitting in the project files. Reading them costs a directory walk.
    /// </para>
    /// </summary>
    public static bool AnyTestProject(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        if (File.Exists(path) && Path.GetExtension(path).Equals(".csproj", StringComparison.OrdinalIgnoreCase))
        {
            return IsTestProject(path);
        }

        // The whole tree, not the directory: a solution at the root with its projects under src/
        // and tests/ is the shape this repository builds, and a non-recursive look would call it
        // testless and skip a rung that had work to do.
        string directory = Directory.Exists(path) ? path : Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".";

        try
        {
            return FindAllProjects(directory).Any(IsTestProject);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A workspace that cannot be walked is not a workspace anyone can prove is testless.
            // Answering "yes" here keeps the rung running, which is the behaviour that predates it.
            return true;
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

    /// <summary>
    /// The <c>ProjectReference</c> entries of a project, each as the assembly name it produces
    /// and the full path of the referenced project file, resolved against the referencing
    /// project's directory. Read straight from the XML, like <see cref="ReadReferences"/>, and
    /// for the same reason.
    /// </summary>
    public static IReadOnlyList<(string Name, string FullPath)> ReadProjectReferencePaths(string projectFile)
    {
        ArgumentNullException.ThrowIfNull(projectFile);

        List<(string, string)> references = [];

        try
        {
            string directory = Path.GetDirectoryName(Path.GetFullPath(projectFile)) ?? ".";
            foreach (XElement element in XDocument.Load(projectFile).Descendants())
            {
                string? include = element.Attribute("Include")?.Value;
                if (string.IsNullOrWhiteSpace(include) ||
                    !element.Name.LocalName.Equals("ProjectReference", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string full = Path.GetFullPath(
                    Path.Combine(directory, include.Replace('\\', Path.DirectorySeparatorChar)));
                references.Add((Path.GetFileNameWithoutExtension(full), full));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or XmlException)
        {
            // A malformed project file tells us nothing about its references, which is the same
            // answer as having none.
        }

        return references;
    }

    /// <summary>Every solution file under <paramref name="root"/>, skipping build output.</summary>
    /// <remarks>
    /// <see cref="FindSolutionFile"/> answers "what governs the build" and looks only at the
    /// root on purpose. This answers "what solutions exist at all" - run ca727be3 created
    /// <c>src/MultiplyApp/solution.slnx</c>, added nothing to it, and no surface ever mentioned
    /// the file again: not at the root, so invisible to build-target resolution; empty, so
    /// harmless; and unreported, so it survived to confuse the next reader.
    /// </remarks>
    public static IEnumerable<string> FindAllSolutions(string root)
    {
        ArgumentNullException.ThrowIfNull(root);

        if (!Directory.Exists(root))
        {
            yield break;
        }

        foreach (string pattern in SolutionPatterns)
        {
            string[] files;
            try
            {
                files = Directory.GetFiles(root, pattern, SearchOption.AllDirectories);
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
    /// How many projects a solution file lists, in either format, or null when the file cannot
    /// be read - unknown must never be reported as empty.
    /// </summary>
    public static int? CountSolutionProjects(string solutionFile)
    {
        ArgumentNullException.ThrowIfNull(solutionFile);

        try
        {
            if (Path.GetExtension(solutionFile).Equals(".slnx", StringComparison.OrdinalIgnoreCase))
            {
                return XDocument.Load(solutionFile)
                    .Descendants()
                    .Count(e => e.Name.LocalName.Equals("Project", StringComparison.OrdinalIgnoreCase));
            }

            return File.ReadLines(solutionFile)
                .Count(line => line.TrimStart().StartsWith("Project(", StringComparison.Ordinal));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or XmlException)
        {
            return null;
        }
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
