using GlassCoder.Tools.Build;
using GlassCoder.Tools.Changes;
using GlassCoder.Tools.Processes;
using GlassCoder.Tools.Registry;
using GlassCoder.TestSupport;

namespace GlassCoder.Tools.Tests;

/// <summary>
/// The runtime evidence a completion critique keeps asking for (workplan task 71).
/// <para>
/// Twice a panel has refused finished work for want of proof that the application runs, and twice
/// the model had no way to produce any. The property that matters here is the inversion: for a
/// desktop application, <em>still running</em> at the timeout is the success case, and exiting
/// immediately is the failure. A tool that read those the usual way round would report every
/// working WPF app as a failure and every instant crash as a pass.
/// </para>
/// </summary>
public sealed class LaunchAppTests : IDisposable
{
    private readonly TempWorkspace _workspace = new();

    public LaunchAppTests() => _workspace.WriteFile("src/App/App.csproj", "<Project />");

    public void Dispose() => _workspace.Dispose();

    [Fact]
    public async Task An_app_still_running_at_the_timeout_counts_as_started()
    {
        // A WPF application does not exit on its own. Surviving the window is the evidence.
        FakeProcessRunner runner = new();
        runner.EnqueueTimedOut();

        ToolObservation<LaunchAppResult> observation = await Tool(runner)
            .LaunchAsync("src/App/App.csproj", timeoutSeconds: 3);

        observation.Ok.ShouldBeTrue();
        observation.Data!.Started.ShouldBeTrue();
        observation.Data.StayedUp.ShouldBeTrue();
        observation.Summary.ShouldContain("still running");

        // And it does not claim more than it saw: the window is the operator's to judge.
        observation.Summary.ShouldContain("operator's Run app");
    }

    [Fact]
    public async Task An_app_that_crashes_on_startup_is_reported_as_a_failure()
    {
        FakeProcessRunner runner = new();
        runner.Enqueue(134, standardError: "Unhandled exception. System.NullReferenceException");

        ToolObservation<LaunchAppResult> observation = await Tool(runner)
            .LaunchAsync("src/App/App.csproj");

        observation.Data!.Started.ShouldBeFalse();
        observation.Summary.ShouldContain("exited 134");
        observation.Summary.ShouldContain("NullReferenceException");

        // The observation is ok - this is information, like a red suite - but the outcome is not.
        observation.Ok.ShouldBeTrue();
        observation.OutcomeOk.ShouldBeFalse();
    }

    [Fact]
    public async Task A_console_app_that_exits_cleanly_is_started_too()
    {
        FakeProcessRunner runner = new();
        runner.Enqueue(0);

        ToolObservation<LaunchAppResult> observation = await Tool(runner)
            .LaunchAsync("src/App/App.csproj");

        observation.Data!.Started.ShouldBeTrue();
        observation.Data.StayedUp.ShouldBeFalse();
        observation.Summary.ShouldContain("exited 0");
    }

    [Fact]
    public async Task A_directory_holding_one_runnable_project_is_launched()
    {
        // Occurrence four of a one-step refusal: ae72c5ad step 10, dd11ef7c step 19, 457867c7
        // step 24, and the same shape as MSB1011 in dbaa0580's build. A directory with exactly one
        // executable project in it is not ambiguous.
        _workspace.WriteFile(
            "src/App/App.csproj",
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>WinExe</OutputType></PropertyGroup></Project>");

        FakeProcessRunner runner = new();
        runner.EnqueueTimedOut();

        ToolObservation<LaunchAppResult> observation = await Tool(runner).LaunchAsync("src/App");

        observation.Ok.ShouldBeTrue();
        observation.Data!.Path.ShouldEndWith("App.csproj", Case.Insensitive);
        runner.Requests.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task A_directory_holding_several_runnable_projects_still_refuses_and_names_them()
    {
        _workspace.WriteFile(
            "src/App/One.csproj",
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>WinExe</OutputType></PropertyGroup></Project>");
        _workspace.WriteFile(
            "src/App/Two.csproj",
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>Exe</OutputType></PropertyGroup></Project>");

        ToolObservation<LaunchAppResult> observation = await Tool(new FakeProcessRunner())
            .LaunchAsync("src/App");

        observation.Ok.ShouldBeFalse();
        observation.Error!.Hint.ShouldNotBeNull().ShouldContain("One.csproj");
        observation.Error.Hint.ShouldContain("Two.csproj");
    }

    [Fact]
    public async Task A_directory_with_nothing_runnable_says_that_rather_than_naming_a_tool()
    {
        // The library case. Pointing at list_projects here sends the model to a tool that will
        // list projects none of which can be launched - the answer belongs in this message.
        _workspace.WriteFile("src/App/Lib.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");

        ToolObservation<LaunchAppResult> observation = await Tool(new FakeProcessRunner())
            .LaunchAsync("src/App");

        observation.Ok.ShouldBeFalse();
        observation.Error!.Hint.ShouldNotBeNull().ShouldContain("no project that produces an executable");
    }

    [Fact]
    public async Task The_timeout_is_bounded_however_it_is_asked_for()
    {
        // A hung application must cost one step, never a run.
        FakeProcessRunner runner = new();
        runner.EnqueueTimedOut();

        await Tool(runner).LaunchAsync("src/App/App.csproj", timeoutSeconds: 99_999);

        TimeSpan timeout = runner.Requests.Single().Timeout.ShouldNotBeNull();
        timeout.ShouldBeLessThanOrEqualTo(TimeSpan.FromSeconds(120));
    }

    /// <summary>
    /// The last step that makes this task worth building: the panel that asked for runtime
    /// evidence reads the verification summary, not tool observations, so a launch that stopped at
    /// the transcript would answer the refutation everywhere except where it is made.
    /// </summary>
    [Fact]
    public async Task What_the_launch_showed_is_kept_for_the_completion_critique()
    {
        RuntimeEvidence evidence = new();
        FakeProcessRunner runner = new();
        runner.EnqueueTimedOut();

        evidence.Latest.ShouldBeNull();

        await new LaunchAppTool(runner, _workspace.Guard("src"), evidence)
            .LaunchAsync("src/App/App.csproj", timeoutSeconds: 2);

        evidence.Latest.ShouldNotBeNull().ShouldContain("Runtime: ok");
    }

    [Fact]
    public async Task A_failed_launch_is_kept_as_a_failure()
    {
        RuntimeEvidence evidence = new();
        FakeProcessRunner runner = new();
        runner.Enqueue(1, standardError: "boom");

        await new LaunchAppTool(runner, _workspace.Guard("src"), evidence)
            .LaunchAsync("src/App/App.csproj");

        evidence.Latest.ShouldNotBeNull().ShouldContain("Runtime: FAILED");
    }

    /// <summary>
    /// The saving this is for: a desktop application draws its window in a second or two, and the
    /// tool used to sit out the whole ten regardless. Ten seconds a launch, every launch.
    /// </summary>
    [Fact]
    public async Task A_window_is_better_evidence_than_surviving_the_clock_and_arrives_sooner()
    {
        _workspace.WriteFile("src/App/bin/Debug/net10.0/App.exe", "not really an executable");

        FakeProcessRunner runner = new();
        runner.EnqueueReady(TimeSpan.FromSeconds(1.4));

        ToolObservation<LaunchAppResult> observation = await Tool(runner, new StubWindows(true))
            .LaunchAsync("src/App/App.csproj", timeoutSeconds: 10);

        observation.Data!.ShowedWindow.ShouldBeTrue();
        observation.Data.Started.ShouldBeTrue();
        observation.Data.StayedUp.ShouldBeTrue();
        observation.OutcomeOk.ShouldBeTrue();
        observation.Summary.ShouldContain("drew a window");

        // And it still refuses to claim the window is correct - only that there is one.
        observation.Summary.ShouldContain("needs eyes on it");
    }

    [Fact]
    public async Task The_built_executable_is_launched_directly_so_the_window_has_an_owner()
    {
        // Under `dotnet run` the window belongs to a grandchild and MainWindowHandle stays zero,
        // so the polling would report "no window" for an application that drew one immediately.
        _workspace.WriteFile("src/App/bin/Debug/net10.0/App.exe", "not really an executable");

        FakeProcessRunner runner = new();
        runner.EnqueueReady();

        await Tool(runner, new StubWindows(true)).LaunchAsync("src/App/App.csproj");

        ProcessRunRequest request = runner.Requests.Single();
        request.FileName.ShouldEndWith("App.exe");
        request.Arguments.ShouldBeEmpty();
        request.ReadyWhen.ShouldNotBeNull();
    }

    [Fact]
    public async Task An_app_that_stays_up_without_ever_drawing_anything_says_exactly_that()
    {
        _workspace.WriteFile("src/App/bin/Debug/net10.0/App.exe", "not really an executable");

        FakeProcessRunner runner = new();
        runner.EnqueueTimedOut();

        ToolObservation<LaunchAppResult> observation = await Tool(runner, new StubWindows(false))
            .LaunchAsync("src/App/App.csproj", timeoutSeconds: 4);

        observation.Data!.ShowedWindow.ShouldBeFalse();
        observation.Data.StayedUp.ShouldBeTrue();
        observation.Summary.ShouldContain("never drew a window");
    }

    /// <summary>
    /// Watched-and-saw-nothing and never-looked are different facts, and only one of them is
    /// evidence against the change. With no executable to launch there is nothing to watch, so
    /// the tool falls back to what task 71 shipped and says no more than it did.
    /// </summary>
    [Fact]
    public async Task With_no_executable_to_find_it_neither_watches_nor_claims_to_have()
    {
        FakeProcessRunner runner = new();
        runner.EnqueueTimedOut();

        ToolObservation<LaunchAppResult> observation = await Tool(runner, new StubWindows(true))
            .LaunchAsync("src/App/App.csproj", timeoutSeconds: 4);

        runner.Requests.Single().ReadyWhen.ShouldBeNull();
        observation.Summary.ShouldNotContain("never drew a window");
        observation.Summary.ShouldContain("still running");
    }

    // ── The probe (the rung above "a window drew") ──

    [Fact]
    public async Task What_the_probe_reads_out_of_the_window_reaches_the_summary()
    {
        // The whole point of the rung: run ae72c5ad shipped a converter whose arithmetic nothing
        // had ever exercised, and three critics refuted it for exactly that. A readback of the
        // other box after typing into the first is the evidence they were asking for.
        _workspace.WriteFile("src/App/bin/Debug/net10.0/App.exe", "not really an executable");

        FakeProcessRunner runner = new();
        runner.EnqueueReady(TimeSpan.FromSeconds(0.7));
        StubProbe probe = new(readsBack: "212");

        ToolObservation<LaunchAppResult> observation = await Tool(runner, new StubWindows(true), probe)
            .LaunchAsync("src/App/App.csproj", timeoutSeconds: 10, probe: "Celsius=100; Convert!; Fahrenheit?");

        probe.Steps.Count.ShouldBe(3);
        probe.ProcessId.ShouldBe(runner.ReadyProcessId, "the probe must look at the process that drew the window");
        observation.Summary.ShouldContain("Fahrenheit? → \"212\"");

        // And the hedge stands down: it is true of a launch that only watched, and an understatement
        // over a readback of the very field the refutation was about.
        observation.Summary.ShouldNotContain("needs eyes on it");
    }

    [Fact]
    public async Task A_probe_that_reads_nothing_leaves_the_hedge_where_it_was()
    {
        _workspace.WriteFile("src/App/bin/Debug/net10.0/App.exe", "not really an executable");

        FakeProcessRunner runner = new();
        runner.EnqueueReady();

        ToolObservation<LaunchAppResult> observation = await Tool(runner, new StubWindows(true), new StubProbe(null))
            .LaunchAsync("src/App/App.csproj", probe: "Missing?");

        observation.Summary.ShouldContain("needs eyes on it");
        observation.Summary.ShouldContain("no element by that name");
    }

    [Fact]
    public async Task A_host_with_no_probe_says_so_rather_than_implying_there_was_nothing_to_see()
    {
        // Watched-and-saw-nothing against never-looked, one level up: the console host has no UI
        // Automation to offer, and a silent launch would read as a window with nothing in it.
        _workspace.WriteFile("src/App/bin/Debug/net10.0/App.exe", "not really an executable");

        FakeProcessRunner runner = new();
        runner.EnqueueReady();

        ToolObservation<LaunchAppResult> observation = await Tool(runner, new StubWindows(true))
            .LaunchAsync("src/App/App.csproj", probe: "Fahrenheit?");

        observation.Summary.ShouldContain("No UI probe is available");
        observation.Summary.ShouldContain("needs eyes on it");
    }

    [Fact]
    public async Task A_launch_that_asked_for_nothing_reads_the_window_anyway()
    {
        // Run dd11ef7c: the probe existed, the goal was two values agreeing, and launch_app was
        // called with two arguments - so the harness said a window drew while the window said 0
        // beside 0. A capability the model must elect is advice; this is the mechanism.
        _workspace.WriteFile("src/App/bin/Debug/net10.0/App.exe", "not really an executable");

        FakeProcessRunner runner = new();
        runner.EnqueueReady();
        StubProbe probe = new("0");

        ToolObservation<LaunchAppResult> observation = await Tool(runner, new StubWindows(true), probe)
            .LaunchAsync("src/App/App.csproj");

        probe.Steps.ShouldBeEmpty("nothing was typed and nothing was clicked");
        probe.SweptProcessId.ShouldBe(runner.ReadyProcessId);
        observation.Summary.ShouldContain("Window: CelsiusTextBox? → \"0\"");
    }

    [Fact]
    public async Task Asking_a_question_does_not_switch_the_sweep_off()
    {
        // Run 457867c7 typed into one box at steps 35-37 and never looked at the rest of a window
        // it had rewritten twice, because asking suppressed the sweep. The launches where the
        // window had changed most were the launches that read least of it.
        _workspace.WriteFile("src/App/bin/Debug/net10.0/App.exe", "not really an executable");

        FakeProcessRunner runner = new();
        runner.EnqueueReady();
        StubProbe probe = new("212");

        ToolObservation<LaunchAppResult> observation = await Tool(runner, new StubWindows(true), probe)
            .LaunchAsync("src/App/App.csproj", probe: "Celsius=100; Fahrenheit?");

        probe.Steps.Count.ShouldBe(2, "the asked-for script still runs");
        probe.SweptProcessId.ShouldBe(runner.ReadyProcessId, "and the window is read as well");

        observation.Summary.ShouldContain("Probe: Celsius=100");
        observation.Summary.ShouldContain("Window: CelsiusTextBox?");
        observation.Summary.ShouldContain("the whole window after that input");
    }

    [Fact]
    public async Task A_step_that_typed_does_not_report_as_a_reading()
    {
        // Step 35 of run 457867c7 read "CelsiusTextBox=0 ok; FahrenheitTextBox=32 ok" - two writes,
        // no evidence, in the same shape as a readback that proved something.
        _workspace.WriteFile("src/App/bin/Debug/net10.0/App.exe", "not really an executable");

        FakeProcessRunner runner = new();
        runner.EnqueueReady();

        ToolObservation<LaunchAppResult> observation = await Tool(runner, new StubWindows(true), new StubProbe("212"))
            .LaunchAsync("src/App/App.csproj", probe: "Celsius=100");

        observation.Summary.ShouldContain("typed, not read back");
        observation.Summary.ShouldNotContain("Celsius=100 ok");
    }

    [Fact]
    public async Task A_window_read_at_rest_does_not_claim_the_window_is_right()
    {
        // The overclaim this repository keeps paying for, one step ahead of it. A sweep says what
        // the boxes hold before anything was typed; that is the defect on dd11ef7c, not evidence
        // against it. The hedge narrows to what is actually missing, which is answerable in one
        // step with a probe.
        _workspace.WriteFile("src/App/bin/Debug/net10.0/App.exe", "not really an executable");

        FakeProcessRunner runner = new();
        runner.EnqueueReady();

        ToolObservation<LaunchAppResult> swept = await Tool(runner, new StubWindows(true), new StubProbe("0"))
            .LaunchAsync("src/App/App.csproj");

        swept.Summary.ShouldContain("nothing was typed into it");
        swept.Summary.ShouldNotContain("needs eyes on it");

        // And an asked-for probe, which did type, drops the hedge entirely.
        FakeProcessRunner second = new();
        second.EnqueueReady();

        ToolObservation<LaunchAppResult> driven = await Tool(second, new StubWindows(true), new StubProbe("212"))
            .LaunchAsync("src/App/App.csproj", probe: "Celsius=100; Fahrenheit?");

        driven.Summary.ShouldNotContain("nothing was typed into it");
        driven.Summary.ShouldNotContain("needs eyes on it");
    }

    [Fact]
    public async Task A_host_with_no_probe_still_launches_and_still_says_it_read_nothing()
    {
        _workspace.WriteFile("src/App/bin/Debug/net10.0/App.exe", "not really an executable");

        FakeProcessRunner runner = new();
        runner.EnqueueReady();

        ToolObservation<LaunchAppResult> observation = await Tool(runner, new StubWindows(true))
            .LaunchAsync("src/App/App.csproj");

        runner.Requests.Single().OnReady.ShouldBeNull("there is nothing to attach");
        observation.Data!.Started.ShouldBeTrue();
        observation.Summary.ShouldContain("No UI probe is available");
        observation.Summary.ShouldContain("needs eyes on it");
    }

    [Fact]
    public async Task A_probe_that_throws_does_not_turn_a_launch_that_worked_into_a_failure()
    {
        // The tool is holding a live application it is about to kill. A probe that falls over is a
        // piece of missing evidence, never a failed launch - and the process still gets stopped.
        _workspace.WriteFile("src/App/bin/Debug/net10.0/App.exe", "not really an executable");

        FakeProcessRunner runner = new();
        runner.EnqueueReady();

        ToolObservation<LaunchAppResult> observation = await Tool(runner, new StubWindows(true), new ThrowingProbe())
            .LaunchAsync("src/App/App.csproj", probe: "Fahrenheit?");

        observation.Ok.ShouldBeTrue();
        observation.Data!.Started.ShouldBeTrue();
        observation.Summary.ShouldContain("drew a window");
    }

    [Fact]
    public async Task The_probe_is_kept_for_the_completion_critique_with_everything_else()
    {
        _workspace.WriteFile("src/App/bin/Debug/net10.0/App.exe", "not really an executable");

        RuntimeEvidence evidence = new();
        FakeProcessRunner runner = new();
        runner.EnqueueReady();

        await new LaunchAppTool(runner, _workspace.Guard("src"), evidence, new StubWindows(true), new StubProbe("212"))
            .LaunchAsync("src/App/App.csproj", probe: "Fahrenheit?");

        evidence.Latest.ShouldNotBeNull().ShouldContain("→ \"212\"");
    }

    [Fact]
    public async Task Every_launch_is_kept_for_the_panel_and_a_repeat_is_kept_once()
    {
        // Run 457867c7 demonstrated three input/output pairs at steps 35-37 and the panel that
        // judged it was handed the last one, because this was a slot rather than a list.
        _workspace.WriteFile("src/App/bin/Debug/net10.0/App.exe", "not really an executable");

        RuntimeEvidence evidence = new();
        ChangeLog changes = new();
        FakeProcessRunner runner = new();
        runner.EnqueueReady().EnqueueReady().EnqueueReady();

        LaunchAppTool tool = new(
            runner, _workspace.Guard("src"), evidence, new StubWindows(true), new StubProbe("212"), changes);

        await tool.LaunchAsync("src/App/App.csproj", probe: "Celsius=100; Fahrenheit?");
        await tool.LaunchAsync("src/App/App.csproj", probe: "Fahrenheit=212; Celsius?");

        // The same launch again, over an unchanged tree: served from the memo, and it must not
        // arrive in the evidence a second time.
        await tool.LaunchAsync("src/App/App.csproj", probe: "Fahrenheit=212; Celsius?");

        string kept = evidence.Latest.ShouldNotBeNull();
        kept.ShouldContain("Celsius=100");
        kept.ShouldContain("Fahrenheit=212");
        kept.Split(Environment.NewLine).Length.ShouldBe(2, "two launches, and the repeat is not a third");
    }

    // ── A launch that cannot show anything new ──

    [Fact]
    public async Task The_same_launch_over_an_unchanged_tree_is_answered_rather_than_re_run()
    {
        // Run ae72c5ad, step 15: refuted at step 14, the model re-issued step 12's launch and got
        // a byte-identical string back. The launch was not wrong, it was spent - and nothing said so.
        ChangeLog changes = new();
        RuntimeEvidence evidence = new();
        FakeProcessRunner runner = new();
        runner.EnqueueTimedOut();

        LaunchAppTool tool = new(runner, _workspace.Guard("src"), evidence, null, null, changes);

        await tool.LaunchAsync("src/App/App.csproj", timeoutSeconds: 2);
        ToolObservation<LaunchAppResult> second = await tool.LaunchAsync("src/App/App.csproj", timeoutSeconds: 2);

        runner.Requests.Count.ShouldBe(1, "the second launch had nothing new to find out");
        second.Reused.ShouldBeTrue("the sentry counts this flag, and it is how a repeat becomes visible");
        second.Summary.ShouldContain("cannot show anything new");
        second.Data!.Started.ShouldBeTrue("a reused answer is still the answer");
    }

    [Fact]
    public async Task A_launch_after_an_applied_change_runs_for_real()
    {
        ChangeLog changes = new();
        FakeProcessRunner runner = new();
        runner.EnqueueTimedOut().EnqueueTimedOut();

        LaunchAppTool tool = new(runner, _workspace.Guard("src"), new RuntimeEvidence(), null, null, changes);

        await tool.LaunchAsync("src/App/App.csproj", timeoutSeconds: 2);

        CodeChange change = changes.Propose("src/App/MainWindow.xaml", "edit_file", "before", "after");
        changes.Update(change.Id, ChangeStatus.Applied);

        ToolObservation<LaunchAppResult> second = await tool.LaunchAsync("src/App/App.csproj", timeoutSeconds: 2);

        runner.Requests.Count.ShouldBe(2);
        second.Reused.ShouldBeFalse();
    }

    [Fact]
    public async Task Asking_the_window_a_different_question_is_a_different_launch()
    {
        // The tree is unchanged, but a probe that reads a different field reads a different fact -
        // and reusing the previous answer would report on a control nobody asked about.
        _workspace.WriteFile("src/App/bin/Debug/net10.0/App.exe", "not really an executable");

        FakeProcessRunner runner = new();
        runner.EnqueueReady().EnqueueReady();

        LaunchAppTool tool = new(
            runner, _workspace.Guard("src"), new RuntimeEvidence(), new StubWindows(true), new StubProbe("212"), new ChangeLog());

        await tool.LaunchAsync("src/App/App.csproj", probe: "Celsius=100; Fahrenheit?");
        ToolObservation<LaunchAppResult> second = await tool.LaunchAsync("src/App/App.csproj", probe: "Celsius=0; Fahrenheit?");

        runner.Requests.Count.ShouldBe(2);
        second.Reused.ShouldBeFalse();
    }

    /// <summary>
    /// A window nobody typed into is a fact the completion panel needs and could not previously
    /// see (run 31983adb, where a bare sweep was accepted 3/3 as proof the button worked).
    /// </summary>
    [Fact]
    public async Task A_launch_that_only_swept_leaves_the_window_untouched()
    {
        _workspace.WriteFile("src/App/bin/Debug/net10.0/App.exe", "not really an executable");

        FakeProcessRunner runner = new();
        runner.EnqueueReady();
        RuntimeEvidence evidence = new();

        LaunchAppTool tool = new(
            runner, _workspace.Guard("src"), evidence, new StubWindows(true), new StubProbe("212"), new ChangeLog());

        await tool.LaunchAsync("src/App/App.csproj");

        evidence.WindowWentUntouched.ShouldBeTrue();
    }

    [Fact]
    public async Task A_probe_that_only_reads_leaves_the_window_untouched_too()
    {
        // The distinction the panel is about: a read left the window exactly as it found it, which
        // is the same evidence about the product as never having asked.
        _workspace.WriteFile("src/App/bin/Debug/net10.0/App.exe", "not really an executable");

        FakeProcessRunner runner = new();
        runner.EnqueueReady();
        RuntimeEvidence evidence = new();

        LaunchAppTool tool = new(
            runner, _workspace.Guard("src"), evidence, new StubWindows(true), new StubProbe("212"), new ChangeLog());

        await tool.LaunchAsync("src/App/App.csproj", probe: "Fahrenheit?");

        evidence.WindowWentUntouched.ShouldBeTrue();
    }

    [Theory]
    [InlineData("Celsius=100")]
    [InlineData("Multiply!")]
    public async Task A_probe_that_typed_or_pressed_counts_as_touching_it(string probe)
    {
        _workspace.WriteFile("src/App/bin/Debug/net10.0/App.exe", "not really an executable");

        FakeProcessRunner runner = new();
        runner.EnqueueReady();
        RuntimeEvidence evidence = new();

        LaunchAppTool tool = new(
            runner, _workspace.Guard("src"), evidence, new StubWindows(true), new StubProbe("212"), new ChangeLog());

        await tool.LaunchAsync("src/App/App.csproj", probe: probe);

        evidence.WindowWentUntouched.ShouldBeFalse();
    }

    private LaunchAppTool Tool(IProcessRunner runner, IWindowPresence? windows = null, IUiProbe? probe = null) =>
        new(runner, _workspace.Guard("src"), new RuntimeEvidence(), windows, probe);

    private sealed class StubWindows(bool answer) : IWindowPresence
    {
        public bool HasVisibleWindow(int processId) => answer;
    }

    /// <summary>Answers every read with the same text, and remembers what it was asked to do.</summary>
    private sealed class StubProbe(string? readsBack) : IUiProbe
    {
        public List<UiProbeStep> Steps { get; } = [];

        public int ProcessId { get; private set; }

        /// <summary>The process an unasked-for sweep was pointed at, or zero if none happened.</summary>
        public int SweptProcessId { get; private set; }

        public Task<IReadOnlyList<UiProbeReading>> ReadAllAsync(
            int processId, CancellationToken cancellationToken = default)
        {
            SweptProcessId = processId;

            return Task.FromResult<IReadOnlyList<UiProbeReading>>(
                readsBack is null
                    ? []
                    : [new UiProbeReading("CelsiusTextBox?", Ok: true, Saw: readsBack, Problem: null)]);
        }

        public Task<IReadOnlyList<UiProbeReading>> RunAsync(
            int processId, IReadOnlyList<UiProbeStep> steps, CancellationToken cancellationToken = default)
        {
            ProcessId = processId;
            Steps.AddRange(steps);

            // The step is echoed in the notation the real probe uses - "Celsius=100", "Convert!",
            // "Fahrenheit?" - because that string is what the summary shows and what a critic reads.
            return Task.FromResult<IReadOnlyList<UiProbeReading>>(
            [
                .. steps.Select(step => step.Action switch
                {
                    UiProbeAction.Read when readsBack is null =>
                        new UiProbeReading($"{step.Element}?", Ok: false, Saw: null, Problem: "no element by that name"),
                    UiProbeAction.Read =>
                        new UiProbeReading($"{step.Element}?", Ok: true, Saw: readsBack, Problem: null),
                    UiProbeAction.Invoke =>
                        new UiProbeReading($"{step.Element}!", Ok: true, Saw: null, Problem: null),
                    _ => new UiProbeReading($"{step.Element}={step.Value}", Ok: true, Saw: null, Problem: null),
                }),
            ]);
        }
    }

    private sealed class ThrowingProbe : IUiProbe
    {
        public Task<IReadOnlyList<UiProbeReading>> RunAsync(
            int processId, IReadOnlyList<UiProbeStep> steps, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("the automation client fell over");

        public Task<IReadOnlyList<UiProbeReading>> ReadAllAsync(
            int processId, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("the automation client fell over");
    }
}
