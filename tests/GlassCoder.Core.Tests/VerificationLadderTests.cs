using GlassCoder.Core.Verification;
using GlassCoder.TestSupport;
using GlassCoder.Tools.Build;
using GlassCoder.Tools.Execution;
using GlassCoder.Tools.Guardrails;
using GlassCoder.Tools.Verification;
using Microsoft.Extensions.Options;

namespace GlassCoder.Core.Tests;

/// <summary>
/// The verification ladder (workplan task 18). Two properties matter and both are about what
/// does <em>not</em> happen: the climb stops at the first failing rung, and tests never run on
/// code that does not compile.
/// </summary>
public sealed class VerificationLadderTests : IDisposable
{
    private readonly TempWorkspace _workspace = new();
    private readonly ScriptedCommandExecutor _executor = new();

    /// <summary>
    /// A project that holds tests, which is what every case below that scripts a test run needs to
    /// be: since run 457867c7 the rung asks whether anything in the tree references a test
    /// framework before it pays for a process, so a bare project would answer the question without
    /// running the scripted command at all.
    /// </summary>
    private const string TestProject =
        """
        <Project Sdk="Microsoft.NET.Sdk">
          <ItemGroup><PackageReference Include="xunit" Version="2.9.2" /></ItemGroup>
        </Project>
        """;

    public void Dispose() => _workspace.Dispose();

    [Fact]
    public async Task A_syntax_error_stops_the_climb_at_rung_one()
    {
        _executor.Enqueue(0, "");   // would be the build, and must never be reached

        VerificationReport report = await Ladder().VerifyAsync(new VerificationRequest(
            FilePath: "src/Pager.cs",
            FileText: "public class Pager { public int X => ; }"));

        report.Passed.ShouldBeFalse();
        report.FailedRung.ShouldBe(VerificationRung.Syntax);
        report.Results.ShouldHaveSingleItem();
        _executor.Commands.ShouldBeEmpty("nothing more expensive than a parse should have run");
    }

    [Fact]
    public async Task A_compile_error_stops_the_climb_before_any_test_runs()
    {
        _workspace.WriteFile("src/Pager.cs", "public class Pager { }");
        _executor.Enqueue(1, "C:\\repo\\src\\Pager.cs(1,1): error CS0103: broken [C:\\repo\\src\\Proj.csproj]");

        VerificationReport report = await Ladder().VerifyAsync(new VerificationRequest(ProjectPath: "src"));

        report.Passed.ShouldBeFalse();
        report.FailedRung.ShouldBe(VerificationRung.Compile);
        report.Summary.ShouldContain("CS0103");
        _executor.Commands.ShouldHaveSingleItem();
        _executor.Commands[0].Arguments[0].ShouldBe("build");
    }

    [Fact]
    public async Task Analyzers_report_but_never_gate()
    {
        // Rung 3 of the ladder: convention drift is worth saying, never worth blocking a fix.
        _workspace.WriteFile("src/Proj.csproj", TestProject);
        _workspace.WriteFile("src/Pager.cs", "public class Pager { public int X => 1; }");
        _executor.Enqueue(0, "");                                              // build: green
        _executor.Enqueue(0, "Passed!  - Failed: 0, Passed: 3, Skipped: 0, Total: 3");   // tests: green

        VerificationReport report = await Ladder().VerifyAsync(new VerificationRequest(ProjectPath: "src"));

        report.Passed.ShouldBeTrue();
        report.Results.ShouldContain(r => r.Rung == VerificationRung.Analyzers && r.Passed);
        report.HighestRungReached.ShouldBe(VerificationRung.UnitTests);
    }

    [Fact]
    public async Task A_test_run_that_verified_nothing_is_not_reported_green()
    {
        // Runs a408b61b and ca727be3 each logged "UnitTests passed" eleven times over a
        // workspace holding no test files: 0 of 0 is not a passing suite. It does not gate - a
        // testless tree is a fact, not a failure - but it stops reading as verification, and
        // the honest line stays in the summary the model and the critics judge.
        _workspace.WriteFile("src/Proj.csproj", TestProject);
        _workspace.WriteFile("src/Pager.cs", "public class Pager { public int X => 1; }");
        _executor.Enqueue(0, "");   // the build
        _executor.Enqueue(0, "");   // dotnet test: exits clean, discovers nothing

        VerificationReport report = await Ladder().VerifyAsync(new VerificationRequest(ProjectPath: "src"));

        report.Passed.ShouldBeTrue("zero tests must not gate");
        report.Unverified.ShouldBeTrue();
        RungResult tests = report.Results.Single(r => r.Rung == VerificationRung.UnitTests);
        tests.Unverified.ShouldBeTrue();
        tests.Summary.ShouldContain("nothing was verified");
    }

    [Fact]
    public async Task A_green_says_when_the_count_did_not_move()
    {
        // Run 29356042: step 16 said it would add a UI test, step 17 applied a refactor, the rung
        // said "7 tests passed" - the same seven as step 13 - and step 18 offered "UI integration"
        // as evidence of adequacy. Two panels accepted it. A passing count is the one signal that
        // cannot tell a test added from no test added; what moved since the last green can.
        _workspace.WriteFile("src/Proj.csproj", TestProject);
        _workspace.WriteFile("src/Pager.cs", "public class Pager { public int X => 1; }");

        const string sevenPassed = "Passed!  - Failed: 0, Passed: 7, Skipped: 0, Total: 7";
        _executor.Enqueue(0, "");               // first climb: the build
        _executor.Enqueue(0, sevenPassed);      //              the tests
        _executor.Enqueue(0, "");               // second climb, over the same target
        _executor.Enqueue(0, sevenPassed);

        TestCountMemo memo = new();
        VerificationLadder ladder = Ladder(memo);

        RungResult first = (await ladder.VerifyAsync(new VerificationRequest(ProjectPath: "src")))
            .Results.Single(r => r.Rung == VerificationRung.UnitTests);
        RungResult second = (await ladder.VerifyAsync(new VerificationRequest(ProjectPath: "src")))
            .Results.Single(r => r.Rung == VerificationRung.UnitTests);

        first.Summary.ShouldNotContain("previous climb", customMessage: "there was no previous climb");
        second.Summary.ShouldContain("7 tests passed.");
        second.Summary.ShouldContain("The same number as the previous climb");

        // After the count, never inside it: the sentry keys repeated failures on the first line.
        second.Summary.IndexOf("The same number", StringComparison.Ordinal)
            .ShouldBeGreaterThan(second.Summary.IndexOf("7 tests passed.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_green_that_gained_a_test_says_nothing_about_the_previous_one()
    {
        _workspace.WriteFile("src/Proj.csproj", TestProject);
        _workspace.WriteFile("src/Pager.cs", "public class Pager { public int X => 1; }");

        _executor.Enqueue(0, "");
        _executor.Enqueue(0, "Passed!  - Failed: 0, Passed: 7, Skipped: 0, Total: 7");
        _executor.Enqueue(0, "");
        _executor.Enqueue(0, "Passed!  - Failed: 0, Passed: 8, Skipped: 0, Total: 8");

        TestCountMemo memo = new();
        VerificationLadder ladder = Ladder(memo);

        await ladder.VerifyAsync(new VerificationRequest(ProjectPath: "src"));
        RungResult second = (await ladder.VerifyAsync(new VerificationRequest(ProjectPath: "src")))
            .Results.Single(r => r.Rung == VerificationRung.UnitTests);

        second.Summary.ShouldContain("8 tests passed.");
        second.Summary.ShouldNotContain("previous climb");
    }

    [Fact]
    public async Task A_tree_with_no_test_project_does_not_pay_for_a_test_process()
    {
        // Steps 3-8 of run 457867c7: six scaffolding changes, six ladder climbs, six dotnet test
        // launches, each to be told that a workspace with no test project ran no tests. The answer
        // is in the project files. It stays Unverified, so the counters and the verdict wording
        // still say a rung ran and established nothing.
        _workspace.WriteFile("src/Proj.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");
        _workspace.WriteFile("src/Pager.cs", "public class Pager { public int X => 1; }");
        _executor.Enqueue(0, "");   // the build, and the only command that should run

        VerificationReport report = await Ladder().VerifyAsync(new VerificationRequest(ProjectPath: "src"));

        report.Passed.ShouldBeTrue("a testless tree is a fact, not a failure");
        report.Unverified.ShouldBeTrue();
        report.Results.Single(r => r.Rung == VerificationRung.UnitTests).Summary
            .ShouldContain("references a test framework");
        _executor.Commands.Count(c => c.Arguments[0] == "test")
            .ShouldBe(0, "the rung answered from the project files");
    }

    [Fact]
    public async Task A_failing_test_stops_the_climb_before_the_full_suite()
    {
        _workspace.WriteFile("src/Proj.csproj", TestProject);
        _workspace.WriteFile("src/Pager.cs", "public class Pager { public int X => 1; }");
        _executor.Enqueue(0, "");
        _executor.Enqueue(1, "  Failed Demo.PagerTests.Last_is_count_minus_one [3 ms]\nFailed!  - Failed: 1, Passed: 2, Skipped: 0, Total: 3");

        VerificationReport report = await Ladder().VerifyAsync(
            new VerificationRequest(ProjectPath: "src", RunFullSuite: true));

        report.Passed.ShouldBeFalse();
        report.FailedRung.ShouldBe(VerificationRung.UnitTests);
        report.Summary.ShouldContain("Last_is_count_minus_one");
        _executor.Commands.Count(c => c.Arguments[0] == "test").ShouldBe(1, "the full suite must not run after a red unit test");
    }

    /// <summary>
    /// The rung says which assertion failed and by how much (workplan task 69).
    /// <para>
    /// This is the path an inline <c>create_file</c> or <c>edit_file</c> verification reports
    /// through, and it used to give names alone while the <c>run_tests</c> record kept the
    /// runner's own output. Run <c>d5edbc59</c> was editing tests through this rung when it
    /// loosened a tolerance that could not help and then deleted the expected value.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_failing_rung_carries_the_assertion_not_only_the_name()
    {
        _workspace.WriteFile("src/Proj.csproj", TestProject);
        _workspace.WriteFile("src/Pager.cs", "public class Pager { public int X => 1; }");
        _executor.Enqueue(0, "");
        _executor.Enqueue(1, """
              Failed Demo.MultiplyTests.Multiply_Decimals [< 1 ms]
              Error Message:
               Assert.Equal() Failure: Values differ
               Expected: 7.011652
               Actual:   7.006652
              Stack Trace:
                 at Demo.MultiplyTests.Multiply_Decimals()
            Failed!  - Failed: 1, Passed: 6, Skipped: 0, Total: 7
            """);

        VerificationReport report = await Ladder().VerifyAsync(new VerificationRequest(ProjectPath: "src"));

        RungResult tests = report.Results.Single(r => r.Rung == VerificationRung.UnitTests);
        tests.Summary.ShouldContain("7.011652", customMessage: "the literal the test asserted");
        tests.Summary.ShouldContain("7.006652", customMessage: "the product the code computed - the half that makes it repairable");

        // The count line is unchanged, because the sentry keys repeated failures on it.
        tests.Summary.Split('\n')[0].ShouldBe("1 of 7 tests failed: Demo.MultiplyTests.Multiply_Decimals");
    }

    [Fact]
    public async Task A_clean_climb_reaches_the_full_suite()
    {
        _workspace.WriteFile("src/Proj.csproj", TestProject);
        _workspace.WriteFile("src/Pager.cs", "public class Pager { public int X => 1; }");
        _executor.Enqueue(0, "");
        _executor.Enqueue(0, "Passed!  - Failed: 0, Passed: 3, Skipped: 0, Total: 3");
        _executor.Enqueue(0, "Passed!  - Failed: 0, Passed: 40, Skipped: 0, Total: 40");

        VerificationReport report = await Ladder().VerifyAsync(new VerificationRequest(
            FilePath: "src/Pager.cs",
            FileText: "public class Pager { public int X => 1; }",
            ProjectPath: "src",
            RunFullSuite: true));

        report.Passed.ShouldBeTrue();
        report.HighestRungReached.ShouldBe(VerificationRung.FullSuite);
        report.FailedRung.ShouldBeNull();
    }

    [Fact]
    public async Task An_unavailable_sandbox_is_a_skipped_rung_not_a_failed_one()
    {
        // "The build could not run" and "the build failed" are different facts, and conflating
        // them sends the agent hunting for a bug that is not there.
        _executor.Unavailable = "Docker is not reachable.";

        VerificationReport report = await Ladder().VerifyAsync(new VerificationRequest(ProjectPath: "src"));

        report.Passed.ShouldBeTrue();
        report.Results.ShouldContain(r => r.Rung == VerificationRung.Compile && r.Skipped);
    }

    private VerificationLadder Ladder(TestCountMemo? testCounts = null)
    {
        IOptions<VerificationOptions> verification = Options.Create(new VerificationOptions());
        IOptions<SandboxOptions> sandbox = Options.Create(new SandboxOptions());
        IPathGuard guard = _workspace.Guard("src");
        DiagnosticSummarizer summarizer = new(verification);

        return new VerificationLadder(
            new RoslynCodeAnalyzer(guard, verification),
            summarizer,
            new BuildTool(_executor, guard, summarizer, sandbox),
            new RunTestsTool(_executor, guard, sandbox),
            new DisabledCriticPanel(),
            guard,
            Options.Create(new VerificationLadderOptions()),
            testCounts);
    }

    /// <summary>Critique is a Phase 2 capability; the ladder tests are about the compiler rungs.</summary>
    private sealed class DisabledCriticPanel : ICriticPanel
    {
        public bool Enabled => false;

        public bool CanCritique(string? role) => false;

        public string ResolveRole(string? role) => role ?? "critic";

        public Task<CritiqueResult> CritiqueAsync(
            string goal,
            string change,
            string evidence,
            string? role = null,
            string? claim = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new CritiqueResult(false, [], 0, "disabled"));
    }

    /// <summary>A command executor that replays scripted results and records what was asked of it.</summary>
    private sealed class ScriptedCommandExecutor : ICommandExecutor
    {
        private readonly Queue<CommandResult> _scripted = new();

        public List<CommandRequest> Commands { get; } = [];

        public string? Unavailable { get; set; }

        public string Sandbox => "test";

        public Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Unavailable is null);

        public void Enqueue(int exitCode, string output) =>
            _scripted.Enqueue(new CommandResult(exitCode, output, string.Empty, TimeSpan.Zero, false, "test"));

        public Task<CommandResult> ExecuteAsync(CommandRequest request, CancellationToken cancellationToken = default)
        {
            if (Unavailable is not null)
            {
                return Task.FromResult(CommandResult.Unavailable(Unavailable, Sandbox));
            }

            Commands.Add(request);
            return Task.FromResult(_scripted.Count > 0
                ? _scripted.Dequeue()
                : new CommandResult(0, string.Empty, string.Empty, TimeSpan.Zero, false, Sandbox));
        }
    }
}
