using System.Text.Json;

namespace GlassCoder.Models;

/// <summary>
/// Turns a configured request-property string into the value that should be sent over the
/// seam. Server-specific decoding knobs are typed - <c>guided_json</c> wants an object,
/// <c>top_k</c> wants a number, a backend name wants a string - but configuration is text, so
/// anything that parses as JSON is sent as JSON and everything else is sent verbatim.
/// </summary>
internal static class RequestPropertyValue
{
    public static object? Parse(string? configuredValue)
    {
        if (string.IsNullOrWhiteSpace(configuredValue))
        {
            return configuredValue;
        }

        string trimmed = configuredValue.Trim();
        char first = trimmed[0];
        bool looksLikeJson = first is '{' or '[' or '"' or '-' or 't' or 'f' or 'n' || char.IsAsciiDigit(first);
        if (!looksLikeJson)
        {
            return configuredValue;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(trimmed);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return configuredValue;
        }
    }
}
