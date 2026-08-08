using System.ComponentModel;
using GlassCoder.Tools.Guardrails;
using GlassCoder.Tools.Registry;
using GlassCoder.Tools.Verification;

namespace GlassCoder.Tools.Build;

/// <summary>One project in the workspace.</summary>
/// <param name="Path">Repo-relative path of the project file.</param>
/// <param name="TargetFrameworks">What it targets, as written in the project file.</param>
/// <param name="ProjectReferences">Projects it references, by assembly name.</param>
/// <param name="PackageReferences">NuGet packages it references, by id.</param>
/// <param name="SourceFiles">How many C# files sit under it.</param>
public sealed record ProjectSummary(
    [property: Description("Repo-relative path of the project file.")] string Path,
    [property: Description("Target framework(s), or null when the project file does not say.")] string? TargetFrameworks,
    [property: Description("Projects this one references.")] IReadOnlyList<string> ProjectReferences,
    [property: Description("NuGet packages this one references.")] IReadOnlyList<string> PackageReferences,
    [property: Description("Number of C# files under this project.")] int SourceFiles);

/// <summary>Result payload of <c>list_projects</c>.</summary>
/// <param name="Solutions">Solution files, repo-relative.</param>
/// <param name="Projects">Every project found, outermost first.</param>
/// <param name="BuildTarget">What the verification ladder would build for a change here.</param>
/// <param name="Warnings">Structural problems worth knowing before editing anything.</param>
public sealed record ListProjectsResult(
    [property: Description("Solution files in the workspace.")] IReadOnlyList<string> Solutions,
    [property: Description("Every project in the workspace.")] IReadOnlyList<ProjectSummary> Projects,
    [property: Description("What a build of the whole workspace would target, or null if nothing is buildable.")] string? BuildTarget,
    [property: Description("Structural problems: nested projects, missing solution entries, empty projects.")] IReadOnlyList<string> Warnings);

/// <summary>
/// <c>list_projects</c> - what this repository is made of (workplan task 44).
/// <para>
/// Exists because the first thing an agent does in an unfamiliar .NET tree is work out its
/// shape, and the only way to do that was three or four <c>glob</c> calls followed by reading
/// project files one at a time. That is four steps of a finite budget spent on a question with
/// one cheap answer.
/// </para>
/// <para>
/// It also reports the hazards that are invisible until they cause a confusing build failure -
/// chiefly a project nested inside another, where the SDK's default glob silently compiles the
/// inner project's sources into the outer one and every error points at the wrong file.
/// </para>
/// </summary>
public sealed class ListProjectsTool : IToolSet
{
    private const string ToolName = "list_projects";

    private readonly IPathGuard _guard;

    /// <summary>Creates the tool.</summary>
    public ListProjectsTool(IPathGuard guard) => _guard = guard;

    /// <summary>Describes the projects and solutions in the workspace.</summary>
    [GlassCoderTool(ToolName, Order = 32)]
    [Description("List the solutions and projects in the workspace with their frameworks, references and "
        + "source counts, plus any structural problems. Call this before creating or wiring up a project.")]
    public Task<ToolObservation<ListProjectsResult>> ListAsync(CancellationToken cancellationToken = default)
    {
        string root = _guard.RepoRoot;

        List<string> solutions = [];
        List<ProjectSummary> projects = [];
        List<string> warnings = [];

        // All solutions, not just the root one - run ca727be3 left an empty solution.slnx in a
        // subdirectory, and nothing ever mentioned it again: not at the root, so build-target
        // resolution never saw it; empty, so builds never noticed; unreported, so it survived.
        string rootDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        foreach (string solution in ProjectLocator.FindAllSolutions(root))
        {
            string relative = _guard.ToRelativePath(solution);
            solutions.Add(relative);

            if (ProjectLocator.CountSolutionProjects(solution) == 0)
            {
                warnings.Add(
                    $"'{relative}' contains no projects. Add them with dotnet_project add_to_solution, "
                    + "or delete the file - an empty solution builds nothing.");
            }

            if (!string.Equals(
                    Path.GetDirectoryName(Path.GetFullPath(solution)), rootDirectory,
                    StringComparison.OrdinalIgnoreCase))
            {
                warnings.Add(
                    $"'{relative}' is not at the workspace root, where build-target resolution looks, "
                    + "so it does not govern any build - projects build individually.");
            }
        }

        List<string> files = [.. ProjectLocator.FindAllProjects(root).OrderBy(p => p, StringComparer.OrdinalIgnoreCase)];

        foreach (string file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();

            ProjectReferences references = ProjectLocator.ReadReferences(file);
            string directory = Path.GetDirectoryName(file)!;
            int sources = CountSources(directory);

            projects.Add(new ProjectSummary(
                _guard.ToRelativePath(file),
                ProjectLocator.ReadTargetFrameworks(file),
                references.Projects,
                references.Packages,
                sources));

            // The quiet one. src/A.csproj with src/A.Tests/ beneath it compiles the tests into
            // A, which then wants a test framework it never referenced - and every error points
            // at the test files rather than at the nesting that pulled them in.
            if (ProjectLocator.ContainsNestedProject(file))
            {
                warnings.Add(
                    $"'{_guard.ToRelativePath(file)}' contains another project in a subdirectory. The SDK compiles "
                    + "those sources into this project too. Move one of them so neither is inside the other.");
            }

            if (sources == 0)
            {
                warnings.Add($"'{_guard.ToRelativePath(file)}' has no C# files.");
            }
        }

        if (solutions.Count == 0 && files.Count > 1)
        {
            warnings.Add(
                "There is no solution file, so there is no single target that builds everything. "
                + "Build projects individually, or create one with dotnet_project new_solution.");
        }

        ListProjectsResult payload = new(
            solutions,
            projects,
            ProjectLocator.ResolveBuildTarget(root),
            warnings);

        string summary = projects.Count == 0
            ? "No .NET projects found in this workspace."
            : $"{projects.Count} project(s), {solutions.Count} solution(s)"
                + (warnings.Count > 0 ? $", {warnings.Count} structural warning(s)." : ".");

        return Task.FromResult(Observation.Ok(ToolName, payload, summary));
    }

    private int CountSources(string directory)
    {
        int count = 0;
        try
        {
            foreach (string file in Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories))
            {
                // The guard already hides bin, obj and friends, so generated output is not
                // counted as code somebody wrote.
                if (_guard.Resolve(file, PathAccess.Read).Allowed)
                {
                    count++;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // An unreadable project directory has no countable sources.
        }

        return count;
    }
}
