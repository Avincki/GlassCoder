using GlassCoder.Core.Agent;
using GlassCoder.Core.Verification;
using GlassCoder.TestSupport;
using GlassCoder.Tools.Build;
using GlassCoder.Tools.Changes;
using GlassCoder.Tools.Execution;
using GlassCoder.Tools.Guardrails;
using GlassCoder.Tools.Registry;
using GlassCoder.Tools.Verification;
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
            string goal, string change, string evidence, string? role = null, CancellationToken cancellationToken = default) =>
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
