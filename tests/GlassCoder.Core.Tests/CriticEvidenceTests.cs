using System.ComponentModel;
using GlassCoder.Core.Agent;
using GlassCoder.Core.Verification;
using GlassCoder.Models.Configuration;
using GlassCoder.TestSupport;
using GlassCoder.Tools;
using GlassCoder.Tools.Build;
using GlassCoder.Tools.Changes;
using GlassCoder.Tools.Registry;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace GlassCoder.Core.Tests;

/// <summary>
/// Everything the harness collected for the completion panel reaches the completion panel.
/// <para>
/// The evidence string is assembled from three sources that grew one at a time - the verification
/// summary, what running the application showed, and what was asked for and refused - and losing
/// one has always been silent. Run <c>457867c7</c> is the instance: it demonstrated three
/// input/output pairs through the window and the panel that accepted it was handed the last one,
/// because the runtime evidence was a slot rather than a list.
/// </para>
/// <para>
/// This is the same guard as <c>RetrospectiveDigestTests</c>, one organ over: a distinct sentinel
/// per source, and the assertion is that all of them survive the assembly. The value is in the
/// fourth source, whenever it arrives, being covered by a test nobody remembered to write.
/// </para>
/// </summary>
public sealed class CriticEvidenceTests
{
    [Fact]
    public async Task Every_source_of_evidence_reaches_the_panel()
    {
        RuntimeEvidence runtime = new();
        AbandonedIntents intents = new();
        RecordingCritics critics = new();
        ChangeLog changes = new();

        // One rung summary, carrying its own sentinel.
        ScriptedLadder ladder = new(new VerificationReport(
            true,
            VerificationRung.UnitTests,
            null,
            [new RungResult(VerificationRung.UnitTests, true, "<<ladder-sentinel>> 8 tests passed.", 5)],
            5));

        AgentLoop loop = new(
            new FakeChatClientFactory(
                new FakeChatClient(
                    FakeChatClient.ToolCall("touch"),
                    FakeChatClient.ToolCall("refuse"),
                    FakeChatClient.Text("done")),
                new ModelRoleOptions { Endpoint = "http://localhost/v1", ModelAlias = "worker" }),
            new ToolRegistry([new EvidenceTools(changes, runtime)]),
            new RecordingStepLogger(),
            TestContextAssembler.Create(),
            new RecordingMetricsRecorder(),
            Options.Create(new AgentOptions()),
            verifier: ladder,
            changes: changes,
            verificationOptions: Options.Create(new VerificationLadderOptions()),
            critics: critics,
            runtime: runtime,
            intents: intents);

        await loop.RunAsync(new AgentRunRequest { TaskId = "task-1", Goal = "Do the thing.", CriticRole = "critic" });

        string evidence = critics.Evidence.ShouldNotBeNull();

        evidence.ShouldContain("<<ladder-sentinel>>", customMessage: "the verification summary");
        evidence.ShouldContain("<<runtime-sentinel>>", customMessage: "what running the application showed");
        evidence.ShouldContain("<<second-launch-sentinel>>", customMessage: "every launch, not only the last");
        evidence.ShouldContain("refuse", customMessage: "what was asked for, refused, and never retried");
    }

    [Fact]
    public async Task A_run_that_never_launched_what_it_built_says_so_to_the_panel()
    {
        // Run dbaa0580: accepted 3/3 at 19:22:27 citing build and tests; refuted 3/3 at 19:22:38
        // by the same critic role, every lens naming the missing launch. The prompt already treats
        // a missing launch as grounds to refute - the completion panel simply could not see an
        // absence, because an absence was the one thing the evidence never said.
        using TempWorkspace workspace = new();
        workspace.WriteFile("src/App/App.csproj",
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>WinExe</OutputType></PropertyGroup></Project>");

        RecordingCritics critics = await RunWithWorkspace(workspace, launches: false);

        critics.Evidence.ShouldNotBeNull().ShouldContain("never launched");
    }

    [Fact]
    public async Task A_run_that_launched_says_what_it_saw_instead()
    {
        using TempWorkspace workspace = new();
        workspace.WriteFile("src/App/App.csproj",
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>WinExe</OutputType></PropertyGroup></Project>");

        RecordingCritics critics = await RunWithWorkspace(workspace, launches: true);

        string evidence = critics.Evidence.ShouldNotBeNull();
        evidence.ShouldContain("<<runtime-sentinel>>");
        evidence.ShouldNotContain("never launched");
    }

    [Fact]
    public async Task A_workspace_with_nothing_runnable_is_not_asked_why_it_did_not_run_anything()
    {
        // A library has nothing to launch, and a critic told otherwise would be refusing work for
        // want of evidence nobody could produce - the deadlock this panel's wording exists to avoid.
        using TempWorkspace workspace = new();
        workspace.WriteFile("src/Lib/Lib.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");

        RecordingCritics critics = await RunWithWorkspace(workspace, launches: false);

        (critics.Evidence ?? string.Empty).ShouldNotContain("never launched");
    }

    /// <summary>Runs the loop over a real workspace root, with or without a launch in it.</summary>
    private static async Task<RecordingCritics> RunWithWorkspace(TempWorkspace workspace, bool launches)
    {
        RuntimeEvidence runtime = new();
        RecordingCritics critics = new();
        ChangeLog changes = new();

        AgentLoop loop = new(
            new FakeChatClientFactory(
                new FakeChatClient(
                    FakeChatClient.ToolCall(launches ? "touch" : "quiet_touch"),
                    FakeChatClient.Text("done")),
                new ModelRoleOptions { Endpoint = "http://localhost/v1", ModelAlias = "worker" }),
            new ToolRegistry([new EvidenceTools(changes, runtime)]),
            new RecordingStepLogger(),
            TestContextAssembler.Create(),
            new RecordingMetricsRecorder(),
            Options.Create(new AgentOptions()),
            changes: changes,
            verificationOptions: Options.Create(new VerificationLadderOptions()),
            critics: critics,
            runtime: runtime,
            workspace: Options.Create(workspace.Options("src")));

        await loop.RunAsync(new AgentRunRequest { TaskId = "task-1", Goal = "Build the app.", CriticRole = "critic" });
        return critics;
    }

    /// <summary>Answers every climb with the same report.</summary>
    private sealed class ScriptedLadder(VerificationReport report) : IVerificationLadder
    {
        public Task<VerificationReport> VerifyAsync(
            VerificationRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(report);
    }

    /// <summary>Records what the panel was asked to judge, and accepts.</summary>
    private sealed class RecordingCritics : ICriticPanel
    {
        public string? Evidence { get; private set; }

        public bool Enabled => true;

        public bool CanCritique(string? role) => true;

        public string ResolveRole(string? role) => role ?? "critic";

        public Task<CritiqueResult> CritiqueAsync(
            string goal,
            string change,
            string evidence,
            string? role = null,
            string? claim = null,
            CancellationToken cancellationToken = default)
        {
            Evidence = evidence;
            return Task.FromResult(new CritiqueResult(false, [], 0, "3/3 accepted.") { RespondingVotes = 3 });
        }
    }

    /// <summary>
    /// One tool that applies a change and records two launches, and one that is always refused.
    /// The launches have to happen inside the run: <see cref="RuntimeEvidence"/> is keyed on the
    /// ambient run id, which the loop sets and a test thread does not have.
    /// </summary>
    private sealed class EvidenceTools : IToolSet
    {
        private readonly IChangeLog _changes;
        private readonly RuntimeEvidence _runtime;

        public EvidenceTools(IChangeLog changes, RuntimeEvidence runtime)
        {
            _changes = changes;
            _runtime = runtime;
        }

        [GlassCoderTool("touch", Order = 1)]
        [Description("Applies a change and launches twice, for tests.")]
        public ToolObservation<Payload> Touch()
        {
            CodeChange change = _changes.Propose("src/C.cs", "touch", string.Empty, "public class C { }");
            _changes.Update(change.Id, ChangeStatus.Applied);

            _runtime.Record("<<runtime-sentinel>> Probe: Celsius=100; Fahrenheit? → \"212\".", started: true);
            _runtime.Record("<<second-launch-sentinel>> Probe: Fahrenheit=212; Celsius? → \"100\".", started: true);

            return Observation.Ok("touch", new Payload("applied"), "applied");
        }

        [GlassCoderTool("refuse", Order = 2)]
        [Description("Always refuses, for tests.")]
        public ToolObservation<Payload> Refuse() =>
            Observation.Fail<Payload>("refuse", ToolErrorCodes.InvalidArgument, "not here", "try elsewhere");

        /// <summary>Applies a change and launches nothing, which is run dbaa0580's whole shape.</summary>
        [GlassCoderTool("quiet_touch", Order = 3)]
        [Description("Applies a change without launching anything, for tests.")]
        public ToolObservation<Payload> QuietTouch()
        {
            CodeChange change = _changes.Propose("src/App/MainWindow.xaml.cs", "quiet_touch", string.Empty, "class W { }");
            _changes.Update(change.Id, ChangeStatus.Applied);
            return Observation.Ok("quiet_touch", new Payload("applied"), "applied");
        }
    }

    public sealed record Payload([property: Description("What happened.")] string Value);
}
