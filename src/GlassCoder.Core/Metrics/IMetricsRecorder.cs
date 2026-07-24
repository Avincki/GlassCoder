using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GlassCoder.Core.Metrics;

/// <summary>Writes the performance indicators somewhere they can be compared (CLAUDE.md §11).</summary>
public interface IMetricsRecorder
{
    /// <summary>Records one run.</summary>
    void Record(RunMetrics metrics);
}

/// <summary>Where metrics are written (workplan task 20).</summary>
public sealed class MetricsOptions
{
    /// <summary>Configuration section these options bind from.</summary>
    public const string SectionName = "GlassCoder:Metrics";

    /// <summary>Whether metrics are written at all.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Directory the JSONL files live in.</summary>
    public string Directory { get; set; } = "metrics";

    /// <summary>File name. One JSON object per line, appended.</summary>
    public string FileName { get; set; } = "metrics.jsonl";
}

/// <summary>
/// Appends metrics as JSONL (workplan task 20).
/// <para>
/// JSONL because the consumer is a notebook, not a person: one self-describing object per line,
/// appendable from concurrent runs, readable by every dataframe library without a schema.
/// </para>
/// </summary>
public sealed class JsonlMetricsRecorder : IMetricsRecorder
{
    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    private readonly MetricsOptions _options;
    private readonly ILogger<JsonlMetricsRecorder> _logger;
    private readonly Lock _gate = new();

    /// <summary>Creates the recorder.</summary>
    public JsonlMetricsRecorder(IOptions<MetricsOptions> options, ILogger<JsonlMetricsRecorder>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        _options = options.Value;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<JsonlMetricsRecorder>.Instance;
    }

    /// <summary>Full path of the file being appended to.</summary>
    public string FilePath => Path.Combine(Path.GetFullPath(_options.Directory), _options.FileName);

    /// <inheritdoc />
    public void Record(RunMetrics metrics)
    {
        ArgumentNullException.ThrowIfNull(metrics);

        if (!_options.Enabled)
        {
            return;
        }

        try
        {
            string path = FilePath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            // Computed properties (rates, per-solved-task figures) are serialised too, so a
            // reader never has to reimplement a definition and get it subtly different.
            string line = JsonSerializer.Serialize(metrics, WriteOptions);

            lock (_gate)
            {
                File.AppendAllText(path, line + Environment.NewLine);
            }

            _logger.LogInformation(
                "Recorded metrics for run {RunId}: {Steps} steps, {TotalTokens} tokens, tool-call validity {Validity:P0}",
                metrics.RunId, metrics.Steps, metrics.TotalTokens, metrics.ToolCallValidityRate);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Losing a metrics line must never take a run down with it.
            _logger.LogError(ex, "Could not write metrics for run {RunId}", metrics.RunId);
        }
    }
}
