namespace GlassCoder.Core.Diagnostics;

/// <summary>
/// OpenTelemetry tracing settings (CLAUDE.md §9, workplan task 5).
/// </summary>
public sealed class TelemetryOptions
{
    /// <summary>Configuration section these options bind from.</summary>
    public const string SectionName = "GlassCoder:Telemetry";

    /// <summary>Whether tracing is wired up at all.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Service name attached to every span.</summary>
    public string ServiceName { get; set; } = "GlassCoder";

    /// <summary>Whether spans are also written to the console. Useful when learning, noisy otherwise.</summary>
    public bool ConsoleExporter { get; set; }

    /// <summary>OTLP collector endpoint, for example <c>http://localhost:4317</c>. Null disables the exporter.</summary>
    public string? OtlpEndpoint { get; set; }

    /// <summary>Extra ActivitySource names to subscribe to, beyond the harness and model sources.</summary>
    public IList<string> AdditionalSources { get; } = [];
}
