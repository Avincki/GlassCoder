using GlassCoder.Core.Agent;
using GlassCoder.Core.Verification;
using GlassCoder.TestSupport;
using GlassCoder.Tools.Build;
using GlassCoder.Tools.Changes;
using GlassCoder.Tools.Execution;
using GlassCoder.Tools.Guardrails;
using GlassCoder.Tools.Registry;
using GlassCoder.Tools.Verification;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace GlassCoder.Core.Tests;

/// <summary>
/// What the ladder builds after a change (workplan task 44).
/// <para>
/// It used to build the workspace root, always, because nothing filled the request's project
/// path and its default was ".". In a repository whose projects live under <c>src/</c> with no
/// root solution that is <c>MSB1003: Specify a project or solution file</c> - a structural fact
/// about the repository, reported to the agent as a failing compile, three hundred milliseconds
/// after every edit.
/// </para>
/// </summary>
public sealed class VerificationTargetTests : IDisposable
{
    private readonly TempWorkspace _workspace = new();
    private readonly ScriptedCommandExecutor _executor = new();

    [Fact]
    public async Task The_project_that_owns_the_change_is_what_gets_built()
    {
        _workspace.WriteFile("src/App/App.csproj", Project);
        _workspace.WriteFile("src/App/Program.cs", "public class P { }");
        _workspace.WriteFile("src/Lib/Lib.csproj", Project);

        await Ladder().VerifyAsync(new VerificationRequest(ChangeDescription: "edited Program.cs")
        {
            ChangedPaths = ["src/App/Program.cs"],
        });

        CommandRequest build = _executor.Commands.First(c => c.Arguments.Contains("build"));
        build.Arguments.ShouldContain("App.csproj");
        build.Arguments.ShouldNotContain(".");
    }

    [Fact]
    public async Task A_library_change_builds_the_project_that_depends_on_it()
    {
        // Run e8f9186a: the library gained a parameter, the ladder built the library alone and
        // reported green, and the test project's broken call sites stayed invisible for the
        // rest of the run. Building the dependent builds the library too.
        _workspace.WriteFile("src/Lib/Lib.csproj", Project);
        _workspace.WriteFile("src/Lib/Thing.cs", "public class T { }");
        _workspace.WriteFile(
            "src/App/App.csproj",
            "<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup><ProjectReference Include=\"..\\Lib\\Lib.csproj\" /></ItemGroup></Project>");
        _workspace.WriteFile("src/App/Program.cs", "public class P { }");

        await Ladder().VerifyAsync(new VerificationRequest(ChangeDescription: "edited Thing.cs")
        {
            ChangedPaths = ["src/Lib/Thing.cs"],
        });

        CommandRequest build = _executor.Commands.First(c => c.Arguments.Contains("build"));
        build.Arguments.ShouldContain("App.csproj");
        build.Arguments.ShouldNotContain("Lib.csproj");
    }

    [Fact]
    public async Task A_root_solution_covers_a_change_that_spans_projects()
    {
        _workspace.WriteFile("Everything.sln", string.Empty);
        _workspace.WriteFile("src/App/App.csproj", Project);
        _workspace.WriteFile("src/Lib/Lib.csproj", Project);

        await Ladder().VerifyAsync(new VerificationRequest(ChangeDescription: "edited both")
        {
            ChangedPaths = ["src/App/Program.cs", "src/Lib/Thing.cs"],
        });

        _executor.Commands.First(c => c.Arguments.Contains("build")).Arguments.ShouldContain("Everything.sln");
    }

    [Fact]
    public async Task An_unbuildable_tree_skips_the_rung_instead_of_failing_it()
    {
        // Nothing to build is a fact about the repository. Reporting it as a failed compile is
        // what sent the agent hunting for a bug that was not in its code.
        _workspace.WriteFile("notes.md", "# no projects here");

        VerificationReport report = await Ladder().VerifyAsync(
            new VerificationRequest(ChangeDescription: "edited notes.md") { ChangedPaths = ["notes.md"] });

        report.Passed.ShouldBeTrue();
        report.Results.ShouldContain(r => r.Rung == VerificationRung.Compile && r.Skipped);
        _executor.Commands.ShouldBeEmpty("nothing should have been launched");
    }

    [Fact]
    public async Task A_configured_target_overrides_what_the_change_suggests()
    {
        _workspace.WriteFile("src/App/App.csproj", Project);
        _workspace.WriteFile("build/All.sln", string.Empty);

        VerificationLadderOptions options = new() { ProjectPath = "build/All.sln" };

        await Ladder(options).VerifyAsync(new VerificationRequest(ChangeDescription: "edited")
        {
            ChangedPaths = ["src/App/Program.cs"],
        });

        _executor.Commands.First(c => c.Arguments.Contains("build")).Arguments.ShouldContain("All.sln");
    }

    private VerificationLadder Ladder(VerificationLadderOptions? options = null)
    {
        IOptions<VerificationOptions> verification = Options.Create(new VerificationOptions());
        IOptions<SandboxOptions> sandbox = Options.Create(new SandboxOptions());
        IPathGuard guard = _workspace.Guard("src", "build");
        DiagnosticSummarizer summarizer = new(verification);

        return new VerificationLadder(
            new RoslynCodeAnalyzer(guard, verification),
            summarizer,
            new BuildTool(_executor, guard, summarizer, sandbox),
            new RunTestsTool(_executor, guard, sandbox),
            new DisabledCritics(),
            guard,
            Options.Create(options ?? new VerificationLadderOptions { RunAnalyzers = false }));
    }

    public void Dispose() => _workspace.Dispose();

    private const string Project = """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
        </Project>
        """;

    /// <summary>Critique is a Phase 2 capability; these tests are about the compiler rungs.</summary>
    private sealed class DisabledCritics : ICriticPanel
    {
        public bool Enabled => false;

        public bool CanCritique(string? role) => false;

        public string ResolveRole(string? role) => role ?? "critic";

        public Task<CritiqueResult> CritiqueAsync(
            string goal, string change, string evidence, string? role = null, string? claim = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new CritiqueResult(false, [], 0, "disabled"));
    }
}

/// <summary>
/// The step budget, made visible to the agent that is spending it (workplan task 44).
/// <para>
/// A run that hit its ceiling spent its last five steps rebuilding an unchanged tree. It could
/// not have paced itself: nothing in the loop ever told it how many steps it had.
/// </para>
/// </summary>
public sealed class StepBudgetVisibilityTests
{
    [Fact]
    public async Task A_run_nearing_its_ceiling_is_told_once()
    {
        RecordingStepLogger transcript = new();

        // Never stops calling tools, so the run can only end at the step limit - the shape of
        // the run this came from. The stall limit is off here on purpose: an identical call
        // repeated eight times is exactly what it exists to cut short, and this test needs the
        // run to actually reach its ceiling to see the warning fire.
        AgentLoop loop = new(
            new FakeChatClientFactory(new FakeChatClient(FakeChatClient.ToolCall("noop"))),
            new ToolRegistry([new NoopTools()]),
            transcript,
            TestContextAssembler.Create(),
            new RecordingMetricsRecorder(),
            Options.Create(new AgentOptions { MaxSteps = 8, MaxStalledSteps = 0 }));

        AgentRunResult result = await loop.RunAsync(new AgentRunRequest { TaskId = "t", Goal = "keep going" });

        result.StopReason.ShouldBe(AgentStopReason.StepLimit);

        List<string> warnings =
        [
            .. transcript.Steps
                .SelectMany(s => s.Prompt)
                .Select(m => m.Text ?? string.Empty)
                .Where(t => t.Contains("steps remain", StringComparison.Ordinal))
                .Distinct()
        ];

        warnings.Count.ShouldBe(1, "the warning is sent once - repeating it would spend the budget it warns about");
        warnings[0].ShouldContain("of 8 steps remain");
        warnings[0].ShouldContain("Do not re-run a build or test whose result you already have");
    }

    [Fact]
    public async Task A_run_that_finishes_early_is_never_warned()
    {
        RecordingStepLogger transcript = new();

        AgentLoop loop = new(
            new FakeChatClientFactory(new FakeChatClient(FakeChatClient.Text("done"))),
            new ToolRegistry([new NoopTools()]),
            transcript,
            TestContextAssembler.Create(),
            new RecordingMetricsRecorder(),
            Options.Create(new AgentOptions { MaxSteps = 30 }));

        await loop.RunAsync(new AgentRunRequest { TaskId = "t", Goal = "finish" });

        transcript.Steps
            .SelectMany(s => s.Prompt)
            .ShouldNotContain(m => (m.Text ?? string.Empty).Contains("steps remain", StringComparison.Ordinal));
    }

    /// <summary>
    /// A limit that fires three steps from done used to mean restarting from zero. The gate
    /// asks the operator: approval adds one configured allotment, and the question returns
    /// when the extended ceiling trips - so a run somebody answers for can be walked to the
    /// finish one allotment at a time, and a run nobody answers for stops exactly as before.
    /// </summary>
    [Fact]
    public async Task A_granted_extension_buys_one_allotment_and_the_next_refusal_stops()
    {
        RecordingStepLogger transcript = new();
        CountingGate gate = new(approvals: 1);

        AgentRunResult result = await new AgentLoop(
            new FakeChatClientFactory(new FakeChatClient(FakeChatClient.ToolCall("noop"))),
            new ToolRegistry([new NoopTools()]),
            transcript,
            TestContextAssembler.Create(),
            new RecordingMetricsRecorder(),
            Options.Create(new AgentOptions { MaxSteps = 2 }),
            limitGate: gate)
            .RunAsync(new AgentRunRequest { TaskId = "t", Goal = "keep going" });

        result.StopReason.ShouldBe(AgentStopReason.StepLimit);
        result.Steps.ShouldBe(4, "one approval adds one more allotment of the configured limit");
        gate.Asked.ShouldBe(2);
        gate.LastLimit!.Allotment.ShouldBe(2);
        gate.LastLimit.Ceiling.ShouldBe(4, "the second question is about the extended ceiling");
    }

    [Fact]
    public async Task A_run_without_a_gate_stops_at_the_limit_as_before()
    {
        RecordingStepLogger transcript = new();

        AgentRunResult result = await new AgentLoop(
            new FakeChatClientFactory(new FakeChatClient(FakeChatClient.ToolCall("noop"))),
            new ToolRegistry([new NoopTools()]),
            transcript,
            TestContextAssembler.Create(),
            new RecordingMetricsRecorder(),
            Options.Create(new AgentOptions { MaxSteps = 2 }))
            .RunAsync(new AgentRunRequest { TaskId = "t", Goal = "keep going" });

        result.StopReason.ShouldBe(AgentStopReason.StepLimit);
        result.Steps.ShouldBe(2);
    }

    private sealed class CountingGate(int approvals) : ILimitExtensionGate
    {
        private int _granted;

        public int Asked { get; private set; }

        public RunLimitReached? LastLimit { get; private set; }

        public Task<bool> RequestExtensionAsync(RunLimitReached limit, CancellationToken cancellationToken)
        {
            Asked++;
            LastLimit = limit;
            return Task.FromResult(_granted++ < approvals);
        }
    }

    private sealed class NoopTools : IToolSet
    {
        [GlassCoderTool("noop")]
        [System.ComponentModel.Description("Does nothing, for tests.")]
        public GlassCoder.Tools.ToolObservation<string> Noop() =>
            GlassCoder.Tools.Observation.Ok("noop", "ok");
    }
}

/// <summary>
/// Repeating a call that cannot succeed (workplan task 45).
/// <para>
/// The budgets counted steps, tokens, time, cost and calls that failed to <em>bind</em>. Nothing
/// counted calls that bound perfectly and could never be satisfied - so a run spent seventeen
/// consecutive steps on one <c>edit_file</c> while tool-call validity read 100%.
/// </para>
/// </summary>
public sealed class RepeatedFailureTests
{
    [Fact]
    public async Task The_run_stops_once_the_same_call_has_failed_enough_times()
    {
        RecordingStepLogger transcript = new();
        AgentRunResult result = await Loop(transcript, maxIdentical: 5)
            .RunAsync(new AgentRunRequest { TaskId = "t", Goal = "keep trying" });

        result.StopReason.ShouldBe(AgentStopReason.RepeatedToolFailure);
        result.Steps.ShouldBe(5, "it stops at the limit rather than grinding on to the step ceiling");

        // The distinction that matters: these calls were valid. The old failure limit counts
        // calls the registry could not bind, and would never have tripped here.
        result.ToolCallValidityRate.ShouldBe(1.0);
    }

    [Fact]
    public async Task The_model_is_told_it_is_repeating_itself_before_the_run_is_stopped()
    {
        RecordingStepLogger transcript = new();

        await Loop(transcript, maxIdentical: 8).RunAsync(new AgentRunRequest { TaskId = "t", Goal = "keep trying" });

        List<string> nudges =
        [
            .. transcript.Steps
                .SelectMany(s => s.Prompt)
                .Select(m => m.Text ?? string.Empty)
                .Where(t => t.Contains("failed the same way", StringComparison.Ordinal))
                .Distinct()
        ];

        nudges.Count.ShouldBe(1);
        nudges[0].ShouldContain("overwrite: true", Case.Sensitive);
    }

    [Fact]
    public async Task A_read_only_success_between_failures_no_longer_resets_the_count()
    {
        // Run 5c071f37 checked a build or re-read a file between every one of ten identical
        // refusals - rational checking, not recovery - and a consecutive counter never armed.
        // Only an applied change resets the count now; looking around does not.
        RecordingStepLogger transcript = new();

        AgentRunResult result = await new AgentLoop(
            new FakeChatClientFactory(new FakeChatClient(
                FakeChatClient.ToolCall("boom"),
                FakeChatClient.ToolCall("boom"),
                FakeChatClient.ToolCall("fine"),
                FakeChatClient.ToolCall("boom"),
                FakeChatClient.Text("done"))),
            new ToolRegistry([new FlakyTools()]),
            transcript,
            TestContextAssembler.Create(),
            new RecordingMetricsRecorder(),
            Options.Create(new AgentOptions { MaxSteps = 30, MaxIdenticalToolFailures = 3 }))
            .RunAsync(new AgentRunRequest { TaskId = "t", Goal = "mixed" });

        result.StopReason.ShouldBe(AgentStopReason.RepeatedToolFailure);
        result.Steps.ShouldBe(4, "the interleaved read did not launder the third identical failure");
    }

    [Fact]
    public async Task An_applied_change_resets_the_count()
    {
        // The one event that honestly resets the argument: the workspace moved, so the same
        // call is no longer the same question. A run that fails, lands a change, and fails the
        // same way afresh is exploring, not looping.
        RecordingStepLogger transcript = new();
        ChangeLog changes = new();

        AgentRunResult result = await new AgentLoop(
            new FakeChatClientFactory(new FakeChatClient(
                FakeChatClient.ToolCall("boom"),
                FakeChatClient.ToolCall("boom"),
                FakeChatClient.ToolCall("apply"),
                FakeChatClient.ToolCall("boom"),
                FakeChatClient.ToolCall("boom"),
                FakeChatClient.Text("done"))),
            new ToolRegistry([new ChangingTools(changes)]),
            transcript,
            TestContextAssembler.Create(),
            new RecordingMetricsRecorder(),
            Options.Create(new AgentOptions { MaxSteps = 30, MaxIdenticalToolFailures = 3 }),
            changes: changes)
            .RunAsync(new AgentRunRequest { TaskId = "t", Goal = "mixed" });

        result.StopReason.ShouldBe(AgentStopReason.Completed);
    }

    [Fact]
    public async Task A_failure_whose_details_wobble_is_still_the_same_failure()
    {
        // The refusal now appends its strike countdown, and a diagnostics total can change while
        // the refusal stays the same refusal - so identity is the first line, which is stable.
        // Keying on the whole message made every repeat look novel and disarmed this limit.
        RecordingStepLogger transcript = new();

        AgentRunResult result = await new AgentLoop(
            new FakeChatClientFactory(new FakeChatClient(FakeChatClient.ToolCall("wobble"))),
            new ToolRegistry([new WobblyTools()]),
            transcript,
            TestContextAssembler.Create(),
            new RecordingMetricsRecorder(),
            Options.Create(new AgentOptions { MaxSteps = 30, MaxIdenticalToolFailures = 3 }))
            .RunAsync(new AgentRunRequest { TaskId = "t", Goal = "keep trying" });

        result.StopReason.ShouldBe(AgentStopReason.RepeatedToolFailure);
        result.Steps.ShouldBe(3);
    }

    [Fact]
    public async Task The_limit_can_be_switched_off()
    {
        RecordingStepLogger transcript = new();

        AgentRunResult result = await Loop(transcript, maxIdentical: 0, maxSteps: 4)
            .RunAsync(new AgentRunRequest { TaskId = "t", Goal = "keep trying" });

        result.StopReason.ShouldBe(AgentStopReason.StepLimit);
    }

    [Fact]
    public async Task A_relayed_failure_behind_a_succeeded_call_still_counts()
    {
        // Run 4b562c91: dotnet_project relays a failed SDK command as ok:true - information,
        // not a tool fault - and five identical failures were invisible to this limit. The
        // outcome flag makes them count without changing what the model reads.
        RecordingStepLogger transcript = new();

        AgentRunResult result = await new AgentLoop(
            new FakeChatClientFactory(new FakeChatClient(FakeChatClient.ToolCall("softboom"))),
            new ToolRegistry([new SoftFailingTools()]),
            transcript,
            TestContextAssembler.Create(),
            new RecordingMetricsRecorder(),
            Options.Create(new AgentOptions { MaxSteps = 30, MaxIdenticalToolFailures = 3 }))
            .RunAsync(new AgentRunRequest { TaskId = "t", Goal = "keep wiring" });

        result.StopReason.ShouldBe(AgentStopReason.RepeatedToolFailure);
        result.Steps.ShouldBe(3);
        result.ToolCallValidityRate.ShouldBe(1.0, "the calls were valid; the operations they relayed failed");
    }

    private sealed class SoftFailingTools : IToolSet
    {
        [GlassCoderTool("softboom")]
        [System.ComponentModel.Description("Relays a failed command as information, for tests.")]
        public GlassCoder.Tools.ToolObservation<string> SoftBoom() =>
            GlassCoder.Tools.Observation.Ok(
                "softboom", "exit 1", "dotnet add_reference failed with exit 1.", outcomeOk: false);
    }

    /// <summary>
    /// Run c5eb67f6 read one test file thirteen times - offset 70, 75, 76, maxLines 20, 25,
    /// 30 - and every variation minted a fresh fingerprint, so the stall counter never armed
    /// while the run read itself to the token limit. Reads of one unchanged path now count as
    /// themselves whatever the window: a nudge at four, and past it they stop counting as
    /// novel, so the ordinary stall stop takes over.
    /// </summary>
    [Fact]
    public async Task Rereading_one_unchanged_file_with_varying_windows_still_stalls()
    {
        RecordingStepLogger transcript = new();

        static ChatResponse Read(int start) => FakeChatClient.ToolCall(
            "read_file",
            new Dictionary<string, object?> { ["path"] = "src/T.cs", ["startLine"] = start });

        AgentRunResult result = await new AgentLoop(
            new FakeChatClientFactory(new FakeChatClient(
                Read(70), Read(75), Read(76), Read(80), Read(20), Read(25), Read(30))),
            new ToolRegistry([new PagingTools()]),
            transcript,
            TestContextAssembler.Create(),
            new RecordingMetricsRecorder(),
            Options.Create(new AgentOptions { MaxSteps = 30, MaxStalledSteps = 3 }))
            .RunAsync(new AgentRunRequest { TaskId = "t", Goal = "fix the file" });

        result.StopReason.ShouldBe(AgentStopReason.Stalled);

        transcript.Steps
            .SelectMany(s => s.Prompt)
            .ShouldContain(m => (m.Text ?? string.Empty).Contains("times without changing", StringComparison.Ordinal));
    }

    private sealed class PagingTools : IToolSet
    {
        [GlassCoderTool("read_file")]
        [System.ComponentModel.Description("Returns a window of a file, for tests.")]
        public GlassCoder.Tools.ToolObservation<string> Read(
            [System.ComponentModel.Description("The file.")] string path,
            [System.ComponentModel.Description("1-based start line.")] int startLine = 1) =>
            GlassCoder.Tools.Observation.Ok(
                "read_file", "content", $"Read lines {startLine}-{startLine + 24} of 133 from {path}.");
    }

    /// <summary>
    /// Runs ea9a1f66 and 216360bf edited between every one of their identical "N of M tests
    /// failed" results, and each edit honestly reset every other counter while fixing nothing.
    /// The test-outcome streak survives applied changes on purpose: only a different outcome
    /// for the same target - a green run, a different failure - ends it.
    /// </summary>
    [Fact]
    public async Task The_same_failing_test_result_three_times_earns_a_nudge_despite_edits()
    {
        RecordingStepLogger transcript = new();
        ChangeLog changes = new();

        AgentRunResult result = await new AgentLoop(
            new FakeChatClientFactory(new FakeChatClient(
                FakeChatClient.ToolCall("run_tests"),
                FakeChatClient.ToolCall("patch"),
                FakeChatClient.ToolCall("run_tests"),
                FakeChatClient.ToolCall("patch"),
                FakeChatClient.ToolCall("run_tests"),
                FakeChatClient.Text("done"))),
            new ToolRegistry([new RedSuiteTools(changes)]),
            transcript,
            TestContextAssembler.Create(),
            new RecordingMetricsRecorder(),
            Options.Create(new AgentOptions { MaxSteps = 30 }),
            changes: changes)
            .RunAsync(new AgentRunRequest { TaskId = "t", Goal = "make it green" });

        result.StopReason.ShouldBe(AgentStopReason.Completed);
        transcript.Steps
            .SelectMany(s => s.Prompt)
            .ShouldContain(m => (m.Text ?? string.Empty).Contains("same failing result", StringComparison.Ordinal));
    }

    /// <summary>Shaped like a test payload, because the metrics collector reads run_tests
    /// results by property name and a string payload throws it off.</summary>
    public sealed record RedSuite(bool Ok, int Passed, int Failed, int Total);

    private sealed class RedSuiteTools(IChangeLog changes) : IToolSet
    {
        [GlassCoderTool("run_tests")]
        [System.ComponentModel.Description("Always reports the same red suite, for tests.")]
        public GlassCoder.Tools.ToolObservation<RedSuite> RunTests(
            [System.ComponentModel.Description("Target.")] string path = "tests") =>
            GlassCoder.Tools.Observation.Ok(
                "run_tests", new RedSuite(false, 6, 1, 7), "1 of 7 tests failed: Demo.Tests.Multiply_ShouldRound.",
                outcomeOk: false);

        [GlassCoderTool("patch")]
        [System.ComponentModel.Description("Applies one distinct change, for tests.")]
        public GlassCoder.Tools.ToolObservation<string> Patch()
        {
            CodeChange change = changes.Propose(
                "src/File.cs", "patch", "before", $"after-{changes.All().Count}");
            changes.Update(change.Id, ChangeStatus.Applied);
            return GlassCoder.Tools.Observation.Ok("patch", "ok");
        }
    }

    /// <summary>
    /// The same streak, arriving by the door the harness spent a year moving it to.
    /// <para>
    /// Since 2026-08-09 the system prompt tells the model not to call <c>run_tests</c> itself, and
    /// since 2026-08-15 <c>update_todos</c> tells it not even to plan one - so a failing suite
    /// normally reaches the loop as the ladder's climb after an applied change, which the counter
    /// above could not see. Run <c>d92c189b</c> produced three byte-identical failing climbs at
    /// steps 22, 23 and 26 and reached this nudge's own conclusion unaided three steps later.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Identical_failing_climbs_earn_the_nudge_though_nothing_called_run_tests()
    {
        RecordingStepLogger transcript = new();
        ChangeLog changes = new();
        ScriptedLadder ladder = new();
        ladder.Enqueue(Red(), Red(), Red());

        await LadderLoop(transcript, changes, ladder, applies: 3)
            .RunAsync(new AgentRunRequest { TaskId = "t", Goal = "make it green" });

        List<string> nudges = Nudges(transcript);
        nudges.Count.ShouldBe(1, "once per streak, however many climbs follow it");
        nudges[0].ShouldContain("MainWindowTests.MultiplyButton_Click", Case.Sensitive);
        nudges[0].ShouldNotContain("run_tests has", Case.Sensitive);
    }

    [Fact]
    public async Task A_different_failure_after_the_nudge_does_not_rearm_it()
    {
        // A new failure set is a different fight, and the run has already been told once. Only a
        // green - the streak's honest end - re-arms the nudge.
        RecordingStepLogger transcript = new();
        ChangeLog changes = new();
        ScriptedLadder ladder = new();
        ladder.Enqueue(Red(), Red(), Red(), Red("2 of 9 tests failed: Demo.ParseTests.Handles_commas"));

        await LadderLoop(transcript, changes, ladder, applies: 4)
            .RunAsync(new AgentRunRequest { TaskId = "t", Goal = "make it green" });

        Nudges(transcript).Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_green_climb_between_the_failures_ends_the_streak()
    {
        // The boundary, kept deliberately where the run_tests path already had it: a green result
        // for the target supersedes the streak, because the suite it was about is no longer red.
        // Run 4bf2eaeb is the case this does not catch - it deleted the failing file at step 36,
        // took the green, and re-created the same three failures at step 40 - and that is a
        // question about what a green means, not about which organ reported it.
        RecordingStepLogger transcript = new();
        ChangeLog changes = new();
        ScriptedLadder ladder = new();
        ladder.Enqueue(Red(), Red(), Green(), Red());

        await LadderLoop(transcript, changes, ladder, applies: 4)
            .RunAsync(new AgentRunRequest { TaskId = "t", Goal = "make it green" });

        Nudges(transcript).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_climb_that_verified_nothing_neither_starts_nor_ends_a_streak()
    {
        // "Nothing was verified" is the ladder's word for a rung that ran no test, and no test is
        // not a different outcome from the last one - it is the absence of one. A climb that
        // established nothing must not launder two identical reds into a fresh start.
        RecordingStepLogger transcript = new();
        ChangeLog changes = new();
        ScriptedLadder ladder = new();
        ladder.Enqueue(Red(), Red(), NothingToRun(), Red());

        await LadderLoop(transcript, changes, ladder, applies: 4)
            .RunAsync(new AgentRunRequest { TaskId = "t", Goal = "make it green" });

        Nudges(transcript).Count.ShouldBe(1);
    }

    private static List<string> Nudges(RecordingStepLogger transcript) =>
    [
        .. transcript.Steps
            .SelectMany(s => s.Prompt)
            .Select(m => m.Text ?? string.Empty)
            .Where(t => t.Contains("same failing result", StringComparison.Ordinal))
            .Distinct()
    ];

    /// <summary>A loop that applies one change per step and lets the scripted ladder judge it.</summary>
    private static AgentLoop LadderLoop(
        RecordingStepLogger transcript,
        ChangeLog changes,
        IVerificationLadder ladder,
        int applies)
    {
        List<ChatResponse> script = [.. Enumerable.Repeat(FakeChatClient.ToolCall("apply"), applies)];
        script.Add(FakeChatClient.Text("done"));

        return new AgentLoop(
            new FakeChatClientFactory(new FakeChatClient([.. script])),
            new ToolRegistry([new ChangingTools(changes)]),
            transcript,
            TestContextAssembler.Create(),
            new RecordingMetricsRecorder(),
            Options.Create(new AgentOptions { MaxSteps = 30 }),
            verifier: ladder,
            changes: changes);
    }

    private const string RedFirstLine =
        "3 of 8 tests failed: MultiplyAppTests.MainWindowTests.MultiplyButton_Click_WithValidInput";

    /// <summary>A red climb. The detail under the first line wobbles on purpose - identity is the
    /// first line, which is the rule the failure counter follows for the same reason.</summary>
    private static VerificationReport Red(string firstLine = RedFirstLine) => new(
        false,
        VerificationRung.UnitTests,
        VerificationRung.UnitTests,
        [
            new RungResult(VerificationRung.Compile, true, "Build succeeded.", 1),
            new RungResult(VerificationRung.UnitTests, false, $"{firstLine}\n  Expected 15, got 50 [7 ms]", 1)
            {
                TestTarget = "tests/MultiplyAppTests",
            },
        ],
        2);

    private static VerificationReport Green() => new(
        true,
        VerificationRung.UnitTests,
        null,
        [
            new RungResult(VerificationRung.Compile, true, "Build succeeded.", 1),
            new RungResult(VerificationRung.UnitTests, true, "5 tests passed.", 1)
            {
                TestTarget = "tests/MultiplyAppTests",
            },
        ],
        2);

    /// <summary>The rung that answered from the sources. It carries a target here so that what
    /// excludes it is <see cref="RungResult.Unverified"/> and nothing else.</summary>
    private static VerificationReport NothingToRun() => new(
        true,
        VerificationRung.UnitTests,
        null,
        [
            new RungResult(VerificationRung.Compile, true, "Build succeeded.", 1),
            new RungResult(
                VerificationRung.UnitTests,
                true,
                "No test is declared in the workspace yet, so there was nothing for this rung to run - " +
                "nothing was verified.",
                1)
            {
                Unverified = true,
                TestTarget = "tests/MultiplyAppTests",
            },
        ],
        2);

    private sealed class ScriptedLadder : IVerificationLadder
    {
        private readonly Queue<VerificationReport> _scripted = new();

        public void Enqueue(params VerificationReport[] reports)
        {
            foreach (VerificationReport report in reports)
            {
                _scripted.Enqueue(report);
            }
        }

        public Task<VerificationReport> VerifyAsync(
            VerificationRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(_scripted.Count > 0 ? _scripted.Dequeue() : Green());
    }

    private static AgentLoop Loop(RecordingStepLogger transcript, int maxIdentical, int maxSteps = 30) => new(
        new FakeChatClientFactory(new FakeChatClient(FakeChatClient.ToolCall("boom"))),
        new ToolRegistry([new FlakyTools()]),
        transcript,
        TestContextAssembler.Create(),
        new RecordingMetricsRecorder(),
        Options.Create(new AgentOptions { MaxSteps = maxSteps, MaxIdenticalToolFailures = maxIdentical }));

    private sealed class FlakyTools : IToolSet
    {
        [GlassCoderTool("boom")]
        [System.ComponentModel.Description("Always fails the same way, for tests.")]
        public GlassCoder.Tools.ToolObservation<string> Boom() =>
            GlassCoder.Tools.Observation.Fail<string>(
                "boom", GlassCoder.Tools.ToolErrorCodes.NotFound, "The text to replace was not found.");

        [GlassCoderTool("fine")]
        [System.ComponentModel.Description("Always succeeds, for tests.")]
        public GlassCoder.Tools.ToolObservation<string> Fine() =>
            GlassCoder.Tools.Observation.Ok("fine", "ok");
    }

    private sealed class ChangingTools(IChangeLog changes) : IToolSet
    {
        [GlassCoderTool("boom")]
        [System.ComponentModel.Description("Always fails the same way, for tests.")]
        public GlassCoder.Tools.ToolObservation<string> Boom() =>
            GlassCoder.Tools.Observation.Fail<string>(
                "boom", GlassCoder.Tools.ToolErrorCodes.NotFound, "The text to replace was not found.");

        [GlassCoderTool("apply")]
        [System.ComponentModel.Description("Applies one change, for tests.")]
        public GlassCoder.Tools.ToolObservation<string> Apply()
        {
            CodeChange change = changes.Propose("src/File.cs", "apply", "before", "after");
            changes.Update(change.Id, ChangeStatus.Applied);
            return GlassCoder.Tools.Observation.Ok("apply", "ok");
        }
    }

    private sealed class WobblyTools : IToolSet
    {
        private int _attempt;

        [GlassCoderTool("wobble")]
        [System.ComponentModel.Description("Fails the same way with varying detail lines, for tests.")]
        public GlassCoder.Tools.ToolObservation<string> Wobble() =>
            GlassCoder.Tools.Observation.Fail<string>(
                "wobble",
                GlassCoder.Tools.ToolErrorCodes.VerificationFailed,
                $"'src/A.cs' was not written: it would not compile.\nAttempt {++_attempt} detail.");
    }
}

/// <summary>
/// Re-confirming what the cache already answered (run 46231701).
/// <para>
/// Task 74 gave <c>build</c> and <c>run_tests</c> a memory and told the model it was reading a
/// reused result. Steps 18, 22 and 23 spent themselves anyway - four of twenty-six on the one
/// axis never in doubt, in a run whose evidence critic dissented over an axis nothing had
/// touched. Making the redundant step cheap did not make it stop.
/// </para>
/// <para>
/// Detection keys on the observation's cache flag, never on the sentence the tool prints: prose
/// that synthesis writes must not be prose that detection reads.
/// </para>
/// </summary>
public sealed class RedundantVerificationTests
{
    [Fact]
    public async Task Two_cached_verifications_with_nothing_between_them_earn_one_nudge()
    {
        RecordingStepLogger transcript = new();

        await Loop(transcript,
            FakeChatClient.ToolCall("build"),
            FakeChatClient.ToolCall("build"),
            FakeChatClient.ToolCall("build"),
            FakeChatClient.Text("done"))
            .RunAsync(new AgentRunRequest { TaskId = "t", Goal = "keep checking" });

        List<string> nudges = Nudges(transcript);

        nudges.Count.ShouldBe(1, "once, like the step-budget warning - repeating it spends the budget it warns about");
        nudges[0].ShouldContain("answered from cache");
        nudges[0].ShouldContain("Compiling is not the open question");

        // It names what is unverified rather than what has been re-confirmed, because the run
        // already knows the second.
        nudges[0].ShouldContain("launch_app");
    }

    [Fact]
    public async Task One_cached_verification_says_nothing()
    {
        RecordingStepLogger transcript = new();

        await Loop(transcript, FakeChatClient.ToolCall("build"), FakeChatClient.Text("done"))
            .RunAsync(new AgentRunRequest { TaskId = "t", Goal = "check once" });

        Nudges(transcript).ShouldBeEmpty("checking once is checking, not marking time");
    }

    [Fact]
    public async Task A_verification_that_actually_ran_re_arms_the_counter()
    {
        // The cache answers, then the same call really builds, then the cache answers again -
        // one repeat either side of real work, not two in a row.
        RecordingStepLogger transcript = new();

        await new AgentLoop(
            new FakeChatClientFactory(new FakeChatClient(
                FakeChatClient.ToolCall("build"),
                FakeChatClient.ToolCall("build"),
                FakeChatClient.ToolCall("build"),
                FakeChatClient.Text("done"))),
            new ToolRegistry([new CachedBuildTools(new ChangeLog(), true, false, true)]),
            transcript,
            TestContextAssembler.Create(),
            new RecordingMetricsRecorder(),
            Options.Create(new AgentOptions { MaxSteps = 10, MaxStalledSteps = 0 }))
            .RunAsync(new AgentRunRequest { TaskId = "t", Goal = "mixed" });

        Nudges(transcript).ShouldBeEmpty();
    }

    [Fact]
    public async Task An_applied_change_resets_the_count()
    {
        // The one event that makes verifying worth doing again.
        RecordingStepLogger transcript = new();
        ChangeLog changes = new();

        await new AgentLoop(
            new FakeChatClientFactory(new FakeChatClient(
                FakeChatClient.ToolCall("build"),
                FakeChatClient.ToolCall("apply"),
                FakeChatClient.ToolCall("build"),
                FakeChatClient.Text("done"))),
            new ToolRegistry([new CachedBuildTools(changes)]),
            transcript,
            TestContextAssembler.Create(),
            new RecordingMetricsRecorder(),
            Options.Create(new AgentOptions { MaxSteps = 10, MaxStalledSteps = 0 }),
            changes: changes)
            .RunAsync(new AgentRunRequest { TaskId = "t", Goal = "edit and check" });

        Nudges(transcript).ShouldBeEmpty();
    }

    private static List<string> Nudges(RecordingStepLogger transcript) =>
    [
        .. transcript.Steps
            .SelectMany(s => s.Prompt)
            .Select(m => m.Text ?? string.Empty)
            .Where(t => t.Contains("answered from cache", StringComparison.Ordinal))
            .Distinct()
    ];

    /// <summary>
    /// The stall limit is off on purpose: an identical call repeated verbatim is exactly what it
    /// exists to cut short, and this test needs the run to keep going long enough to be nudged.
    /// </summary>
    private static AgentLoop Loop(RecordingStepLogger transcript, params ChatResponse[] responses) => new(
        new FakeChatClientFactory(new FakeChatClient(responses)),
        new ToolRegistry([new CachedBuildTools(new ChangeLog())]),
        transcript,
        TestContextAssembler.Create(),
        new RecordingMetricsRecorder(),
        Options.Create(new AgentOptions { MaxSteps = 10, MaxStalledSteps = 0 }));

    /// <summary>
    /// One <c>build</c> whose answers come from the cache or not, per the scripted sequence -
    /// which is how the real tool behaves. The last entry repeats once the script runs out; no
    /// script at all means every answer is a cached one.
    /// </summary>
    private sealed class CachedBuildTools(IChangeLog changes, params bool[] cached) : IToolSet
    {
        private int _call;

        [GlassCoderTool("build")]
        [System.ComponentModel.Description("Builds, sometimes from the cache, for tests.")]
        public GlassCoder.Tools.ToolObservation<string> Build()
        {
            bool fromCache = cached.Length == 0 || cached[Math.Min(_call++, cached.Length - 1)];

            return GlassCoder.Tools.Observation.Ok(
                "build",
                "ok",
                fromCache
                    ? "Build succeeded (unchanged since the last build, so this result was reused)."
                    : "Build succeeded.",
                reused: fromCache);
        }

        [GlassCoderTool("apply")]
        [System.ComponentModel.Description("Applies one change, for tests.")]
        public GlassCoder.Tools.ToolObservation<string> Apply()
        {
            CodeChange change = changes.Propose("src/File.cs", "apply", "before", "after");
            changes.Update(change.Id, ChangeStatus.Applied);
            return GlassCoder.Tools.Observation.Ok("apply", "ok");
        }
    }
}
