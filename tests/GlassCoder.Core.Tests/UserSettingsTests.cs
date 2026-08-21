using System.Reflection;
using GlassCoder.Core.Agent;
using GlassCoder.Core.Configuration;
using GlassCoder.Core.Verification;
using GlassCoder.Models.Configuration;
using GlassCoder.TestSupport;
using GlassCoder.Tools.Git;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace GlassCoder.Core.Tests;

/// <summary>
/// The settings the desktop dialog saves (CLAUDE.md §9, §13).
/// <para>
/// Three properties are worth asserting rather than hoping for: a key never lands in the
/// settings file, a saved setting never outranks what a run states explicitly, and a list does
/// not grow every time the dialog is opened.
/// </para>
/// </summary>
public sealed class UserSettingsTests
{
    [Fact]
    public void A_protected_secret_round_trips_without_being_stored_in_the_clear()
    {
        DpapiSecretProtector protector = new();

        string stored = protector.Protect("sk-test-0123456789abcdef");

        stored.ShouldNotContain("sk-test-0123456789abcdef");
        protector.Unprotect(stored).ShouldBe("sk-test-0123456789abcdef");
    }

    [Fact]
    public void Nonsense_in_the_secrets_file_decrypts_to_nothing_rather_than_throwing()
    {
        DpapiSecretProtector protector = new();

        protector.Unprotect("dpapi:not-base-64!").ShouldBeNull();
        protector.Unprotect("hand-edited").ShouldBeNull();
        protector.Unprotect(string.Empty).ShouldBeNull();
    }

    [Fact]
    public void The_api_key_is_written_to_the_secrets_file_and_never_to_the_settings_file()
    {
        using TempWorkspace workspace = new();
        UserSettingsStore store = new(new DpapiSecretProtector(), workspace.Root);

        store.Save(Settings(apiKey: "sk-live-abcdefghijklmnop"));

        string settingsFile = File.ReadAllText(store.SettingsFilePath);
        settingsFile.ShouldNotContain("sk-live-abcdefghijklmnop");
        settingsFile.ShouldNotContain("\"ApiKey\"", Case.Sensitive);
        settingsFile.ShouldContain("\"ApiKeyEnvironmentVariable\"", Case.Sensitive);
        settingsFile.ShouldContain("http://localhost:9001/v1");

        File.ReadAllText(store.SecretsFilePath).ShouldNotContain("sk-live-abcdefghijklmnop");
        store.LoadSecrets()["GlassCoder:Models:Roles:worker:ApiKey"].ShouldBe("sk-live-abcdefghijklmnop");
    }

    [Fact]
    public void Saved_settings_and_keys_come_back_through_configuration()
    {
        using TempWorkspace workspace = new();
        UserSettingsStore store = new(new DpapiSecretProtector(), workspace.Root);

        GlassCoderSettings saved = Settings(apiKey: "sk-live-abcdefghijklmnop");
        saved.Agent.MaxSteps = 42;
        saved.Workspace.WritablePaths.Add("src");
        store.Save(saved);

        GlassCoderSettings reloaded = GlassCoderSettings.ReadFrom(Configuration(store));

        reloaded.Agent.MaxSteps.ShouldBe(42);
        reloaded.Workspace.WritablePaths.ShouldBe(["src"]);
        reloaded.Models.Roles["worker"].Endpoint.ShouldBe("http://localhost:9001/v1");
        reloaded.Models.Roles["worker"].ApiKey.ShouldBe("sk-live-abcdefghijklmnop");
    }

    [Fact]
    public void Saved_settings_beat_appsettings_and_lose_to_an_environment_variable()
    {
        using TempWorkspace workspace = new();
        UserSettingsStore store = new(new DpapiSecretProtector(), workspace.Root);

        GlassCoderSettings saved = Settings();
        saved.Agent.MaxSteps = 42;
        saved.Agent.MaxWallClockSeconds = 111;
        store.Save(saved);

        Environment.SetEnvironmentVariable("GlassCoder__Agent__MaxSteps", "77");
        try
        {
            IConfigurationBuilder builder = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["GlassCoder:Agent:MaxSteps"] = "5",
                    ["GlassCoder:Agent:MaxWallClockSeconds"] = "5",
                })
                .AddEnvironmentVariables();

            // Inserted after the fact, exactly as the host does it.
            builder.AddGlassCoderUserSettings(store);

            AgentOptions agent = GlassCoderSettings.ReadFrom(builder.Build()).Agent;

            agent.MaxSteps.ShouldBe(77, "an environment variable still overrides a saved setting");
            agent.MaxWallClockSeconds.ShouldBe(111, "a saved setting still overrides appsettings.json");
        }
        finally
        {
            Environment.SetEnvironmentVariable("GlassCoder__Agent__MaxSteps", null);
        }
    }

    [Fact]
    public void A_saved_setting_beats_appsettings_under_the_real_host_builder()
    {
        // The regression this file previously missed. A hand-rolled ConfigurationBuilder has one
        // environment source, at the end; HostApplicationBuilder has two, and the first sits
        // *before* appsettings.json. Inserting ahead of that one buried every saved setting that
        // appsettings.json also mentions - which was nearly all of them.
        using TempWorkspace application = new();
        using TempWorkspace settings = new();
        using TempWorkspace project = new();

        application.WriteFile("appsettings.json", """
            { "GlassCoder": { "Workspace": { "RepoRoot": "." }, "Agent": { "MaxSteps": 30 } } }
            """);

        UserSettingsStore store = new(new DpapiSecretProtector(), settings.Root);
        GlassCoderSettings saved = Settings();
        saved.Workspace.RepoRoot = project.Root;
        saved.Agent.MaxSteps = 42;
        store.Save(saved);

        HostApplicationBuilder builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            ContentRootPath = application.Root,
        });

        builder.Configuration.AddGlassCoderUserSettings(store);

        builder.Configuration["GlassCoder:Workspace:RepoRoot"].ShouldBe(
            project.Root, "the folder chosen in the workspace pane must survive a restart");
        builder.Configuration["GlassCoder:Agent:MaxSteps"].ShouldBe(
            "42", "every saved setting outranks appsettings.json, not just the workspace root");
    }

    [Fact]
    public void An_environment_variable_still_wins_under_the_real_host_builder()
    {
        using TempWorkspace application = new();
        using TempWorkspace settings = new();

        application.WriteFile("appsettings.json", """{ "GlassCoder": { "Agent": { "MaxSteps": 30 } } }""");

        UserSettingsStore store = new(new DpapiSecretProtector(), settings.Root);
        GlassCoderSettings saved = Settings();
        saved.Agent.MaxSteps = 42;
        store.Save(saved);

        Environment.SetEnvironmentVariable("GlassCoder__Agent__MaxSteps", "77");
        try
        {
            HostApplicationBuilder builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
            {
                ContentRootPath = application.Root,
            });

            builder.Configuration.AddGlassCoderUserSettings(store);

            builder.Configuration["GlassCoder:Agent:MaxSteps"].ShouldBe(
                "77", "an ablation arm stated on the command line must not be overridden by a saved setting");
        }
        finally
        {
            Environment.SetEnvironmentVariable("GlassCoder__Agent__MaxSteps", null);
        }
    }

    [Fact]
    public void The_workspace_root_is_discovered_by_walking_up_to_the_repository()
    {
        using TempWorkspace repository = new();
        repository.CreateDirectory(".git");
        string deep = repository.CreateDirectory("src/GlassCoder.Wpf/bin/Debug/net10.0-windows");

        // Exactly the walk a double-clicked desktop launch makes from its own build output.
        WorkspaceRootLocator.Find(deep).ShouldBe(repository.Root);
    }

    [Fact]
    public void A_solution_file_marks_a_repository_root_too()
    {
        using TempWorkspace repository = new();
        repository.WriteFile("Thing.sln", "solution");
        string deep = repository.CreateDirectory("src/bin/Debug");

        WorkspaceRootLocator.Find(deep).ShouldBe(repository.Root);
    }

    [Fact]
    public void Nothing_is_discovered_outside_a_repository()
    {
        using TempWorkspace elsewhere = new();

        // Null rather than a guess: the saved setting is then the only way to say where the work
        // is, which is the honest answer for an installed copy.
        WorkspaceRootLocator.Find(elsewhere.CreateDirectory("app")).ShouldBeNull();
    }

    [Theory]
    [InlineData(".")]
    [InlineData(" . ")]
    [InlineData("")]
    [InlineData(null)]
    public void The_placeholder_root_counts_as_unset(string? configured) =>
        WorkspaceRootLocator.IsUnset(configured).ShouldBeTrue();

    [Fact]
    public void A_chosen_root_is_never_treated_as_unset() =>
        WorkspaceRootLocator.IsUnset(@"C:\repos\GlassCoder").ShouldBeFalse();

    [Fact]
    public void The_bash_switch_round_trips_through_the_settings_file()
    {
        // It was read straight from configuration and was not a property at all, so the dialog
        // could not offer it and a save could not carry it.
        using TempWorkspace workspace = new();
        UserSettingsStore store = new(new DpapiSecretProtector(), workspace.Root);

        GlassCoderSettings saved = Settings();
        saved.Sandbox.EnableBashTool = true;
        store.Save(saved);

        GlassCoderSettings.ReadFrom(Configuration(store)).Sandbox.EnableBashTool.ShouldBeTrue();
    }

    [Fact]
    public void Git_settings_round_trip_through_the_settings_file()
    {
        // Until the Git section joined GlassCoderSettings the dialog could not reach it at all,
        // so the tools could only be switched on by hand-editing appsettings.json.
        using TempWorkspace workspace = new();
        UserSettingsStore store = new(new DpapiSecretProtector(), workspace.Root);

        GlassCoderSettings saved = Settings();
        saved.Git.Enabled = true;
        saved.Git.Remote = "upstream";
        saved.Git.AllowHooks = true;
        saved.Git.CommitTrailer = "Co-Authored-By: Someone <someone@example.invalid>";
        saved.Git.PushableBranches.Add("feature/pager");
        saved.Git.ProtectedBranches.Add("main");
        saved.Git.PullRequestBaseBranch = "develop";
        saved.Git.GitHubExecutable = @"C:\tools\gh.exe";
        store.Save(saved);

        GitOptions reloaded = GlassCoderSettings.ReadFrom(Configuration(store)).Git;

        reloaded.Enabled.ShouldBeTrue();
        reloaded.Remote.ShouldBe("upstream");
        reloaded.AllowHooks.ShouldBeTrue();
        reloaded.CommitTrailer.ShouldBe("Co-Authored-By: Someone <someone@example.invalid>");
        reloaded.PushableBranches.ShouldBe(["feature/pager"]);
        reloaded.ProtectedBranches.ShouldBe(["main"]);
        reloaded.PullRequestBaseBranch.ShouldBe("develop");
        reloaded.GitHubExecutable.ShouldBe(@"C:\tools\gh.exe");
    }

    [Fact]
    public void Retrospective_settings_round_trip_through_the_settings_file()
    {
        // Reported from use: the work-order button greyed out with "Set
        // GlassCoder:Retrospective:HarnessRepoPath". The setting had been there - the backup this
        // operator's own store took before the save on 2026-08-09 still holds it - and the save
        // deleted it, because GlassCoderSettings had no Retrospective section and Save() writes
        // the whole file from that model. Every save since has re-deleted it. The section is only
        // reachable by hand-editing the file, which makes surviving a save the whole contract.
        using TempWorkspace workspace = new();
        UserSettingsStore store = new(new DpapiSecretProtector(), workspace.Root);

        GlassCoderSettings saved = Settings();
        saved.Retrospective.HarnessRepoPath = @"C:\repos\GlassCoder";
        saved.Retrospective.MaxBudgetUsd = 8.0m;
        store.Save(saved);

        RetrospectiveOptions reloaded = GlassCoderSettings.ReadFrom(Configuration(store)).Retrospective;

        reloaded.HarnessRepoPath.ShouldBe(@"C:\repos\GlassCoder");
        reloaded.MaxBudgetUsd.ShouldBe(8.0m);

        // And the second save is the one that used to do the damage: the dialog reads the
        // effective configuration back and writes it out again every time it is opened.
        store.Save(GlassCoderSettings.ReadFrom(Configuration(store)));
        GlassCoderSettings.ReadFrom(Configuration(store)).Retrospective.HarnessRepoPath
            .ShouldBe(@"C:\repos\GlassCoder");
    }

    [Fact]
    public void File_review_settings_round_trip_through_the_settings_file()
    {
        // The same hole, one section over, found while fixing the first: the property was on
        // GlassCoderSettings and ReadFrom never filled it, so a save wrote defaults over it.
        using TempWorkspace workspace = new();
        UserSettingsStore store = new(new DpapiSecretProtector(), workspace.Root);

        GlassCoderSettings saved = Settings();
        saved.FileReview.Model = "claude-fable-5";
        store.Save(saved);

        store.Save(GlassCoderSettings.ReadFrom(Configuration(store)));

        GlassCoderSettings.ReadFrom(Configuration(store)).FileReview.Model.ShouldBe("claude-fable-5");
    }

    [Fact]
    public void Every_section_on_the_settings_model_is_read_back_from_configuration()
    {
        // The guard, rather than two more fixes. A section ReadFrom skips is a section the file
        // silently loses on the next save, and the only symptom is a feature that stops working
        // weeks later - which is how GlassCoder:Retrospective went. Written by reflection so the
        // next section added to the model is covered the day it is added, not the day it breaks.
        List<string> unread = [];

        foreach (PropertyInfo section in typeof(GlassCoderSettings).GetProperties()
            .Where(p => p.PropertyType is { IsClass: true, IsAbstract: false } && p.PropertyType != typeof(string)))
        {
            if (section.PropertyType.GetField("SectionName", BindingFlags.Public | BindingFlags.Static)
                    ?.GetValue(null) is not string sectionName ||
                Scalar(section.PropertyType) is not { } scalar)
            {
                continue;
            }

            IConfiguration configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    [$"{sectionName}:{scalar.Name}"] = Marker(scalar.PropertyType),
                })
                .Build();

            object? read = scalar.GetValue(section.GetValue(GlassCoderSettings.ReadFrom(configuration)));
            if (!string.Equals(read?.ToString(), Marker(scalar.PropertyType), StringComparison.Ordinal))
            {
                unread.Add($"{section.Name} ({sectionName}:{scalar.Name})");
            }
        }

        unread.ShouldBeEmpty("every section of the model must be bound by ReadFrom, or a save drops it");
    }

    /// <summary>A settable scalar to prove a section was bound, or null when it has none.</summary>
    private static PropertyInfo? Scalar(Type options) =>
        options.GetProperties()
            .FirstOrDefault(p => p.CanWrite && p.PropertyType == typeof(string))
        ?? options.GetProperties()
            .FirstOrDefault(p => p.CanWrite && p.PropertyType == typeof(int));

    private static string Marker(Type type) => type == typeof(int) ? "4242" : "marker-value";

    [Fact]
    public void The_branch_lists_do_not_grow_on_every_visit_to_the_dialog()
    {
        using TempWorkspace workspace = new();
        UserSettingsStore store = new(new DpapiSecretProtector(), workspace.Root);

        GlassCoderSettings settings = Settings();
        settings.Git.Enabled = true;
        settings.Git.ProtectedBranches.Add("main");

        for (int visit = 0; visit < 3; visit++)
        {
            store.Save(settings);
            settings = GlassCoderSettings.ReadFrom(Configuration(store));
        }

        settings.Git.ProtectedBranches.ShouldBe(["main"]);
    }

    [Fact]
    public void An_unusable_remote_is_refused_before_it_becomes_a_puzzling_tool_failure()
    {
        GlassCoderSettings settings = Settings();
        settings.Git.Enabled = true;
        settings.Git.Remote = "--mirror";

        settings.Validate().ShouldContain(f => f.Contains("GlassCoder:Git:Remote", StringComparison.Ordinal));
    }

    [Fact]
    public void A_branch_policy_that_refuses_everything_is_refused_itself()
    {
        GlassCoderSettings settings = Settings();
        settings.Git.Enabled = true;
        settings.Git.PushableBranches.Add("main");
        settings.Git.ProtectedBranches.Add("main");

        settings.Validate().ShouldContain(f => f.Contains("nothing could ever be pushed", StringComparison.Ordinal));
    }

    [Fact]
    public void Git_settings_are_not_validated_while_the_tools_are_switched_off()
    {
        // Nothing can call git, so a name that would be unusable is not worth blocking a save on.
        GlassCoderSettings settings = Settings();
        settings.Git.Enabled = false;
        settings.Git.Remote = "--mirror";

        settings.Validate().ShouldNotContain(f => f.Contains("GlassCoder:Git", StringComparison.Ordinal));
    }

    [Fact]
    public void A_list_setting_does_not_grow_every_time_the_dialog_is_opened()
    {
        using TempWorkspace workspace = new();
        UserSettingsStore store = new(new DpapiSecretProtector(), workspace.Root);

        // The binder appends to a list that already holds defaults, so a naive save-then-load
        // doubles the denied globs on every visit.
        GlassCoderSettings settings = Settings();
        int defaults = settings.Workspace.DeniedGlobs.Count;

        for (int visit = 0; visit < 3; visit++)
        {
            store.Save(settings);
            settings = GlassCoderSettings.ReadFrom(Configuration(store));
        }

        settings.Workspace.DeniedGlobs.Count.ShouldBe(defaults);
    }

    [Fact]
    public void The_endpoints_offered_by_the_dialog_survive_a_save_and_do_not_multiply()
    {
        using TempWorkspace workspace = new();
        UserSettingsStore store = new(new DpapiSecretProtector(), workspace.Root);

        // The picker is a list on the Models section, so it is subject to the same append-on-bind
        // behaviour that doubles every other list. Three visits, because doubling needs two to
        // show and a wrong fix can still be wrong on the third.
        GlassCoderSettings settings = Settings();
        settings.Models.KnownEndpoints.Add("http://localhost:9001/v1");
        settings.Models.KnownEndpoints.Add("https://api.anthropic.com");

        for (int visit = 0; visit < 3; visit++)
        {
            store.Save(settings);
            settings = GlassCoderSettings.ReadFrom(Configuration(store));
        }

        settings.Models.KnownEndpoints.ShouldBe(["http://localhost:9001/v1", "https://api.anthropic.com"]);
    }

    [Fact]
    public void Settings_that_would_stop_the_harness_from_starting_are_reported_before_they_are_saved()
    {
        GlassCoderSettings settings = Settings();
        settings.Models.Roles["worker"].ModelAlias = "/models/qwen3/checkpoint-1200";
        settings.Agent.Role = "nonexistent";

        IReadOnlyList<string> failures = settings.Validate();

        failures.ShouldContain(failure => failure.Contains("checkpoint path", StringComparison.Ordinal));
        failures.ShouldContain(failure => failure.Contains("'nonexistent'", StringComparison.Ordinal));
    }

    [Fact]
    public void Clearing_the_settings_falls_back_to_the_layer_below()
    {
        using TempWorkspace workspace = new();
        UserSettingsStore store = new(new DpapiSecretProtector(), workspace.Root);

        store.Save(Settings(apiKey: "sk-live-abcdefghijklmnop"));
        store.Exists.ShouldBeTrue();

        store.Clear();

        store.Exists.ShouldBeFalse();
        store.LoadSecrets().ShouldBeEmpty();
    }

    private static GlassCoderSettings Settings(string? apiKey = null)
    {
        GlassCoderSettings settings = new();
        settings.Models.Roles["worker"] = new ModelRoleOptions
        {
            Endpoint = "http://localhost:9001/v1",
            ModelAlias = "worker",
            ApiKey = apiKey,
        };

        return settings;
    }

    private static IConfiguration Configuration(IUserSettingsStore store) =>
        new ConfigurationBuilder().AddGlassCoderUserSettings(store).Build();
}
