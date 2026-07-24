using System.Security.Cryptography;
using System.Text;

namespace GlassCoder.Core.Configuration;

/// <summary>
/// Encrypts the values that must never be readable at rest - today, the API keys the settings
/// dialog stores (CLAUDE.md §9: never log or leak secrets).
/// <para>
/// A seam rather than a static helper because protection is platform-specific: DPAPI is the
/// right answer on Windows and does not exist anywhere else, and unit tests want a fake that
/// does not touch the user's key store.
/// </para>
/// </summary>
public interface ISecretProtector
{
    /// <summary>Short name of the protection scheme, for display: <c>dpapi</c>, <c>plain</c>, …</summary>
    string Scheme { get; }

    /// <summary>Whether values really are encrypted, or merely encoded.</summary>
    bool IsEncrypted { get; }

    /// <summary>Turns a secret into the opaque string written to disk.</summary>
    string Protect(string secret);

    /// <summary>
    /// Recovers a secret written by <see cref="Protect"/>. Returns <see langword="null"/> when
    /// the value cannot be recovered - a secrets file copied to another machine or another
    /// Windows account decrypts to nothing, and that is a fact the UI has to be able to report
    /// rather than an exception to crash on.
    /// </summary>
    string? Unprotect(string stored);
}

/// <summary>
/// The Windows implementation: DPAPI at <see cref="DataProtectionScope.CurrentUser"/>, so the
/// ciphertext is bound to the logged-in account and no key material lives in the repository or
/// in the settings file.
/// <para>
/// Off Windows there is no equivalent that needs no key management, so the protector says so
/// through <see cref="IsEncrypted"/> and falls back to base64 <em>encoding</em>. That is not
/// protection and is never described as protection: the settings dialog shows the scheme, so an
/// operator on another OS can see that the file is only obscured and put the key in an
/// environment variable instead.
/// </para>
/// </summary>
public sealed class DpapiSecretProtector : ISecretProtector
{
    private const string DpapiPrefix = "dpapi:";
    private const string PlainPrefix = "plain:";

    // Additional entropy is not a key - it only makes a blob from another application on the
    // same account undecryptable here, and vice versa.
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("GlassCoder.UserSettings.v1");

    /// <inheritdoc />
    public bool IsEncrypted => OperatingSystem.IsWindows();

    /// <inheritdoc />
    public string Scheme => IsEncrypted ? "dpapi" : "plain";

    /// <inheritdoc />
    public string Protect(string secret)
    {
        ArgumentNullException.ThrowIfNull(secret);

        byte[] plaintext = Encoding.UTF8.GetBytes(secret);
        if (!OperatingSystem.IsWindows())
        {
            return PlainPrefix + Convert.ToBase64String(plaintext);
        }

        byte[] ciphertext = ProtectedData.Protect(plaintext, Entropy, DataProtectionScope.CurrentUser);
        return DpapiPrefix + Convert.ToBase64String(ciphertext);
    }

    /// <inheritdoc />
    public string? Unprotect(string stored)
    {
        if (string.IsNullOrWhiteSpace(stored))
        {
            return null;
        }

        try
        {
            if (stored.StartsWith(PlainPrefix, StringComparison.Ordinal))
            {
                return Encoding.UTF8.GetString(Convert.FromBase64String(stored[PlainPrefix.Length..]));
            }

            if (!stored.StartsWith(DpapiPrefix, StringComparison.Ordinal) || !OperatingSystem.IsWindows())
            {
                return null;
            }

            byte[] ciphertext = Convert.FromBase64String(stored[DpapiPrefix.Length..]);
            return Encoding.UTF8.GetString(
                ProtectedData.Unprotect(ciphertext, Entropy, DataProtectionScope.CurrentUser));
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            // Wrong user, wrong machine, or a hand-edited file. The caller reports it; there is
            // nothing here worth crashing the application over.
            return null;
        }
    }
}
