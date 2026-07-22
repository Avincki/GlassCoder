using GlassCoder.Lab.Ablation;
using GlassCoder.Lab.TaskSuite;
using GlassCoder.TestSupport;

namespace GlassCoder.Lab.Tests;

/// <summary>
/// The task suite (workplan task 21) and the ablation arms (task 22).
/// <para>
/// The suite's own correctness matters more than most code here: if a fixture does not actually
/// start red, or its oracle cannot fail, then every pass@1 number computed from it is a
/// fabrication.
/// </para>
/// </summary>
public sealed class TaskSuiteAndAblationTests : IDisposable
{
    private readonly TempWorkspace _workspace = new();

    public void Dispose() => _workspace.Dispose();

    [Fact]
    public void The_suite_has_the_eight_tasks_from_the_specification_in_order()
    {
        TaskSuiteDefinition.All.Count.ShouldBe(8);
        TaskSuiteDefinition.All.Select(t => t.Order).ShouldBe([1, 2, 3, 4, 5, 6, 7, 8]);
        TaskSuiteDefinition.All.Select(t => t.Id).Distinct().Count().ShouldBe(8);
        TaskSuiteDefinition.All.ShouldAllBe(t => !string.IsNullOrWhiteSpace(t.Stresses));
    }

    [Fact]
    public void Every_task_carries_a_buildable_fixture_with_an_oracle()
    {
        foreach (SuiteTask task in TaskSuiteDefinition.All)
        {
            task.Files.ShouldContainKey("Fixture.csproj");
            task.Files.ShouldContainKey("Program.cs");
            task.Files["Program.cs"].ShouldContain("Check.Exit()", customMessage: task.Id);
            task.Goal.ShouldNotBeNullOrWhiteSpace();
        }
    }

    [Fact]
    public void Materialising_a_task_writes_its_whole_fixture()
    {
        SuiteTask task = TaskSuiteDefinition.Find("suite-06-wire-module")!;
        string directory = Path.Combine(_workspace.Root, "fixture");

        TaskSuiteRunner.Materialise(task, directory);

        File.Exists(Path.Combine(directory, "Fixture.csproj")).ShouldBeTrue();
        File.Exists(Path.Combine(directory, "Modules", "Slugger.cs")).ShouldBeTrue("nested paths are created");
        File.ReadAllText(Path.Combine(directory, "Fixture.csproj")).ShouldContain("Compile Remove");
    }

    [Fact]
    public void Materialising_twice_starts_from_a_clean_fixture()
    {
        // Two ablation arms are only comparable if they start from byte-identical repositories.
        SuiteTask task = TaskSuiteDefinition.All[0];
        string directory = Path.Combine(_workspace.Root, "fixture");

        TaskSuiteRunner.Materialise(task, directory);
        File.WriteAllText(Path.Combine(directory, "Contamination.cs"), "// left over from a previous arm");

        TaskSuiteRunner.Materialise(task, directory);

        File.Exists(Path.Combine(directory, "Contamination.cs")).ShouldBeFalse();
    }

    [Fact]
    public async Task An_oracle_that_cannot_run_is_reported_rather_than_scored()
    {
        ScriptedCommandExecutor executor = new() { Unavailable = "Docker is not reachable." };
        TaskSuiteRunner runner = new(executor);

        OracleResult result = await runner.JudgeAsync(TaskSuiteDefinition.All[0], _workspace.Root);

        result.Passed.ShouldBeFalse();
        result.OracleOutput.ShouldContain("could not run");
    }

    [Fact]
    public async Task An_oracle_passes_only_when_the_fixture_says_every_check_held()
    {
        ScriptedCommandExecutor executor = new();
        executor.Enqueue(0, "  pass  one\nALL TESTS PASSED");
        TaskSuiteRunner runner = new(executor);

        (await runner.JudgeAsync(TaskSuiteDefinition.All[0], _workspace.Root)).Passed.ShouldBeTrue();

        // A zero exit code alone is not enough - a fixture that printed nothing is not a pass.
        ScriptedCommandExecutor quiet = new();
        quiet.Enqueue(0, "Build succeeded.");
        (await new TaskSuiteRunner(quiet).JudgeAsync(TaskSuiteDefinition.All[0], _workspace.Root))
            .Passed.ShouldBeFalse();
    }

    [Fact]
    public async Task A_failing_fixture_is_a_failed_oracle()
    {
        ScriptedCommandExecutor executor = new();
        executor.Enqueue(1, "  FAIL  Greeter greets with Hello\n1 TEST(S) FAILED");

        OracleResult result = await new TaskSuiteRunner(executor).JudgeAsync(TaskSuiteDefinition.All[0], _workspace.Root);

        result.Passed.ShouldBeFalse();
        result.OracleOutput.ShouldContain("FAILED");
    }

    [Fact]
    public void Every_standard_arm_changes_exactly_one_lever()
    {
        // An arm that moves two things at once measures neither.
        foreach (AblationArm arm in StandardArms.Default.Where(a => a.Name != StandardArms.Baseline.Name))
        {
            arm.Settings.Count.ShouldBe(1, arm.Name);
            arm.Description.ShouldNotBeNullOrWhiteSpace();
        }

        StandardArms.Baseline.Settings.ShouldBeEmpty();
    }

    [Fact]
    public void Arms_are_configuration_only_and_carry_real_configuration_keys()
    {
        foreach (AblationArm arm in StandardArms.Default)
        {
            foreach (string key in arm.Settings.Keys)
            {
                key.ShouldStartWith("GlassCoder:", customMessage: arm.Name);
            }
        }
    }

    [Fact]
    public void An_ablation_report_computes_pass_at_one_per_arm()
    {
        SuiteTask task = TaskSuiteDefinition.All[0];
        AblationReport report = new(
        [
            new AblationCell(StandardArms.Baseline, task, true, Metrics("baseline")),
            new AblationCell(StandardArms.Baseline, task, false, Metrics("baseline")),
            new AblationCell(StandardArms.NoContext, task, false, Metrics("no-context")),
        ]);

        report.PassRate("baseline").ShouldBe(0.5d);
        report.PassRate("no-context").ShouldBe(0d);
        report.ToText().ShouldContain("baseline");
    }

    private static Core.Metrics.RunMetrics Metrics(string arm) => new()
    {
        RunId = "r",
        TaskId = "t",
        Role = "worker",
        Source = $"ablation:{arm}",
        Arm = arm,
        RecordedAt = DateTimeOffset.UnixEpoch,
        StopReason = "Completed",
        Steps = 3,
        InputTokens = 10,
        OutputTokens = 5,
        TotalTokens = 15,
        WallClockMs = 1000,
        CostUsd = 0.01m,
        ToolCallsTotal = 2,
        ToolCallsValid = 2,
        Edits = 1,
        EditsWithCompileErrors = 0,
        Builds = 1,
        BuildFailures = 0,
        TestRuns = 1,
        TestFailures = 0,
        EditsToGreen = 0,
        RecoveryOpportunities = 0,
        Recoveries = 0,
        DiagnosticsReported = 0,
        DiagnosticsShown = 0,
    };
}
