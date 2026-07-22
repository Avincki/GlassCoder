using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Formatting.Compact;

namespace GlassCoder.Core.Diagnostics;

/// <summary>
/// Builds the Serilog pipeline: one machine-readable JSONL sink and one human-readable view
/// (CLAUDE.md §9, workplan task 5).
/// </summary>
/// <remarks>
/// The JSONL sink receives everything, including the destructured per-step records - that file
/// alone is enough to replay a run as a transcript. The human sinks exclude those records,
/// because a step blob is unreadable in a console; the loop writes a one-line human summary
/// alongside each one.
/// </remarks>
public static class SerilogBootstrap
{
    /// <summary>Property that marks an event as carrying a full <see cref="StepRecord"/>.</summary>
    public const string StepPropertyName = "Step";

    /// <summary>Property that marks an event as carrying a full <see cref="RunRecord"/>.</summary>
    public const string RunPropertyName = "Run";

    private const string HumanTemplate =
        "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}";

    /// <summary>Creates the logger described by <paramref name="options"/>.</summary>
    public static Logger CreateLogger(LoggingOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        string directory = Path.GetFullPath(options.Directory);
        System.IO.Directory.CreateDirectory(directory);

        LogEventLevel minimumLevel = Enum.TryParse(options.MinimumLevel, ignoreCase: true, out LogEventLevel parsed)
            ? parsed
            : LogEventLevel.Information;

        LoggerConfiguration configuration = new LoggerConfiguration()
            .MinimumLevel.Is(minimumLevel)
            .Enrich.FromLogContext()
            .Enrich.WithProperty("Application", "GlassCoder")
            .Enrich.With(new RedactingEnricher(options.RedactedPropertyNames))
            .Destructure.ToMaximumDepth(16)
            .Destructure.ToMaximumCollectionCount(2000)

            // Machine-readable transcript: every event, one JSON object per line.
            .WriteTo.File(
                new CompactJsonFormatter(),
                Path.Combine(directory, options.JsonlFileName),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: options.RetainedFileCountLimit,
                shared: false)

            // Human-readable view: the same events minus the per-step blobs.
            .WriteTo.Logger(human => human
                .Filter.ByExcluding(e => e.Properties.ContainsKey(StepPropertyName) || e.Properties.ContainsKey(RunPropertyName))
                .WriteTo.File(
                    Path.Combine(directory, options.TextFileName),
                    outputTemplate: HumanTemplate,
                    rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: options.RetainedFileCountLimit));

        if (options.Console)
        {
            configuration = configuration.WriteTo.Logger(console => console
                .Filter.ByExcluding(e => e.Properties.ContainsKey(StepPropertyName) || e.Properties.ContainsKey(RunPropertyName))
                .WriteTo.Console(outputTemplate: HumanTemplate));
        }

        return configuration.CreateLogger();
    }
}
