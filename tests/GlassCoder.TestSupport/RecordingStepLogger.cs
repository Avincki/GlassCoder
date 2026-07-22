using GlassCoder.Core.Diagnostics;

namespace GlassCoder.TestSupport;

/// <summary>Captures step records so tests can assert on the transcript the loop produced.</summary>
public sealed class RecordingStepLogger : IStepLogger
{
    /// <summary>Every step recorded, in order.</summary>
    public List<StepRecord> Steps { get; } = [];

    /// <inheritdoc />
    public void LogStep(StepRecord record) => Steps.Add(record);
}
