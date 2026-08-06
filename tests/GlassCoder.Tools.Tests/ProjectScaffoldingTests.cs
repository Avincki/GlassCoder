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

    // ── What a library change has to build ──

    [Fact]
    public void A_change_in_a_library_builds_the_project_that_depends_on_it()
    {
        // Run e8f9186a: the library gained a parameter, the ladder built the library alone and
        // reported green, and the test project's broken call sites stayed invisible for the rest
        // of the run. Building the dependent builds the library first, so one target covers the
        // whole affected closure.
        using TempWorkspace workspace = new();
        workspace.WriteFile("src/Lib/Lib.csproj", Project());
        string changed = workspace.WriteFile("src/Lib/Thing.cs", "class T { }");
        workspace.WriteFile("tests/Lib.Tests/Lib.Tests.csproj", Project("..\\..\\src\\Lib\\Lib.csproj"));

        ProjectLocator.ResolveBuildTarget(workspace.Root, [changed])
            .ShouldBe("tests/Lib.Tests/Lib.Tests.csproj");
    }

    [Fact]
    public void A_chain_of_dependents_builds_the_project_at_the_top()
    {
        using TempWorkspace workspace = new();
        workspace.WriteFile("src/Core/Core.csproj", Project());
        string changed = workspace.WriteFile("src/Core/Thing.cs", "class T { }");
        workspace.WriteFile("src/Mid/Mid.csproj", Project("..\\Core\\Core.csproj"));
        workspace.WriteFile("src/Top/Top.csproj", Project("..\\Mid\\Mid.csproj"));

        ProjectLocator.ResolveBuildTarget(workspace.Root, [changed]).ShouldBe("src/Top/Top.csproj");
    }

    [Fact]
    public void Two_unrelated_dependents_fall_back_to_the_solution()
    {
        // No single project covers both dependents, and the solution does. Without one, the
        // owner is still the best single target there is.
        using TempWorkspace workspace = new();
        workspace.WriteFile("Everything.sln", string.Empty);
        workspace.WriteFile("src/Lib/Lib.csproj", Project());
        string changed = workspace.WriteFile("src/Lib/Thing.cs", "class T { }");
        workspace.WriteFile("src/A/A.csproj", Project("..\\Lib\\Lib.csproj"));
        workspace.WriteFile("src/B/B.csproj", Project("..\\Lib\\Lib.csproj"));

        ProjectLocator.ResolveBuildTarget(workspace.Root, [changed]).ShouldBe("Everything.sln");
    }

    [Fact]
    public void Two_unrelated_dependents_without_a_solution_still_build_the_owner()
    {
        using TempWorkspace workspace = new();
        workspace.WriteFile("src/Lib/Lib.csproj", Project());
        string changed = workspace.WriteFile("src/Lib/Thing.cs", "class T { }");
        workspace.WriteFile("src/A/A.csproj", Project("..\\Lib\\Lib.csproj"));
        workspace.WriteFile("src/B/B.csproj", Project("..\\Lib\\Lib.csproj"));

        ProjectLocator.ResolveBuildTarget(workspace.Root, [changed]).ShouldBe("src/Lib/Lib.csproj");
    }

    [Fact]
    public async Task A_target_with_no_project_names_the_projects_instead_of_a_tool_to_call()
    {
        // Run e8f9186a called build "." once, was pointed at list_projects, never called it,
        // and went back to editing. The projects belong in the message the model is reading.
        using TempWorkspace workspace = new();
        workspace.WriteFile("src/Lib/Lib.csproj", Project());
        workspace.WriteFile("tests/Lib.Tests/Lib.Tests.csproj", Project("..\\..\\src\\Lib\\Lib.csproj"));

        ScriptedCommandExecutor executor = new();
        executor.Enqueue(1, "MSBUILD : error MSB1003: Specify a project or solution file.");
        BuildTool build = new(
            executor,
            workspace.Guard("."),
            new DiagnosticSummarizer(Options.Create(new VerificationOptions())),
            Options.Create(new SandboxOptions()));

        ToolObservation<BuildResult> observation = await build.BuildAsync(".");

        observation.Summary.ShouldNotBeNull();
        observation.Summary.ShouldContain("src/Lib/Lib.csproj");
        observation.Summary.ShouldContain("tests/Lib.Tests/Lib.Tests.csproj");
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
    public async Task A_solution_named_in_the_argument_loses_its_extension_too()
    {
        // Run 56f01cc5: refused at the repository root, the agent retried with a writable
        // directory as the path and the file name in the argument - and the argument went to
        // -n verbatim, so 'dotnet new sln -n ArrayProcessor.sln' wrote ArrayProcessor.sln.slnx.
        // The SDK appends its format extension to whatever name it is handed.
        using TempWorkspace workspace = new();
        ScriptedCommandExecutor executor = new();

        await Tool(workspace, executor).RunAsync(
            DotnetProjectOperation.NewSolution, "src", "ArrayProcessor.sln");

        List<string> arguments = [.. executor.Commands.Single().Arguments];
        arguments[arguments.IndexOf("-n") + 1].ShouldBe("ArrayProcessor", "the extension is the SDK's to choose");
        arguments[arguments.IndexOf("-o") + 1].ShouldEndWith("src");
    }

    /// <summary>
    /// .NET 10's <c>dotnet new sln</c> writes <c>.slnx</c>, and a caller who scaffolded a
    /// solution one step ago reasonably asks for it back as <c>.sln</c>. Run d18c0e57: the add
    /// failed with exit 1, the follow-up glob for <c>*.sln</c> matched nothing, and the
    /// solution thread of the run died there.
    /// </summary>
    [Fact]
    public async Task Adding_to_a_solution_forgives_the_extension_the_sdk_did_not_use()
    {
        using TempWorkspace workspace = new();
        workspace.WriteFile("src/App/App.csproj", Project());
        workspace.WriteFile("src/App.slnx", "<Solution />");
        ScriptedCommandExecutor executor = new();

        ToolObservation<DotnetProjectResult> observation = await Tool(workspace, executor).RunAsync(
            DotnetProjectOperation.AddToSolution, "src/App.sln", "src/App/App.csproj");

        observation.Ok.ShouldBeTrue(observation.Error?.Message);
        executor.Commands.Single().Arguments[1].ShouldEndWith("App.slnx");
        observation.Summary.ShouldContain("App.slnx", customMessage: "the message must name the file the add landed in, not the caller's spelling");
    }

    [Fact]
    public async Task Creating_a_solution_reports_the_file_the_sdk_actually_wrote()
    {
        using TempWorkspace workspace = new();
        workspace.CreateDirectory("src");
        RewritingExecutor executor = new(Path.Combine(workspace.Root, "src", "Every.slnx"), "<Solution />");

        ToolObservation<DotnetProjectResult> observation = await new DotnetProjectTool(
            executor, workspace.Guard("src"), new ChangeLog(), Options.Create(new SandboxOptions()))
            .RunAsync(DotnetProjectOperation.NewSolution, "src/Every.sln");

        observation.Ok.ShouldBeTrue(observation.Error?.Message);
        observation.Summary.ShouldContain("Every.slnx", customMessage: "the next add_to_solution and glob must be aimed at a file that exists");
    }

    /// <summary>
    /// The classlib template's Class1.cs serves no purpose, outlives every run that does not
    /// happen to delete it, and once collided with the very class the run was writing (CS0101,
    /// run d21eb210). It is removed at scaffold time, through the change log.
    /// </summary>
    [Fact]
    public async Task A_classlib_template_stub_is_removed_at_scaffold_time()
    {
        using TempWorkspace workspace = new();
        workspace.CreateDirectory("src/Lib");
        string stub = Path.Combine(workspace.Root, "src", "Lib", "Class1.cs");
        ChangeLog changes = new();

        ToolObservation<DotnetProjectResult> observation = await new DotnetProjectTool(
            new RewritingExecutor(stub, "namespace Lib;\n\npublic class Class1\n{\n\n}\n"),
            workspace.Guard("src"), changes, Options.Create(new SandboxOptions()))
            .RunAsync(DotnetProjectOperation.New, "src/Lib", "classlib");

        observation.Ok.ShouldBeTrue(observation.Error?.Message);
        observation.Summary.ShouldContain("Class1.cs was removed");
        File.Exists(stub).ShouldBeFalse();

        // Through the change log, so a file that existed for one tool call is still accounted for.
        CodeChange recorded = changes.All().ShouldHaveSingleItem();
        recorded.Status.ShouldBe(ChangeStatus.Applied);
        recorded.AfterText.ShouldBeEmpty();
    }

    /// <summary>
    /// The test stub goes too. It was named-not-deleted first, on the theory the model might
    /// write into it - and two runs in a row read the warning, wrote their tests in a fresh
    /// file, and left the empty stub counting as a pass. A suggestion the model reliably
    /// ignores is not a mechanism.
    /// </summary>
    [Fact]
    public async Task A_test_template_stub_is_removed_at_scaffold_time()
    {
        using TempWorkspace workspace = new();
        workspace.CreateDirectory("tests");
        string stub = Path.Combine(workspace.Root, "tests", "UnitTest1.cs");
        ChangeLog changes = new();

        ToolObservation<DotnetProjectResult> observation = await new DotnetProjectTool(
            new RewritingExecutor(stub, "namespace tests;\n\npublic class UnitTest1\n{\n    [Fact]\n    public void Test1()\n    {\n\n    }\n}\n"),
            workspace.Guard("tests"), changes, Options.Create(new SandboxOptions()))
            .RunAsync(DotnetProjectOperation.New, "tests", "xunit");

        observation.Ok.ShouldBeTrue(observation.Error?.Message);
        observation.Summary.ShouldContain("UnitTest1.cs was removed");
        File.Exists(stub).ShouldBeFalse();

        CodeChange recorded = changes.All().ShouldHaveSingleItem();
        recorded.Status.ShouldBe(ChangeStatus.Applied);
        recorded.AfterText.ShouldBeEmpty();
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

    // ── Formatting (workplan task 52) ──

    [Fact]
    public async Task Formatting_is_an_operation_rather_than_a_tool_of_its_own()
    {
        // dotnet format is an SDK verb, and this tool already wraps SDK verbs with the path
        // guard, the change log and the build cache. A separate tool would duplicate all three
        // and add another name to a tool list that is re-sent on every request.
        using TempWorkspace workspace = new();
        workspace.WriteFile("src/App/App.csproj", Project());
        ScriptedCommandExecutor executor = new();

        await Tool(workspace, executor).RunAsync(DotnetProjectOperation.Format, "src/App/App.csproj");

        List<string> arguments = [.. executor.Commands.Single().Arguments];
        arguments[0].ShouldBe("format");
        arguments[1].ShouldEndWith("App.csproj");
    }

    [Fact]
    public async Task Every_file_a_formatting_pass_rewrites_reaches_the_change_log()
    {
        // The reason this needed a before/after sweep rather than one named file: a pass that
        // silently reformats forty files is exactly the invisible change the log exists to
        // prevent, and the SDK does not say which ones it touched.
        using TempWorkspace workspace = new();
        workspace.WriteFile("src/App/App.csproj", Project());
        workspace.WriteFile("src/App/Tidy.cs", "class Tidy { }\n");
        string untidy = workspace.WriteFile("src/App/Untidy.cs", "class   Untidy   {   }\n");

        ChangeLog changes = new();
        RewritingExecutor executor = new(untidy, "class Untidy { }\n");

        ToolObservation<DotnetProjectResult> observation = await new DotnetProjectTool(
            executor, workspace.Guard("src"), changes, Options.Create(new SandboxOptions()))
            .RunAsync(DotnetProjectOperation.Format, "src/App/App.csproj");

        observation.Ok.ShouldBeTrue(observation.Error?.Message);
        observation.Summary.ShouldContain("1 file(s) rewritten");

        CodeChange recorded = changes.All().ShouldHaveSingleItem();
        recorded.Path.ShouldBe("src/App/Untidy.cs", "the file it left alone is not a change");
        recorded.Status.ShouldBe(ChangeStatus.Applied);
        recorded.AfterText.ShouldBe("class Untidy { }\n");
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

    /// <summary>
    /// An executor that rewrites a file, the way <c>dotnet format</c> does. The scripted executor
    /// has no side effects, and a formatting test that never changes a file would assert only
    /// that nothing was recorded.
    /// </summary>
    private sealed class RewritingExecutor(string path, string content) : ICommandExecutor
    {
        public string Sandbox => "test";

        public Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);

        public Task<CommandResult> ExecuteAsync(CommandRequest request, CancellationToken cancellationToken = default)
        {
            File.WriteAllText(path, content);
            return Task.FromResult(new CommandResult(0, string.Empty, string.Empty, TimeSpan.Zero, false, Sandbox));
        }
    }

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
