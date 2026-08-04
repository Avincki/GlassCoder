using GlassCoder.TestSupport;
using GlassCoder.Tools;
using GlassCoder.Tools.Build;
using GlassCoder.Tools.Changes;
using GlassCoder.Tools.Execution;
using GlassCoder.Tools.Guardrails;
using GlassCoder.Tools.Verification;
using Microsoft.Extensions.Options;

namespace GlassCoder.Tools.Tests;

/// <summary>
/// Working out what a .NET repository is shaped like, and changing that shape (workplan task 44).
/// <para>
/// Every case here comes from one failed run. Asked to add unit tests to a repository whose
/// projects live under <c>src/</c> with no solution, the harness spent thirty steps and produced
/// no tests: it built the workspace root and got MSB1003, refused to write the test file because
/// an in-memory compile could not see a project it had not built yet, and had to hand-author the
/// <c>.csproj</c> as XML because nothing could ask the SDK to do it.
/// </para>
/// </summary>
public sealed class ProjectScaffoldingTests
{
    // ── What to build ──

    [Fact]
    public void A_tree_with_no_root_project_still_has_something_to_build()
    {
        // The MSB1003 case, exactly: projects under src/, nothing at the root. Answering "."
        // here is what made every automatic verification fail in 300 ms with a message that had
        // nothing to do with the edit that triggered it.
        using TempWorkspace workspace = new();
        workspace.WriteFile("src/ArrayOperations.csproj", Project());
        workspace.WriteFile("src/Utils/ArrayOperations.cs", "namespace Utils; public static class A { }");

        string? target = ProjectLocator.ResolveBuildTarget(workspace.Root);

        target.ShouldBe("src/ArrayOperations.csproj");
    }

    [Fact]
    public void A_change_inside_one_project_builds_that_project()
    {
        using TempWorkspace workspace = new();
        workspace.WriteFile("src/App/App.csproj", Project());
        string changed = workspace.WriteFile("src/App/Program.cs", "class P { }");
        workspace.WriteFile("src/App.Tests/App.Tests.csproj", Project());

        string? target = ProjectLocator.ResolveBuildTarget(workspace.Root, [changed]);

        target.ShouldBe("src/App/App.csproj", "the tightest correct target is the project that owns the change");
    }

    [Fact]
    public void A_change_spanning_projects_falls_back_to_the_solution()
    {
        using TempWorkspace workspace = new();
        workspace.WriteFile("Everything.sln", string.Empty);
        workspace.WriteFile("src/App/App.csproj", Project());
        workspace.WriteFile("src/Lib/Lib.csproj", Project());
        string one = workspace.WriteFile("src/App/Program.cs", "class P { }");
        string two = workspace.WriteFile("src/Lib/Thing.cs", "class T { }");

        ProjectLocator.ResolveBuildTarget(workspace.Root, [one, two]).ShouldBe("Everything.sln");
    }

    [Fact]
    public void A_tree_with_nothing_buildable_says_so_rather_than_guessing()
    {
        // Null is the honest answer, and it is what lets the ladder skip the rung instead of
        // reporting a structural fact about the repository as a failing compile.
        using TempWorkspace workspace = new();
        workspace.WriteFile("README.md", "# nothing to build");

        ProjectLocator.ResolveBuildTarget(workspace.Root).ShouldBeNull();
    }

    [Fact]
    public void Build_output_is_never_mistaken_for_a_project()
    {
        using TempWorkspace workspace = new();
        workspace.WriteFile("src/App/App.csproj", Project());
        workspace.WriteFile("src/App/obj/Debug/App.csproj", Project());

        ProjectLocator.FindAllProjects(workspace.Root).Count().ShouldBe(1);
    }

    // ── Structural hazards ──

    [Fact]
    public void A_project_nested_inside_another_is_detected()
    {
        // The hazard that produced the confusing half of the failed run: the SDK's default glob
        // pulls src/A.Tests/*.cs into src/A.csproj, so the parent project needs a test framework
        // it never referenced and every error points at the wrong file.
        using TempWorkspace workspace = new();
        string outer = workspace.WriteFile("src/ArrayOperations.csproj", Project());
        workspace.WriteFile("src/ArrayOperations.Tests/ArrayOperations.Tests.csproj", Project());

        ProjectLocator.ContainsNestedProject(outer).ShouldBeTrue();
    }

    [Fact]
    public void Sibling_projects_are_not_nested()
    {
        using TempWorkspace workspace = new();
        string one = workspace.WriteFile("src/App/App.csproj", Project());
        workspace.WriteFile("src/App.Tests/App.Tests.csproj", Project());

        ProjectLocator.ContainsNestedProject(one).ShouldBeFalse();
    }

    [Fact]
    public async Task List_projects_reports_the_layout_and_its_hazards()
    {
        using TempWorkspace workspace = new();
        workspace.WriteFile("src/ArrayOperations.csproj", Project());
        workspace.WriteFile("src/Utils/ArrayOperations.cs", "namespace Utils; public static class A { }");
        workspace.WriteFile("src/ArrayOperations.Tests/ArrayOperations.Tests.csproj", Project(reference: "..\\ArrayOperations.csproj"));

        ToolObservation<ListProjectsResult> observation =
            await new ListProjectsTool(workspace.Guard()).ListAsync();

        observation.Ok.ShouldBeTrue();
        ListProjectsResult result = observation.Data!;

        result.Projects.Count.ShouldBe(2);
        result.Solutions.ShouldBeEmpty();
        result.Warnings.ShouldContain(w => w.Contains("contains another project", StringComparison.Ordinal));
        result.Warnings.ShouldContain(w => w.Contains("no solution file", StringComparison.Ordinal));

        ProjectSummary tests = result.Projects.Single(p => p.Path.Contains("Tests", StringComparison.Ordinal));
        tests.TargetFrameworks.ShouldBe("net6.0");
        tests.ProjectReferences.ShouldBe(["ArrayOperations"]);
        tests.SourceFiles.ShouldBe(0);
    }

    // ── Changing the shape ──

    [Fact]
    public async Task Adding_a_reference_asks_the_sdk_rather_than_editing_xml()
    {
        using TempWorkspace workspace = new();
        workspace.WriteFile("src/App.Tests/App.Tests.csproj", Project());
        workspace.WriteFile("src/App/App.csproj", Project());
        ScriptedCommandExecutor executor = new();

        ToolObservation<DotnetProjectResult> observation = await Tool(workspace, executor).RunAsync(
            DotnetProjectOperation.AddReference, "src/App.Tests/App.Tests.csproj", "src/App/App.csproj");

        observation.Ok.ShouldBeTrue();
        observation.Data!.Succeeded.ShouldBeTrue();

        IReadOnlyList<string> arguments = executor.Commands.Single().Arguments;
        arguments[0].ShouldBe("add");
        arguments[2].ShouldBe("reference");
        arguments[1].ShouldEndWith("App.Tests.csproj");
        arguments[3].ShouldEndWith("App.csproj");
    }

    [Fact]
    public async Task Adding_a_package_passes_the_version_only_when_one_was_asked_for()
    {
        using TempWorkspace workspace = new();
        workspace.WriteFile("src/App.Tests/App.Tests.csproj", Project());
        ScriptedCommandExecutor executor = new();
        DotnetProjectTool tool = Tool(workspace, executor);

        await tool.RunAsync(DotnetProjectOperation.AddPackage, "src/App.Tests/App.Tests.csproj", "xunit");
        await tool.RunAsync(DotnetProjectOperation.AddPackage, "src/App.Tests/App.Tests.csproj", "xunit", "2.9.2");

        executor.Commands[0].Arguments.ShouldNotContain("--version");
        executor.Commands[1].Arguments.TakeLast(2).ShouldBe(["--version", "2.9.2"]);
    }

    [Fact]
    public async Task A_new_project_takes_its_name_from_its_directory()
    {
        using TempWorkspace workspace = new();
        ScriptedCommandExecutor executor = new();

        await Tool(workspace, executor).RunAsync(
            DotnetProjectOperation.New, "src/ArrayOperations.Tests", "xunit");

        IReadOnlyList<string> arguments = executor.Commands.Single().Arguments;
        arguments[0].ShouldBe("new");
        arguments[1].ShouldBe("xunit");
        arguments[^1].ShouldBe("ArrayOperations.Tests");
    }

    [Fact]
    public async Task A_solution_path_names_a_file_rather_than_a_folder_to_put_one_in()
    {
        // From a run: the agent asked for src/MyMathLib.sln and got a *directory* of that name
        // holding MyMathLib.sln.slnx - a folder named like a solution containing a solution named
        // like a folder. It built, by accident, which is how nearly-right survives a whole run.
        using TempWorkspace workspace = new();
        ScriptedCommandExecutor executor = new();

        await Tool(workspace, executor).RunAsync(DotnetProjectOperation.NewSolution, "src/MyMathLib.sln");

        List<string> arguments = [.. executor.Commands.Single().Arguments];
        arguments[arguments.IndexOf("-n") + 1].ShouldBe("MyMathLib", "the .sln belongs to the file, not the name");
        // ...and it goes in src, not in a new src/MyMathLib.sln folder.
        arguments[arguments.IndexOf("-o") + 1].ShouldEndWith("src");
    }

    [Fact]
    public async Task A_solution_asked_for_by_directory_still_works()
    {
        // The other calling convention has to keep working: name a folder, get a solution in it
        // named after the folder.
        using TempWorkspace workspace = new();
        ScriptedCommandExecutor executor = new();

        await Tool(workspace, executor).RunAsync(DotnetProjectOperation.NewSolution, "src/Everything");

        List<string> arguments = [.. executor.Commands.Single().Arguments];
        arguments[arguments.IndexOf("-n") + 1].ShouldBe("Everything");
        arguments[arguments.IndexOf("-o") + 1].ShouldEndWith("Everything");
    }

    [Fact]
    public async Task A_template_this_tool_does_not_know_is_refused_before_anything_runs()
    {
        using TempWorkspace workspace = new();
        ScriptedCommandExecutor executor = new();

        ToolObservation<DotnetProjectResult> observation = await Tool(workspace, executor).RunAsync(
            DotnetProjectOperation.New, "src/Thing", "sudo-rm-rf");

        observation.Ok.ShouldBeFalse();
        observation.Error!.Code.ShouldBe(ToolErrorCodes.InvalidArgument);
        executor.Commands.ShouldBeEmpty();
    }

    [Fact]
    public async Task An_operation_outside_the_writable_set_is_refused()
    {
        using TempWorkspace workspace = new();
        ScriptedCommandExecutor executor = new();

        // The guard is rooted with "src" writable, so anything above it is out of bounds - the
        // same boundary the file tools answer to.
        ToolObservation<DotnetProjectResult> observation = await Tool(workspace, executor).RunAsync(
            DotnetProjectOperation.Restore, "../elsewhere");

        observation.Ok.ShouldBeFalse();
        observation.Error!.Code.ShouldBe(ToolErrorCodes.PathNotAllowed);
        executor.Commands.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_failed_command_is_an_observation_rather_than_a_tool_fault()
    {
        using TempWorkspace workspace = new();
        workspace.WriteFile("src/App/App.csproj", Project());
        ScriptedCommandExecutor executor = new();
        executor.Enqueue(1, "error NU1101: Unable to find package nosuchpackage.");

        ToolObservation<DotnetProjectResult> observation = await Tool(workspace, executor).RunAsync(
            DotnetProjectOperation.AddPackage, "src/App/App.csproj", "nosuchpackage");

        observation.Ok.ShouldBeTrue("a refused operation is information the agent acts on");
        observation.Data!.Succeeded.ShouldBeFalse();
        observation.Data.Output.ShouldContain("NU1101");
    }

    private static DotnetProjectTool Tool(TempWorkspace workspace, ICommandExecutor executor) =>
        new(executor, workspace.Guard("src"), new ChangeLog(), Options.Create(new SandboxOptions()));

    private static string Project(string? reference = null) =>
        $"""
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <TargetFramework>net6.0</TargetFramework>
          </PropertyGroup>
          {(reference is null ? string.Empty : $"""<ItemGroup><ProjectReference Include="{reference}" /></ItemGroup>""")}
        </Project>
        """;
}
