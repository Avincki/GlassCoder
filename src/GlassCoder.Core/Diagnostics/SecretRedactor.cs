using System.Text.RegularExpressions;

namespace GlassCoder.Core.Diagnostics;

/// <summary>
/// Value-level redaction (CLAUDE.md §9: never log secrets or API keys).
/// <para>
/// Two layers guard the log store. This one catches secret-shaped <em>values</em> wherever they
/// appear - a bearer token pasted into a prompt, a key echoed by a tool. The other layer,
/// <see cref="RedactingEnricher"/>, blanks secret-named <em>properties</em>. Neither is
/// sufficient alone.
/// </para>
/// </summary>
public static partial class SecretRedactor
{
    /// <summary>Marker written in place of a redacted value.</summary>
    public const string Marker = "[redacted]";

    /// <summary>Marker written when content logging is switched off.</summary>
    public const string ContentDisabledMarker = "[content logging disabled]";

    /// <summary>Replaces anything that looks like a credential with <see cref="Marker"/>.</summary>
    public static string? Scrub(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value;
        }

        string scrubbed = BearerToken().Replace(value, $"Bearer {Marker}");
        scrubbed = ProviderKey().Replace(scrubbed, Marker);
        scrubbed = AssignedSecret().Replace(scrubbed, match => $"{match.Groups[1].Value}{Marker}");
        return scrubbed;
    }

    /// <summary>Truncates a value to <paramref name="maxLength"/>, saying how much was dropped.</summary>
    public static string? Truncate(string? value, int maxLength)
    {
        if (value is null || maxLength <= 0 || value.Length <= maxLength)
        {
            return value;
        }

        return string.Concat(value.AsSpan(0, maxLength), $" … [{value.Length - maxLength} more characters]");
    }

    /// <summary>Applies the redaction switch, then value scrubbing, then truncation.</summary>
    public static string? Sanitise(string? value, bool logContent, int maxLength)
    {
        if (value is null)
        {
            return null;
        }

        return logContent ? Truncate(Scrub(value), maxLength) : ContentDisabledMarker;
    }

    [GeneratedRegex(@"Bearer\s+[A-Za-z0-9\-._~+/]+=*", RegexOptions.IgnoreCase, 500)]
    private static partial Regex BearerToken();

    // sk-..., ghp_..., xoxb-... and similar provider-issued key shapes.
    [GeneratedRegex(@"\b(?:sk|rk|pk)-[A-Za-z0-9_\-]{16,}\b|\bgh[pousr]_[A-Za-z0-9]{16,}\b|\bxox[baprs]-[A-Za-z0-9\-]{10,}\b", RegexOptions.None, 500)]
    private static partial Regex ProviderKey();

    // api_key=..., "password": "...", token: ... - the optional quote before the separator is
    // what makes this match inside JSON as well as in key=value text.
    [GeneratedRegex(
        """(?i)\b((?:api[_-]?key|access[_-]?token|refresh[_-]?token|password|secret|credential)"?\s*[:=]\s*"?)[^\s",;}]+""",
        RegexOptions.None,
        500)]
    private static partial Regex AssignedSecret();
}
