using System.Diagnostics;
using System.Reflection;

namespace GlassCoder.Core.Diagnostics;

/// <summary>
/// The harness's own <see cref="ActivitySource"/>. Model calls are traced by the
/// <c>.UseOpenTelemetry()</c> stage of the client pipeline; this source adds the spans that
/// only the harness knows about - a run, and each loop step within it.
/// </summary>
public static class GlassCoderActivity
{
    /// <summary>ActivitySource name the tracer provider must subscribe to.</summary>
    public const string SourceName = "GlassCoder.Core";

    /// <summary>The source itself.</summary>
    public static ActivitySource Source { get; } = new(
        SourceName,
        typeof(GlassCoderActivity).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? "0.0.0");
}
