using System.Windows.Threading;
using GlassCoder.Core.Configuration;
using GlassCoder.Core.Diagnostics;
using GlassCoder.TestSupport;
using GlassCoder.Tools.Changes;
using GlassCoder.Tools.Guardrails;
using GlassCoder.Wpf.Services;
using GlassCoder.Wpf.ViewModels;
using GlassCoder.Core.DependencyInjection;
using GlassCoder.Wpf.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace GlassCoder.Wpf.Tests;

/// <summary>
/// The operator's verdict on a launched application.
/// <para>
/// The screen is the one oracle the ladder and the critics do not have: a run can compile, pass
/// its tests and be accepted by three critics with its result field off the bottom of the
/// window. This is the only thing in the harness that judges what was actually on it, so it has
/// to reach the transcript rather than the operator's memory.
/// </para>
/// </summary>
public sealed class OperatorRatingTests
{
    [Fact]
    public void The_question_is_asked_when_the_application_closes_and_not_before()
    {
        using TempWorkspace workspace = new();
        WriteApp(workspace);
        FakeShell shell = new();

        (bool askedOnLaunch, bool askedOnExit, string application) = OverPane(workspace, shell, (dispatcher, pane) =>
        {
            pane.RunAppCommand.Execute(null);
            bool duringRun = pane.IsRatingApp;

            shell.OnAppExit.ShouldNotBeNull("the pane never asked to be told when the app ended");
            CloseApp(dispatcher, pane, shell);

            return (duringRun, pane.IsRatingApp, pane.RatedApplication);
        });

        askedOnLaunch.ShouldBeFalse("nobody can rate a window that has not opened yet");
        askedOnExit.ShouldBeTrue();
        application.ShouldContain("App");
    }

    [Fact]
    public void A_launch_that_failed_never_asks()
    {
        using TempWorkspace workspace = new();
        WriteApp(workspace);
        FakeShell shell = new() { LaunchFailure = "dotnet is not on the PATH" };

        bool asked = OverPane(workspace, shell, (dispatcher, pane) =>
        {
            pane.RunAppCommand.Execute(null);
            return pane.IsRatingApp;
        });

        asked.ShouldBeFalse();
    }

    [Fact]
    public void Recording_is_refused_until_a_score_is_chosen()
    {
        using TempWorkspace workspace = new();
        WriteApp(workspace);
        FakeShell shell = new();
        RecordingStepLogger steps = new();

        int logged = OverPane(workspace, shell, steps, (dispatcher, pane) =>
        {
            pane.RunAppCommand.Execute(null);
            CloseApp(dispatcher, pane, shell);

            pane.SubmitRatingCommand.CanExecute(null).ShouldBeFalse("no score, nothing to record");
            pane.SubmitRatingCommand.Execute(null);
            int before = steps.Steps.Count;

            pane.AppRating = 4;
            pane.SubmitRatingCommand.CanExecute(null).ShouldBeTrue();

            return before;
        });

        logged.ShouldBe(0);
    }

    [Fact]
    public void A_recorded_rating_reaches_the_transcript_with_its_comment()
    {
        using TempWorkspace workspace = new();
        WriteApp(workspace);
        FakeShell shell = new();
        RecordingStepLogger steps = new();

        bool stillAsking = OverPane(workspace, shell, steps, (dispatcher, pane) =>
        {
            pane.RunAppCommand.Execute(null);
            CloseApp(dispatcher, pane, shell);

            pane.AppRating = 2;
            pane.AppComment = "  result field is clipped at the bottom  ";
            pane.SubmitRatingCommand.Execute(null);

            return pane.IsRatingApp;
        });

        stillAsking.ShouldBeFalse("the strip closes once the verdict is in");

        StepRecord record = steps.Steps.ShouldHaveSingleItem();
        record.Role.ShouldBe("human", "a person did this, not the model");
        record.Outcome.ShouldBe("operator rating 2/5");

        ToolCallRecord call = record.ToolCalls.ShouldHaveSingleItem();
        call.Name.ShouldBe("operator_rating", "the transcript is grepped by tool name");
        call.Arguments!["rating"].ShouldBe(2);
        call.Arguments["outOf"].ShouldBe(5);
        call.Arguments["comment"].ShouldBe("result field is clipped at the bottom");
    }

    /// <summary>A comment nobody wrote is absent rather than empty, so a reader can tell.</summary>
    [Fact]
    public void An_unwritten_comment_is_null_rather_than_blank()
    {
        using TempWorkspace workspace = new();
        WriteApp(workspace);
        FakeShell shell = new();
        RecordingStepLogger steps = new();

        OverPane(workspace, shell, steps, (dispatcher, pane) =>
        {
            pane.RunAppCommand.Execute(null);
            CloseApp(dispatcher, pane, shell);
            pane.AppRating = 5;
            pane.SubmitRatingCommand.Execute(null);
            return 0;
        });

        steps.Steps.Single().ToolCalls.Single().Arguments!["comment"].ShouldBeNull();
    }

    /// <summary>Skipping records nothing. A rating given to dismiss a strip is worse than none.</summary>
    [Fact]
    public void Skipping_closes_the_strip_and_records_nothing()
    {
        using TempWorkspace workspace = new();
        WriteApp(workspace);
        FakeShell shell = new();
        RecordingStepLogger steps = new();

        bool asking = OverPane(workspace, shell, steps, (dispatcher, pane) =>
        {
            pane.RunAppCommand.Execute(null);
            CloseApp(dispatcher, pane, shell);
            pane.AppRating = 1;
            pane.SkipRatingCommand.Execute(null);
            return pane.IsRatingApp;
        });

        asking.ShouldBeFalse();
        steps.Steps.ShouldBeEmpty();
    }

    /// <summary>
    /// Ends the launched application and waits for the question to arrive. The exit callback
    /// fires on a background thread and marshals to the UI one, so a test that read the strip
    /// straight afterwards would be reading it before the pane had been told - and would pass
    /// for the wrong reason.
    /// </summary>
    private static void CloseApp(Dispatcher dispatcher, WorkspaceViewModel pane, FakeShell shell)
    {
        shell.OnAppExit!();

        UiThread.Pump(dispatcher, () => pane.IsRatingApp, TimeSpan.FromSeconds(5))
            .ShouldBeTrue("the pane never asked how the application looked");
    }

    private static void WriteApp(TempWorkspace workspace) => workspace.WriteFile(
        "src/App/App.csproj",
        "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>WinExe</OutputType></PropertyGroup></Project>");

    private static T OverPane<T>(TempWorkspace workspace, FakeShell shell, Func<Dispatcher, WorkspaceViewModel, T> assert) =>
        OverPane(workspace, shell, new RecordingStepLogger(), assert);

    /// <summary>
    /// The pane out of the real container, with the shell and the step logger stood in for.
    /// Built rather than newed so the optional IStepLogger the pane takes is proved to be wired,
    /// which is exactly the kind of dependency that silently stays null.
    /// </summary>
    private static T OverPane<T>(
        TempWorkspace workspace, FakeShell shell, IStepLogger steps, Func<Dispatcher, WorkspaceViewModel, T> assert) =>
        UiThread.Run(dispatcher =>
        {
            IConfiguration configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["GlassCoder:Workspace:RepoRoot"] = workspace.Root,
                    ["GlassCoder:Models:Roles:worker:Endpoint"] = "http://localhost:8001/v1",
                    ["GlassCoder:Models:Roles:worker:ModelAlias"] = "worker",
                    ["GlassCoder:Telemetry:Enabled"] = "false",
                    ["GlassCoder:Metrics:Enabled"] = "false",
                    ["GlassCoder:Provenance:Enabled"] = "false",
                })
                .Build();

            ServiceCollection services = new();
            services.AddSingleton(configuration);
            services.AddLogging();
            services.AddGlassCoder(configuration);
            services.AddGlassCoderDesktop(dispatcher);
            services.AddSingleton<IDesktopShell>(shell);
            services.AddSingleton(steps);

            using ServiceProvider provider = services.BuildServiceProvider();
            WorkspaceViewModel pane = provider.GetRequiredService<WorkspaceViewModel>();

            UiThread.Pump(dispatcher, () => pane.Loaded.IsCompleted, TimeSpan.FromSeconds(15))
                .ShouldBeTrue("the pane never finished its first read of the workspace");

            return assert(dispatcher, pane);
        });
}
