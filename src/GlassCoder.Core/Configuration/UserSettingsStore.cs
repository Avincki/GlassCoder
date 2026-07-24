using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using GlassCoder.Models.Configuration;

namespace GlassCoder.Core.Configuration;

/// <summary>
/// The default <see cref="IUserSettingsStore"/>: two JSON files under the user's application
/// data directory.
/// <para>
/// Not the copy of <c>appsettings.json</c> next to the executable - that file is build output
/// (<c>PreserveNewest</c>), so anything saved into it is one rebuild away from being silently
/// discarded. A per-user file also keeps one operator's endpoints and keys out of another's,
/// and out of the repository.
/// </para>
/// </summary>
public sealed class UserSettingsStore : IUserSettingsStore
{
    /// <summary>Editable configuration, layered over <c>appsettings.json</c>.</summary>
    public const string SettingsFileName = "settings.json";

    /// <summary>Protected API keys. Never contains anything else.</summary>
    public const string SecretsFileName = "secrets.json";

    /// <summary>Overrides the settings directory. Set it to make a portable or test install.</summary>
    public const string DirectoryEnvironmentVariable = "GLASSCODER_SETTINGS_DIR";

    private static readonly JsonSerializerOptions FileJson = new()
    {
        WriteIndented = true,
        // Enums as names: SandboxMode is Docker or Local in the file, not 0 or 1. The binder
        // reads either, but only one of them survives a human reading the file.
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly ISecretProtector _protector;

    /// <summary>Creates the store.</summary>
    /// <param name="protector">Protects API keys at rest.</param>
    /// <param name="directory">
    /// Where the two files live. Defaults to <c>%APPDATA%\GlassCoder</c>, or whatever
    /// <see cref="DirectoryEnvironmentVariable"/> names.
    /// </param>
    public UserSettingsStore(ISecretProtector protector, string? directory = null)
    {
        ArgumentNullException.ThrowIfNull(protector);

        _protector = protector;
        DirectoryPath = ResolveDirectory(directory);
        SettingsFilePath = Path.Combine(DirectoryPath, SettingsFileName);
        SecretsFilePath = Path.Combine(DirectoryPath, SecretsFileName);
    }

    /// <inheritdoc />
    public string DirectoryPath { get; }

    /// <inheritdoc />
    public string SettingsFilePath { get; }

    /// <inheritdoc />
    public string SecretsFilePath { get; }

    /// <inheritdoc />
    public string ProtectionScheme => _protector.Scheme;

    /// <inheritdoc />
    public bool SecretsAreEncrypted => _protector.IsEncrypted;

    /// <inheritdoc />
    public bool Exists => File.Exists(SettingsFilePath) || File.Exists(SecretsFilePath);

    /// <summary>The configuration key an API key for <paramref name="role"/> is bound from.</summary>
    public static string ApiKeyConfigurationKey(string role) =>
        $"{ModelsOptions.SectionName}:Roles:{role}:{nameof(ModelRoleOptions.ApiKey)}";

    /// <inheritdoc />
    public IReadOnlyDictionary<string, string?> LoadSecrets()
    {
        Dictionary<string, string?> secrets = new(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(SecretsFilePath))
        {
            return secrets;
        }

        Dictionary<string, string>? stored;
        try
        {
            stored = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(SecretsFilePath));
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // An unreadable secrets file must not stop the harness from starting: it starts
            // without the keys, and every call that needed one fails with its own clear error.
            return secrets;
        }

        if (stored is null)
        {
            return secrets;
        }

        foreach ((string key, string value) in stored)
        {
            secrets[key] = _protector.Unprotect(value);
        }

        return secrets;
    }

    /// <inheritdoc />
    public void Save(GlassCoderSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        Directory.CreateDirectory(DirectoryPath);

        JsonObject document = new()
        {
            [GlassCoderSettings.RootSectionName] = JsonSerializer.SerializeToNode(settings, FileJson),
        };

        Dictionary<string, string> secrets = ExtractSecrets(settings, document);

        WriteAtomically(SettingsFilePath, document.ToJsonString(FileJson));

        if (secrets.Count > 0)
        {
            WriteAtomically(SecretsFilePath, JsonSerializer.Serialize(secrets, FileJson));
        }
        else if (File.Exists(SecretsFilePath))
        {
            File.Delete(SecretsFilePath);
        }
    }

    /// <inheritdoc />
    public void Clear()
    {
        File.Delete(SettingsFilePath);
        File.Delete(SecretsFilePath);
    }

    private static string ResolveDirectory(string? directory)
    {
        if (!string.IsNullOrWhiteSpace(directory))
        {
            return Path.GetFullPath(directory);
        }

        string? fromEnvironment = Environment.GetEnvironmentVariable(DirectoryEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(fromEnvironment))
        {
            return Path.GetFullPath(fromEnvironment);
        }

        string applicationData = Environment.GetFolderPath(
            Environment.SpecialFolder.ApplicationData,
            Environment.SpecialFolderOption.Create);

        return Path.Combine(applicationData, "GlassCoder");
    }

    /// <summary>
    /// Lifts every role's API key out of the serialised document and into the protected set.
    /// The <c>ApiKey</c> property is <em>removed</em> rather than nulled, so the settings file
    /// contains no trace of a key having been there.
    /// </summary>
    private Dictionary<string, string> ExtractSecrets(GlassCoderSettings settings, JsonObject document)
    {
        Dictionary<string, string> secrets = new(StringComparer.OrdinalIgnoreCase);

        JsonNode? roles = document[GlassCoderSettings.RootSectionName]?[nameof(GlassCoderSettings.Models)]
            ?[nameof(ModelsOptions.Roles)];

        foreach ((string role, ModelRoleOptions options) in settings.Models.Roles)
        {
            (roles?[role] as JsonObject)?.Remove(nameof(ModelRoleOptions.ApiKey));

            if (!string.IsNullOrWhiteSpace(options.ApiKey))
            {
                secrets[ApiKeyConfigurationKey(role)] = _protector.Protect(options.ApiKey);
            }
        }

        return secrets;
    }

    /// <summary>
    /// Writes through a temporary file so an interrupted save leaves the previous settings
    /// intact rather than a half-written file the harness cannot start from.
    /// </summary>
    private static void WriteAtomically(string path, string content)
    {
        string temporary = path + ".tmp";
        File.WriteAllText(temporary, content);
        File.Move(temporary, path, overwrite: true);
    }
}
