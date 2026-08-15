using GlassCoder.TestSupport;
using GlassCoder.Tools;
using GlassCoder.Tools.Build;
using GlassCoder.Tools.Execution;
using GlassCoder.Tools.Processes;
using GlassCoder.Tools.Verification;
using Microsoft.Extensions.Options;

namespace GlassCoder.Tools.Tests;

/// <summary>
/// Every tool that relays a process's exit code must relay its verdict too.
/// <para>
/// <c>Ok</c> says the call ran; <c>OutcomeOk</c> says the thing it ran did what it was for, and the
/// progress machinery counts the second. Run <c>dbaa0580</c> failed three builds and
/// <c>RunProgressSentry</c> and <c>AbandonedIntents</c> saw three successes, because <c>build</c>
/// was the one tool of four relaying an exit code that never set it - a run failing the same build
/// ten times would have been invisible to both.
/// </para>
/// <para>
/// Stated once, here, over every such tool side by side, so the next one added is compared against
/// a rule rather than against whichever neighbour its author happened to read.
/// </para>
/// </summary>
public sealed class ProcessOutcomeTests : IDisposable
{
    private readonly TempWorkspace _workspace = new();

    public ProcessOutcomeTests() =>
        _workspace.WriteFile("src/App/App.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");

    public void Dispose() => _workspace.Dispose();

    [Fact]
    public async Task A_failed_build_is_a_failed_outcome()
    {
        ScriptedCommandExecutor executor = new();
        executor.Enqueue(1, "src/App/Program.cs(3,5): error CS1002: ; expected [src/App/App.csproj]");

        ToolObservation<BuildResult> observation = await new BuildTool(
            executor,
            _workspace.Guard("src"),
            new DiagnosticSummarizer(Options.Create(new VerificationOptions())),
            Options.Create(new SandboxOptions())).BuildAsync("src/App/App.csproj");

        Assert(observation.Ok, observation.OutcomeOk, "build");
    }

    [Fact]
    public async Task A_red_suite_is_a_failed_outcome()
    {
        ScriptedCommandExecutor executor = new();
        executor.Enqueue(1, "  Failed App.Tests.Adds [3 ms]\nFailed!  - Failed: 1, Passed: 2, Skipped: 0, Total: 3");

        ToolObservation<TestRunResult> observation = await new RunTestsTool(
            executor,
            _workspace.Guard("src"),
            Options.Create(new SandboxOptions())).RunTestsAsync("src/App/App.csproj");

        Assert(observation.Ok, observation.OutcomeOk, "run_tests");
    }

    [Fact]
    public async Task An_application_that_crashed_on_startup_is_a_failed_outcome()
    {
        FakeProcessRunner runner = new();
        runner.Enqueue(134, standardError: "Unhandled exception.");

        ToolObservation<LaunchAppResult> observation = await new LaunchAppTool(
            runner, _workspace.Guard("src"), new RuntimeEvidence()).LaunchAsync("src/App/App.csproj");

        Assert(observation.Ok, observation.OutcomeOk, "launch_app");
    }

    private static void Assert(bool ok, bool outcomeOk, string tool)
    {
        ok.ShouldBeTrue($"{tool} relays a failure as information, never as a broken call");
        outcomeOk.ShouldBeFalse(
            $"{tool} relays a process exit code, so the progress machinery has to see the failure - " +
            "OutcomeOk is the only field that carries it");
    }
}
