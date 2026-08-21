using GlassCoder.Core.Configuration;
using GlassCoder.Models.Configuration;
using GlassCoder.TestSupport;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace GlassCoder.Core.Tests;

/// <summary>
/// Carrying a configuration to a second machine, and giving a project its own (CLAUDE.md §9, §13).
/// <para>
/// The properties worth asserting are the ones that would be silent if they broke: a key never
/// travels in the clear, a wrong passphrase says so rather than importing settings with no keys,
/// a project file never carries a key at all, and a project's settings beat the machine's while
/// still losing to an ablation arm.
/// </para>
/// </summary>
public sealed class SettingsTransferTests
{
    private const string Key = "sk-live-abcdefghijklmnop";
    private const string Passphrase = "correct horse battery staple";

    [Fact]
    public void An_exported_key_round_trips_under_its_passphrase_and_is_never_written_in_the_clear()
    {
        using TempWorkspace workspace = new();
        SettingsTransfer transfer = new();
        string path = Path.Combine(workspace.Root, "export.glassconfig");

        transfer.Export(Settings(Key), path, Passphrase).ShouldBe(1);

        File.ReadAllText(path).ShouldNotContain(Key);
        File.ReadAllText(path).ShouldNotContain("\"ApiKey\"", Case.Sensitive);

        ImportedSettings imported = transfer.Import(path, Passphrase);

        imported.KeysRestored.ShouldBe(1);
        imported.KeysWithheld.ShouldBe(0);
        imported.Settings.Models.Roles["worker"].ApiKey.ShouldBe(Key);
        imported.Settings.Models.Roles["worker"].Endpoint.ShouldBe("http://localhost:9001/v1");
    }

    [Fact]
    public void Every_other_setting_survives_the_round_trip()
    {
        using TempWorkspace workspace = new();
        SettingsTransfer transfer = new();
        string path = Path.Combine(workspace.Root, "export.glassconfig");

        GlassCoderSettings settings = Settings(Key);
        settings.Agent.MaxSteps = 42;
        settings.Sandbox.Mode = GlassCoder.Tools.Execution.SandboxMode.Local;
        settings.Workspace.WritablePaths.Add("src");
        settings.Git.Enabled = true;
        settings.Git.PushableBranches.Add("feature/x");

        transfer.Export(settings, path, Passphrase);
        GlassCoderSettings imported = transfer.Import(path, Passphrase).Settings;

        imported.Agent.MaxSteps.ShouldBe(42);
        imported.Sandbox.Mode.ShouldBe(GlassCoder.Tools.Execution.SandboxMode.Local);
        imported.Workspace.WritablePaths.ShouldContain("src");
        imported.Git.PushableBranches.ShouldContain("feature/x");
    }

    [Fact]
    public void The_wrong_passphrase_is_reported_rather_than_importing_settings_with_no_keys()
    {
        using TempWorkspace workspace = new();
        SettingsTransfer transfer = new();
        string path = Path.Combine(workspace.Root, "export.glassconfig");

        transfer.Export(Settings(Key), path, Passphrase);

        // Silently importing keyless settings would look like success, and the failure would only
        // surface later as an unexplained 401 from the endpoint.
        Should.Throw<SettingsTransferException>(() => transfer.Import(path, "not the passphrase"))
            .Message.ShouldContain("passphrase");
    }

    [Fact]
    public void No_passphrase_means_no_keys_in_the_file_rather_than_keys_in_the_clear()
    {
        using TempWorkspace workspace = new();
        SettingsTransfer transfer = new();
        string path = Path.Combine(workspace.Root, "export.glassconfig");

        transfer.Export(Settings(Key), path, passphrase: null).ShouldBe(0);

        File.ReadAllText(path).ShouldNotContain(Key);
        transfer.ContainsKeys(path).ShouldBeFalse();

        ImportedSettings imported = transfer.Import(path, passphrase: null);

        imported.KeysRestored.ShouldBe(0);
        imported.KeysWithheld.ShouldBe(0);
        imported.Settings.Models.Roles["worker"].ApiKey.ShouldBeNull();
        imported.Settings.Models.Roles["worker"].Endpoint.ShouldBe("http://localhost:9001/v1");
    }

    [Fact]
    public void A_file_with_keys_can_be_imported_without_them_and_says_how_many_stayed_behind()
    {
        using TempWorkspace workspace = new();
        SettingsTransfer transfer = new();
        string path = Path.Combine(workspace.Root, "export.glassconfig");

        transfer.Export(Settings(Key), path, Passphrase);
        transfer.ContainsKeys(path).ShouldBeTrue();

        ImportedSettings imported = transfer.Import(path, passphrase: null);

        imported.KeysRestored.ShouldBe(0);
        imported.KeysWithheld.ShouldBe(1);
        imported.Settings.Models.Roles["worker"].Endpoint.ShouldBe("http://localhost:9001/v1");
    }

    [Fact]
    public void An_export_is_also_a_valid_config_file()
    {
        // The reason the protected keys hang off a sibling property rather than being folded into
        // the settings: an exported file can be handed straight to --config as an ablation arm.
        using TempWorkspace workspace = new();
        SettingsTransfer transfer = new();
        string path = Path.Combine(workspace.Root, "export.glassconfig");

        GlassCoderSettings settings = Settings(Key);
        settings.Agent.MaxSteps = 42;
        transfer.Export(settings, path, Passphrase);

        IConfiguration configuration = new ConfigurationBuilder()
            .AddJsonFile(path, optional: false)
            .Build();

        configuration["GlassCoder:Agent:MaxSteps"].ShouldBe("42");
        configuration["GlassCoder:Models:Roles:worker:ApiKey"].ShouldBeNull();
    }

    [Fact]
    public void A_file_that_is_not_a_configuration_is_refused_by_name()
    {
        using TempWorkspace workspace = new();
        SettingsTransfer transfer = new();

        string notJson = workspace.WriteFile("notes.txt", "this is not JSON at all");
        string wrongShape = workspace.WriteFile("other.json", """{ "SomethingElse": { "A": 1 } }""");

        Should.Throw<SettingsTransferException>(() => transfer.Import(notJson, null));
        Should.Throw<SettingsTransferException>(() => transfer.Import(wrongShape, null))
            .Message.ShouldContain("GlassCoder");
    }

    [Fact]
    public void A_project_file_carries_the_project_sections_and_never_a_key()
    {
        using TempWorkspace project = new();
        ProjectSettingsStore store = new();

        GlassCoderSettings settings = Settings(Key);
        settings.Workspace.WritablePaths.Add("src");
        settings.Agent.MaxSteps = 42;

        string path = store.Save(settings, project.Root);
        string written = File.ReadAllText(path);

        written.ShouldNotContain(Key);
        written.ShouldNotContain("\"ApiKey\"", Case.Sensitive);
        written.ShouldNotContain("\"Models\"", Case.Sensitive);

        // Machine-shaped sections stay on the machine; a repository has no business restating the
        // loop budget for everyone who clones it.
        written.ShouldNotContain("\"Agent\"", Case.Sensitive);
        written.ShouldContain("\"Workspace\"", Case.Sensitive);
        written.ShouldContain("src");

        // The file's own location is the root, so writing an absolute path in would only be a way
        // to be wrong once the project is cloned somewhere else.
        written.ShouldNotContain("\"RepoRoot\"", Case.Sensitive);
    }

    [Fact]
    public void A_project_file_beats_the_machine_settings_for_that_project()
    {
        using TempWorkspace settingsDirectory = new();
        using TempWorkspace project = new();

        UserSettingsStore user = new(new DpapiSecretProtector(), settingsDirectory.Root);
        GlassCoderSettings machine = Settings();
        machine.Workspace.WritablePaths.Add("machine-wide");
        machine.Agent.MaxSteps = 42;
        user.Save(machine);

        GlassCoderSettings forProject = Settings();
        forProject.Workspace.WritablePaths.Add("only-this-project");
        new ProjectSettingsStore().Save(forProject, project.Root);

        IConfiguration configuration = new ConfigurationBuilder()
            .AddGlassCoderUserSettings(user)
            .AddGlassCoderProjectSettings(project.Root)
            .Build();

        configuration["GlassCoder:Workspace:WritablePaths:0"].ShouldBe(
            "only-this-project", "the project's own paths must win over the machine-wide ones");
        configuration["GlassCoder:Workspace:RepoRoot"].ShouldBe(
            project.Root, "the project file's location is what says where the project is");
        configuration["GlassCoder:Agent:MaxSteps"].ShouldBe(
            "42", "a section the project file does not mention still comes from the machine");
    }

    [Fact]
    public void An_environment_variable_still_beats_a_project_file()
    {
        // Same bargain saved settings have always had: a project must not quietly redefine what an
        // ablation arm means on the machine it runs on.
        using TempWorkspace application = new();
        using TempWorkspace settingsDirectory = new();
        using TempWorkspace project = new();

        application.WriteFile("appsettings.json", """{ "GlassCoder": { "Agent": { "MaxSteps": 30 } } }""");

        GlassCoderSettings forProject = Settings();
        forProject.Verification.ExtraReferenceDirectories.Add("from-the-project");
        new ProjectSettingsStore().Save(forProject, project.Root);

        Environment.SetEnvironmentVariable(
            "GlassCoder__Verification__ExtraReferenceDirectories__0", "from-the-environment");
        try
        {
            HostApplicationBuilder builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
            {
                ContentRootPath = application.Root,
            });

            builder.Configuration.AddGlassCoderUserSettings(
                new UserSettingsStore(new DpapiSecretProtector(), settingsDirectory.Root));
            builder.Configuration.AddGlassCoderProjectSettings(project.Root);

            builder.Configuration["GlassCoder:Verification:ExtraReferenceDirectories:0"]
                .ShouldBe("from-the-environment");
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                "GlassCoder__Verification__ExtraReferenceDirectories__0", null);
        }
    }

    [Fact]
    public void A_project_with_no_file_adds_no_layer_at_all()
    {
        using TempWorkspace project = new();

        IConfigurationBuilder builder = new ConfigurationBuilder();
        int before = builder.Sources.Count;

        builder.AddGlassCoderProjectSettings(project.Root);

        builder.Sources.Count.ShouldBe(before, "an absent project file must not look like one in force");
    }

    [Fact]
    public void A_passphrase_protector_refuses_what_another_passphrase_encrypted()
    {
        byte[] salt = PassphraseSecretProtector.NewSalt();

        // Few iterations: this asserts the authentication, not the key-stretching cost.
        PassphraseSecretProtector right = new(Passphrase, salt, iterations: 1000);
        PassphraseSecretProtector wrong = new("something else", salt, iterations: 1000);

        string stored = right.Protect(Key);

        stored.ShouldNotContain(Key);
        right.Unprotect(stored).ShouldBe(Key);
        wrong.Unprotect(stored).ShouldBeNull();
        right.Unprotect("aesgcm:not-base-64!").ShouldBeNull();
        right.Unprotect("hand-edited").ShouldBeNull();
    }

    [Fact]
    public void A_tampered_value_fails_to_decrypt_rather_than_yielding_a_wrong_key()
    {
        byte[] salt = PassphraseSecretProtector.NewSalt();
        PassphraseSecretProtector protector = new(Passphrase, salt, iterations: 1000);

        string stored = protector.Protect(Key);
        byte[] blob = Convert.FromBase64String(stored["aesgcm:".Length..]);
        blob[^1] ^= 0xFF;

        protector.Unprotect("aesgcm:" + Convert.ToBase64String(blob)).ShouldBeNull();
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
}
