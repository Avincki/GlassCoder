using GlassCoder.Core.Metrics;

namespace GlassCoder.TestSupport;

/// <summary>Captures metrics records so tests can assert on what was measured.</summary>
public sealed class RecordingMetricsRecorder : IMetricsRecorder
{
    /// <summary>Every record written, in order.</summary>
    public List<RunMetrics> Records { get; } = [];

    /// <summary>The most recent record.</summary>
    public RunMetrics? Last => Records.Count == 0 ? null : Records[^1];

    /// <inheritdoc />
    public void Record(RunMetrics metrics) => Records.Add(metrics);
}
