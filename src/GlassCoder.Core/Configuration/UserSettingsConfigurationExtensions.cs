using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.EnvironmentVariables;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.FileProviders.Physical;

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
    /// <para>
    /// The scan runs <b>backwards</b>, and that detail is load-bearing.
    /// <see cref="Microsoft.Extensions.Hosting.HostApplicationBuilder"/> registers
    /// <em>two</em> environment-variable sources: the <c>DOTNET_</c>-prefixed host one before
    /// <c>appsettings.json</c>, and the unprefixed application one after it. Stopping at the
    /// first put saved settings underneath <c>appsettings.json</c>, so every saved value that
    /// also appears there - which is nearly all of them - was silently discarded at startup.
    /// The last environment source is the one this must land in front of.
    /// </para>
    /// </summary>
    public static IConfigurationBuilder AddGlassCoderUserSettings(
        this IConfigurationBuilder builder,
        IUserSettingsStore store)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(store);

        InsertBeforeEnvironment(builder, BuildSources(store));
        return builder;
    }

    /// <summary>
    /// Layers a project's own <c>.glasscoder.json</c> over the per-user settings.
    /// <para>
    /// It goes in <em>after</em> the per-user file and still ahead of the environment, so the
    /// project wins over the machine-wide default - which is the whole point, since the machine
    /// cannot know one project's writable paths from another's - while an ablation arm still wins
    /// over the project.
    /// </para>
    /// <para>
    /// The root is also asserted here rather than read from the file. The file's location is the
    /// root; a path written inside it would only be a way to be wrong once the project is moved or
    /// cloned somewhere else.
    /// </para>
    /// </summary>
    /// <param name="builder">The configuration being built.</param>
    /// <param name="projectRoot">The repository the agent is working on.</param>
    public static IConfigurationBuilder AddGlassCoderProjectSettings(
        this IConfigurationBuilder builder,
        string projectRoot)
    {
        ArgumentNullException.ThrowIfNull(builder);

        if (string.IsNullOrWhiteSpace(projectRoot))
        {
            return builder;
        }

        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(projectRoot));
        string path = Path.Combine(root, ProjectSettingsStore.ProjectFileName);

        if (!File.Exists(path))
        {
            // Nothing is inserted at all when there is no file. An optional source pointing at a
            // path that does not exist would work, but it would also make every launch look as
            // though a project layer were in force.
            return builder;
        }

        // The file provider is built by hand, and that is not incidental. AddJsonFile(absolutePath)
        // resolves a PhysicalFileProvider with ExclusionFilters.Sensitive, which refuses to serve
        // any dot-prefixed file - so a project file named like every other project file would be
        // skipped, and skipped silently, because the source is optional.
        ConfigurationBuilder scratch = new();
        scratch.AddJsonFile(
            new PhysicalFileProvider(root, ExclusionFilters.None),
            ProjectSettingsStore.ProjectFileName,
            optional: true,
            reloadOnChange: false);

        scratch.AddInMemoryCollection(new Dictionary<string, string?>
        {
            [$"{GlassCoder.Tools.Guardrails.WorkspaceOptions.SectionName}:RepoRoot"] = root,
        });

        InsertBeforeEnvironment(builder, [.. scratch.Sources]);
        return builder;
    }

    /// <summary>
    /// Inserts sources immediately ahead of the last environment-variable source, preserving the
    /// order they are given in.
    /// </summary>
    private static void InsertBeforeEnvironment(IConfigurationBuilder builder, List<IConfigurationSource> sources)
    {
        int index = builder.Sources.Count;
        for (int i = builder.Sources.Count - 1; i >= 0; i--)
        {
            if (builder.Sources[i] is EnvironmentVariablesConfigurationSource)
            {
                index = i;
                break;
            }
        }

        foreach (IConfigurationSource source in sources)
        {
            builder.Sources.Insert(index++, source);
        }
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
