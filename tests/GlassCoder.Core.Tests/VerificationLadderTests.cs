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
        _workspace.WriteFile("src/Proj.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");
        _workspace.WriteFile("src/Pager.cs", "public class Pager { public int X => 1; }");
        _executor.Enqueue(0, "");                                              // build: green
        _executor.Enqueue(0, "Passed!  - Failed: 0, Passed: 3, Skipped: 0, Total: 3");   // tests: green

        VerificationReport report = await Ladder().VerifyAsync(new VerificationRequest(ProjectPath: "src"));

        report.Passed.ShouldBeTrue();
        report.Results.ShouldContain(r => r.Rung == VerificationRung.Analyzers && r.Passed);
        report.HighestRungReached.ShouldBe(VerificationRung.UnitTests);
    }

    [Fact]
    public async Task A_failing_test_stops_the_climb_before_the_full_suite()
    {
        _workspace.WriteFile("src/Proj.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");
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

    [Fact]
    public async Task A_clean_climb_reaches_the_full_suite()
    {
        _workspace.WriteFile("src/Proj.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");
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

    private VerificationLadder Ladder()
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
            Options.Create(new VerificationLadderOptions()));
    }

    /// <summary>Critique is a Phase 2 capability; the ladder tests are about the compiler rungs.</summary>
    private sealed class DisabledCriticPanel : ICriticPanel
    {
        public bool Enabled => false;

        public Task<CritiqueResult> CritiqueAsync(string goal, string change, string evidence, CancellationToken cancellationToken = default) =>
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
