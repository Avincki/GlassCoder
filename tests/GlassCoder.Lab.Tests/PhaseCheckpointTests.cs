using GlassCoder.Core.Agent;
using GlassCoder.Core.Diagnostics;
using GlassCoder.Core.Metrics;
using GlassCoder.Lab.Checkpoints;
using GlassCoder.TestSupport;
using GlassCoder.Tools;
using GlassCoder.Tools.Build;
using GlassCoder.Tools.Execution;
using GlassCoder.Tools.FileSystem;
using GlassCoder.Tools.Guardrails;
using GlassCoder.Tools.Registry;
using GlassCoder.Tools.Verification;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Serilog;

namespace GlassCoder.Lab.Tests;

/// <summary>
/// The Phase 0 and Phase 1 checkpoints (workplan tasks 13 and 19).
/// <para>
/// These run the <em>real</em> harness - real tools, real registry, real loop, real guardrail -
/// over a temporary repository, with only the model and the container faked. That is the point
/// of a checkpoint: it answers "is this phase instrumented and stable end to end", which a test
/// of any single component cannot.
/// </para>
/// </summary>
public sealed class PhaseCheckpointTests : IDisposable
{
    private readonly TempWorkspace _workspace = new();
    private readonly RecordingMetricsRecorder _metrics = new();
    private readonly ScriptedCommandExecutor _executor = new();

    public void Dispose() => _workspace.Dispose();

    [Fact]
    public async Task Phase_0_runs_green_with_read_only_tools_and_records_tool_call_validity()
    {
        _workspace.WriteFile("src/AgentLoop.cs", "// the controller loop lives here\npublic class AgentLoop { }\n");
        _workspace.WriteFile("src/Other.cs", "public class Other { }\n");

        // A model that navigates the way the system prompt asks it to: glob, then grep, then
        // read. The script covers both checkpoint cases, since one client serves the whole run.
        FakeChatClient client = new(
            // phase0-locate
            FakeChatClient.ToolCall("glob", new Dictionary<string, object?> { ["pattern"] = "**/*.cs" }, "c1"),
            FakeChatClient.ToolCall("grep", new Dictionary<string, object?> { ["pattern"] = "controller loop" }, "c2"),
            FakeChatClient.ToolCall("read_file", new Dictionary<string, object?> { ["path"] = "src/AgentLoop.cs" }, "c3"),
            FakeChatClient.Text("src/AgentLoop.cs"),

            // phase0-comprehend
            FakeChatClient.ToolCall("read_file", new Dictionary<string, object?> { ["path"] = "src/AgentLoop.cs" }, "c4"),
            FakeChatClient.Text("It declares the AgentLoop type. It is the controller loop of the harness."));

        CheckpointReport report = await Checkpoint(client, Phase0Tools()).RunAsync("Phase 0", PhaseCases.Phase0());

        report.Passed.ShouldBeTrue(report.ToText());
        report.ToolCallValidityRate.ShouldBe(1d);
        report.Cases.Count.ShouldBe(2);
    }

    [Fact]
    public async Task Phase_0_advertises_exactly_the_read_only_tools()
    {
        // "No editing" is a property of the configuration, not of the model's good intentions.
        PhaseCheckpoint checkpoint = Checkpoint(new FakeChatClient(FakeChatClient.Text("done")), Phase0Tools());

        checkpoint.ToolNames.ShouldBe(["read_file", "grep", "glob"]);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Phase_0_records_a_metrics_row_per_case_with_the_oracle_verdict()
    {
        FakeChatClient client = new(
            FakeChatClient.ToolCall("glob", new Dictionary<string, object?> { ["pattern"] = "**/*.cs" }),
            FakeChatClient.Text("src/AgentLoop.cs"));

        await Checkpoint(client, Phase0Tools()).RunAsync("Phase 0", PhaseCases.Phase0());

        _metrics.Records.Count.ShouldBe(2);
        _metrics.Records.ShouldAllBe(m => m.Source == "checkpoint:Phase 0");
        _metrics.Records.ShouldAllBe(m => m.OraclePassed != null);
        _metrics.Records[0].PassAtOne.ShouldNotBeNull();
        _metrics.Records[0].ToolCallValidityRate.ShouldBe(1d);
    }

    [Fact]
    public async Task Phase_0_transcripts_replay_from_the_log()
    {
        // Phase 0's deliverable is instrumentation, so "did it run" is not enough: the run has
        // to come back out of the log as a transcript.
        string logDirectory = Path.Combine(_workspace.Root, "logs");
        LoggingOptions logging = new() { Directory = logDirectory, Console = false };
        using Serilog.Core.Logger serilog = SerilogBootstrap.CreateLogger(logging);
        using ILoggerFactory factory = LoggerFactory.Create(builder => builder.AddSerilog(serilog));

        FakeChatClient client = new(
            FakeChatClient.ToolCall("glob", new Dictionary<string, object?> { ["pattern"] = "**/*.cs" }),
            FakeChatClient.Text("done"));

        PhaseCheckpoint checkpoint = Checkpoint(
            client,
            Phase0Tools(),
            new StepLogger(factory.CreateLogger<StepLogger>(), Options.Create(logging)));

        await checkpoint.RunAsync("Phase 0", [PhaseCases.Phase0()[0]]);
        serilog.Dispose();

        IReadOnlyList<RunTranscript> transcripts = PhaseCheckpoint.ReadTranscripts(logDirectory);
        transcripts.ShouldHaveSingleItem().IsComplete.ShouldBeTrue();
    }

    [Fact]
    public async Task Phase_1_closes_the_loop_edit_then_build_then_test()
    {
        _workspace.WriteFile("src/Proj.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");
        _workspace.WriteFile("src/Pager.cs", "namespace Demo;\npublic sealed class Pager\n{\n    public int Last => 10;\n}\n");

        _executor.Enqueue(0, "");                                                        // build: green
        _executor.Enqueue(0, "Passed!  - Failed: 0, Passed: 3, Skipped: 0, Total: 3");   // tests: green

        FakeChatClient client = new(
            FakeChatClient.ToolCall("read_file", new Dictionary<string, object?> { ["path"] = "src/Pager.cs" }, "c1"),
            FakeChatClient.ToolCall("edit_file", new Dictionary<string, object?>
            {
                ["path"] = "src/Pager.cs",
                ["oldText"] = "public int Last => 10;",
                ["newText"] = "public int Last => 9;",
            }, "c2"),
            FakeChatClient.ToolCall("build", new Dictionary<string, object?> { ["path"] = "src" }, "c3"),
            FakeChatClient.ToolCall("run_tests", new Dictionary<string, object?> { ["path"] = "src" }, "c4"),
            FakeChatClient.Text("Fixed the off-by-one and the tests are green."));

        CheckpointReport report = await Checkpoint(client, Phase1Tools())
            .RunAsync("Phase 1", PhaseCases.Phase1("src/Pager.cs", "PagerTests"));

        report.Passed.ShouldBeTrue(report.ToText());
        File.ReadAllText(Path.Combine(_workspace.Root, "src", "Pager.cs")).ShouldContain("=> 9;");

        RunMetrics metrics = _metrics.Records.ShouldHaveSingleItem();
        metrics.Edits.ShouldBe(1);
        metrics.Builds.ShouldBe(1);
        metrics.TestRuns.ShouldBe(1);
        metrics.EditsWithCompileErrors.ShouldBe(0);
        metrics.CompileErrorRatePerEdit.ShouldBe(0d);
        metrics.OraclePassed.ShouldBe(true);
    }

    [Fact]
    public async Task Phase_1_measures_a_break_and_the_recovery_from_it()
    {
        // The two Phase 1 watch metrics only mean anything when the agent actually breaks
        // something: compile-error rate per edit, and edits-to-green.
        _workspace.WriteFile("src/Proj.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");
        _workspace.WriteFile("src/Pager.cs", "namespace Demo;\npublic sealed class Pager\n{\n    public int Last => 10;\n}\n");

        _executor.Enqueue(1, @"C:\repo\src\Pager.cs(4,5): error CS0103: The name 'Coont' does not exist [C:\repo\src\Proj.csproj]");
        _executor.Enqueue(0, "");

        FakeChatClient client = new(
            FakeChatClient.ToolCall("edit_file", new Dictionary<string, object?>
            {
                ["path"] = "src/Pager.cs",
                ["oldText"] = "=> 10;",
                ["newText"] = "=> 11;",
            }, "c1"),
            FakeChatClient.ToolCall("build", new Dictionary<string, object?> { ["path"] = "src" }, "c2"),
            FakeChatClient.ToolCall("edit_file", new Dictionary<string, object?>
            {
                ["path"] = "src/Pager.cs",
                ["oldText"] = "=> 11;",
                ["newText"] = "=> 12;",
            }, "c3"),
            FakeChatClient.ToolCall("build", new Dictionary<string, object?> { ["path"] = "src" }, "c4"),
            FakeChatClient.Text("Recovered."));

        await Checkpoint(client, Phase1Tools()).RunAsync("Phase 1", PhaseCases.Phase1("src/Pager.cs", "PagerTests"));

        RunMetrics metrics = _metrics.Records.ShouldHaveSingleItem();
        metrics.Edits.ShouldBe(2);
        metrics.BuildFailures.ShouldBe(1);
        metrics.EditsWithCompileErrors.ShouldBe(1);
        metrics.CompileErrorRatePerEdit.ShouldBe(0.5d);
        metrics.RecoveryOpportunities.ShouldBe(1);
        metrics.Recoveries.ShouldBe(1);
        metrics.RecoveryRate.ShouldBe(1d);
        metrics.EditsToGreen.ShouldBe(1);
        metrics.DiagnosticsReported.ShouldBe(1);
    }

    [Fact]
    public async Task Phase_1_advertises_build_before_run_tests()
    {
        // Tool order is part of the contract: the cheaper, higher-value oracle comes first.
        PhaseCheckpoint checkpoint = Checkpoint(new FakeChatClient(FakeChatClient.Text("done")), Phase1Tools());

        checkpoint.ToolNames.ShouldBe(["read_file", "grep", "glob", "edit_file", "build", "run_tests"]);
        await Task.CompletedTask;
    }

    private IReadOnlyList<IToolSet> Phase0Tools()
    {
        IPathGuard guard = _workspace.Guard();
        IOptions<ToolsOptions> tools = Options.Create(new ToolsOptions());

        return [new ReadFileTool(guard, tools), new GrepTool(guard, tools), new GlobTool(guard, tools)];
    }

    private IReadOnlyList<IToolSet> Phase1Tools()
    {
        IPathGuard guard = _workspace.Guard("src");
        IOptions<ToolsOptions> tools = Options.Create(new ToolsOptions());
        IOptions<VerificationOptions> verification = Options.Create(new VerificationOptions());
        IOptions<SandboxOptions> sandbox = Options.Create(new SandboxOptions());
        RoslynCodeAnalyzer analyzer = new(guard, verification);
        DiagnosticSummarizer summarizer = new(verification);

        return
        [
            new ReadFileTool(guard, tools),
            new GrepTool(guard, tools),
            new GlobTool(guard, tools),
            new EditFileTool(guard, analyzer, summarizer, verification),
            new BuildTool(_executor, guard, summarizer, sandbox),
            new RunTestsTool(_executor, guard, sandbox),
        ];
    }

    private PhaseCheckpoint Checkpoint(IChatClient client, IReadOnlyList<IToolSet> toolSets, IStepLogger? stepLogger = null)
    {
        ToolRegistry registry = new(toolSets);
        AgentLoop loop = new(
            new FakeChatClientFactory(client),
            registry,
            stepLogger ?? new RecordingStepLogger(),
            TestContextAssembler.Create(guard: _workspace.Guard()),
            new RecordingMetricsRecorder(),
            Options.Create(new AgentOptions { MaxSteps = 12 }));

        return new PhaseCheckpoint(loop, registry, _metrics);
    }
}
