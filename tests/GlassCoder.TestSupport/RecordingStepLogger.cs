using GlassCoder.Core.Diagnostics;

namespace GlassCoder.TestSupport;

/// <summary>Captures step records so tests can assert on the transcript the loop produced.</summary>
public sealed class RecordingStepLogger : IStepLogger
{
    /// <summary>Every step recorded, in order.</summary>
    public List<StepRecord> Steps { get; } = [];

    /// <summary>The run record, once the run has finished.</summary>
    public RunRecord? Run { get; private set; }

    /// <inheritdoc />
    public void LogStep(StepRecord record) => Steps.Add(record);

    /// <inheritdoc />
    public void LogRun(RunRecord record) => Run = record;
}
