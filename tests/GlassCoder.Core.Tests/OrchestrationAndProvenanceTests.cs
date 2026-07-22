using GlassCoder.Core.Agent;
using GlassCoder.Core.Context;
using GlassCoder.Core.Orchestration;
using GlassCoder.Core.Provenance;
using GlassCoder.Core.Verification;
using GlassCoder.Models;
using GlassCoder.Models.Configuration;
using GlassCoder.TestSupport;
using GlassCoder.Tools.Registry;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace GlassCoder.Core.Tests;

/// <summary>
/// Orchestration (workplan task 33), the critique panel (task 23) and provenance (task 35).
/// </summary>
public sealed class OrchestrationAndProvenanceTests : IDisposable
{
    private readonly TempWorkspace _workspace = new();

    public void Dispose() => _workspace.Dispose();

    [Fact]
    public async Task A_fan_out_runs_every_sub_task_and_reports_its_speed_up()
    {
        Orchestrator orchestrator = Orchestrator(parallelism: 3);

        FanOutResult result = await orchestrator.FanOutAsync(
        [
            new SubTask("a", "Do A"),
            new SubTask("b", "Do B"),
            new SubTask("c", "Do C"),
        ]);

        result.Results.Count.ShouldBe(3);
        result.CompletedCount.ShouldBe(3);
        result.Results.Select(r => r.SubTask.Id).Order().ShouldBe(["a", "b", "c"]);
        result.TotalTokens.ShouldBeGreaterThan(0);
        result.Speedup.ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task An_empty_fan_out_is_not_an_error()
    {
        (await Orchestrator().FanOutAsync([])).Results.ShouldBeEmpty();
    }

    [Fact]
    public async Task Each_sub_agent_gets_its_own_budget()
    {
        // Sharing one budget would let a greedy sub-agent starve the rest.
        Orchestrator orchestrator = Orchestrator(subAgentMaxSteps: 2, alwaysCallsTools: true);

        FanOutResult result = await orchestrator.FanOutAsync([new SubTask("a", "Do A"), new SubTask("b", "Do B")]);

        result.Results.ShouldAllBe(r => r.Result.Steps == 2);
        result.Results.ShouldAllBe(r => r.Result.StopReason == AgentStopReason.StepLimit);
    }

    [Fact]
    public async Task Orchestration_is_off_until_it_is_asked_for()
    {
        // Multi-agent is a layer over a working loop, added last on purpose.
        Orchestrator(enabled: false).Enabled.ShouldBeFalse();
        Orchestrator().Enabled.ShouldBeTrue();
        await Task.CompletedTask;
    }

    [Fact]
    public async Task A_critic_panel_refutes_when_a_majority_refutes()
    {
        FakeChatClient client = new(
            FakeChatClient.Text("""{"refuted": true, "confidence": 0.9, "reason": "It ignores the empty case."}"""));

        CriticPanel panel = new(
            new FakeChatClientFactory(client),
            Options.Create(new CritiqueOptions { Enabled = true, CriticCount = 3, Role = ModelRoles.Worker }));

        CritiqueResult result = await panel.CritiqueAsync("Fix the bug", "a diff", "it compiles");

        result.Refuted.ShouldBeTrue();
        result.RefutingVotes.ShouldBe(3);
        result.Summary.ShouldContain("empty case");
    }

    [Fact]
    public async Task A_critic_panel_accepts_when_the_critics_cannot_refute()
    {
        FakeChatClient client = new(
            FakeChatClient.Text("""{"refuted": false, "confidence": 0.8, "reason": "The tests cover it."}"""));

        CriticPanel panel = new(
            new FakeChatClientFactory(client),
            Options.Create(new CritiqueOptions { Enabled = true, CriticCount = 3, Role = ModelRoles.Worker }));

        CritiqueResult result = await panel.CritiqueAsync("Fix the bug", "a diff", "tests pass");

        result.Refuted.ShouldBeFalse();
        result.Votes.Count.ShouldBe(3);
    }

    [Fact]
    public async Task Critique_is_disabled_by_default()
    {
        CriticPanel panel = new(
            new FakeChatClientFactory(new FakeChatClient()),
            Options.Create(new CritiqueOptions()));

        panel.Enabled.ShouldBeFalse();
        (await panel.CritiqueAsync("g", "c", "e")).Refuted.ShouldBeFalse();
    }

    [Fact]
    public void Provenance_records_the_harness_version_and_the_configuration_hash()
    {
        ProvenanceStamp stamp = Stamper().Stamp();

        stamp.HarnessVersion.ShouldNotBeNullOrWhiteSpace();
        stamp.ConfigHash.ShouldNotBeNullOrWhiteSpace();
        stamp.StampedAt.ShouldBeGreaterThan(DateTimeOffset.UnixEpoch);
    }

    [Fact]
    public void Context_with_no_root_files_is_trivially_fresh()
    {
        Stamper().Stamp().ContextFresh.ShouldBeTrue();
    }

    [Fact]
    public void Context_written_before_the_code_is_stale()
    {
        _workspace.WriteFile("CONTEXT.md", "# how this repo works");
        string source = _workspace.WriteFile("src/Widget.cs", "public class Widget { }");
        File.SetLastWriteTimeUtc(source, DateTime.UtcNow.AddHours(1));

        ContextOptions context = new();
        context.RootContextFiles.Add("CONTEXT.md");

        ProvenanceStamp stamp = Stamper(context).Stamp();

        stamp.ContextFresh.ShouldBeFalse();
        stamp.StaleReason.ShouldContain("src/Widget.cs");
    }

    [Fact]
    public void The_harness_own_output_never_makes_a_run_look_stale()
    {
        // Without this exclusion, writing the logs for one run would invalidate the next -
        // a trigger loop that never settles.
        _workspace.WriteFile("CONTEXT.md", "# how this repo works");
        string log = _workspace.WriteFile("logs/glasscoder.cs", "// not really source, but it ends in .cs");
        File.SetLastWriteTimeUtc(log, DateTime.UtcNow.AddHours(1));

        ContextOptions context = new();
        context.RootContextFiles.Add("CONTEXT.md");

        Stamper(context).Stamp().ContextFresh.ShouldBeTrue();
    }

    private ProvenanceStamper Stamper(ContextOptions? context = null) =>
        new(_workspace.Guard(), Options.Create(new ProvenanceOptions()), Options.Create(context ?? new ContextOptions()));

    private Orchestrator Orchestrator(
        bool enabled = true,
        int parallelism = 3,
        int subAgentMaxSteps = 8,
        bool alwaysCallsTools = false)
    {
        IAgentLoop Factory()
        {
            FakeChatClient client = alwaysCallsTools
                ? new FakeChatClient(FakeChatClient.ToolCall("noop"))
                : new FakeChatClient(FakeChatClient.Text("sub-agent done"));

            return new AgentLoop(
                new FakeChatClientFactory(client),
                new ToolRegistry([new NoopTools()]),
                new RecordingStepLogger(),
                TestContextAssembler.Create(),
                new RecordingMetricsRecorder(),
                Options.Create(new AgentOptions()));
        }

        return new Orchestrator(
            Factory,
            Options.Create(new OrchestrationOptions
            {
                Enabled = enabled,
                MaxDegreeOfParallelism = parallelism,
                SubAgentMaxSteps = subAgentMaxSteps,
            }),
            Options.Create(new AgentOptions()));
    }

    private sealed class NoopTools : IToolSet
    {
        [GlassCoderTool("noop")]
        [System.ComponentModel.Description("Does nothing, for tests.")]
        public Tools.ToolObservation<string> Noop() => Tools.Observation.Ok("noop", "ok");
    }
}
