using GlassCoder.Models.Configuration;

namespace GlassCoder.Core.Configuration;

/// <summary>
/// Reads and writes the per-user settings layer - the file the settings dialog saves to
/// (CLAUDE.md §13: every endpoint, budget and limit is configuration, never code).
/// <para>
/// Two files, for one reason: API keys are not settings. Everything editable goes to
/// <c>settings.json</c> in the clear, because a configuration file you cannot read is a
/// configuration file you cannot debug. Keys go to <c>secrets.json</c> through an
/// <see cref="ISecretProtector"/> and never appear in the settings file at all.
/// </para>
/// </summary>
public interface IUserSettingsStore
{
    /// <summary>Directory holding both files.</summary>
    string DirectoryPath { get; }

    /// <summary>Full path of the settings file.</summary>
    string SettingsFilePath { get; }

    /// <summary>Full path of the protected secrets file.</summary>
    string SecretsFilePath { get; }

    /// <summary>How secrets are protected at rest, for display.</summary>
    string ProtectionScheme { get; }

    /// <summary>Whether secrets are genuinely encrypted rather than merely encoded.</summary>
    bool SecretsAreEncrypted { get; }

    /// <summary>Whether anything has been saved yet.</summary>
    bool Exists { get; }

    /// <summary>
    /// The stored API keys, as flat configuration keys
    /// (<c>GlassCoder:Models:Roles:worker:ApiKey</c>) to their decrypted values. A
    /// <see langword="null"/> value means the entry exists but could not be decrypted on this
    /// machine, which the caller reports rather than silently treats as "no key".
    /// </summary>
    IReadOnlyDictionary<string, string?> LoadSecrets();

    /// <summary>
    /// Writes both files. Every <see cref="ModelRoleOptions.ApiKey"/> is moved into the
    /// protected file, so no caller can accidentally persist a key in the clear.
    /// </summary>
    void Save(GlassCoderSettings settings);

    /// <summary>Removes both files, dropping back to whatever <c>appsettings.json</c> says.</summary>
    void Clear();
}
