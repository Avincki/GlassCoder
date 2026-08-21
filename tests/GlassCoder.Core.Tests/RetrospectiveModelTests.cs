using System.Text.Json;
using GlassCoder.Core.Diagnostics;
using GlassCoder.Core.Verification;
using GlassCoder.TestSupport;
using GlassCoder.Tools.Changes;
using GlassCoder.Tools.Processes;

namespace GlassCoder.Core.Tests;

/// <summary>
/// Which model produced the run, as the retrospective reports it.
/// <para>
/// The harness can address a different model per role, and the same alias can be served by a
/// different checkpoint next week. A retrospective that could not say which one answered was
/// missing a term: capability is read here as model × harness × context, and a review blind to
/// the first spends its recommendations on the other two. The reports name it now, and these
/// tests hold the naming to the two rules that make it worth having - it comes off the steps that
/// witnessed the run rather than off configuration, and it survives being read back off disk.
/// </para>
/// </summary>
public sealed class RetrospectiveModelTests
{
    [Fact]
    public void The_run_header_names_the_model_that_answered()
    {
        string digest = Render([Step(0, "worker", "qwen3-27b")]);

        digest.ShouldContain("- Models: worker: qwen3-27b");
    }

    [Fact]
    public void The_checkpoint_leads_and_the_alias_follows_it()
    {
        // The line the whole feature is for. Before this, an OpenAI-compatible server echoing its
        // own alias made every report say "worker: worker" - true, and no help at all to somebody
        // asking which of two checkpoints wrote the better code.
        string digest = Render([Step(0, "worker", "worker", "org/Qwen3.8-27B-NVFP4")]);

        digest.ShouldContain("- Models: worker: org/Qwen3.8-27B-NVFP4 (alias \"worker\")");
        digest.ShouldNotContain("worker: worker");
    }

    [Fact]
    public void An_unnamed_checkpoint_says_the_alias_is_all_there_is()
    {
        // Honest about which of the two it has. A bare alias printed as though it were a
        // checkpoint is what made the absence invisible in the first place.
        string digest = Render([Step(0, "worker", "worker", null)]);

        digest.ShouldContain("worker: worker (an alias - the server did not name the checkpoint)");
    }

    [Fact]
    public void One_alias_on_two_checkpoints_is_two_models()
    {
        // Repointing an endpoint and running again is the comparison this supports, and the alias
        // is identical across it. Comparing aliases would call these the same model.
        string digest = RetrospectiveTranscript.Render(
            [Step(0, "worker", "worker", "org/Qwen3.8-27B", runId: "run-1"),
             Step(0, "worker", "worker", "org/Llama-70B", runId: "run-2")],
            new RetrospectiveRequest("run-2") { StopReason = "Completed", Steps = 1 });

        digest.ShouldContain("More than one model answered in this session.");
    }

    [Fact]
    public void A_step_heading_names_the_weights_rather_than_the_alias()
    {
        string digest = Render(
            [Step(0, "worker", "worker", "org/Qwen3.8-27B"), Step(1, "worker", "worker", "org/Llama-70B")]);

        digest.ShouldContain("#### Step 0 · worker · org/Qwen3.8-27B · continued");
        digest.ShouldContain("#### Step 1 · worker · org/Llama-70B · continued");
    }

    [Fact]
    public void Two_roles_on_two_models_are_both_named()
    {
        // The case the feature exists for: a local worker judged by a hosted critic. Reading a
        // critique in the digest without knowing which model cast it is reading an unattributed
        // opinion.
        string digest = Render([Step(0, "worker", "qwen3-27b"), Step(1, "critic", "claude-opus-5")]);

        digest.ShouldContain("worker: qwen3-27b");
        digest.ShouldContain("critic: claude-opus-5");
    }

    [Fact]
    public void One_role_is_named_once_however_many_steps_it_took()
    {
        // The renderer's standing rule against re-announcement. Forty steps of the same name is
        // forty lines that say nothing, in a digest already fighting a character budget.
        string digest = Render(
            [Step(0, "worker", "qwen3-27b"), Step(1, "worker", "qwen3-27b"), Step(2, "worker", "qwen3-27b")]);

        digest.Split("qwen3-27b").Length.ShouldBe(2);
        digest.ShouldContain("#### Step 0 · worker · continued");
    }

    [Fact]
    public void A_run_that_changed_model_names_it_on_every_step()
    {
        // The one case where the step heading has to carry it: with two models in one run, the
        // run header says both and the steps are otherwise indistinguishable, so a claim about
        // step 1 could not be attributed at all.
        string digest = Render([Step(0, "worker", "qwen3-27b"), Step(1, "worker", "llama-70b")]);

        digest.ShouldContain("#### Step 0 · worker · qwen3-27b · continued");
        digest.ShouldContain("#### Step 1 · worker · llama-70b · continued");
    }

    [Fact]
    public void A_server_that_reported_no_model_says_so_rather_than_being_dropped()
    {
        // Absent reads as unknown and never as none. Dropping the role here would report a run
        // that had no model, which is a stronger and false claim.
        string digest = Render([Step(0, "worker", null)]);

        digest.ShouldContain("worker (the server reported no model id)");
    }

    [Fact]
    public void A_session_that_changed_model_between_runs_is_flagged_at_the_top()
    {
        // Two runs, two models, one session. Without this line the process stage reads a
        // difference between run 1 and run 2 as evidence about the harness.
        string digest = RetrospectiveTranscript.Render(
            [Step(0, "worker", "qwen3-27b", runId: "run-1"), Step(0, "worker", "llama-70b", runId: "run-2")],
            new RetrospectiveRequest("run-2") { StopReason = "Completed", Steps = 1 });

        digest.ShouldContain("More than one model answered in this session.");
        digest.ShouldContain("could be the model rather than the harness");
    }

    [Fact]
    public void A_session_on_one_model_is_not_flagged()
    {
        string digest = RetrospectiveTranscript.Render(
            [Step(0, "worker", "qwen3-27b", runId: "run-1"), Step(0, "worker", "qwen3-27b", runId: "run-2")],
            new RetrospectiveRequest("run-2") { StopReason = "Completed", Steps = 1 });

        digest.ShouldNotContain("More than one model answered");
    }

    [Fact]
    public void The_models_of_one_run_are_not_the_models_of_its_neighbour()
    {
        IReadOnlyList<ModelInUse> models = RetrospectiveTranscript.ModelsInUse(
            [Step(0, "worker", "qwen3-27b", runId: "run-1"), Step(0, "worker", "llama-70b", runId: "run-2")],
            "run-2");

        models.ShouldHaveSingleItem().ModelId.ShouldBe("llama-70b");
    }

    [Fact]
    public void One_model_serving_two_roles_is_two_facts()
    {
        // Aiming worker and critic at one server is the thing the endpoint picker exists to make
        // easy, and a critic on the same weights as the worker it judges is worth seeing.
        IReadOnlyList<ModelInUse> models = RetrospectiveTranscript.ModelsInUse(
            [Step(0, "worker", "qwen3-27b"), Step(1, "critic", "qwen3-27b")]);

        models.Count.ShouldBe(2);
        models.Select(m => m.Role).ShouldBe(["worker", "critic"]);
    }

    [Fact]
    public async Task A_stage_file_records_the_models_that_produced_the_run()
    {
        using TempWorkspace workspace = new();

        Retrospective written =
            await Reviewer(workspace, [Step(0, "worker", "qwen3-27b"), Step(1, "critic", "claude-opus-5")])
                .ReviewAsync(new RetrospectiveRequest("run-1") { StopReason = "Completed", Steps = 2 });

        string code = File.ReadAllText(StageFile(written, "1-code"));

        // Named apart from `model`, which is the reviewer's own. The two were one word away from
        // being read as each other.
        code.ShouldContain("runModels: worker=qwen3-27b; critic=claude-opus-5");
        code.ShouldContain("model: claude-opus-5");
    }

    [Fact]
    public async Task A_reopened_retrospective_still_knows_which_models_produced_the_run()
    {
        // The whole point of writing it into the front matter. A folder is the only thing that
        // outlives the session, and a report that has to be believed on its own has to carry it.
        using TempWorkspace workspace = new();

        Retrospective written = await Reviewer(workspace, [Step(0, "worker", "qwen3-27b")])
            .ReviewAsync(new RetrospectiveRequest("run-1") { StopReason = "Completed", Steps = 1 });

        SavedRetrospective reopened = Reviewer(workspace, [])
            .LoadFrom(written.Directory!)
            .ShouldNotBeNull();

        reopened.Run.Models.ShouldBe([new ModelInUse("worker", "qwen3-27b")]);
    }

    [Fact]
    public async Task A_stage_file_carries_the_checkpoint_and_hands_it_back()
    {
        // The alias reproduces the run; the checkpoint distinguishes it. The front matter keeps
        // both, because a folder is the only thing that outlives the session.
        using TempWorkspace workspace = new();

        Retrospective written = await Reviewer(workspace, [Step(0, "worker", "worker", "org/Qwen3.8-27B")])
            .ReviewAsync(new RetrospectiveRequest("run-1") { StopReason = "Completed", Steps = 1 });

        File.ReadAllText(StageFile(written, "1-code"))
            .ShouldContain("runModels: worker=worker|org/Qwen3.8-27B");

        Reviewer(workspace, []).LoadFrom(written.Directory!).ShouldNotBeNull()
            .Run.Models.ShouldBe([new ModelInUse("worker", "worker", "org/Qwen3.8-27B")]);
    }

    [Fact]
    public async Task A_folder_written_before_this_was_carried_reads_as_unknown()
    {
        // Every field in that block is optional on purpose, and a retrospective somebody kept from
        // last month is still a retrospective. Unknown, never none.
        using TempWorkspace workspace = new();

        Retrospective written = await Reviewer(workspace, [Step(0, "worker", "qwen3-27b")])
            .ReviewAsync(new RetrospectiveRequest("run-1") { StopReason = "Completed", Steps = 1 });

        string path = StageFile(written, "1-code");
        File.WriteAllLines(
            path, File.ReadAllLines(path).Where(line => !line.StartsWith("runModels:", StringComparison.Ordinal)));

        Reviewer(workspace, []).LoadFrom(written.Directory!).ShouldNotBeNull().Run.Models.ShouldBeEmpty();
    }

    [Fact]
    public async Task Every_stage_is_told_which_models_produced_the_run()
    {
        // All three, because all three can misattribute. The code stage judges what the model
        // wrote, the process stage judges how it worked, and the harness stage turns both into
        // proposals about GlassCoder - which is the one that most needs to know it was a model.
        using TempWorkspace workspace = new();
        FakeProcessRunner runner = Probed().Enqueue(0, Report()).Enqueue(0, Report()).Enqueue(0, Recommendations());

        await Reviewer(workspace, [Step(0, "worker", "qwen3-27b")], runner)
            .ReviewAsync(new RetrospectiveRequest("run-1") { StopReason = "Completed", Steps = 1 });

        // The version probe is the first invocation; the three stages follow it.
        runner.Requests.Count.ShouldBe(4);
        foreach (ProcessRunRequest stage in runner.Requests.Skip(1))
        {
            stage.StandardInput.ShouldNotBeNull().ShouldContain("worker: qwen3-27b");
        }
    }

    [Fact]
    public async Task A_run_with_no_recorded_model_tells_the_stages_not_to_conclude_one()
    {
        using TempWorkspace workspace = new();
        FakeProcessRunner runner = Probed().Enqueue(0, Report()).Enqueue(0, Report()).Enqueue(0, Recommendations());

        await Reviewer(workspace, [], runner)
            .ReviewAsync(new RetrospectiveRequest("run-1") { StopReason = "Completed", Steps = 0 });

        runner.Requests.Skip(1).First().StandardInput
            .ShouldNotBeNull()
            .ShouldContain("treat any conclusion about the model itself as unfounded");
    }

    private static string Render(IReadOnlyList<StepRecord> steps) =>
        RetrospectiveTranscript.Render(
            steps, new RetrospectiveRequest("run-1") { StopReason = "Completed", Steps = steps.Count });

    private static StepRecord Step(
        int index, string role, string? modelId, string? checkpoint = null, string runId = "run-1") => new()
    {
        RunId = runId,
        TaskId = "desktop",
        StepIndex = index,
        Role = role,
        ModelId = modelId,
        ModelCheckpoint = checkpoint,
        StartedAt = DateTimeOffset.UnixEpoch,
        Prompt = [],
        ToolCalls = [],
        ModelLatencyMs = 1,
        StepLatencyMs = 1,
        Outcome = "continued",
    };

    /// <summary>One stage's file, in the folder the retrospective says it wrote.</summary>
    private static string StageFile(Retrospective written, string stage) =>
        Path.Combine(written.Directory.ShouldNotBeNull(), $"{stage}.md");

    private static ClaudeCodeRetrospectiveReviewer Reviewer(
        TempWorkspace workspace, IReadOnlyList<StepRecord> steps, FakeProcessRunner? runner = null) =>
        new(runner ?? Probed().Enqueue(0, Report()).Enqueue(0, Report()).Enqueue(0, Recommendations()),
            workspace.Guard(),
            new ChangeLog(),
            TempWorkspace.Wrap(new RetrospectiveOptions { Enabled = true, HarnessRepoPath = string.Empty }),
            logger: null,
            new StepsOnly(steps),
            steps: null,
            timeProvider: null);

    private static FakeProcessRunner Probed() =>
        new FakeProcessRunner().Enqueue(0, JsonSerializer.Serialize(new { version = "1.0.0" }));

    private static string Report() =>
        JsonSerializer.Serialize(new
        {
            type = "result",
            subtype = "success",
            total_cost_usd = 0.5,
            duration_ms = 10,
            session_id = "s",
            modelUsage = new { },
            structured_output = new { report = "Nothing of note." },
        });

    private static string Recommendations() =>
        JsonSerializer.Serialize(new
        {
            type = "result",
            subtype = "success",
            total_cost_usd = 0.5,
            duration_ms = 10,
            session_id = "s",
            structured_output = new
            {
                report = "Nothing of note.",
                recommendations = Array.Empty<object>(),
            },
        });

    /// <summary>A bus that is only ever asked for its steps, which is all the reviewer reads.</summary>
    private sealed class StepsOnly : ITranscriptBus
    {
        public StepsOnly(IReadOnlyList<StepRecord> steps) => Steps = steps;

        public IReadOnlyList<StepRecord> Steps { get; }

        public IReadOnlyList<ReviewRecord> Reviews => [];

        public IReadOnlyList<RunRecord> Runs => [];

        public event EventHandler<StepRecord>? StepRecorded { add { } remove { } }

        public event EventHandler<RunRecord>? RunRecorded { add { } remove { } }

        public event EventHandler<ReviewRecord>? ReviewRecorded { add { } remove { } }

        public int NextStepIndex(string runId) => Steps.Count;

        public void Clear()
        {
        }
    }
}
