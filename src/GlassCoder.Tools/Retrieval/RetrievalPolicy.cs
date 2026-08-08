using GlassCoder.Tools.Changes;
using Microsoft.Extensions.Options;

namespace GlassCoder.Tools.Retrieval;

/// <summary>
/// Default <see cref="IRetrievalPolicy"/>: budget, indication and anti-loop, checked in that
/// order, with per-run state scoped by the ambient <see cref="RunContext"/>.
/// <para>
/// Scoped by run id rather than by an explicit <c>BeginRun</c> the loop must remember to call.
/// A reset nobody invokes is a budget that never resets, which is the class of dormancy this
/// repository keeps finding in its own history; keying on the run id the tools already see
/// makes forgetting impossible.
/// </para>
/// </summary>
public sealed class RetrievalPolicy : IRetrievalPolicy
{
    private readonly IOptionsMonitor<RetrievalOptions> _options;
    private readonly IRetrievalSignals _signals;
    private readonly IChangeLog _changes;
    private readonly Lock _gate = new();

    private string _runId = string.Empty;
    private int _allowed;
    private int _charsReturned;
    private int _callsSinceApplied;
    private int _appliedAtLastCall;
    private Dictionary<string, int> _blocked = new(StringComparer.Ordinal);

    /// <summary>Creates the policy.</summary>
    public RetrievalPolicy(
        IOptionsMonitor<RetrievalOptions> options,
        IRetrievalSignals signals,
        IChangeLog changes)
    {
        _options = options;
        _signals = signals;
        _changes = changes;
    }

    /// <inheritdoc />
    public RetrievalStats Stats
    {
        get
        {
            lock (_gate)
            {
                Sync();
                return new RetrievalStats(_allowed, new Dictionary<string, int>(_blocked, StringComparer.Ordinal), _charsReturned);
            }
        }
    }

    /// <inheritdoc />
    public bool TryAdmit(RetrievalRequest request, out RetrievalDenial? denial)
    {
        ArgumentNullException.ThrowIfNull(request);
        RetrievalOptions options = _options.CurrentValue;

        lock (_gate)
        {
            Sync();

            denial = Judge(request, options);
            if (denial is not null)
            {
                Block(denial);
                return false;
            }

            return true;
        }
    }

    /// <inheritdoc />
    public void RecordCall(RetrievalRequest request, int charsReturned)
    {
        ArgumentNullException.ThrowIfNull(request);

        lock (_gate)
        {
            Sync();

            _allowed++;
            _charsReturned += Math.Max(0, charsReturned);

            // The anti-loop counter measures calls that bought nothing. A call made after work
            // landed starts the count again; one made after another call did not.
            int applied = AppliedThisRun();
            _callsSinceApplied = applied > _appliedAtLastCall ? 1 : _callsSinceApplied + 1;
            _appliedAtLastCall = applied;
        }
    }

    /// <inheritdoc />
    public void RecordDenial(RetrievalDenial denial)
    {
        ArgumentNullException.ThrowIfNull(denial);

        lock (_gate)
        {
            Sync();
            Block(denial);
        }
    }

    /// <summary>The first reason this call cannot run, or null when it may.</summary>
    private RetrievalDenial? Judge(RetrievalRequest request, RetrievalOptions options)
    {
        if (!options.Enabled || !options.For(request.Server).Enabled)
        {
            // Reachable in tests and through a mid-run configuration reload; in a normal run the
            // tool is not registered at all, so the model never sees it to call.
            return new RetrievalDenial(
                ToolErrorCodes.RetrievalDisabled,
                $"Retrieval from {request.Server} is switched off for this run.",
                "Answer from the workspace: grep, find_symbol and read_file, then build.");
        }

        if (options.MaxCallsPerRun > 0 && _allowed >= options.MaxCallsPerRun)
        {
            return new RetrievalDenial(
                ToolErrorCodes.RetrievalBudgetExhausted,
                $"This run has used its {options.MaxCallsPerRun} retrieval calls.",
                "Use what you have already been told, and verify it with build.");
        }

        if (_charsReturned >= options.MaxResultChars * Math.Max(1, options.MaxCallsPerRun))
        {
            return new RetrievalDenial(
                ToolErrorCodes.RetrievalBudgetExhausted,
                "This run has used its retrieval result budget.",
                "Use what you have already been told, and verify it with build.");
        }

        if (!options.AllowProactive && !_signals.ExternalKnowledgeIndicated)
        {
            return new RetrievalDenial(
                ToolErrorCodes.RetrievalNotIndicated,
                "Nothing in this run says the answer is outside the workspace. Retrieval is for a " +
                "type or member no source here declares, not for a name you can look up locally.",
                "Try find_symbol for a declaration, grep for a use, or build to see the real error.");
        }

        if (options.MaxCallsWithoutAppliedChange > 0 &&
            _callsSinceApplied >= options.MaxCallsWithoutAppliedChange &&
            AppliedThisRun() == _appliedAtLastCall)
        {
            return new RetrievalDenial(
                ToolErrorCodes.RetrievalBudgetExhausted,
                $"{_callsSinceApplied} retrieval calls have produced no change to the workspace.",
                "Write the change you already have the answer for. Retrieval will be available " +
                "again once something has been applied.");
        }

        return null;
    }

    /// <summary>Applied changes this run - the one event that honestly resets the argument.</summary>
    private int AppliedThisRun()
    {
        string runId = RunContext.Current.RunId;
        int applied = 0;

        foreach (CodeChange change in _changes.All())
        {
            if (change.Status == ChangeStatus.Applied && string.Equals(change.RunId, runId, StringComparison.Ordinal))
            {
                applied++;
            }
        }

        return applied;
    }

    /// <summary>Drops the previous run's totals the first time a new run asks anything.</summary>
    private void Sync()
    {
        string runId = RunContext.Current.RunId;
        if (string.Equals(runId, _runId, StringComparison.Ordinal))
        {
            return;
        }

        _runId = runId;
        _allowed = 0;
        _charsReturned = 0;
        _callsSinceApplied = 0;
        _appliedAtLastCall = AppliedThisRun();
        _blocked = new Dictionary<string, int>(StringComparer.Ordinal);
    }

    private void Block(RetrievalDenial denial) =>
        _blocked[denial.Code] = _blocked.GetValueOrDefault(denial.Code) + 1;
}
