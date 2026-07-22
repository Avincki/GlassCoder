namespace GlassCoder.Models.Configuration;

/// <summary>
/// All served roles the harness can address, bound from configuration (CLAUDE.md §6, §13).
/// Multiple roles may be served concurrently and addressed from one harness.
/// </summary>
public sealed class ModelsOptions
{
    /// <summary>Configuration section these options bind from.</summary>
    public const string SectionName = "GlassCoder:Models";

    /// <summary>Role name (<c>worker</c>, <c>drafter</c>, <c>critic</c>, ...) to its endpoint settings.</summary>
    public IDictionary<string, ModelRoleOptions> Roles { get; } =
        new Dictionary<string, ModelRoleOptions>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Role used when a caller does not name one.</summary>
    public string DefaultRole { get; set; } = ModelRoles.Worker;

    /// <summary>
    /// ActivitySource name for the <c>.UseOpenTelemetry()</c> stage of the client pipeline
    /// (CLAUDE.md §9). The tracer provider must subscribe to this same name.
    /// </summary>
    public string TelemetrySourceName { get; set; } = "GlassCoder.Models";

    /// <summary>
    /// Whether prompts and responses are attached to spans. Off by default - this is the
    /// redaction switch on the tracing side (CLAUDE.md §9).
    /// </summary>
    public bool EnableSensitiveTelemetryData { get; set; }
}
