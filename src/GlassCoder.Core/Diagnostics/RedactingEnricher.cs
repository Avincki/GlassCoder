using Serilog.Core;
using Serilog.Events;

namespace GlassCoder.Core.Diagnostics;

/// <summary>
/// Blanks secret-named properties and scrubs secret-shaped string values on every log event,
/// in both sinks (CLAUDE.md §9).
/// <para>
/// This runs inside the logging pipeline rather than at the call sites, because a redaction
/// rule that depends on every caller remembering it is not a redaction rule.
/// </para>
/// </summary>
public sealed class RedactingEnricher : ILogEventEnricher
{
    private readonly HashSet<string> _redactedNames;

    /// <summary>Creates the enricher for a set of property names.</summary>
    public RedactingEnricher(IEnumerable<string> redactedPropertyNames)
    {
        ArgumentNullException.ThrowIfNull(redactedPropertyNames);
        _redactedNames = new HashSet<string>(redactedPropertyNames, StringComparer.OrdinalIgnoreCase);
    }

    /// <inheritdoc />
    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        ArgumentNullException.ThrowIfNull(logEvent);

        List<LogEventProperty>? replacements = null;

        foreach ((string name, LogEventPropertyValue value) in logEvent.Properties)
        {
            if (_redactedNames.Contains(name))
            {
                (replacements ??= []).Add(new LogEventProperty(name, new ScalarValue(SecretRedactor.Marker)));
                continue;
            }

            if (value is ScalarValue { Value: string text })
            {
                string? scrubbed = SecretRedactor.Scrub(text);
                if (!string.Equals(scrubbed, text, StringComparison.Ordinal))
                {
                    (replacements ??= []).Add(new LogEventProperty(name, new ScalarValue(scrubbed)));
                }
            }
        }

        if (replacements is null)
        {
            return;
        }

        foreach (LogEventProperty replacement in replacements)
        {
            logEvent.AddOrUpdateProperty(replacement);
        }
    }
}
