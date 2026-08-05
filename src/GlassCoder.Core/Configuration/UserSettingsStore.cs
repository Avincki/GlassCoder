using System.Text.Json;
using System.Text.Json.Nodes;

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
    public static string ApiKeyConfigurationKey(string role) => SettingsDocument.ApiKeyConfigurationKey(role);

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

        JsonObject document = SettingsDocument.Serialize(settings);
        Dictionary<string, string> secrets = SettingsDocument.LiftApiKeys(settings, document, _protector);

        SettingsDocument.WriteAtomically(SettingsFilePath, document.ToJsonString(SettingsDocument.FileJson));

        if (secrets.Count > 0)
        {
            SettingsDocument.WriteAtomically(
                SecretsFilePath, JsonSerializer.Serialize(secrets, SettingsDocument.FileJson));
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

}
