using GlassCoder.TestSupport;
using GlassCoder.Tools.Build;
using GlassCoder.Tools.Execution;
using GlassCoder.Tools.Verification;
using Microsoft.Extensions.Options;

namespace GlassCoder.Tools.Tests;

/// <summary>
/// What the two oracles say when they cannot say anything typed (2026-08-06 run analysis).
/// <para>
/// Both runs that day were steered by three messages: "Build failed with 0 error(s)" answered
/// with blind identical retries, and "0 of 0 tests failed" read as green. The contract under
/// test: a failure the parser cannot type carries the raw tail, a transient failure is retried
/// once before being reported, and zero tests is never a quiet outcome.
/// </para>
/// </summary>
public sealed class BuildAndTestFeedbackTests : IDisposable
{
    private readonly TempWorkspace _workspace = new();
    private readonly ScriptedCommandExecutor _executor = new();

    public BuildAndTestFeedbackTests()
    {
        _workspace.CreateDirectory("src");
    }

    public void Dispose() => _workspace.Dispose();

    [Fact]
    public async Task A_failure_with_nothing_parseable_carries_the_raw_output_tail()
    {
        _executor.Enqueue(1, "The file is locked by: GlassCoder.Wpf (1234)");
        _executor.Enqueue(1, "The file is still locked by: GlassCoder.Wpf (1234)");

        ToolObservation<BuildResult> observation = await Build().BuildAsync("src");

        observation.Ok.ShouldBeTrue("a failed build is a handled outcome, not a tool fault");
        observation.Data!.Succeeded.ShouldBeFalse();
        observation.Summary.ShouldContain("no compiler diagnostics could be parsed");
        observation.Summary.ShouldContain("still locked", customMessage: "the reported output must be the retry's, not the first attempt's");
        observation.Data.Diagnostics.ShouldContain("still locked");
    }

    [Fact]
    public async Task A_transient_failure_is_retried_once_and_can_heal()
    {
        _executor.Enqueue(1, "The file is locked by: GlassCoder.Wpf (1234)");
        _executor.Enqueue(0, "");

        ToolObservation<BuildResult> observation = await Build().BuildAsync("src");

        observation.Data!.Succeeded.ShouldBeTrue();
        observation.Summary.ShouldContain("Build succeeded");
        _executor.Commands.Count.ShouldBe(2);
    }

    [Fact]
    public async Task A_failure_the_parser_can_type_is_not_retried()
    {
        _executor.Enqueue(1, @"C:\repo\src\Pager.cs(1,1): error CS0103: broken [C:\repo\src\Proj.csproj]");

        ToolObservation<BuildResult> observation = await Build().BuildAsync("src");

        observation.Data!.Succeeded.ShouldBeFalse();
        observation.Summary.ShouldContain("1 error(s)");
        _executor.Commands.ShouldHaveSingleItem("a typed failure will not change on a retry, so there must not be one");
    }

    [Fact]
    public async Task A_test_run_that_dies_before_any_test_says_so_instead_of_zero_of_zero()
    {
        _executor.Enqueue(1, "error NETSDK1005: Assets file not found. Run a NuGet package restore.");

        ToolObservation<TestRunResult> observation = await Tests().RunTestsAsync("src");

        observation.Data!.Ok.ShouldBeFalse();
        observation.Summary.ShouldNotContain("0 of 0");
        observation.Summary.ShouldContain("before any test executed");
        observation.Summary.ShouldContain("NETSDK1005", customMessage: "the cause has to be in the message the model is already reading");
    }

    [Fact]
    public async Task A_clean_run_of_zero_tests_is_a_warning_not_a_pass()
    {
        _executor.Enqueue(0, "Determining projects to restore...");

        ToolObservation<TestRunResult> observation = await Tests().RunTestsAsync("src");

        observation.Summary.ShouldContain("0 tests");
        observation.Summary.ShouldContain("Nothing was verified");
        observation.Summary.ShouldNotContain("passed.", customMessage: "reassurance is exactly what this message must not offer");
    }

    [Fact]
    public async Task A_failing_run_names_its_tests_and_reports_a_failed_outcome()
    {
        // Runs ea9a1f66 and 216360bf each looped for four and five cycles on "N of M tests
        // failed" - a count the model had to dig past to learn which test kept refusing its
        // fixes, and a success the progress machinery could not count as anything.
        _executor.Enqueue(1,
            "  Failed Demo.Tests.Multiply_ShouldRound [3 ms]\n" +
            "Failed!  - Failed: 1, Passed: 6, Skipped: 0, Total: 7");

        ToolObservation<TestRunResult> observation = await Tests().RunTestsAsync("src");

        observation.Ok.ShouldBeTrue("a red suite is a handled outcome, not a tool fault");
        observation.OutcomeOk.ShouldBeFalse("and a failure to the progress machinery");
        observation.Summary.ShouldContain("1 of 7 tests failed: Demo.Tests.Multiply_ShouldRound");
    }

    private BuildTool Build() => new(
        _executor,
        _workspace.Guard(),
        new DiagnosticSummarizer(Options.Create(new VerificationOptions())),
        Options.Create(new SandboxOptions()));

    private RunTestsTool Tests() => new(_executor, _workspace.Guard(), Options.Create(new SandboxOptions()));
}
