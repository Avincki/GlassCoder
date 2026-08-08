using GlassCoder.Lab.Ablation;
using GlassCoder.Lab.TaskSuite;
using GlassCoder.TestSupport;
using GlassCoder.Tools.Retrieval;

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
        // An arm that moves two things at once measures neither - but "one lever" is a
        // difference from the baseline, not a count of keys. Every arm now states the optional
        // capabilities explicitly, because an arm is a layer over whatever is already there and
        // that includes the settings the desktop dialog saves: an operator who switched
        // retrieval on to try it would otherwise have made every arm a retrieval arm.
        foreach (AblationArm arm in StandardArms.Default.Where(a => a.Name != StandardArms.Baseline.Name))
        {
            DifferencesFromBaseline(arm).Count.ShouldBe(1, arm.Name);
            arm.Description.ShouldNotBeNullOrWhiteSpace();
        }
    }

    /// <summary>
    /// The baseline pins every optional capability rather than staying silent about it. A lever
    /// an arm does not name is a lever the arm does not control.
    /// </summary>
    [Fact]
    public void The_baseline_states_every_optional_capability_rather_than_inheriting_it()
    {
        IReadOnlyDictionary<string, string?> baseline = StandardArms.Baseline.Settings;

        foreach (string key in new[]
        {
            "GlassCoder:Critique:Enabled",
            "GlassCoder:Orchestration:Enabled",
            "GlassCoder:Sandbox:EnableBashTool",
            "GlassCoder:Retrieval:Enabled",
            "GlassCoder:Retrieval:Learn:Enabled",
            "GlassCoder:Retrieval:GitHub:Enabled",
        })
        {
            baseline.ContainsKey(key).ShouldBeTrue(key);
            baseline[key].ShouldBe("false", key);
        }

        // Pinned as well as off: an arm that reached the network would stop being comparable
        // with the one beside it.
        baseline["GlassCoder:Retrieval:Mode"].ShouldBe("Replay");
    }

    /// <summary>
    /// Every retrieval lever is named, including ones added after this was written.
    /// <para>
    /// The list above and <c>DifferencesFromBaseline</c> can both only check keys somebody
    /// remembered to write down - an omitted key is invisible to both, and is inherited from
    /// <c>%APPDATA%\GlassCoder\settings.json</c> instead. That is how <c>AllowProactive</c>,
    /// the single lever deciding whether a retrieval call may be admitted at all, came to be
    /// unpinned: a machine where the operator had ticked "Allow unprompted" to try the feature
    /// ran the retrieval arms unrestricted, and one where they had not admitted nothing, with
    /// both reports looking the same.
    /// </para>
    /// <para>
    /// So the question is asked of the options type rather than of a list: a new scalar setting
    /// fails this until somebody decides whether an experiment should pin it.
    /// </para>
    /// </summary>
    [Fact]
    public void The_baseline_pins_every_retrieval_lever_the_options_declare()
    {
        // Not levers: where the corpus lives on this machine, and the per-server sections, whose
        // own Enabled keys the baseline pins individually above.
        string[] notLevers = ["CacheDirectory", "Learn", "GitHub"];

        IReadOnlyDictionary<string, string?> baseline = StandardArms.Baseline.Settings;

        foreach (System.Reflection.PropertyInfo property in typeof(RetrievalOptions).GetProperties())
        {
            if (notLevers.Contains(property.Name, StringComparer.Ordinal))
            {
                continue;
            }

            baseline.ContainsKey($"GlassCoder:Retrieval:{property.Name}").ShouldBeTrue(
                $"GlassCoder:Retrieval:{property.Name} is a retrieval setting no arm pins, so every " +
                "arm inherits it from whatever this machine has saved");
        }
    }

    /// <summary>Each retrieval arm moves its own server and nothing else.</summary>
    [Fact]
    public void Each_retrieval_arm_enables_exactly_the_servers_it_names()
    {
        DifferencesFromBaseline(StandardArms.WithLearn).Keys
            .ShouldBe(["GlassCoder:Retrieval:Enabled", "GlassCoder:Retrieval:Learn:Enabled"], ignoreOrder: true);

        DifferencesFromBaseline(StandardArms.WithCodeSearch).Keys
            .ShouldBe(["GlassCoder:Retrieval:Enabled", "GlassCoder:Retrieval:GitHub:Enabled"], ignoreOrder: true);

        DifferencesFromBaseline(StandardArms.WithRetrieval).Keys.ShouldBe(
            [
                "GlassCoder:Retrieval:Enabled",
                "GlassCoder:Retrieval:Learn:Enabled",
                "GlassCoder:Retrieval:GitHub:Enabled",
            ],
            ignoreOrder: true);
    }

    /// <summary>What an arm actually moves: the keys where it disagrees with the baseline.</summary>
    private static Dictionary<string, string?> DifferencesFromBaseline(AblationArm arm)
    {
        IReadOnlyDictionary<string, string?> baseline = StandardArms.Baseline.Settings;
        Dictionary<string, string?> moved = new(StringComparer.Ordinal);

        foreach ((string key, string? value) in arm.Settings)
        {
            if (!baseline.TryGetValue(key, out string? pinned) ||
                !string.Equals(pinned, value, StringComparison.Ordinal))
            {
                moved[key] = value;
            }
        }

        return moved;
    }

    [Fact]
    public void Arms_are_configuration_only_and_carry_real_configuration_keys()
    {
        foreach (AblationArm arm in StandardArms.All)
        {
            foreach (string key in arm.Settings.Keys)
            {
                key.ShouldStartWith("GlassCoder:", customMessage: arm.Name);
            }
        }
    }

    [Fact]
    public void Arm_names_are_unique_across_the_whole_catalogue()
    {
        StandardArms.All.Select(a => a.Name).Distinct(StringComparer.OrdinalIgnoreCase)
            .Count().ShouldBe(StandardArms.All.Count);
    }

    [Fact]
    public void Each_capability_arm_enables_exactly_one_dormant_capability()
    {
        foreach (AblationArm arm in StandardArms.Capabilities.Where(a =>
            a.Name != StandardArms.Baseline.Name && a.Name != StandardArms.AllCapabilities.Name))
        {
            Dictionary<string, string?> moved = DifferencesFromBaseline(arm);
            moved.Count.ShouldBe(1, arm.Name);
            moved.Values.ShouldAllBe(v => v == "true");
        }
    }

    [Fact]
    public void The_combination_arm_is_exactly_the_union_of_the_isolation_arms()
    {
        // Task 38 asks for isolation and combination. The combined arm must move the same
        // levers the isolation arms move and nothing else, or reading them against each
        // other stops meaning anything.
        Dictionary<string, string?> union = StandardArms.Capabilities
            .Where(a => a.Name != StandardArms.Baseline.Name && a.Name != StandardArms.AllCapabilities.Name)
            .SelectMany(DifferencesFromBaseline)
            .ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);

        Dictionary<string, string?> combined = DifferencesFromBaseline(StandardArms.AllCapabilities);

        combined.Count.ShouldBe(union.Count);
        foreach ((string key, string? value) in union)
        {
            combined.ShouldContainKeyAndValue(key, value);
        }
    }

    [Fact]
    public void Arm_selection_resolves_names_and_sets_and_refuses_the_unknown()
    {
        StandardArms.Resolve(null, out _).ShouldBe(StandardArms.Default);
        StandardArms.Resolve("default", out _).ShouldBe(StandardArms.Default);
        StandardArms.Resolve("capabilities", out _).ShouldBe(StandardArms.Capabilities);

        StandardArms.Resolve("baseline, with-bash", out _)!
            .Select(a => a.Name).ShouldBe(["baseline", "with-bash"]);
        StandardArms.Resolve("with-bash,with-bash", out _)!.Count.ShouldBe(1);

        StandardArms.Resolve("no-such-arm", out string? unknown).ShouldBeNull();
        unknown.ShouldBe("no-such-arm");
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
