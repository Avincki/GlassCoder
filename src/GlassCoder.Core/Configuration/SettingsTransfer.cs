using System.Text.Json;
using System.Text.Json.Nodes;

namespace GlassCoder.Core.Configuration;

/// <summary>
/// Carries a whole configuration between machines as one file (CLAUDE.md §13).
/// <para>
/// The settings folder is already openable and <c>settings.json</c> is already plain JSON, so
/// copying the settings themselves needs no feature. What copying cannot do is carry the API
/// keys: <c>secrets.json</c> holds DPAPI ciphertext bound to one Windows account, so it arrives
/// on the second machine decrypting to nothing. This is the seam that solves only that - keys
/// leave DPAPI under a passphrase on the way out and go back under DPAPI on the way in.
/// </para>
/// </summary>
public interface ISettingsTransfer
{
    /// <summary>Extension of an exported file, for the file pickers.</summary>
    string FileExtension { get; }

    /// <summary>
    /// Writes every section to <paramref name="path"/>.
    /// </summary>
    /// <param name="settings">What to write.</param>
    /// <param name="path">Where to write it.</param>
    /// <param name="passphrase">
    /// Encrypts the API keys. <see langword="null"/> or empty leaves them out of the file
    /// entirely - there is no option that writes a key in the clear, because a file that quietly
    /// contains one is how keys reach places nobody meant to send them.
    /// </param>
    /// <returns>How many keys were written.</returns>
    int Export(GlassCoderSettings settings, string path, string? passphrase);

    /// <summary>Whether <paramref name="path"/> carries keys, so a caller knows to ask for the passphrase.</summary>
    bool ContainsKeys(string path);

    /// <summary>Reads a file written by <see cref="Export"/>.</summary>
    /// <param name="path">The file to read.</param>
    /// <param name="passphrase">
    /// What the keys were encrypted with. <see langword="null"/> or empty imports the settings and
    /// leaves the keys behind rather than failing.
    /// </param>
    /// <exception cref="SettingsTransferException">
    /// The file is not a GlassCoder configuration, or the passphrase does not open it.
    /// </exception>
    ImportedSettings Import(string path, string? passphrase);
}

/// <summary>What an import produced, and what it could not.</summary>
/// <param name="Settings">The imported configuration.</param>
/// <param name="KeysRestored">API keys decrypted and bound.</param>
/// <param name="KeysWithheld">Keys the file carried that no passphrase was supplied for.</param>
public sealed record ImportedSettings(GlassCoderSettings Settings, int KeysRestored, int KeysWithheld);

/// <summary>
/// A file that cannot be imported, with a message written for the status line rather than for a
/// stack trace: the two ways this fails - wrong file, wrong passphrase - are both things the
/// operator can fix, and neither is worth taking the application down for.
/// </summary>
public sealed class SettingsTransferException : Exception
{
    /// <summary>Creates the exception.</summary>
    public SettingsTransferException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception.</summary>
    public SettingsTransferException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>Creates the exception.</summary>
    public SettingsTransferException()
    {
    }
}

/// <summary>
/// The default <see cref="ISettingsTransfer"/>.
/// <para>
/// The exported file keeps the single <c>GlassCoder</c> root the binder reads, with the protected
/// keys in a sibling <c>Secrets</c> property. That is not decoration: it means an export is also a
/// valid <c>--config</c> file, so one can be handed straight to the console host as an ablation
/// arm without being imported anywhere. The binder ignores the sibling property; only this class
/// looks at it.
/// </para>
/// </summary>
public sealed class SettingsTransfer : ISettingsTransfer
{
    /// <summary>Extension of an exported file.</summary>
    public const string Extension = ".glassconfig";

    private const string SchemeProperty = "Scheme";
    private const string SaltProperty = "Salt";
    private const string IterationsProperty = "Iterations";
    private const string ValuesProperty = "Values";

    /// <inheritdoc />
    public string FileExtension => Extension;

    /// <inheritdoc />
    public int Export(GlassCoderSettings settings, string path, string? passphrase)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        JsonObject document = SettingsDocument.Serialize(settings);

        if (string.IsNullOrWhiteSpace(passphrase))
        {
            // No passphrase means no keys. Removing them is the whole of the work.
            SettingsDocument.LiftApiKeys(settings, document, protector: null);
            SettingsDocument.WriteAtomically(path, document.ToJsonString(SettingsDocument.FileJson));
            return 0;
        }

        PassphraseSecretProtector protector = new(passphrase, PassphraseSecretProtector.NewSalt());
        Dictionary<string, string> keys = SettingsDocument.LiftApiKeys(settings, document, protector);

        if (keys.Count > 0)
        {
            JsonObject values = [];
            foreach ((string key, string value) in keys)
            {
                values[key] = value;
            }

            document[SettingsDocument.SecretsPropertyName] = new JsonObject
            {
                [SchemeProperty] = PassphraseSecretProtector.SchemeName,
                [IterationsProperty] = protector.Iterations,
                [SaltProperty] = Convert.ToBase64String(protector.Salt),
                [ValuesProperty] = values,
            };
        }

        SettingsDocument.WriteAtomically(path, document.ToJsonString(SettingsDocument.FileJson));
        return keys.Count;
    }

    /// <inheritdoc />
    public bool ContainsKeys(string path)
    {
        try
        {
            return ReadKeys(Read(path)).Values.Count > 0;
        }
        catch (SettingsTransferException)
        {
            // Whether a file nobody can read carries keys is not a question worth answering here;
            // Import says what is wrong with it, in one place.
            return false;
        }
    }

    /// <inheritdoc />
    public ImportedSettings Import(string path, string? passphrase)
    {
        JsonObject document = Read(path);
        ProtectedKeys keys = ReadKeys(document);

        // Removed before the document is bound: the keys travel as a sibling property rather than
        // as configuration, and leaving it in place would copy the ciphertext into whatever this
        // import is saved to next. Their values are already copied out, so this detaches nothing
        // still in use.
        document.Remove(SettingsDocument.SecretsPropertyName);

        if (keys.Values.Count == 0 || string.IsNullOrWhiteSpace(passphrase))
        {
            return new ImportedSettings(SettingsDocument.Bind(document, []), KeysRestored: 0, keys.Values.Count);
        }

        return Decrypt(document, keys, passphrase);
    }

    private static ImportedSettings Decrypt(JsonObject document, ProtectedKeys keys, string passphrase)
    {
        byte[] salt;
        try
        {
            salt = Convert.FromBase64String(keys.Salt);
        }
        catch (FormatException ex)
        {
            throw new SettingsTransferException(
                "The file's key material is malformed, so its API keys cannot be read. Import it without a " +
                "passphrase to bring the settings across, and enter the keys again.", ex);
        }

        PassphraseSecretProtector protector = new(passphrase, salt, keys.Iterations);

        List<KeyValuePair<string, string?>> decrypted = [];
        foreach ((string key, string value) in keys.Values)
        {
            if (protector.Unprotect(value) is { } plaintext)
            {
                decrypted.Add(new KeyValuePair<string, string?>(key, plaintext));
            }
        }

        // AES-GCM authenticates, so a value that fails to decrypt is a value that was not
        // encrypted under this passphrase. All of them failing means one wrong answer, not a
        // corrupt file, and saying so is more use than importing settings with silently no keys.
        if (decrypted.Count == 0)
        {
            throw new SettingsTransferException(
                "That passphrase does not open this file's API keys. The settings were not imported.");
        }

        return new ImportedSettings(
            SettingsDocument.Bind(document, decrypted),
            decrypted.Count,
            keys.Values.Count - decrypted.Count);
    }

    private static JsonObject Read(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        JsonNode? parsed;
        try
        {
            parsed = JsonNode.Parse(File.ReadAllText(path));
        }
        catch (JsonException ex)
        {
            throw new SettingsTransferException($"'{Path.GetFileName(path)}' is not valid JSON: {ex.Message}", ex);
        }

        if (parsed is not JsonObject document || document[GlassCoderSettings.RootSectionName] is not JsonObject)
        {
            throw new SettingsTransferException(
                $"'{Path.GetFileName(path)}' is not a GlassCoder configuration file: it has no " +
                $"'{GlassCoderSettings.RootSectionName}' section.");
        }

        return document;
    }

    /// <summary>
    /// Copies the protected keys out of the document, so what follows works on plain strings and
    /// nothing depends on a node that is about to be detached.
    /// </summary>
    private static ProtectedKeys ReadKeys(JsonObject document)
    {
        if (document[SettingsDocument.SecretsPropertyName] is not JsonObject secrets ||
            secrets[ValuesProperty] is not JsonObject values)
        {
            return new ProtectedKeys(string.Empty, PassphraseSecretProtector.DefaultIterations, []);
        }

        Dictionary<string, string> copied = new(StringComparer.OrdinalIgnoreCase);
        foreach ((string key, JsonNode? value) in values)
        {
            if (value?.GetValueKind() == JsonValueKind.String)
            {
                copied[key] = value.GetValue<string>();
            }
        }

        string salt = secrets[SaltProperty]?.GetValueKind() == JsonValueKind.String
            ? secrets[SaltProperty]!.GetValue<string>()
            : string.Empty;

        int iterations = secrets[IterationsProperty]?.GetValueKind() == JsonValueKind.Number
            ? secrets[IterationsProperty]!.GetValue<int>()
            : PassphraseSecretProtector.DefaultIterations;

        return new ProtectedKeys(salt, Math.Max(1, iterations), copied);
    }

    private sealed record ProtectedKeys(string Salt, int Iterations, Dictionary<string, string> Values);
}
