using GlassCoder.Tools.Build;
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
    public async Task A_directory_is_refused_rather_than_guessed_at()
    {
        ToolObservation<LaunchAppResult> observation = await Tool(new FakeProcessRunner())
            .LaunchAsync("src/App");

        observation.Ok.ShouldBeFalse();
        observation.Error!.Hint.ShouldContain("list_projects");
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

    private LaunchAppTool Tool(IProcessRunner runner, IWindowPresence? windows = null) =>
        new(runner, _workspace.Guard("src"), new RuntimeEvidence(), windows);

    private sealed class StubWindows(bool answer) : IWindowPresence
    {
        public bool HasVisibleWindow(int processId) => answer;
    }
}
