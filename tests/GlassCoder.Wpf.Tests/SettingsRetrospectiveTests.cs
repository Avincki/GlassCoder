using System.Windows;
using System.Windows.Controls;
using GlassCoder.Core.Configuration;
using GlassCoder.Core.Verification;
using GlassCoder.Models;
using GlassCoder.Models.Configuration;
using GlassCoder.TestSupport;
using GlassCoder.Wpf.ViewModels;
using GlassCoder.Wpf.Views;
using Microsoft.Extensions.Configuration;

namespace GlassCoder.Wpf.Tests;

/// <summary>
/// The retrospective's settings tab.
/// <para>
/// It exists because of a specific failure: a saved <c>MaxBudgetUsd</c> of 2.00 - written back
/// when that was the default, and outranking both <c>appsettings.json</c> and the raised code
/// default of 8.00 - cut off stage three every time, and there was no surface anywhere in the
/// application that showed the number or let anyone change it. Two things are asserted here: that
/// every field round-trips through a save, and that the one box which can undo the containment
/// argument refuses to.
/// </para>
/// </summary>
public sealed class SettingsRetrospectiveTests
{
    [Fact]
    public void The_saved_budget_is_what_the_tab_shows_rather_than_the_code_default()
    {
        // The exact shape of the incident: a stale saved value beating a raised default, with
        // nothing on any surface to say so.
        using Fixture fixture = new(Configured(("GlassCoder:Retrospective:MaxBudgetUsd", "2.0")));

        fixture.ViewModel.Settings.Retrospective.MaxBudgetUsd.ShouldBe(2.0m);
        new RetrospectiveOptions().MaxBudgetUsd.ShouldBe(8.00m);
    }

    [Fact]
    public void Raising_the_budget_in_the_dialog_survives_a_save()
    {
        using Fixture fixture = new(Configured(("GlassCoder:Retrospective:MaxBudgetUsd", "2.0")));

        fixture.ViewModel.Settings.Retrospective.MaxBudgetUsd = 8.0m;
        fixture.ViewModel.SaveCommand.Execute(null);

        fixture.Reload().Retrospective.MaxBudgetUsd.ShouldBe(8.0m);
    }

    [Fact]
    public void Every_field_on_the_tab_round_trips()
    {
        using Fixture fixture = new(Configured());

        RetrospectiveOptions edited = fixture.ViewModel.Settings.Retrospective;
        edited.Enabled = true;
        edited.Model = "claude-sonnet-5";
        edited.CliPath = "claude";
        edited.PermissionMode = "plan";
        edited.Bare = false;
        edited.MaxBudgetUsd = 9.5m;
        edited.TimeoutSeconds = 1200;
        edited.MaxRecommendations = 20;
        edited.MaxChangeCharacters = 30000;
        edited.MaxTranscriptCharacters = 50000;
        edited.OutputDirectory = ".glasscoder/looks-back";
        edited.HarnessRepoPath = @"C:\repos\GlassCoder";
        edited.WorkOrderDirectory = "docs/orders";
        fixture.ViewModel.RetrospectiveApiKeyVariable = "GLASSCODER_REVIEW_KEY";

        fixture.ViewModel.SaveCommand.Execute(null);

        RetrospectiveOptions saved = fixture.Reload().Retrospective;
        saved.Model.ShouldBe("claude-sonnet-5");
        saved.MaxBudgetUsd.ShouldBe(9.5m);
        saved.TimeoutSeconds.ShouldBe(1200);
        saved.MaxRecommendations.ShouldBe(20);
        saved.MaxChangeCharacters.ShouldBe(30000);
        saved.MaxTranscriptCharacters.ShouldBe(50000);
        saved.OutputDirectory.ShouldBe(".glasscoder/looks-back");
        saved.HarnessRepoPath.ShouldBe(@"C:\repos\GlassCoder");
        saved.WorkOrderDirectory.ShouldBe("docs/orders");
        saved.ApiKeyEnvironmentVariable.ShouldBe("GLASSCODER_REVIEW_KEY");
    }

    [Fact]
    public void An_empty_key_variable_is_saved_as_no_variable_at_all()
    {
        // A text box cannot hold null, and the difference matters: a name here makes the CLI
        // authenticate with that key instead of the subscription login it already has.
        using Fixture fixture = new(Configured(
            ("GlassCoder:Retrospective:ApiKeyEnvironmentVariable", "GLASSCODER_REVIEW_KEY")));

        fixture.ViewModel.RetrospectiveApiKeyVariable.ShouldBe("GLASSCODER_REVIEW_KEY");

        fixture.ViewModel.RetrospectiveApiKeyVariable = "   ";
        fixture.ViewModel.SaveCommand.Execute(null);

        fixture.Reload().Retrospective.ApiKeyEnvironmentVariable.ShouldBeNull();
    }

    [Fact]
    public void The_tool_list_is_edited_as_lines()
    {
        using Fixture fixture = new(Configured());

        fixture.ViewModel.RetrospectiveAllowedTools.ShouldBe("Read" + Environment.NewLine + "Grep" +
            Environment.NewLine + "Glob");

        fixture.ViewModel.RetrospectiveAllowedTools = "Read" + Environment.NewLine + "Glob";

        fixture.ViewModel.Settings.Retrospective.AllowedTools.ShouldBe(["Read", "Glob"]);
    }

    [Fact]
    public void A_writing_tool_is_refused_rather_than_saved()
    {
        // The containment argument in one assertion. A retrospective stage runs on the host,
        // outside the sandbox every other command goes through; the only thing that makes that
        // defensible is that it cannot write. Before the tab, that was protected by nobody
        // editing the JSON.
        using Fixture fixture = new(Configured());

        fixture.ViewModel.RetrospectiveAllowedTools =
            "Read" + Environment.NewLine + "Grep" + Environment.NewLine + "Bash";
        fixture.ViewModel.SaveCommand.Execute(null);

        fixture.ViewModel.ValidationFailures.ShouldContain(failure => failure.Contains("Bash"));
        fixture.Store.Exists.ShouldBeFalse();
    }

    [Fact]
    public void Taking_a_tool_out_of_the_list_sticks()
    {
        // The bug this pass found. The binder appends to a collection property that already holds
        // something - for IList<string> and string[] alike, setter or no setter - so while the
        // options object defaulted to the three tools, a saved ["Read","Glob"] bound back to
        // ["Read","Grep","Glob"] and the removal vanished. The list defaults to empty now, and
        // the fallback happens at the point of use instead.
        using Fixture fixture = new(Configured());

        fixture.ViewModel.RetrospectiveAllowedTools = "Read" + Environment.NewLine + "Glob";
        fixture.ViewModel.SaveCommand.Execute(null);

        fixture.Reload().Retrospective.AllowedTools.ShouldBe(["Read", "Glob"]);
    }

    [Fact]
    public void An_emptied_list_falls_back_to_the_read_only_default()
    {
        // Empty means "nothing configured", not "no tools": a stage handed an empty --allowedTools
        // could not read the run it was asked to judge. The dialog fills the box back in, so what
        // the operator sees is what the stage gets rather than a blank.
        using Fixture fixture = new(Configured());

        fixture.ViewModel.RetrospectiveAllowedTools = string.Empty;
        fixture.ViewModel.SaveCommand.Execute(null);

        fixture.ViewModel.ValidationFailures.ShouldBeEmpty();

        RetrospectiveOptions reloaded = fixture.Reload().Retrospective;
        reloaded.AllowedTools.ShouldBe(["Read", "Grep", "Glob"]);
        reloaded.EffectiveAllowedTools.ShouldBe(["Read", "Grep", "Glob"]);
    }

    [Fact]
    public void A_switched_off_retrospective_is_not_checked()
    {
        // The rule ValidateGit already follows: a setting nothing can reach is not worth blocking
        // a save over.
        using Fixture fixture = new(Configured(("GlassCoder:Retrospective:Enabled", "false")));

        fixture.ViewModel.RetrospectiveAllowedTools = "Bash";
        fixture.ViewModel.SaveCommand.Execute(null);

        fixture.ViewModel.ValidationFailures.ShouldBeEmpty();
    }

    [Fact]
    public void A_zero_budget_is_allowed_because_it_means_no_ceiling()
    {
        // ClaudeCliSession omits --max-budget-usd below one, so zero is a real choice rather than
        // a mistake. Asserted so a later tightening cannot quietly turn it into an error.
        using Fixture fixture = new(Configured());

        fixture.ViewModel.Settings.Retrospective.MaxBudgetUsd = 0m;
        fixture.ViewModel.SaveCommand.Execute(null);

        fixture.ViewModel.ValidationFailures.ShouldBeEmpty();
        fixture.Reload().Retrospective.MaxBudgetUsd.ShouldBe(0m);
    }

    [Fact]
    public void The_tool_list_does_not_double_on_every_visit()
    {
        // What the operator's own settings file looked like by the time this tab was written:
        // ["Read","Grep","Glob","Read","Grep","Glob"]. The binder appends to a list that already
        // holds defaults, so a saved list is re-appended to the defaults on the next load.
        using Fixture fixture = new(Configured(
            ("GlassCoder:Retrospective:AllowedTools:0", "Read"),
            ("GlassCoder:Retrospective:AllowedTools:1", "Grep"),
            ("GlassCoder:Retrospective:AllowedTools:2", "Glob")));

        fixture.ViewModel.Settings.Retrospective.AllowedTools.ShouldBe(["Read", "Grep", "Glob"]);

        fixture.ViewModel.SaveCommand.Execute(null);

        fixture.Reload().Retrospective.AllowedTools.ShouldBe(["Read", "Grep", "Glob"]);
    }

    [Fact]
    public void The_dialog_shows_the_tab()
    {
        // Bindings inside a tab fail silently when they are wrong - an empty box and no error
        // anywhere - so this builds the window and reads the realised controls.
        using Fixture fixture = new(Configured(("GlassCoder:Retrospective:MaxBudgetUsd", "2.0")));

        (bool Found, string Budget, int Modes) tab = UiThread.RunOnApplicationThread(_ =>
        {
            TestApplication.Ensure();
            SettingsWindow window = new(fixture.ViewModel);
            window.ShowInTaskbar = false;
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Left = -32000;
            window.Top = -32000;
            window.Show();
            window.UpdateLayout();

            try
            {
                TabControl tabs = FindTabControl(window)
                    ?? throw new InvalidOperationException("No TabControl in the settings window.");

                TabItem? item = tabs.Items.OfType<TabItem>()
                    .FirstOrDefault(candidate => (candidate.Header as string) == "Retrospective");

                if (item is null)
                {
                    return (false, string.Empty, 0);
                }

                // A tab's content is not realised until it is the selected one, and a binding
                // that walks the visual tree has nothing to walk until then.
                tabs.SelectedItem = item;
                window.UpdateLayout();

                // Read the rendered box rather than the view model. A binding with the wrong
                // path fails silently in WPF - no exception, no build error, just an empty
                // control - so asking the view model what it holds would pass either way.
                TextBox budget = (TextBox)window.FindName("RetrospectiveBudget");

                return (true, budget.Text, fixture.ViewModel.PermissionModes.Count);
            }
            finally
            {
                window.Close();
            }
        });

        tab.Found.ShouldBeTrue();

        // The number the operator could not see anywhere before this tab existed.
        tab.Budget.ShouldBe("2.0");
        tab.Modes.ShouldBe(2);
    }

    private static TabControl? FindTabControl(DependencyObject root)
    {
        int count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (int index = 0; index < count; index++)
        {
            DependencyObject child = System.Windows.Media.VisualTreeHelper.GetChild(root, index);
            if (child is TabControl tabs)
            {
                return tabs;
            }

            if (FindTabControl(child) is { } nested)
            {
                return nested;
            }
        }

        return null;
    }

    /// <summary>
    /// A configuration with one served role - which every settings validator needs before it will
    /// look at anything else - plus whatever this test is actually about.
    /// </summary>
    private static IEnumerable<KeyValuePair<string, string?>> Configured(
        params (string Key, string Value)[] settings)
    {
        yield return new KeyValuePair<string, string?>(
            "GlassCoder:Models:Roles:worker:Endpoint", "http://localhost:8002/v1");
        yield return new KeyValuePair<string, string?>(
            "GlassCoder:Models:Roles:worker:ModelAlias", "worker");
        yield return new KeyValuePair<string, string?>("GlassCoder:Models:DefaultRole", "worker");
        yield return new KeyValuePair<string, string?>("GlassCoder:Agent:Role", "worker");

        foreach ((string key, string value) in settings)
        {
            yield return new KeyValuePair<string, string?>(key, value);
        }
    }

    /// <summary>The dialog over a throwaway settings directory, with the real stores behind it.</summary>
    private sealed class Fixture : IDisposable
    {
        private readonly TempWorkspace _workspace = new();

        public Fixture(IEnumerable<KeyValuePair<string, string?>> configuration)
        {
            Store = new UserSettingsStore(new DpapiSecretProtector(), _workspace.Root);

            ViewModel = new SettingsViewModel(
                new ConfigurationBuilder().AddInMemoryCollection(configuration).Build(),
                Store,
                new ProjectSettingsStore(),
                new SettingsTransfer(),
                new SilentProbe(),
                new FakeShell());
        }

        public UserSettingsStore Store { get; }

        public SettingsViewModel ViewModel { get; }

        /// <summary>What a fresh start would bind, reading only what the save actually wrote.</summary>
        public GlassCoderSettings Reload() =>
            GlassCoderSettings.ReadFrom(new ConfigurationBuilder().AddGlassCoderUserSettings(Store).Build());

        public void Dispose() => _workspace.Dispose();
    }

    private sealed class SilentProbe : IModelConnectionProbe
    {
        public Task<ConnectionCheckResult> CheckAsync(
            string role,
            ModelRoleOptions settings,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("No test here presses Test.");
    }
}
