using GlassCoder.Core.Configuration;
using GlassCoder.Core.DependencyInjection;
using GlassCoder.Tools.Guardrails;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace GlassCoder.Core.Hosting;

/// <summary>
/// The one bootstrap both front ends use (CLAUDE.md §4, workplan task 3).
/// <para>
/// The WPF app and the console host must resolve the same services from the same configuration,
/// or they slowly become two different agents and no measurement taken in one applies to the
/// other.
/// </para>
/// </summary>
public static class GlassCoderHost
{
    /// <summary>
    /// Creates a host builder with GlassCoder configuration, logging and services registered.
    /// </summary>
    /// <param name="args">Command-line arguments, which become the highest-precedence config source.</param>
    /// <param name="configPath">
    /// Optional configuration file layered over <c>appsettings.json</c>. This is how an ablation
    /// arm is selected: one file, no code change.
    /// </param>
    public static HostApplicationBuilder CreateBuilder(string[]? args = null, string? configPath = null)
    {
        // The content root is the application directory so appsettings.json is found wherever
        // the process is launched from; the *working* directory stays free to mean "the
        // repository the agent is working on".
        HostApplicationBuilder builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            Args = args,
            ContentRootPath = AppContext.BaseDirectory,
        });

        // What the settings dialog saved, layered over appsettings.json and under everything a
        // run states explicitly. Both front ends get it from here, so the desktop app and the
        // console host still resolve the same services from the same configuration.
        DpapiSecretProtector protector = new();
        UserSettingsStore userSettings = new(protector);
        builder.Configuration.AddGlassCoderUserSettings(userSettings);

        // The project's own settings, if it has any. Layered here rather than in one front end so
        // a console run and a window opened on the same repository still agree about it.
        UseProjectSettings(builder);

        if (!string.IsNullOrWhiteSpace(configPath))
        {
            builder.Configuration.AddJsonFile(Path.GetFullPath(configPath), optional: false, reloadOnChange: false);
        }

        builder.Services.AddSingleton<ISecretProtector>(protector);
        builder.Services.AddSingleton<IUserSettingsStore>(userSettings);
        builder.Services.AddSingleton<IProjectSettingsStore, ProjectSettingsStore>();
        builder.Services.AddSingleton<ISettingsTransfer, SettingsTransfer>();
        builder.Services.AddGlassCoderLogging(builder.Configuration);
        builder.Services.AddGlassCoder(builder.Configuration);

        return builder;
    }

    /// <summary>
    /// Layers the project's <c>.glasscoder.json</c> over whatever the configuration says so far.
    /// <para>
    /// Called once during <see cref="CreateBuilder"/>, which is enough for the console host - it
    /// is run from the repository it works on, so the unset root really does mean "here". The
    /// desktop app calls it a second time after it has discovered a root, because for a
    /// double-clicked window "here" is the folder the executable lives in and the project file is
    /// somewhere else entirely.
    /// </para>
    /// </summary>
    public static void UseProjectSettings(HostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        string? configured = builder.Configuration[$"{WorkspaceOptions.SectionName}:RepoRoot"];
        string root = WorkspaceRootLocator.IsUnset(configured)
            ? Directory.GetCurrentDirectory()
            : configured!;

        builder.Configuration.AddGlassCoderProjectSettings(root);
    }
}
