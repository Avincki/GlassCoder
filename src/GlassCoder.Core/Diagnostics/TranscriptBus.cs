namespace GlassCoder.Core.Diagnostics;

/// <summary>
/// An in-process feed of the transcript, for anything that wants to watch a run happen
/// (workplan task 26).
/// </summary>
/// <remarks>
/// The UI could tail the JSONL file instead, and that would be worse: it would parse what it had
/// just serialised, lag behind by a flush, and duplicate the schema. The log store stays the
/// durable record; this is the live one.
/// </remarks>
public interface ITranscriptBus
{
    /// <summary>Steps recorded so far this session.</summary>
    IReadOnlyList<StepRecord> Steps { get; }

    /// <summary>Raised as each step is recorded.</summary>
    event EventHandler<StepRecord>? StepRecorded;

    /// <summary>Raised when a run finishes.</summary>
    event EventHandler<RunRecord>? RunRecorded;

    /// <summary>Drops everything held, for the start of a new session.</summary>
    void Clear();
}

/// <summary>
/// The step logger the loop actually holds: it writes to the durable log <em>and</em> publishes
/// to anything watching (workplan tasks 11, 26).
/// </summary>
public sealed class TranscriptBus : IStepLogger, ITranscriptBus
{
    private readonly IStepLogger _inner;
    private readonly Lock _gate = new();
    private readonly List<StepRecord> _steps = [];
    private readonly int _maxSteps;

    /// <summary>Wraps a durable step logger.</summary>
    /// <param name="inner">The logger that writes the transcript to disk.</param>
    /// <param name="maxSteps">How many steps to keep in memory before dropping the oldest.</param>
    public TranscriptBus(IStepLogger inner, int maxSteps = 5000)
    {
        _inner = inner;
        _maxSteps = maxSteps;
    }

    /// <inheritdoc />
    public IReadOnlyList<StepRecord> Steps
    {
        get
        {
            lock (_gate)
            {
                return [.. _steps];
            }
        }
    }

    /// <inheritdoc />
    public event EventHandler<StepRecord>? StepRecorded;

    /// <inheritdoc />
    public event EventHandler<RunRecord>? RunRecorded;

    /// <inheritdoc />
    public void LogStep(StepRecord record)
    {
        _inner.LogStep(record);

        lock (_gate)
        {
            _steps.Add(record);
            if (_steps.Count > _maxSteps)
            {
                _steps.RemoveRange(0, _steps.Count - _maxSteps);
            }
        }

        StepRecorded?.Invoke(this, record);
    }

    /// <inheritdoc />
    public void LogRun(RunRecord record)
    {
        _inner.LogRun(record);
        RunRecorded?.Invoke(this, record);
    }

    /// <inheritdoc />
    public void Clear()
    {
        lock (_gate)
        {
            _steps.Clear();
        }
    }
}
