using System.ComponentModel;
using GlassCoder.Core.Agent;
using GlassCoder.Core.Diagnostics;
using GlassCoder.Core.Verification;
using GlassCoder.Models.Configuration;
using GlassCoder.TestSupport;
using GlassCoder.Tools;
using GlassCoder.Tools.Build;
using GlassCoder.Tools.Changes;
using GlassCoder.Tools.Execution;
using GlassCoder.Tools.FileSystem;
using GlassCoder.Tools.Guardrails;
using GlassCoder.Tools.Registry;
using GlassCoder.Tools.Verification;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace GlassCoder.Core.Tests;

/// <summary>
/// The loop climbs the verification ladder after every step that applied a change (workplan
/// task 36). The properties that matter: a read-only step never climbs, a failed climb reaches
/// the model as an observation rather than killing the run, the outcome is tied to the change
/// that produced it, and the critique rung's spend is billed at the critic's own prices. The
/// critique panel itself no longer rides the per-step ladder: it judges the completion claim,
/// once, and its refutation reaches the model as capped advice (run 4b582162).
/// </summary>
public sealed class AgentLoopVerificationTests
{
    [Fact]
    public async Task A_step_that_applies_a_change_climbs_the_ladder()
    {
        Harness harness = new(
            FakeChatClient.ToolCall("mutate", new Dictionary<string, object?> { ["content"] = "public class C { }" }),
            FakeChatClient.Text("done"));

        AgentRunResult result = await harness.RunAsync();

        result.StopReason.ShouldBe(AgentStopReason.Completed);
        VerificationRequest request = harness.Ladder.Requests.ShouldHaveSingleItem();
        request.FilePath.ShouldBe("src/C.cs");
        request.FileText.ShouldBe("public class C { }");
        request.Goal.ShouldBe("Do the thing.");
        request.CriticRole.ShouldBe("critic-remote");
        request.ChangeDescription.ShouldBeNull(
            "per-step climbs offer nothing to refute - the panel judges the completion claim instead");

        // The clean bill is an observation too - otherwise the model spends its next step
        // calling build to learn what the harness already knows.
        harness.Client.Requests[1].Messages
            .ShouldContain(m => m.Role == ChatRole.User && m.Text != null && m.Text.Contains("passed"));
    }

    [Fact]
    public async Task A_read_only_step_never_climbs()
    {
        Harness harness = new(FakeChatClient.ToolCall("echo"), FakeChatClient.Text("done"));

        await harness.RunAsync();

        harness.Ladder.Requests.ShouldBeEmpty();
        harness.StepLogger.Steps[0].Verification.ShouldBeNull();
    }

    [Fact]
    public async Task A_failed_climb_is_fed_back_to_the_model_and_the_run_carries_on()
    {
        Harness harness = new(
            FakeChatClient.ToolCall("mutate"),
            FakeChatClient.Text("done"));
        harness.Ladder.Enqueue(FailedReport());

        AgentRunResult result = await harness.RunAsync();

        // The failure policy is correction, not abortion: the model gets the summary and the
        // loop keeps going.
        result.StopReason.ShouldBe(AgentStopReason.Completed);
        harness.Client.Requests[1].Messages
            .ShouldContain(m => m.Role == ChatRole.User && m.Text != null &&
                m.Text.Contains("FAILED at Compile") && m.Text.Contains("CS0103"));
    }

    /// <summary>
    /// Run d18c0e57 said "Completed" over eleven failed builds and the next run inherited the
    /// wreckage. The loop now challenges the first stop over a red tree; a model that insists
    /// is let through, but the run record says what actually happened.
    /// </summary>
    [Fact]
    public async Task A_stop_over_a_red_tree_is_challenged_once_then_recorded()
    {
        Harness harness = new(
            FakeChatClient.ToolCall("mutate"),
            FakeChatClient.Text("done"),
            FakeChatClient.Text("done anyway"));
        harness.Ladder.Enqueue(FailedReport());

        AgentRunResult result = await harness.RunAsync();

        result.StopReason.ShouldBe(AgentStopReason.Completed);
        harness.Client.Requests.Count.ShouldBe(3, "the first stop must be challenged, not accepted");
        harness.Client.Requests[2].Messages
            .ShouldContain(m => m.Role == ChatRole.User && m.Text != null && m.Text.Contains("Do not stop yet"));
        result.FinalText.ShouldBe("done anyway");
        result.Error.ShouldNotBeNull();
        result.Error.ShouldContain("verification");
    }

    [Fact]
    public async Task A_stop_over_a_green_tree_is_not_challenged()
    {
        Harness harness = new(FakeChatClient.ToolCall("mutate"), FakeChatClient.Text("done"));
        harness.Ladder.Enqueue(PassedReport());

        AgentRunResult result = await harness.RunAsync();

        result.StopReason.ShouldBe(AgentStopReason.Completed);
        result.Error.ShouldBeNull();
        result.FinalText.ShouldBe("done");
        harness.Client.Requests.Count.ShouldBe(2);
    }

    /// <summary>
    /// Run 21f25fea cycled read-only calls for twenty-five steps of byte-identical answers at
    /// 100% validity - the failure loop-breaker never armed because nothing failed. The
    /// success-side twin: a step counts as stalled only when every successful call in it
    /// repeats an earlier one; three such steps earn a nudge, five end the run.
    /// </summary>
    [Fact]
    public async Task A_run_spinning_on_identical_successful_calls_is_nudged_then_stopped()
    {
        Harness harness = new(
            FakeChatClient.ToolCall("echo"),
            FakeChatClient.ToolCall("echo", callId: "c2"),
            FakeChatClient.ToolCall("echo", callId: "c3"),
            FakeChatClient.ToolCall("echo", callId: "c4"),
            FakeChatClient.ToolCall("echo", callId: "c5"),
            FakeChatClient.ToolCall("echo", callId: "c6"),
            FakeChatClient.ToolCall("echo", callId: "c7"));

        AgentRunResult result = await harness.RunAsync();

        result.StopReason.ShouldBe(AgentStopReason.Stalled);
        result.Error.ShouldNotBeNull();
        result.Error.ShouldContain("identical answer");
        result.Steps.ShouldBe(6, "the first call is novel; the five verbatim repeats after it hit the default limit");

        // The nudge landed after the third stalled step, in the window the model saw next.
        harness.Client.Requests[4].Messages
            .ShouldContain(m => m.Role == ChatRole.User && m.Text != null && m.Text.Contains("cannot add information"));
    }

    /// <summary>
    /// The false positive the per-step rule exists to avoid: re-reading after compaction and
    /// check-then-act rhythms interleave verbatim repeats with novel work. The bare echo here
    /// repeats six times - a cumulative per-call count would have stopped the run - but every
    /// repeat is followed by a step that learns something new, so the stall count keeps
    /// resetting and the run ends at its own pace.
    /// </summary>
    [Fact]
    public async Task A_repeated_call_interleaved_with_novel_work_is_not_a_stall()
    {
        Harness harness = new(
            FakeChatClient.ToolCall("echo"),
            FakeChatClient.ToolCall("echo", callId: "c2"),
            FakeChatClient.ToolCall("echo", new Dictionary<string, object?> { ["text"] = "b" }, "c3"),
            FakeChatClient.ToolCall("echo", callId: "c4"),
            FakeChatClient.ToolCall("echo", new Dictionary<string, object?> { ["text"] = "c" }, "c5"),
            FakeChatClient.ToolCall("echo", callId: "c6"),
            FakeChatClient.ToolCall("echo", new Dictionary<string, object?> { ["text"] = "d" }, "c7"),
            FakeChatClient.ToolCall("echo", callId: "c8"),
            FakeChatClient.ToolCall("echo", new Dictionary<string, object?> { ["text"] = "e" }, "c9"),
            FakeChatClient.ToolCall("echo", callId: "c10"),
            FakeChatClient.Text("done"));

        AgentRunResult result = await harness.RunAsync();

        result.StopReason.ShouldBe(AgentStopReason.Completed);
        result.Steps.ShouldBe(11);
    }

    [Fact]
    public async Task A_repeated_call_that_applies_changes_is_progress_not_a_stall()
    {
        // Every mutate lands a change, so the repeat counter resets each step and the run
        // completes normally - re-inspecting a workspace you just changed is legitimate.
        Harness harness = new(
            FakeChatClient.ToolCall("mutate"),
            FakeChatClient.ToolCall("mutate", callId: "c2"),
            FakeChatClient.ToolCall("mutate", callId: "c3"),
            FakeChatClient.ToolCall("mutate", callId: "c4"),
            FakeChatClient.ToolCall("mutate", callId: "c5"),
            FakeChatClient.Text("done"));

        AgentRunResult result = await harness.RunAsync();

        result.StopReason.ShouldBe(AgentStopReason.Completed);
    }

    /// <summary>
    /// Run d21eb210 deleted the only copy of its deliverable; when the build then missed the
    /// file, it removed the reference instead of restoring the file, and the goal quietly went
    /// with it. A failure right after a deletion names the recovery that keeps the work.
    /// </summary>
    [Fact]
    public async Task A_failed_climb_after_a_deletion_names_the_restore_path()
    {
        Harness harness = new(
            FakeChatClient.ToolCall("remove"),
            FakeChatClient.Text("done"),
            FakeChatClient.Text("done anyway"));
        harness.Ladder.Enqueue(FailedReport());

        await harness.RunAsync();

        harness.Client.Requests[1].Messages
            .ShouldContain(m => m.Role == ChatRole.User && m.Text != null && m.Text.Contains("restore the file"));
    }

    [Fact]
    public async Task A_failed_climb_after_an_ordinary_edit_does_not_mention_restoring()
    {
        Harness harness = new(
            FakeChatClient.ToolCall("mutate"),
            FakeChatClient.Text("done"),
            FakeChatClient.Text("done anyway"));
        harness.Ladder.Enqueue(FailedReport());

        await harness.RunAsync();

        harness.Client.Requests[1].Messages
            .ShouldContain(m => m.Role == ChatRole.User && m.Text != null && m.Text.Contains("FAILED at Compile"));
        harness.Client.Requests[1].Messages
            .ShouldNotContain(m => m.Text != null && m.Text.Contains("restore the file"));
    }

    [Fact]
    public async Task The_outcome_is_tied_to_the_change_that_produced_it()
    {
        Harness harness = new(FakeChatClient.ToolCall("mutate"), FakeChatClient.Text("done"));
        harness.Ladder.Enqueue(FailedReport());

        await harness.RunAsync();

        CodeChange change = harness.Changes.All().ShouldHaveSingleItem();
        change.Status.ShouldBe(ChangeStatus.Applied);
        change.VerificationSummary.ShouldNotBeNull();
        change.VerificationSummary.ShouldContain("CS0103");
    }

    [Fact]
    public async Task The_climb_lands_in_the_step_record()
    {
        Harness harness = new(FakeChatClient.ToolCall("mutate"), FakeChatClient.Text("done"));
        harness.Ladder.Enqueue(FailedReport());

        await harness.RunAsync();

        StepVerificationRecord verification = harness.StepLogger.Steps[0].Verification.ShouldNotBeNull();
        verification.Passed.ShouldBeFalse();
        verification.FailedRung.ShouldBe(nameof(VerificationRung.Compile));
        verification.Summary.ShouldContain("CS0103");
        harness.StepLogger.Steps[1].Verification.ShouldBeNull();
    }

    [Fact]
    public async Task Critique_spend_is_billed_at_the_critic_roles_prices()
    {
        // The worker role prices at zero here, so any cost on the result is the critic's -
        // the second price table RunBudget owed whoever wired rung 6 into the loop.
        Harness harness = new(FakeChatClient.ToolCall("mutate"), FakeChatClient.Text("done"));
        harness.Ladder.Enqueue(CritiquedReport(0.42m));

        AgentRunResult result = await harness.RunAsync();

        result.EstimatedCostUsd.ShouldBe(0.42m);
        harness.StepLogger.Steps[0].Verification.ShouldNotBeNull().CritiqueCostUsd.ShouldBe(0.42m);
    }

    [Fact]
    public async Task Ladder_outcomes_move_the_recovery_metrics()
    {
        // Break, then fix: the round trip is exactly what recovery rate counts, and before
        // task 36 it could only be counted when the model called build itself.
        Harness harness = new(
            FakeChatClient.ToolCall("mutate"),
            FakeChatClient.ToolCall("mutate", callId: "call-2"),
            FakeChatClient.Text("done"));
        harness.Ladder.Enqueue(FailedReport());
        harness.Ladder.Enqueue(PassedReport());

        await harness.RunAsync();

        Metrics.RunMetrics metrics = harness.Metrics.Last.ShouldNotBeNull();
        metrics.Builds.ShouldBe(2);
        metrics.BuildFailures.ShouldBe(1);
        metrics.RecoveryOpportunities.ShouldBe(1);
        metrics.Recoveries.ShouldBe(1);
        metrics.TestRuns.ShouldBe(1);
    }

    [Fact]
    public async Task A_climb_where_every_rung_skipped_says_nothing()
    {
        // No sandbox and not a C# file: silence is more honest than a hollow "verified".
        Harness harness = new(FakeChatClient.ToolCall("mutate"), FakeChatClient.Text("done"));
        harness.Ladder.Enqueue(SkippedReport());

        await harness.RunAsync();

        harness.Ladder.Requests.ShouldHaveSingleItem();
        harness.StepLogger.Steps[0].Verification.ShouldBeNull();
        harness.Client.Requests[1].Messages.Count(m => m.Role == ChatRole.User).ShouldBe(1, "only the goal");
    }

    [Fact]
    public async Task Verification_can_be_switched_off()
    {
        Harness harness = new(
            new VerificationLadderOptions { VerifyAppliedChanges = false },
            FakeChatClient.ToolCall("mutate"),
            FakeChatClient.Text("done"));

        await harness.RunAsync();

        harness.Ladder.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_broken_ladder_does_not_break_the_run()
    {
        // The harness failing to verify is not the model failing to code.
        Harness harness = new(FakeChatClient.ToolCall("mutate"), FakeChatClient.Text("done"))
        {
            LadderOverride = new ThrowingLadder(),
        };

        AgentRunResult result = await harness.RunAsync();

        result.StopReason.ShouldBe(AgentStopReason.Completed);
        harness.StepLogger.Steps[0].Verification.ShouldBeNull();
    }

    [Fact]
    public async Task A_real_edit_climbs_the_real_ladder()
    {
        // End to end with the real pieces: EditFileTool applies a change the in-memory check
        // accepts, and the real ladder then catches what only a full build can see.
        using TempWorkspace workspace = new();
        workspace.WriteFile("src/Proj.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");
        workspace.WriteFile("src/Pager.cs", "public class Pager { public int X => 1; }");

        ScriptedCommandExecutor executor = new();
        executor.Enqueue(1, "C:\\repo\\src\\Pager.cs(1,1): error CS0103: broken [C:\\repo\\src\\Proj.csproj]");

        PathGuard guard = workspace.Guard("src");
        IOptions<VerificationOptions> verification = Options.Create(new VerificationOptions());
        IOptions<SandboxOptions> sandbox = Options.Create(new SandboxOptions());
        RoslynCodeAnalyzer analyzer = new(guard, verification);
        DiagnosticSummarizer summarizer = new(verification);
        ChangeLog changes = new();

        FakeChatClient client = new(
            FakeChatClient.ToolCall("edit_file", new Dictionary<string, object?>
            {
                ["edits"] = new[]
                {
                    new Dictionary<string, object?>
                    {
                        ["path"] = "src/Pager.cs",
                        ["oldText"] = "int X => 1",
                        ["newText"] = "int X => 2",
                    },
                },
            }),
            FakeChatClient.Text("done"));

        VerificationLadder ladder = new(
            analyzer,
            summarizer,
            new BuildTool(executor, guard, summarizer, sandbox),
            new RunTestsTool(executor, guard, sandbox),
            new CriticPanel(
                new FakeChatClientFactory(client, new ModelRoleOptions { Endpoint = "http://localhost/v1", ModelAlias = "worker" }),
                Options.Create(new CritiqueOptions())),
            guard,
            Options.Create(new VerificationLadderOptions()));

        RecordingStepLogger stepLogger = new();
        RecordingMetricsRecorder metrics = new();
        AgentLoop loop = new(
            new FakeChatClientFactory(client, new ModelRoleOptions { Endpoint = "http://localhost/v1", ModelAlias = "worker" }),
            new ToolRegistry([new EditFileTool(guard, analyzer, summarizer, verification, changes)]),
            stepLogger,
            TestContextAssembler.Create(),
            metrics,
            Options.Create(new AgentOptions()),
            verifier: ladder,
            changes: changes,
            verificationOptions: Options.Create(new VerificationLadderOptions()));

        AgentRunResult result = await loop.RunAsync(
            new AgentRunRequest { TaskId = "task-1", Goal = "Bump X." });

        result.StopReason.ShouldBe(AgentStopReason.Completed);

        // The climb happened: syntax in memory, then the scripted build, and no test after
        // the red build.
        executor.Commands.ShouldHaveSingleItem().Arguments[0].ShouldBe("build");
        StepVerificationRecord climbed = stepLogger.Steps[0].Verification.ShouldNotBeNull();
        climbed.Passed.ShouldBeFalse();
        climbed.FailedRung.ShouldBe(nameof(VerificationRung.Compile));

        // The model heard about it, the change log carries it, and the metrics counted it.
        client.Requests[1].Messages
            .ShouldContain(m => m.Role == ChatRole.User && m.Text != null && m.Text.Contains("CS0103"));
        changes.All().ShouldHaveSingleItem().VerificationSummary.ShouldNotBeNull();
        Metrics.RunMetrics run = metrics.Last.ShouldNotBeNull();
        run.Edits.ShouldBe(1);
        run.Builds.ShouldBe(1);
        run.BuildFailures.ShouldBe(1);
        run.EditsWithCompileErrors.ShouldBe(1);
        run.RecoveryOpportunities.ShouldBe(1);
    }

    // ── The completion critique ──
    //
    // Run 4b582162: the panel sat on the ladder, judged every step's diff against the whole run
    // goal, and refuted 14 of 14 changes - including the correct ones - until the user cancelled
    // the run. The panel now speaks at most twice, at completion claims: once on the claim, and
    // once on the recovery - because run f4ed50e0 answered a refutation with UI-test packages,
    // wrote no test that used them, and completed on the spent critique unexamined. The second
    // verdict is final either way; a bounded panel can never become 4b582162's loop.

    [Fact]
    public async Task A_refuted_completion_claim_is_rejudged_once_then_finishes()
    {
        Harness harness = new(
            FakeChatClient.ToolCall("mutate"),
            FakeChatClient.Text("done"),
            FakeChatClient.Text("done for real"))
        {
            Critics = new FakeCriticPanel
            {
                Next = new CritiqueResult(
                    true, [], 3, $"3/3 critics refuted the change: {new string('x', 2000)}")
                {
                    RespondingVotes = 3,
                },
            },
        };

        AgentRunResult result = await harness.RunAsync();

        result.StopReason.ShouldBe(AgentStopReason.Completed);
        result.FinalText.ShouldBe("done for real");
        result.Error.ShouldBeNull("advisory mode invited finish-as-is, so finishing as-is is no caveat");

        // Two panels, no more: the claim and the recovery each get judged; a third claim would
        // complete without another round of critics.
        harness.Critics.Requests.Count.ShouldBe(2);

        // The refutation reached the model marked as advice, capped rather than verbatim.
        string advisory = harness.Client.Requests[2].Messages
            .Last(m => m.Role == ChatRole.User).Text.ShouldNotBeNull();
        advisory.ShouldContain("Advisory review");
        advisory.ShouldContain("finish as-is if you disagree");
        advisory.ShouldContain("not package references alone", customMessage: "run f4ed50e0's package theater is named in the recovery instruction");
        advisory.ShouldContain("[...]", customMessage: "two thousand characters of critic prose must not reach the worker");

        // The full verdict is in the transcript, on the step that was challenged.
        StepVerificationRecord record = harness.StepLogger.Steps[1].Verification.ShouldNotBeNull();
        record.Passed.ShouldBeTrue("critique does not gate");
        record.HighestRungReached.ShouldBe(nameof(VerificationRung.Critique));
        record.Summary.ShouldContain("3/3 critics refuted");
    }

    [Fact]
    public async Task A_second_refutation_under_a_gating_critique_completes_with_a_caveat()
    {
        // The bounded alternative to arguing forever: the run ends, and the record says the
        // panel was never convinced.
        Harness harness = new(
            new VerificationLadderOptions { CritiqueGates = true },
            FakeChatClient.ToolCall("mutate"),
            FakeChatClient.Text("done"),
            FakeChatClient.Text("done for real"))
        {
            Critics = new FakeCriticPanel
            {
                Next = new CritiqueResult(true, [], 3, "3/3 critics refuted the change: no tests exercise the UI.")
                {
                    RespondingVotes = 3,
                },
            },
        };

        AgentRunResult result = await harness.RunAsync();

        result.StopReason.ShouldBe(AgentStopReason.Completed);
        result.FinalText.ShouldBe("done for real");
        result.Error.ShouldNotBeNull();
        result.Error.ShouldContain("second critique refutation");
        harness.Critics.Requests.Count.ShouldBe(2);
    }

    [Fact]
    public async Task A_recovery_that_convinces_the_second_panel_completes_clean()
    {
        Harness harness = new(
            new VerificationLadderOptions { CritiqueGates = true },
            FakeChatClient.ToolCall("mutate"),
            FakeChatClient.Text("done"),
            FakeChatClient.Text("done, with the evidence added"))
        {
            Critics = new FakeCriticPanel(),
        };
        harness.Critics.Sequence.Enqueue(
            new CritiqueResult(true, [], 3, "3/3 critics refuted the change: thin evidence.")
            {
                RespondingVotes = 3,
            });
        harness.Critics.Sequence.Enqueue(
            new CritiqueResult(false, [], 0, "3/3 critics accepted the change.")
            {
                RespondingVotes = 3,
            });

        AgentRunResult result = await harness.RunAsync();

        result.StopReason.ShouldBe(AgentStopReason.Completed);
        result.Error.ShouldBeNull("a recovery the panel accepted needs no caveat");
        harness.Critics.Requests.Count.ShouldBe(2);
    }

    [Fact]
    public async Task The_critics_judge_the_claim_against_the_last_ladder_evidence()
    {
        Harness harness = new(FakeChatClient.ToolCall("mutate"), FakeChatClient.Text("done"))
        {
            Critics = new FakeCriticPanel(),
        };
        harness.Ladder.Enqueue(PassedReport());

        AgentRunResult result = await harness.RunAsync();

        result.StopReason.ShouldBe(AgentStopReason.Completed);
        result.FinalText.ShouldBe("done");

        (string goal, string change, string evidence, string? role) = harness.Critics.Requests.ShouldHaveSingleItem();
        goal.ShouldBe("Do the thing.");
        change.ShouldContain("src/C.cs", customMessage: "the panel judges the run's diffs, not a paraphrase");
        evidence.ShouldContain("3 tests passed.", customMessage: "the ladder's last word is the evidence");
        evidence.ShouldContain("done", customMessage: "the claim under judgment is the agent's own summary");
        role.ShouldBe("critic-remote");

        // Accepted: no extra message, no extra step - but the verdict lands in the step record,
        // vote by vote, with the lens each critic judged through.
        harness.Client.Requests.Count.ShouldBe(2);
        StepVerificationRecord record = harness.StepLogger.Steps[1].Verification.ShouldNotBeNull();
        record.Summary.ShouldContain("accepted");
        StepCritiqueRecord votes = record.Critique.ShouldNotBeNull();
        votes.CriticRole.ShouldBe("critic-remote");
        ReviewVoteRecord vote = votes.Votes.ShouldHaveSingleItem();
        vote.Lens.ShouldBe("evidence");
        vote.Reason.ShouldBe("The tests prove the claim.");
    }

    [Fact]
    public async Task A_run_that_changed_nothing_makes_no_refutable_claim()
    {
        Harness harness = new(FakeChatClient.ToolCall("echo"), FakeChatClient.Text("the answer is 4"))
        {
            Critics = new FakeCriticPanel(),
        };

        AgentRunResult result = await harness.RunAsync();

        result.StopReason.ShouldBe(AgentStopReason.Completed);
        harness.Critics.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_broken_critic_panel_does_not_block_completion()
    {
        Harness harness = new(FakeChatClient.ToolCall("mutate"), FakeChatClient.Text("done"))
        {
            CriticsOverride = new ThrowingCriticPanel(),
        };

        AgentRunResult result = await harness.RunAsync();

        result.StopReason.ShouldBe(AgentStopReason.Completed);
        result.FinalText.ShouldBe("done");
    }

    [Fact]
    public async Task Completion_critique_spend_is_billed_at_the_critic_roles_prices()
    {
        Harness harness = new(FakeChatClient.ToolCall("mutate"), FakeChatClient.Text("done"))
        {
            Critics = new FakeCriticPanel
            {
                Next = new CritiqueResult(false, [], 0, "3/3 critics accepted the change.")
                {
                    RespondingVotes = 3,
                    EstimatedCostUsd = 0.42m,
                },
            },
        };

        AgentRunResult result = await harness.RunAsync();

        result.EstimatedCostUsd.ShouldBe(0.42m);
        harness.StepLogger.Steps[1].Verification.ShouldNotBeNull().CritiqueCostUsd.ShouldBe(0.42m);
    }

    private static VerificationReport PassedReport() => new(
        true,
        VerificationRung.UnitTests,
        null,
        [
            new RungResult(VerificationRung.Syntax, true, "Syntax ok.", 1),
            new RungResult(VerificationRung.Compile, true, "Build succeeded.", 1),
            new RungResult(VerificationRung.UnitTests, true, "3 tests passed.", 1),
        ],
        3);

    private static VerificationReport FailedReport() => new(
        false,
        VerificationRung.Compile,
        VerificationRung.Compile,
        [
            new RungResult(VerificationRung.Syntax, true, "Syntax ok.", 1),
            new RungResult(VerificationRung.Compile, false, "error CS0103: broken", 1),
        ],
        2);

    private static VerificationReport CritiquedReport(decimal costUsd) => new(
        true,
        VerificationRung.Critique,
        null,
        [
            new RungResult(VerificationRung.Compile, true, "Build succeeded.", 1),
            new RungResult(VerificationRung.Critique, true, "3/3 critics accepted the change.", 1)
            {
                Critique = new CritiqueResult(false, [], 0, "accepted") { EstimatedCostUsd = costUsd },
            },
        ],
        2);

    private static VerificationReport SkippedReport() => new(
        true,
        VerificationRung.None,
        null,
        [
            new RungResult(VerificationRung.Syntax, true, "Not a C# file.", 0, Skipped: true),
            new RungResult(VerificationRung.Compile, true, "No sandbox.", 0, Skipped: true),
            new RungResult(VerificationRung.UnitTests, true, "No sandbox.", 0, Skipped: true),
        ],
        0);

    /// <summary>A ladder that replays scripted reports and records what it was asked to verify.</summary>
    private sealed class RecordingLadder : IVerificationLadder
    {
        private readonly Queue<VerificationReport> _scripted = new();

        public List<VerificationRequest> Requests { get; } = [];

        public void Enqueue(VerificationReport report) => _scripted.Enqueue(report);

        public Task<VerificationReport> VerifyAsync(
            VerificationRequest request,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return Task.FromResult(_scripted.Count > 0 ? _scripted.Dequeue() : PassedReport());
        }
    }

    private sealed class ThrowingLadder : IVerificationLadder
    {
        public Task<VerificationReport> VerifyAsync(
            VerificationRequest request,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("the sandbox exploded");
    }

    /// <summary>A panel that returns a scripted verdict and records what it was asked to judge.</summary>
    private sealed class FakeCriticPanel : ICriticPanel
    {
        public List<(string Goal, string Change, string Evidence, string? Role)> Requests { get; } = [];

        public CritiqueResult Next { get; set; } =
            new(
                false,
                [new CritiqueVerdict(false, 0.9, "The tests prove the claim.") { Lens = "evidence" }],
                0,
                "3/3 critics accepted the change.")
            {
                RespondingVotes = 3,
            };

        /// <summary>Verdicts to hand out in order before falling back to <see cref="Next"/>.</summary>
        public Queue<CritiqueResult> Sequence { get; } = new();

        public bool Enabled => true;

        public bool CanCritique(string? role) => true;

        public string ResolveRole(string? role) => role ?? "critic";

        public Task<CritiqueResult> CritiqueAsync(
            string goal,
            string change,
            string evidence,
            string? role = null,
            CancellationToken cancellationToken = default)
        {
            Requests.Add((goal, change, evidence, role));

            // The real panel stamps the role it ran on; the record downstream carries it.
            CritiqueResult verdict = Sequence.Count > 0 ? Sequence.Dequeue() : Next;
            return Task.FromResult(verdict with { Role = ResolveRole(role) });
        }
    }

    private sealed class ThrowingCriticPanel : ICriticPanel
    {
        public bool Enabled => true;

        public bool CanCritique(string? role) => true;

        public string ResolveRole(string? role) => role ?? "critic";

        public Task<CritiqueResult> CritiqueAsync(
            string goal,
            string change,
            string evidence,
            string? role = null,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("the critic endpoint is down");
    }

    /// <summary>Wires the loop over a scripted client, a change-writing tool and a scripted ladder.</summary>
    private sealed class Harness
    {
        private readonly VerificationLadderOptions _options;

        public Harness(params ChatResponse[] responses)
            : this(new VerificationLadderOptions(), responses)
        {
        }

        public Harness(VerificationLadderOptions options, params ChatResponse[] responses)
        {
            _options = options;
            Client = new FakeChatClient(responses);
        }

        public FakeChatClient Client { get; }

        public RecordingLadder Ladder { get; } = new();

        public IVerificationLadder? LadderOverride { get; init; }

        // Null by default: most of these tests are about the ladder, and a panel that speaks
        // on every completion would entangle them with the critique boundary.
        public FakeCriticPanel? Critics { get; init; }

        public ICriticPanel? CriticsOverride { get; init; }

        public ChangeLog Changes { get; } = new();

        public RecordingStepLogger StepLogger { get; } = new();

        public RecordingMetricsRecorder Metrics { get; } = new();

        public Task<AgentRunResult> RunAsync(CancellationToken cancellationToken = default)
        {
            AgentLoop loop = new(
                new FakeChatClientFactory(Client, new ModelRoleOptions { Endpoint = "http://localhost/v1", ModelAlias = "worker" }),
                new ToolRegistry([new MutatingTools(Changes)]),
                StepLogger,
                TestContextAssembler.Create(),
                Metrics,
                Options.Create(new AgentOptions()),
                verifier: LadderOverride ?? Ladder,
                changes: Changes,
                verificationOptions: Options.Create(_options),
                critics: CriticsOverride ?? Critics);

            return loop.RunAsync(
                new AgentRunRequest { TaskId = "task-1", Goal = "Do the thing.", CriticRole = "critic-remote" },
                cancellationToken);
        }
    }

    private sealed class MutatingTools : IToolSet
    {
        private readonly IChangeLog _changes;

        public MutatingTools(IChangeLog changes) => _changes = changes;

        [GlassCoderTool("mutate", Order = 1)]
        [Description("Applies a change to the workspace, for tests.")]
        public ToolObservation<MutateData> Mutate(
            [Description("New content for the file.")] string content = "public class C { }")
        {
            CodeChange change = _changes.Propose("src/C.cs", "mutate", string.Empty, content);
            _changes.Update(change.Id, ChangeStatus.Applied);
            return Observation.Ok("mutate", new MutateData(change.Id), "applied");
        }

        [GlassCoderTool("echo", Order = 2)]
        [Description("Echoes text back, for tests.")]
        public ToolObservation<MutateData> Echo([Description("Text to echo back.")] string text = "hello") =>
            Observation.Ok("echo", new MutateData(text), "echoed");

        [GlassCoderTool("remove", Order = 3)]
        [Description("Deletes the test file, for tests.")]
        public ToolObservation<MutateData> Remove()
        {
            // Before-text to nothing is the shape the change log gives a deletion.
            CodeChange change = _changes.Propose("src/C.cs", "remove", "public class C { }", string.Empty);
            _changes.Update(change.Id, ChangeStatus.Applied);
            return Observation.Ok("remove", new MutateData(change.Id), "removed");
        }
    }

    public sealed record MutateData([property: Description("Identifier or echoed text.")] string Value);
}
