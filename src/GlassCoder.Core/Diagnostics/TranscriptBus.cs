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

    /// <summary>Reviews recorded so far this session, so a view built mid-session can replay them.</summary>
    IReadOnlyList<ReviewRecord> Reviews { get; }

    /// <summary>Raised as each step is recorded.</summary>
    event EventHandler<StepRecord>? StepRecorded;

    /// <summary>Raised when a run finishes.</summary>
    event EventHandler<RunRecord>? RunRecorded;

    /// <summary>Raised when a finished run's second opinion is recorded (workplan task 37).</summary>
    event EventHandler<ReviewRecord>? ReviewRecorded;

    /// <summary>
    /// The index a step recorded <em>outside</em> the loop should carry: one past the highest
    /// this run has reached (workplan task 65).
    /// <para>
    /// A human action - a manual commit, a push, an operator's rating - is a step the loop
    /// never numbered, and its caller has no way to know what the run got to. Both callers
    /// counted from zero instead, so a rating given after step 25 was logged as step 0 and sat
    /// in the transcript's <c>#</c> column claiming to be the first thing that happened. The
    /// answer lives here because this is the object that saw every step; it is the same
    /// convention <c>StepRowViewModel.ForReview</c> already uses for the post-run review.
    /// </para>
    /// </summary>
    /// <param name="runId">The run the action belongs to, or the no-run placeholder.</param>
    int NextStepIndex(string runId);

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
    private readonly List<ReviewRecord> _reviews = [];
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
    public IReadOnlyList<ReviewRecord> Reviews
    {
        get
        {
            lock (_gate)
            {
                return [.. _reviews];
            }
        }
    }

    /// <inheritdoc />
    public int NextStepIndex(string runId)
    {
        ArgumentNullException.ThrowIfNull(runId);

        lock (_gate)
        {
            int highest = -1;
            int reviews = 0;

            foreach (StepRecord step in _steps)
            {
                if (string.Equals(step.RunId, runId, StringComparison.Ordinal) && step.StepIndex > highest)
                {
                    highest = step.StepIndex;
                }
            }

            foreach (ReviewRecord review in _reviews)
            {
                if (string.Equals(review.RunId, runId, StringComparison.Ordinal))
                {
                    reviews++;
                }
            }

            // Reviews are counted although they are not steps, because the transcript numbers
            // each one "one past the run's last step" and a review is not in _steps to push this
            // along. Without the term, a run that ended at step 18 gave its review row 19 and
            // then handed 19 to the operator's rating as well - two rows claiming one number.
            // A gap is harmless where a collision is not, so this errs upward.
            return highest + 1 + reviews;
        }
    }

    /// <inheritdoc />
    public event EventHandler<StepRecord>? StepRecorded;

    /// <inheritdoc />
    public event EventHandler<RunRecord>? RunRecorded;

    /// <inheritdoc />
    public event EventHandler<ReviewRecord>? ReviewRecorded;

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
    public void LogReview(ReviewRecord record)
    {
        _inner.LogReview(record);

        lock (_gate)
        {
            _reviews.Add(record);
        }

        ReviewRecorded?.Invoke(this, record);
    }

    /// <inheritdoc />
    public void Clear()
    {
        lock (_gate)
        {
            _steps.Clear();
            _reviews.Clear();
        }
    }
}
