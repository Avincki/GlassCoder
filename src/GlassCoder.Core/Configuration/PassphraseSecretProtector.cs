using System.Security.Cryptography;
using System.Text;

namespace GlassCoder.Core.Configuration;

/// <summary>
/// The portable <see cref="ISecretProtector"/>: AES-GCM under a key derived from a passphrase
/// (CLAUDE.md §9).
/// <para>
/// <see cref="DpapiSecretProtector"/> is the right answer at rest and the wrong one in transit.
/// Its ciphertext is bound to one Windows account on one machine, so a settings file carried to a
/// second machine arrives with every key decrypting to nothing. That is a deliberate property of
/// DPAPI, not a defect - but it means an export needs a key the operator can carry in their head
/// rather than one the operating system holds.
/// </para>
/// <para>
/// So: PBKDF2-HMAC-SHA256 to turn the passphrase into a key, AES-GCM to encrypt each value under
/// a fresh nonce. GCM rather than CBC because it authenticates: a hand-edited or truncated export
/// fails to decrypt instead of yielding a plausible-looking wrong key that only fails later, at
/// the endpoint, as a confusing 401.
/// </para>
/// </summary>
public sealed class PassphraseSecretProtector : ISecretProtector
{
    /// <summary>Named in the exported file so a future reader knows what produced the values.</summary>
    public const string SchemeName = "aes-gcm-pbkdf2";

    /// <summary>
    /// PBKDF2 iterations. The OWASP floor for HMAC-SHA256, and about a third of a second here -
    /// paid twice per export, which nobody notices, and which costs an offline guesser dearly.
    /// </summary>
    public const int DefaultIterations = 600_000;

    /// <summary>Bytes of salt. Fresh per export, stored beside the values it protects.</summary>
    public const int SaltBytes = 16;

    private const string Prefix = "aesgcm:";
    private const int NonceBytes = 12;
    private const int TagBytes = 16;
    private const int KeyBytes = 32;

    private readonly byte[] _key;

    /// <summary>Derives the key. Deliberately the slow part: it runs once per file, not per value.</summary>
    /// <param name="passphrase">What the operator types. Must not be empty.</param>
    /// <param name="salt">Fresh random bytes when exporting; the file's salt when importing.</param>
    /// <param name="iterations">PBKDF2 iterations, read from the file when importing.</param>
    public PassphraseSecretProtector(string passphrase, byte[] salt, int iterations = DefaultIterations)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(passphrase);
        ArgumentNullException.ThrowIfNull(salt);
        ArgumentOutOfRangeException.ThrowIfLessThan(iterations, 1);

        Salt = salt;
        Iterations = iterations;
        _key = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(passphrase), salt, iterations, HashAlgorithmName.SHA256, KeyBytes);
    }

    /// <summary>The salt this instance derived its key from, for writing into the file.</summary>
    public byte[] Salt { get; }

    /// <summary>The iteration count this instance used, for writing into the file.</summary>
    public int Iterations { get; }

    /// <inheritdoc />
    public string Scheme => SchemeName;

    /// <inheritdoc />
    public bool IsEncrypted => true;

    /// <summary>Fresh salt for a new export.</summary>
    public static byte[] NewSalt() => RandomNumberGenerator.GetBytes(SaltBytes);

    /// <inheritdoc />
    public string Protect(string secret)
    {
        ArgumentNullException.ThrowIfNull(secret);

        byte[] plaintext = Encoding.UTF8.GetBytes(secret);
        byte[] nonce = RandomNumberGenerator.GetBytes(NonceBytes);
        byte[] tag = new byte[TagBytes];
        byte[] ciphertext = new byte[plaintext.Length];

        using (AesGcm aes = new(_key, TagBytes))
        {
            aes.Encrypt(nonce, plaintext, ciphertext, tag);
        }

        // nonce | tag | ciphertext, so a reader needs no lengths beyond the two constants.
        byte[] blob = new byte[nonce.Length + tag.Length + ciphertext.Length];
        nonce.CopyTo(blob, 0);
        tag.CopyTo(blob, nonce.Length);
        ciphertext.CopyTo(blob, nonce.Length + tag.Length);

        return Prefix + Convert.ToBase64String(blob);
    }

    /// <inheritdoc />
    public string? Unprotect(string stored)
    {
        if (string.IsNullOrWhiteSpace(stored) || !stored.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return null;
        }

        try
        {
            byte[] blob = Convert.FromBase64String(stored[Prefix.Length..]);
            if (blob.Length < NonceBytes + TagBytes)
            {
                return null;
            }

            byte[] plaintext = new byte[blob.Length - NonceBytes - TagBytes];
            using AesGcm aes = new(_key, TagBytes);
            aes.Decrypt(
                blob.AsSpan(0, NonceBytes),
                blob.AsSpan(NonceBytes + TagBytes),
                blob.AsSpan(NonceBytes, TagBytes),
                plaintext);

            return Encoding.UTF8.GetString(plaintext);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            // The wrong passphrase, or a file somebody edited. Null is the contract; the caller
            // is what decides whether "none of them decrypted" is worth a message.
            return null;
        }
    }
}
