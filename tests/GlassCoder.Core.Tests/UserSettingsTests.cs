using GlassCoder.Core.Agent;
using GlassCoder.Core.Configuration;
using GlassCoder.Models.Configuration;
using GlassCoder.TestSupport;
using Microsoft.Extensions.Configuration;

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
