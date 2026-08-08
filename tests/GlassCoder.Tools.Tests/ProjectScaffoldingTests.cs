using GlassCoder.TestSupport;
using GlassCoder.Tools;
using GlassCoder.Tools.Build;
using GlassCoder.Tools.Changes;
using GlassCoder.Tools.Execution;
using GlassCoder.Tools.FileSystem;
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
    public async Task Stray_whitespace_on_arguments_is_forgiven_before_the_sdk_sees_it()
    {
        // Run f4ed50e0 sent add_package ' FlaUI.UIA3' - one leading space - and the SDK refused
        // it twice before the model noticed. Whitespace is never intent.
        using TempWorkspace workspace = new();
        workspace.WriteFile("src/App/App.csproj", Project());
        ScriptedCommandExecutor executor = new();

        ToolObservation<DotnetProjectResult> observation = await Tool(workspace, executor).RunAsync(
            DotnetProjectOperation.AddPackage, " src/App/App.csproj ", " xunit ");

        observation.Ok.ShouldBeTrue(observation.Error?.Message);
        IReadOnlyList<string> arguments = executor.Commands.Single().Arguments;
        arguments[3].ShouldBe("xunit");
        arguments[1].ShouldEndWith("App.csproj");
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

        // Through the change log, so a file that existed for one tool call is still accounted
        // for - as the scaffold's creation and then its removal, so a revert means "gone"
        // rather than "back to the stub".
        changes.All().Count.ShouldBe(2);
        changes.All()[0].BeforeText.ShouldBeEmpty();
        CodeChange removal = changes.All()[1];
        removal.Status.ShouldBe(ChangeStatus.Applied);
        removal.AfterText.ShouldBeEmpty();
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

        // Creation then removal, same as the classlib stub.
        changes.All().Count.ShouldBe(2);
        changes.All()[0].BeforeText.ShouldBeEmpty();
        CodeChange removal = changes.All()[1];
        removal.Status.ShouldBe(ChangeStatus.Applied);
        removal.AfterText.ShouldBeEmpty();
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

        // Information to the model, failure to the machinery: run 4b562c91 sent the same
        // misshapen call five times because every relay read as success to the loop-breakers.
        observation.OutcomeOk.ShouldBeFalse();
    }

    /// <summary>
    /// The wpf template is offered, and its scaffold survives. Two runs asked for 'wpf'
    /// unprompted, were refused, and spent ~7 steps hand-converting a console project - during
    /// which the leftover Program.cs failed three ladder climbs (runs 5c071f37, e3993510). The
    /// scaffolded window is the app's starting skeleton, so unlike Class1.cs it must be kept.
    /// </summary>
    [Fact]
    public async Task A_wpf_template_is_offered_and_its_scaffold_is_kept()
    {
        using TempWorkspace workspace = new();
        workspace.CreateDirectory("src/App");
        ScriptedCommandExecutor executor = new();

        ToolObservation<DotnetProjectResult> observation = await Tool(workspace, executor)
            .RunAsync(DotnetProjectOperation.New, "src/App", "wpf");

        observation.Ok.ShouldBeTrue(observation.Error?.Message);
        executor.Commands[0].Arguments[0].ShouldBe("new");
        executor.Commands[0].Arguments[1].ShouldBe("wpf");
        observation.Summary.ShouldContain("skeleton");
        observation.Summary.ShouldNotContain("removed");
    }

    [Fact]
    public async Task An_unknown_template_is_refused_and_the_refusal_lists_the_desktop_pair()
    {
        using TempWorkspace workspace = new();
        workspace.CreateDirectory("src/App");
        ScriptedCommandExecutor executor = new();

        ToolObservation<DotnetProjectResult> observation = await Tool(workspace, executor)
            .RunAsync(DotnetProjectOperation.New, "src/App", "maui");

        observation.Ok.ShouldBeFalse();
        observation.Error!.Code.ShouldBe(ToolErrorCodes.InvalidArgument);
        observation.Error.Hint.ShouldNotBeNull();
        observation.Error.Hint.ShouldContain("wpf");
        executor.Commands.ShouldBeEmpty("a refused template must not reach the SDK");
    }

    /// <summary>
    /// Run 4b562c91 sent add_to_solution with the project and solution swapped five times
    /// running, and the CLI's "Solution argument is misplaced" taught it nothing - the run
    /// shipped an empty solution. When the argument names the solution the intent is
    /// unambiguous, so the pieces go the right way round whichever way they arrive.
    /// </summary>
    [Fact]
    public async Task A_swapped_add_to_solution_is_put_the_right_way_round()
    {
        using TempWorkspace workspace = new();
        workspace.WriteFile("src/App/App.csproj", Project());
        workspace.WriteFile("src/App/sln.slnx", "<Solution />");
        ScriptedCommandExecutor executor = new();

        ToolObservation<DotnetProjectResult> observation = await Tool(workspace, executor)
            .RunAsync(DotnetProjectOperation.AddToSolution, "src/App/App.csproj", "src/App/sln.slnx");

        observation.Ok.ShouldBeTrue(observation.Error?.Message);
        IReadOnlyList<string> arguments = executor.Commands[0].Arguments;
        arguments[0].ShouldBe("sln");
        arguments[1].ShouldEndWith("sln.slnx");
        arguments[2].ShouldBe("add");
        arguments[3].ShouldEndWith("App.csproj");
        observation.Summary.ShouldContain("App.csproj");
    }

    /// <summary>
    /// The run's other shape: the project's directory as the path and a bare solution name as
    /// the argument. The directory holds exactly one project, and the solution sits beside it -
    /// a bare 'sln.slnx' means "the one I just made there", not one at the workspace root.
    /// </summary>
    [Fact]
    public async Task A_directory_and_a_bare_solution_name_still_find_the_project_and_the_solution()
    {
        using TempWorkspace workspace = new();
        workspace.WriteFile("src/App/App.csproj", Project());
        workspace.WriteFile("src/App/sln.slnx", "<Solution />");
        ScriptedCommandExecutor executor = new();

        ToolObservation<DotnetProjectResult> observation = await Tool(workspace, executor)
            .RunAsync(DotnetProjectOperation.AddToSolution, "src/App", "sln.slnx");

        observation.Ok.ShouldBeTrue(observation.Error?.Message);
        IReadOnlyList<string> arguments = executor.Commands[0].Arguments;
        arguments[1].ShouldEndWith("sln.slnx");
        arguments[1].ShouldContain("App");
        arguments[3].ShouldEndWith("App.csproj");
    }

    [Fact]
    public async Task An_unrepairable_swap_goes_through_unchanged()
    {
        // Two projects in the directory: no single answer, so no guess - the CLI's own error
        // is then the honest one.
        using TempWorkspace workspace = new();
        workspace.WriteFile("src/App/App.csproj", Project());
        workspace.WriteFile("src/App/Other.csproj", Project());
        workspace.WriteFile("src/App/sln.slnx", "<Solution />");
        ScriptedCommandExecutor executor = new();

        await Tool(workspace, executor)
            .RunAsync(DotnetProjectOperation.AddToSolution, "src/App", "sln.slnx");

        // No guess: the call reaches the SDK exactly as sent, and the CLI's own answer stands.
        executor.Commands[0].Arguments[1].ShouldEndWith("App");
        executor.Commands[0].Arguments[3].ShouldEndWith("sln.slnx");
    }

    // ── The framework seam between the tool's own templates (runs a408b61b, ca727be3) ──

    /// <summary>
    /// The wpf template scaffolds net10.0-windows; the xunit template scaffolds net10.0 - so
    /// wiring the pair, the very sequence the templates exist for, failed. The CLI's error
    /// lists the referencing project's framework as the constraint, which reads as "change the
    /// app": run a408b61b obeyed, downgrading the WPF app before eventually widening the test
    /// project - seven steps and three hand-edited csproj files. The narrow shape is
    /// deterministic, so the tool now widens the referencing project and retries.
    /// </summary>
    [Fact]
    public async Task A_framework_mismatch_on_add_reference_is_repaired_and_retried()
    {
        using TempWorkspace workspace = new();
        workspace.WriteFile(
            "tests/App.Tests/App.Tests.csproj",
            "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n    <TargetFramework>net10.0</TargetFramework>\n  </PropertyGroup>\n</Project>");
        workspace.WriteFile(
            "src/App/App.csproj",
            "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n    <TargetFramework>net10.0-windows</TargetFramework>\n    <UseWPF>true</UseWPF>\n  </PropertyGroup>\n</Project>");
        ScriptedCommandExecutor executor = new();
        executor.Enqueue(1,
            "Project `App.csproj` cannot be added due to incompatible targeted frameworks between the two "
            + "projects. Review the project you are trying to add and verify that is compatible with the "
            + "following targets:\n- net10.0");
        executor.Enqueue(0, "Reference added to the project.");
        ChangeLog changes = new();

        ToolObservation<DotnetProjectResult> observation = await new DotnetProjectTool(
            executor, workspace.Guard("src", "tests"), changes, Options.Create(new SandboxOptions()))
            .RunAsync(DotnetProjectOperation.AddReference, "tests/App.Tests/App.Tests.csproj", "src/App/App.csproj");

        observation.Ok.ShouldBeTrue(observation.Error?.Message);
        observation.Data!.Succeeded.ShouldBeTrue();
        observation.OutcomeOk.ShouldBeTrue();
        observation.Summary.ShouldContain("widened from net10.0 to net10.0-windows");
        File.ReadAllText(Path.Combine(workspace.Root, "tests", "App.Tests", "App.Tests.csproj"))
            .ShouldContain("<TargetFramework>net10.0-windows</TargetFramework>");
        executor.Commands.Count.ShouldBe(2, "the add is retried once after the widening");
        changes.All().ShouldContain(c =>
            c.Path == "tests/App.Tests/App.Tests.csproj" && c.AfterText.Contains("net10.0-windows"));
    }

    [Fact]
    public async Task A_framework_mismatch_with_directory_paths_is_still_repaired()
    {
        // Run c5eb67f6 spelled the referencing project as its directory - which the CLI
        // accepts, so the repair must too. Reading a TFM out of a directory yielded "an
        // unknown framework", the widen never fired, and the model hand-edited the csproj.
        using TempWorkspace workspace = new();
        workspace.WriteFile(
            "tests/App.Tests/App.Tests.csproj",
            "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n    <TargetFramework>net10.0</TargetFramework>\n  </PropertyGroup>\n</Project>");
        workspace.WriteFile(
            "src/App/App.csproj",
            "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n    <TargetFramework>net10.0-windows</TargetFramework>\n    <UseWPF>true</UseWPF>\n  </PropertyGroup>\n</Project>");
        ScriptedCommandExecutor executor = new();
        executor.Enqueue(1, "cannot be added due to incompatible targeted frameworks between the two projects.");
        executor.Enqueue(0, "Reference added to the project.");

        ToolObservation<DotnetProjectResult> observation = await new DotnetProjectTool(
            executor, workspace.Guard("src", "tests"), new ChangeLog(), Options.Create(new SandboxOptions()))
            .RunAsync(DotnetProjectOperation.AddReference, "tests/App.Tests", "src/App");

        observation.Ok.ShouldBeTrue(observation.Error?.Message);
        observation.Data!.Succeeded.ShouldBeTrue();
        observation.Summary.ShouldContain("widened from net10.0 to net10.0-windows");
        File.ReadAllText(Path.Combine(workspace.Root, "tests", "App.Tests", "App.Tests.csproj"))
            .ShouldContain("<TargetFramework>net10.0-windows</TargetFramework>");
        executor.Commands.Count.ShouldBe(2);
    }

    [Fact]
    public async Task An_unrepairable_framework_mismatch_carries_the_diagnosis()
    {
        // Any shape outside the single-TFM base-plus-suffix case is not auto-edited - but the
        // CLI's misleading message must never reach the model raw. Both frameworks and the side
        // to change go in the summary.
        using TempWorkspace workspace = new();
        workspace.WriteFile(
            "tests/App.Tests/App.Tests.csproj",
            "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n    <TargetFramework>net8.0</TargetFramework>\n  </PropertyGroup>\n</Project>");
        workspace.WriteFile(
            "src/App/App.csproj",
            "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n    <TargetFramework>net10.0-windows</TargetFramework>\n  </PropertyGroup>\n</Project>");
        ScriptedCommandExecutor executor = new();
        executor.Enqueue(1, "cannot be added due to incompatible targeted frameworks between the two projects.");

        ToolObservation<DotnetProjectResult> observation = await new DotnetProjectTool(
            executor, workspace.Guard("src", "tests"), new ChangeLog(), Options.Create(new SandboxOptions()))
            .RunAsync(DotnetProjectOperation.AddReference, "tests/App.Tests/App.Tests.csproj", "src/App/App.csproj");

        observation.Ok.ShouldBeTrue(observation.Error?.Message);
        observation.Data!.Succeeded.ShouldBeFalse();
        observation.OutcomeOk.ShouldBeFalse();
        observation.Summary.ShouldContain("net8.0");
        observation.Summary.ShouldContain("net10.0-windows");
        observation.Summary.ShouldContain("REFERENCING");
        executor.Commands.Count.ShouldBe(1, "an ambiguous shape is never auto-edited or retried");
        File.ReadAllText(Path.Combine(workspace.Root, "tests", "App.Tests", "App.Tests.csproj"))
            .ShouldContain("<TargetFramework>net8.0</TargetFramework>");
    }

    [Fact]
    public async Task A_scaffold_summary_names_the_framework()
    {
        // The framework is the one fact the next call trips over, and both a408b61b and
        // ca727be3 discovered the wpf/xunit mismatch only from add_reference's exit 1.
        using TempWorkspace workspace = new();
        workspace.CreateDirectory("src/App");
        string csproj = Path.Combine(workspace.Root, "src", "App", "App.csproj");

        ToolObservation<DotnetProjectResult> observation = await new DotnetProjectTool(
            new RewritingExecutor(
                csproj,
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0-windows</TargetFramework><UseWPF>true</UseWPF></PropertyGroup></Project>"),
            workspace.Guard("src"), new ChangeLog(), Options.Create(new SandboxOptions()))
            .RunAsync(DotnetProjectOperation.New, "src/App", "wpf");

        observation.Ok.ShouldBeTrue(observation.Error?.Message);
        observation.Summary.ShouldContain("targeting net10.0-windows");
    }

    // ── Solutions that govern nothing (run ca727be3) ──

    /// <summary>
    /// Run ca727be3 created <c>src/MultiplyApp/solution.slnx</c>, added nothing to it, and no
    /// surface mentioned the file again: off the root, so build-target resolution never saw
    /// it; empty, so builds never noticed. Said at creation, because afterwards nobody says it.
    /// </summary>
    [Fact]
    public async Task A_solution_created_off_root_says_it_will_not_govern_builds()
    {
        using TempWorkspace workspace = new();
        workspace.CreateDirectory("src");
        RewritingExecutor executor = new(Path.Combine(workspace.Root, "src", "Every.slnx"), "<Solution />");

        ToolObservation<DotnetProjectResult> observation = await new DotnetProjectTool(
            executor, workspace.Guard("src"), new ChangeLog(), Options.Create(new SandboxOptions()))
            .RunAsync(DotnetProjectOperation.NewSolution, "src/Every.sln");

        observation.Ok.ShouldBeTrue(observation.Error?.Message);
        observation.Summary.ShouldContain("not at the workspace root");
    }

    [Fact]
    public async Task A_solution_created_at_the_root_gets_no_such_note()
    {
        using TempWorkspace workspace = new();
        RewritingExecutor executor = new(Path.Combine(workspace.Root, "Every.slnx"), "<Solution />");

        ToolObservation<DotnetProjectResult> observation = await new DotnetProjectTool(
            executor, workspace.Guard("."), new ChangeLog(), Options.Create(new SandboxOptions()))
            .RunAsync(DotnetProjectOperation.NewSolution, "Every.sln");

        observation.Ok.ShouldBeTrue(observation.Error?.Message);
        observation.Summary.ShouldNotContain("not at the workspace root");
    }

    [Fact]
    public async Task An_empty_off_root_solution_is_warned_about_by_list_projects()
    {
        using TempWorkspace workspace = new();
        workspace.WriteFile("src/App/App.csproj", Project());
        workspace.WriteFile("src/sln.slnx", "<Solution />");

        ToolObservation<ListProjectsResult> observation =
            await new ListProjectsTool(workspace.Guard()).ListAsync();

        observation.Ok.ShouldBeTrue();
        observation.Data!.Solutions.ShouldContain("src/sln.slnx");
        observation.Data.Warnings.ShouldContain(w => w.Contains("contains no projects", StringComparison.Ordinal));
        observation.Data.Warnings.ShouldContain(w => w.Contains("not at the workspace root", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_populated_root_solution_draws_no_solution_warnings()
    {
        using TempWorkspace workspace = new();
        workspace.WriteFile("Everything.slnx", "<Solution><Project Path=\"src/App/App.csproj\" /></Solution>");
        workspace.WriteFile("src/App/App.csproj", Project());

        ToolObservation<ListProjectsResult> observation =
            await new ListProjectsTool(workspace.Guard()).ListAsync();

        observation.Ok.ShouldBeTrue();
        observation.Data!.Solutions.ShouldContain("Everything.slnx");
        observation.Data.Warnings.ShouldNotContain(w => w.Contains("contains no projects", StringComparison.Ordinal));
        observation.Data.Warnings.ShouldNotContain(w => w.Contains("not at the workspace root", StringComparison.Ordinal));
    }

    // ── Where a scaffold may land (run 008007e11a) ──

    /// <summary>
    /// Run 008007e11a: told its window should be a dialog, the model asked <c>new</c> for
    /// 'src/MultiplyApp/DialogWindow.xaml' - a file name - and received a complete second WPF
    /// application nested inside the first, then spent the rest of its token budget failing to
    /// delete it. A path that names a file is a misread of what <c>new</c> does, and the
    /// refusal points at create_file, the tool the model actually wanted.
    /// </summary>
    [Fact]
    public async Task A_path_that_names_a_file_is_refused_before_the_sdk_runs()
    {
        using TempWorkspace workspace = new();
        workspace.WriteFile("src/App/App.csproj", Project());
        ScriptedCommandExecutor executor = new();

        ToolObservation<DotnetProjectResult> observation = await Tool(workspace, executor).RunAsync(
            DotnetProjectOperation.New, "src/App/DialogWindow.xaml", "wpf");

        observation.Ok.ShouldBeFalse();
        observation.Error!.Code.ShouldBe(ToolErrorCodes.InvalidArgument);
        observation.Error.Hint.ShouldNotBeNull();
        observation.Error.Hint.ShouldContain("create_file");
        executor.Commands.ShouldBeEmpty("the refusal must come before six files exist");
    }

    [Fact]
    public async Task A_scaffold_inside_an_existing_project_is_refused()
    {
        // The SDK's default glob compiles a nested project's sources into its parent.
        // list_projects has warned about that after the fact all along; this is the same
        // knowledge applied while refusing is still one cheap step.
        using TempWorkspace workspace = new();
        workspace.WriteFile("src/App/App.csproj", Project());
        ScriptedCommandExecutor executor = new();

        ToolObservation<DotnetProjectResult> observation = await Tool(workspace, executor).RunAsync(
            DotnetProjectOperation.New, "src/App/Dialog", "wpf");

        observation.Ok.ShouldBeFalse();
        observation.Error!.Code.ShouldBe(ToolErrorCodes.InvalidArgument);
        observation.Error.Message.ShouldContain("App.csproj");
        executor.Commands.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_scaffold_above_an_existing_project_is_refused()
    {
        // The same nesting the other way up: a project scaffolded at src/ would swallow the
        // sources of every project already under it.
        using TempWorkspace workspace = new();
        workspace.WriteFile("src/App/App.csproj", Project());
        ScriptedCommandExecutor executor = new();

        ToolObservation<DotnetProjectResult> observation = await Tool(workspace, executor).RunAsync(
            DotnetProjectOperation.New, "src", "console");

        observation.Ok.ShouldBeFalse();
        observation.Error!.Code.ShouldBe(ToolErrorCodes.InvalidArgument);
        observation.Error.Message.ShouldContain("App.csproj");
        executor.Commands.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_sibling_scaffold_is_still_welcome()
    {
        // The correct shape the refusals steer towards has to keep working.
        using TempWorkspace workspace = new();
        workspace.WriteFile("src/App/App.csproj", Project());
        ScriptedCommandExecutor executor = new();

        ToolObservation<DotnetProjectResult> observation = await Tool(workspace, executor).RunAsync(
            DotnetProjectOperation.New, "src/Dialog", "wpf");

        observation.Ok.ShouldBeTrue(observation.Error?.Message);
        executor.Commands.ShouldNotBeEmpty();
    }

    // ── The scaffold reaches the change log (run 008007e11a) ──

    /// <summary>
    /// <c>dotnet new</c> writes without going through the change log, so a scaffolded file
    /// used to have no baseline - "how this run found it" became whatever the first later
    /// touch happened to record.
    /// </summary>
    [Fact]
    public async Task A_scaffolded_file_is_recorded_as_created_by_this_run()
    {
        using TempWorkspace workspace = new();
        workspace.CreateDirectory("src/App");
        string window = Path.Combine(workspace.Root, "src", "App", "MainWindow.xaml");
        ChangeLog changes = new();

        ToolObservation<DotnetProjectResult> observation = await new DotnetProjectTool(
            new RewritingExecutor(window, "<Window />"),
            workspace.Guard("src"), changes, Options.Create(new SandboxOptions()))
            .RunAsync(DotnetProjectOperation.New, "src/App", "wpf");

        observation.Ok.ShouldBeTrue(observation.Error?.Message);

        CodeChange recorded = changes.All().ShouldHaveSingleItem();
        recorded.Path.ShouldBe("src/App/MainWindow.xaml");
        recorded.BeforeText.ShouldBeEmpty("the run created it, and the baseline must say so");
        recorded.AfterText.ShouldBe("<Window />");
        recorded.Status.ShouldBe(ChangeStatus.Applied);
    }

    [Fact]
    public async Task A_file_already_there_is_not_claimed_as_scaffolded()
    {
        using TempWorkspace workspace = new();
        workspace.WriteFile("src/App/README.md", "already here");
        string window = Path.Combine(workspace.Root, "src", "App", "MainWindow.xaml");
        ChangeLog changes = new();

        await new DotnetProjectTool(
            new RewritingExecutor(window, "<Window />"),
            workspace.Guard("src"), changes, Options.Create(new SandboxOptions()))
            .RunAsync(DotnetProjectOperation.New, "src/App", "wpf");

        changes.All().ShouldHaveSingleItem().Path.ShouldBe("src/App/MainWindow.xaml");
    }

    [Fact]
    public async Task A_created_solution_is_recorded_as_created_by_this_run()
    {
        using TempWorkspace workspace = new();
        workspace.CreateDirectory("src");
        ChangeLog changes = new();
        RewritingExecutor executor = new(Path.Combine(workspace.Root, "src", "Every.slnx"), "<Solution />");

        await new DotnetProjectTool(
            executor, workspace.Guard("src"), changes, Options.Create(new SandboxOptions()))
            .RunAsync(DotnetProjectOperation.NewSolution, "src/Every.sln");

        CodeChange recorded = changes.All().ShouldHaveSingleItem();
        recorded.Path.ShouldBe("src/Every.slnx");
        recorded.BeforeText.ShouldBeEmpty();
        recorded.Status.ShouldBe(ChangeStatus.Applied);
    }

    /// <summary>
    /// Revert on a scaffolded file now means what it says. Before the creation was recorded,
    /// "how this run found it" resolved to the scaffold's content via whatever touched the
    /// file first - in run 008007e11a that was a delete, and the revert restored the very
    /// file the model was trying to be rid of.
    /// </summary>
    [Fact]
    public async Task Reverting_a_scaffolded_file_removes_it_rather_than_restoring_the_scaffold()
    {
        using TempWorkspace workspace = new();
        workspace.CreateDirectory("src/App");
        string window = Path.Combine(workspace.Root, "src", "App", "MainWindow.xaml");
        ChangeLog changes = new();
        PathGuard guard = workspace.Guard("src");

        await new DotnetProjectTool(
            new RewritingExecutor(window, "<Window />"),
            guard, changes, Options.Create(new SandboxOptions()))
            .RunAsync(DotnetProjectOperation.New, "src/App", "wpf");

        ToolObservation<FileOperationResult> revert = await new FileOperationTool(guard, changes)
            .RunAsync(FileOperation.Revert, "src/App/MainWindow.xaml");

        revert.Ok.ShouldBeTrue(revert.Error?.Message);
        revert.Summary.ShouldContain("removed");
        File.Exists(window).ShouldBeFalse("this run created the file, so found-state is absence");
    }

    /// <summary>
    /// The exact 008007e11a sequence: scaffold, delete, revert. The delete already put the
    /// file back to how the run found it - not existing - so the revert has nothing to do,
    /// and says so instead of resurrecting the scaffold.
    /// </summary>
    [Fact]
    public async Task Reverting_a_deleted_scaffold_file_does_not_resurrect_it()
    {
        using TempWorkspace workspace = new();
        workspace.CreateDirectory("src/App");
        string window = Path.Combine(workspace.Root, "src", "App", "MainWindow.xaml");
        ChangeLog changes = new();
        PathGuard guard = workspace.Guard("src");

        await new DotnetProjectTool(
            new RewritingExecutor(window, "<Window />"),
            guard, changes, Options.Create(new SandboxOptions()))
            .RunAsync(DotnetProjectOperation.New, "src/App", "wpf");

        FileOperationTool files = new(guard, changes);
        await files.RunAsync(FileOperation.Delete, "src/App/MainWindow.xaml");
        ToolObservation<FileOperationResult> revert =
            await files.RunAsync(FileOperation.Revert, "src/App/MainWindow.xaml");

        revert.Ok.ShouldBeFalse();
        revert.Error!.Message.ShouldContain("already as this run found it");
        File.Exists(window).ShouldBeFalse();
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
