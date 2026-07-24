using GlassCoder.Core.Configuration;
using GlassCoder.Core.DependencyInjection;
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

        if (!string.IsNullOrWhiteSpace(configPath))
        {
            builder.Configuration.AddJsonFile(Path.GetFullPath(configPath), optional: false, reloadOnChange: false);
        }

        builder.Services.AddSingleton<ISecretProtector>(protector);
        builder.Services.AddSingleton<IUserSettingsStore>(userSettings);
        builder.Services.AddGlassCoderLogging(builder.Configuration);
        builder.Services.AddGlassCoder(builder.Configuration);

        return builder;
    }
}
