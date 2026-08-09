using GlassCoder.TestSupport;
using GlassCoder.Tools;
using GlassCoder.Tools.Build;
using GlassCoder.Tools.Changes;
using GlassCoder.Tools.Execution;
using GlassCoder.Tools.FileSystem;
using GlassCoder.Tools.Verification;
using Microsoft.Extensions.Options;

namespace GlassCoder.Tools.Tests;

/// <summary>
/// The cheap compile gate, and when it must decline to answer (workplan task 44).
/// <para>
/// The gate refuses a write that would not compile, which is the right trade when it can see the
/// whole picture. Its reference set is scavenged from build output rather than evaluated from the
/// project file, so for a project whose dependencies have not been built yet it cannot - and every
/// type the file imports comes back as CS0246, including the correct ones.
/// </para>
/// <para>
/// That is how a run asked to add unit tests produced none: the test file's <c>using Utils;</c>
/// was right, the gate could not see <c>Utils</c>, and the write was refused three times. An
/// inconclusive check must not gate.
/// </para>
/// </summary>
public sealed class PreWriteGateTests
{
    [Fact]
    public async Task A_project_whose_references_are_not_built_yet_is_inconclusive_rather_than_broken()
    {
        using TempWorkspace workspace = new();
        workspace.WriteFile("src/ArrayOperations.csproj", Project());
        workspace.WriteFile("src/Utils/ArrayOperations.cs", "namespace Utils { public static class A { } }");
        workspace.WriteFile(
            "src/ArrayOperations.Tests/ArrayOperations.Tests.csproj",
            Project(reference: "..\\ArrayOperations.csproj"));

        string testFile = Path.Combine(workspace.Root, "src", "ArrayOperations.Tests", "ArrayOperationsTests.cs");
        RoslynCodeAnalyzer analyzer = new(workspace.Guard("src"), Options.Create(new VerificationOptions()));

        DiagnosticReport report = await analyzer.CheckEditAsync(
            testFile,
            "using Utils;\npublic class T { public void Run() { _ = typeof(A); } }");

        report.FailureReason.ShouldNotBeNull("the reference set is known to be incomplete");
        report.FailureReason.ShouldContain("ArrayOperations");
        report.FailureReason.ShouldContain("build tool");
        report.Diagnostics.ShouldBeEmpty("a check that could not run reports no findings");
    }

    [Fact]
    public async Task The_gate_lets_that_file_through_instead_of_refusing_it()
    {
        // The end-to-end shape of the defect: three refused create_file calls, and no test file
        // on disk when the run hit its step limit.
        using TempWorkspace workspace = new();
        workspace.WriteFile("src/ArrayOperations.csproj", Project());
        workspace.WriteFile("src/Utils/ArrayOperations.cs", "namespace Utils { public static class A { } }");
        workspace.WriteFile(
            "src/ArrayOperations.Tests/ArrayOperations.Tests.csproj",
            Project(reference: "..\\ArrayOperations.csproj"));

        CreateFileTool tool = Create(workspace);

        ToolObservation<CreateFileResult> observation = await tool.CreateFileAsync(
            "src/ArrayOperations.Tests/ArrayOperationsTests.cs",
            "using Utils;\npublic class T { public void Run() { _ = typeof(A); } }");

        observation.Ok.ShouldBeTrue();
        File.Exists(Path.Combine(workspace.Root, "src", "ArrayOperations.Tests", "ArrayOperationsTests.cs"))
            .ShouldBeTrue();
    }

    [Fact]
    public async Task A_project_with_no_references_is_still_gated()
    {
        // The suppression has to be narrow. A single-project tree is exactly the case the gate
        // was built for, and switching it off everywhere would trade one defect for another.
        using TempWorkspace workspace = new();
        workspace.WriteFile("src/App.csproj", Project());
        workspace.WriteFile("src/Program.cs", "public class P { }");

        CreateFileTool tool = Create(workspace);

        ToolObservation<CreateFileResult> observation = await tool.CreateFileAsync(
            "src/Broken.cs", "public class B { public void M() { NoSuchType x = null; } }");

        observation.Ok.ShouldBeFalse();
        observation.Error!.Code.ShouldBe(ToolErrorCodes.VerificationFailed);
    }

    private static CreateFileTool Create(TempWorkspace workspace)
    {
        IOptions<VerificationOptions> verification = Options.Create(new VerificationOptions());
        return new CreateFileTool(
            workspace.Guard("src"),
            new RoslynCodeAnalyzer(workspace.Guard("src"), verification),
            new DiagnosticSummarizer(verification),
            verification,
            new ChangeLog(),
            new AutoApprovalGate(Options.Create(new ApprovalOptions())));
    }

    private static string Project(string? reference = null) =>
        $"""
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup><TargetFramework>net6.0</TargetFramework></PropertyGroup>
          {(reference is null ? string.Empty : $"""<ItemGroup><ProjectReference Include="{reference}" /></ItemGroup>""")}
        </Project>
        """;
}

/// <summary>
/// Not building the same unchanged tree twice (workplan task 44).
/// <para>
/// The failed run built eight times in thirty steps, three of them consecutively with no edit in
/// between. Each cost ten to thirty seconds and a step of a finite budget, and each returned the
/// answer the one before it had already given.
/// </para>
/// </summary>
public sealed class BuildCacheTests
{
    [Fact]
    public async Task Building_an_unchanged_tree_twice_only_builds_once()
    {
        using TempWorkspace workspace = new();
        ScriptedCommandExecutor executor = new();
        ChangeLog changes = new();
        BuildTool build = Build(workspace, executor, changes);

        await build.BuildAsync("src");
        ToolObservation<BuildResult> second = await build.BuildAsync("src");

        executor.Commands.Count.ShouldBe(1);
        second.Ok.ShouldBeTrue();
        second.Summary.ShouldContain("unchanged");
    }

    [Fact]
    public async Task A_change_makes_the_next_build_real_again()
    {
        using TempWorkspace workspace = new();
        ScriptedCommandExecutor executor = new();
        ChangeLog changes = new();
        BuildTool build = Build(workspace, executor, changes);

        await build.BuildAsync("src");
        changes.Propose("src/Program.cs", "edit_file", "before", "after");
        await build.BuildAsync("src");

        executor.Commands.Count.ShouldBe(2);
    }

    [Fact]
    public async Task A_failed_build_is_never_replayed()
    {
        // Caching a failure could leave the agent fixing something it already fixed. A stale
        // success is impossible - anything that could change the answer empties the cache.
        using TempWorkspace workspace = new();
        ScriptedCommandExecutor executor = new();
        executor.Enqueue(1, "Program.cs(1,1): error CS1002: ; expected");
        BuildTool build = Build(workspace, executor, new ChangeLog());

        await build.BuildAsync("src");
        await build.BuildAsync("src");

        executor.Commands.Count.ShouldBe(2);
    }

    [Fact]
    public async Task Rewriting_a_project_file_through_the_sdk_empties_the_cache()
    {
        using TempWorkspace workspace = new();
        workspace.WriteFile("src/App.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        ScriptedCommandExecutor executor = new();
        ChangeLog changes = new();
        BuildCache cache = new(changes);
        BuildTool build = new(
            executor, workspace.Guard("src"), Summarizer(), Options.Create(new SandboxOptions()), cache);
        DotnetProjectTool project = new(
            executor, workspace.Guard("src"), changes, Options.Create(new SandboxOptions()), cache);

        await build.BuildAsync("src");
        await project.RunAsync(DotnetProjectOperation.AddPackage, "src/App.csproj", "xunit");
        await build.BuildAsync("src");

        // build, add package, build - the SDK rewrote the project outside the change log, so the
        // cached answer had to be thrown away.
        executor.Commands.Count.ShouldBe(3);
    }

    /// <summary>
    /// The same argument one rung up (workplan task 74). Run <c>d5edbc59</c> spent steps 19, 20,
    /// 25 and 26 re-establishing greens that its inline verification had already reported at
    /// steps 17 and 24 - four of twenty-eight steps on the one axis that was never in doubt.
    /// </summary>
    [Fact]
    public async Task Running_unchanged_tests_twice_only_runs_them_once()
    {
        using TempWorkspace workspace = new();
        ScriptedCommandExecutor executor = new();
        executor.Enqueue(0, "Passed!  - Failed: 0, Passed: 7, Skipped: 0, Total: 7");
        RunTestsTool tests = Tests(workspace, executor, new ChangeLog());

        await tests.RunTestsAsync("src");
        ToolObservation<TestRunResult> second = await tests.RunTestsAsync("src");

        executor.Commands.Count.ShouldBe(1);
        second.Ok.ShouldBeTrue();
        second.Summary.ShouldContain("unchanged");
        second.Data!.Total.ShouldBe(7, "the remembered result is the whole result, not a stub");
    }

    [Fact]
    public async Task A_change_makes_the_next_test_run_real_again()
    {
        using TempWorkspace workspace = new();
        ScriptedCommandExecutor executor = new();
        executor.Enqueue(0, "Passed!  - Failed: 0, Passed: 7, Skipped: 0, Total: 7");
        executor.Enqueue(0, "Passed!  - Failed: 0, Passed: 8, Skipped: 0, Total: 8");
        ChangeLog changes = new();
        RunTestsTool tests = Tests(workspace, executor, changes);

        await tests.RunTestsAsync("src");
        changes.Propose("src/WidgetTests.cs", "create_file", string.Empty, "new test");
        await tests.RunTestsAsync("src");

        executor.Commands.Count.ShouldBe(2);
    }

    [Fact]
    public async Task A_red_suite_is_never_remembered()
    {
        // The same reasoning as a failed build: a red run is the observation the agent is acting
        // on, and replaying it could leave it re-fixing something it has already fixed.
        using TempWorkspace workspace = new();
        ScriptedCommandExecutor executor = new();
        executor.Enqueue(1, "  Failed A.B.C [1 ms]\nFailed!  - Failed: 1, Passed: 6, Skipped: 0, Total: 7");
        executor.Enqueue(1, "  Failed A.B.C [1 ms]\nFailed!  - Failed: 1, Passed: 6, Skipped: 0, Total: 7");
        RunTestsTool tests = Tests(workspace, executor, new ChangeLog());

        await tests.RunTestsAsync("src");
        await tests.RunTestsAsync("src");

        executor.Commands.Count.ShouldBe(2);
    }

    [Fact]
    public async Task A_run_that_executed_nothing_is_never_remembered()
    {
        // "0 of 0 tests" is the absence of a result, not a green one. Serving it from a cache
        // would put "nothing was verified" behind a hit, which is the failure RungResult.
        // Unverified exists to prevent.
        using TempWorkspace workspace = new();
        ScriptedCommandExecutor executor = new();
        executor.Enqueue(0, "Passed!  - Failed: 0, Passed: 0, Skipped: 0, Total: 0");
        executor.Enqueue(0, "Passed!  - Failed: 0, Passed: 0, Skipped: 0, Total: 0");
        RunTestsTool tests = Tests(workspace, executor, new ChangeLog());

        await tests.RunTestsAsync("src");
        await tests.RunTestsAsync("src");

        executor.Commands.Count.ShouldBe(2);
    }

    [Fact]
    public async Task A_different_filter_is_a_different_question()
    {
        // "All tests passed" under one filter says nothing about another.
        using TempWorkspace workspace = new();
        ScriptedCommandExecutor executor = new();
        executor.Enqueue(0, "Passed!  - Failed: 0, Passed: 7, Skipped: 0, Total: 7");
        executor.Enqueue(0, "Passed!  - Failed: 0, Passed: 2, Skipped: 0, Total: 2");
        RunTestsTool tests = Tests(workspace, executor, new ChangeLog());

        await tests.RunTestsAsync("src");
        await tests.RunTestsAsync("src", "FullyQualifiedName~Widget");

        executor.Commands.Count.ShouldBe(2);
    }

    [Fact]
    public async Task Discovery_is_never_served_from_the_cache()
    {
        // Discovery is cheap, and a stale list of names is precisely what a discovery call asks
        // about - it is the one question a remembered answer cannot answer.
        using TempWorkspace workspace = new();
        ScriptedCommandExecutor executor = new();
        executor.Enqueue(0, "Passed!  - Failed: 0, Passed: 7, Skipped: 0, Total: 7");
        executor.Enqueue(0, "The following Tests are available:\n    A.B.C\n");
        RunTestsTool tests = Tests(workspace, executor, new ChangeLog());

        await tests.RunTestsAsync("src");
        await tests.RunTestsAsync("src", listOnly: true);

        executor.Commands.Count.ShouldBe(2);
    }

    /// <summary>
    /// Why this cache had never once been read in production. <c>AgentLoop</c> ends every verified
    /// step by writing the ladder's summary back onto each applied change - at the status it
    /// already had - and <c>IChangeLog.Update</c> raises <c>Changed</c> for any write at all. That
    /// arrives a few milliseconds after the ladder's own Compile and UnitTests rungs have filled
    /// this, so it was emptied on every step. Two runs on 2026-08-09 recorded zero hits between
    /// them, and the mechanism had shipped a day earlier.
    /// </summary>
    [Fact]
    public async Task A_verification_summary_written_onto_an_applied_change_does_not_empty_the_cache()
    {
        using TempWorkspace workspace = new();
        ScriptedCommandExecutor executor = new();
        ChangeLog changes = new();
        BuildTool build = Build(workspace, executor, changes);

        CodeChange change = changes.Propose("src/App.cs", "edit_file", "before", "after");
        changes.Update(change.Id, ChangeStatus.Applied);

        await build.BuildAsync("src");

        // Exactly what AgentLoop:604 does: same status, plus what the ladder found.
        changes.Update(change.Id, ChangeStatus.Applied, verificationSummary: "passed at rung UnitTests");

        await build.BuildAsync("src");

        executor.Commands.Count.ShouldBe(1, "re-announcing a change at its existing status moved no bytes");
    }

    [Fact]
    public async Task A_real_status_move_still_empties_it()
    {
        using TempWorkspace workspace = new();
        ScriptedCommandExecutor executor = new();
        ChangeLog changes = new();
        BuildTool build = Build(workspace, executor, changes);

        CodeChange change = changes.Propose("src/App.cs", "edit_file", "before", "after");
        changes.Update(change.Id, ChangeStatus.Applied);

        await build.BuildAsync("src");

        // Written and then undone: the tree moved, whatever the cache remembers is wrong.
        changes.Update(change.Id, ChangeStatus.Reverted);

        await build.BuildAsync("src");

        executor.Commands.Count.ShouldBe(2);
    }

    /// <summary>
    /// The build a test run does not have to do again. The ladder is the case that pays: its
    /// Compile rung builds the target its UnitTests rung is about to test, moments earlier.
    /// </summary>
    [Fact]
    public async Task Tests_after_a_green_build_of_the_same_target_skip_the_build()
    {
        using TempWorkspace workspace = new();
        ScriptedCommandExecutor executor = new();
        executor.Enqueue(0).Enqueue(0, "Passed!  - Failed: 0, Passed: 7, Skipped: 0, Total: 7");
        BuildCache cache = new(new ChangeLog());
        (BuildTool build, RunTestsTool tests) = Pair(workspace, executor, cache);

        await build.BuildAsync("src");
        await tests.RunTestsAsync("src");

        executor.Commands[^1].Arguments.ShouldContain("--no-build");
    }

    [Fact]
    public async Task Tests_with_no_fresh_build_behind_them_still_build()
    {
        using TempWorkspace workspace = new();
        ScriptedCommandExecutor executor = new();
        executor.Enqueue(0, "Passed!  - Failed: 0, Passed: 7, Skipped: 0, Total: 7");
        BuildCache cache = new(new ChangeLog());
        (_, RunTestsTool tests) = Pair(workspace, executor, cache);

        await tests.RunTestsAsync("src");

        executor.Commands[^1].Arguments.ShouldNotContain("--no-build");
    }

    /// <summary>
    /// The one failure this optimisation can manufacture: the cache says the target built, but its
    /// output is not where the runner looked. Paying for the build once beats reporting it as a
    /// test failure, which is what a run that executed nothing looks like.
    /// </summary>
    [Fact]
    public async Task A_skipped_build_that_runs_no_tests_is_rebuilt_and_retried_once()
    {
        using TempWorkspace workspace = new();
        ScriptedCommandExecutor executor = new();
        executor.Enqueue(0)
            .Enqueue(1, "MSBUILD : error MSB1009: Project file does not exist.")
            .Enqueue(0, "Passed!  - Failed: 0, Passed: 7, Skipped: 0, Total: 7");
        BuildCache cache = new(new ChangeLog());
        (BuildTool build, RunTestsTool tests) = Pair(workspace, executor, cache);

        await build.BuildAsync("src");
        ToolObservation<TestRunResult> observation = await tests.RunTestsAsync("src");

        executor.Commands.Count.ShouldBe(3);
        executor.Commands[1].Arguments.ShouldContain("--no-build");
        executor.Commands[2].Arguments.ShouldNotContain("--no-build");
        observation.Data!.Total.ShouldBe(7);
    }

    private static (BuildTool Build, RunTestsTool Tests) Pair(
        TempWorkspace workspace, ICommandExecutor executor, BuildCache cache) =>
        (new BuildTool(executor, workspace.Guard("src"), Summarizer(), Options.Create(new SandboxOptions()), cache),
         new RunTestsTool(executor, workspace.Guard("src"), Options.Create(new SandboxOptions()), cache));

    private static BuildTool Build(TempWorkspace workspace, ICommandExecutor executor, ChangeLog changes) =>
        new(executor, workspace.Guard("src"), Summarizer(), Options.Create(new SandboxOptions()), new BuildCache(changes));

    private static RunTestsTool Tests(TempWorkspace workspace, ICommandExecutor executor, ChangeLog changes) =>
        new(executor, workspace.Guard("src"), Options.Create(new SandboxOptions()), new BuildCache(changes));

    private static DiagnosticSummarizer Summarizer() => new(Options.Create(new VerificationOptions()));
}
