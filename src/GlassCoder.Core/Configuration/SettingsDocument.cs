using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using GlassCoder.Models.Configuration;
using Microsoft.Extensions.Configuration;

namespace GlassCoder.Core.Configuration;

/// <summary>
/// The shape settings take on disk, in the one place every writer shares (CLAUDE.md §13).
/// <para>
/// Three files now carry settings - the per-user <c>settings.json</c>, a project's
/// <c>.glasscoder.json</c>, and an exported <c>.glassconfig</c> - and all three must produce a
/// document the configuration binder can read back. More importantly, all three must lift the API
/// keys out of the body the same way. A second implementation of that step is a second chance to
/// leave a key in a file that gets committed, so there is only one.
/// </para>
/// </summary>
public static class SettingsDocument
{
    /// <summary>Property on an exported document that carries the protected keys.</summary>
    public const string SecretsPropertyName = "Secrets";

    /// <summary>
    /// The sections whose values name things <em>inside a repository</em> - paths, context files,
    /// reference directories, branches - and which are therefore wrong the moment the agent is
    /// pointed at a different project.
    /// <para>
    /// Everything else (the served roles and their endpoints, the sandbox, logging, telemetry,
    /// loop budgets) describes the machine or the experiment rather than the repository, and stays
    /// where one copy serves every project.
    /// </para>
    /// </summary>
    public static IReadOnlyList<string> ProjectSectionNames { get; } =
    [
        nameof(GlassCoderSettings.Workspace),
        nameof(GlassCoderSettings.Context),
        nameof(GlassCoderSettings.Verification),
        nameof(GlassCoderSettings.VerificationLadder),
        nameof(GlassCoderSettings.Git),
        nameof(GlassCoderSettings.Provenance),
    ];

    /// <summary>
    /// How settings are written: indented, enums as names. <c>SandboxMode</c> is <c>Docker</c> or
    /// <c>Local</c> in the file, not <c>0</c> or <c>1</c> - the binder reads either, but only one
    /// of them survives a human reading the file.
    /// </summary>
    public static JsonSerializerOptions FileJson { get; } = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>The configuration key an API key for <paramref name="role"/> is bound from.</summary>
    public static string ApiKeyConfigurationKey(string role) =>
        $"{ModelsOptions.SectionName}:Roles:{role}:{nameof(ModelRoleOptions.ApiKey)}";

    /// <summary>
    /// Serialises settings into the single-rooted document the configuration binder reads, so
    /// every file written here is also a file that can be passed to <c>--config</c>.
    /// </summary>
    public static JsonObject Serialize(GlassCoderSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        return new JsonObject
        {
            [GlassCoderSettings.RootSectionName] = JsonSerializer.SerializeToNode(settings, FileJson),
        };
    }

    /// <summary>
    /// Lifts every role's API key out of <paramref name="document"/> and returns them, protected,
    /// as flat configuration keys.
    /// <para>
    /// The <c>ApiKey</c> property is <em>removed</em> rather than nulled, so the document contains
    /// no trace of a key having been there. Passing a null <paramref name="protector"/> removes
    /// the keys and returns nothing, which is what a file that must never carry a key wants.
    /// </para>
    /// </summary>
    public static Dictionary<string, string> LiftApiKeys(
        GlassCoderSettings settings,
        JsonObject document,
        ISecretProtector? protector)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(document);

        Dictionary<string, string> secrets = new(StringComparer.OrdinalIgnoreCase);

        JsonNode? roles = document[GlassCoderSettings.RootSectionName]?[nameof(GlassCoderSettings.Models)]
            ?[nameof(ModelsOptions.Roles)];

        foreach ((string role, ModelRoleOptions options) in settings.Models.Roles)
        {
            (roles?[role] as JsonObject)?.Remove(nameof(ModelRoleOptions.ApiKey));

            if (protector is not null && !string.IsNullOrWhiteSpace(options.ApiKey))
            {
                secrets[ApiKeyConfigurationKey(role)] = protector.Protect(options.ApiKey);
            }
        }

        return secrets;
    }

    /// <summary>
    /// Narrows a document to the named sections, dropping the rest. Used to write a project file
    /// that says only what is true of that project.
    /// </summary>
    public static JsonObject KeepOnly(JsonObject document, IEnumerable<string> sectionNames)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(sectionNames);

        HashSet<string> keep = new(sectionNames, StringComparer.Ordinal);

        if (document[GlassCoderSettings.RootSectionName] is not JsonObject root)
        {
            return document;
        }

        foreach (string section in root.Select(property => property.Key).ToList())
        {
            if (!keep.Contains(section))
            {
                root.Remove(section);
            }
        }

        return document;
    }

    /// <summary>
    /// Builds configuration from a settings document plus a set of decrypted keys, binding it the
    /// way the harness binds its own - so an imported file produces exactly the settings it would
    /// produce if it were the file in force.
    /// </summary>
    public static GlassCoderSettings Bind(JsonObject document, IEnumerable<KeyValuePair<string, string?>> secrets)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(secrets);

        using MemoryStream json = new(Encoding.UTF8.GetBytes(document.ToJsonString(FileJson)));

        // Bound from the document alone rather than layered over appsettings.json: an imported
        // file has to mean what it says, not what it says blended with this machine's defaults.
        ConfigurationBuilder builder = new();
        builder.AddJsonStream(json);
        builder.AddInMemoryCollection(secrets);

        return GlassCoderSettings.ReadFrom(builder.Build());
    }

    /// <summary>
    /// Writes through a temporary file so an interrupted write leaves the previous content intact
    /// rather than a half-written file nothing can start from.
    /// </summary>
    public static void WriteAtomically(string path, string content)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string temporary = path + ".tmp";
        File.WriteAllText(temporary, content);
        File.Move(temporary, path, overwrite: true);
    }
}
