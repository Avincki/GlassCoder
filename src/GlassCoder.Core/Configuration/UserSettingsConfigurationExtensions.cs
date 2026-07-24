using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.EnvironmentVariables;

namespace GlassCoder.Core.Configuration;

/// <summary>
/// Layers the settings dialog's output into the configuration both front ends bind
/// (CLAUDE.md §4, §13).
/// </summary>
public static class UserSettingsConfigurationExtensions
{
    /// <summary>
    /// Inserts the per-user settings file and the decrypted API keys <em>ahead of</em> the
    /// environment-variable source.
    /// <para>
    /// Position is the whole point. Appending them would let a saved setting outrank
    /// <c>GlassCoder__Agent__MaxSteps=50</c> and the <c>--config</c> file that selects an
    /// ablation arm, which would make an arm mean something different on a machine where
    /// somebody had once opened the dialog. Saved settings therefore beat <c>appsettings.json</c>
    /// and lose to everything a run states explicitly.
    /// </para>
    /// </summary>
    public static IConfigurationBuilder AddGlassCoderUserSettings(
        this IConfigurationBuilder builder,
        IUserSettingsStore store)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(store);

        int index = builder.Sources.Count;
        for (int i = 0; i < builder.Sources.Count; i++)
        {
            if (builder.Sources[i] is EnvironmentVariablesConfigurationSource)
            {
                index = i;
                break;
            }
        }

        foreach (IConfigurationSource source in BuildSources(store))
        {
            builder.Sources.Insert(index++, source);
        }

        return builder;
    }

    private static List<IConfigurationSource> BuildSources(IUserSettingsStore store)
    {
        // Built through the ordinary helpers on a scratch builder, then moved: AddJsonFile is
        // what turns an absolute path into a file provider plus a file name, and AddInMemory
        // is what the secrets have to look like once they are decrypted. Re-implementing
        // either here would only be a way to get it subtly wrong.
        ConfigurationBuilder scratch = new();

        scratch.AddJsonFile(store.SettingsFilePath, optional: true, reloadOnChange: false);

        // A null value means "stored but not decryptable on this machine". Passing it through
        // would blank out a key that appsettings.json or an environment variable does supply.
        List<KeyValuePair<string, string?>> secrets = [];
        foreach ((string key, string? value) in store.LoadSecrets())
        {
            if (!string.IsNullOrEmpty(value))
            {
                secrets.Add(new KeyValuePair<string, string?>(key, value));
            }
        }

        if (secrets.Count > 0)
        {
            scratch.AddInMemoryCollection(secrets);
        }

        return [.. scratch.Sources];
    }
}
