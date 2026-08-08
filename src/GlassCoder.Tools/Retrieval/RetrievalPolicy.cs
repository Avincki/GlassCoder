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
    /// <summary>
    /// How many runs' budgets to keep. A desktop session holds a handful; the orchestrator's
    /// fan-out holds one per sub-agent. Well above either, and bounded so a long-lived host does
    /// not accumulate one entry per run for ever.
    /// </summary>
    private const int MaximumTrackedRuns = 64;

    private readonly IOptionsMonitor<RetrievalOptions> _options;
    private readonly IRetrievalSignals _signals;
    private readonly IChangeLog _changes;
    private readonly Lock _gate = new();

    /// <summary>
    /// One budget per run, rather than one budget belonging to whichever run asked last.
    /// <para>
    /// The single-slot version reset itself the moment it saw a different run id, which is
    /// correct for the desktop - runs are sequential there - and wrong for the orchestrator,
    /// whose fan-out interleaves sub-agents. Two of them alternating calls reset each other's
    /// counters on every call, so <see cref="RetrievalOptions.MaxCallsPerRun"/> never fired and
    /// the fan-out could make unbounded upstream calls.
    /// </para>
    /// </summary>
    private readonly Dictionary<string, RunBudget> _runs = new(StringComparer.Ordinal);

    private readonly List<string> _order = [];

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
                RunBudget run = Current();
                return new RetrievalStats(
                    run.Allowed, new Dictionary<string, int>(run.Blocked, StringComparer.Ordinal), run.CharsReturned);
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
            RunBudget run = Current();

            denial = Judge(request, options, run);
            if (denial is not null)
            {
                Block(run, denial);
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
            Spend(Current(), charsReturned);
        }
    }

    /// <inheritdoc />
    public void RecordFailedCall(RetrievalRequest request, RetrievalDenial denial)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(denial);

        lock (_gate)
        {
            RunBudget run = Current();

            // Both, and that is the point. An admitted call that failed upstream has still been
            // made - it opened a socket, spent a step and returned nothing - so it consumes the
            // budget exactly as a successful one does. Counting only the refusal is what let an
            // expired GitHub token produce twenty-five live round-trips in a run capped at three.
            Spend(run, charsReturned: 0);
            Block(run, denial);
        }
    }

    /// <inheritdoc />
    public void RecordDenial(RetrievalDenial denial)
    {
        ArgumentNullException.ThrowIfNull(denial);

        lock (_gate)
        {
            Block(Current(), denial);
        }
    }

    /// <summary>Charges one admitted call against a run. Called under <see cref="_gate"/>.</summary>
    private void Spend(RunBudget run, int charsReturned)
    {
        run.Allowed++;
        run.CharsReturned += Math.Max(0, charsReturned);

        // The anti-loop counter measures calls that bought nothing. A call made after work
        // landed starts the count again; one made after another call did not.
        int applied = AppliedThisRun();
        run.CallsSinceApplied = applied > run.AppliedAtLastCall ? 1 : run.CallsSinceApplied + 1;
        run.AppliedAtLastCall = applied;
    }

    /// <summary>The first reason this call cannot run, or null when it may.</summary>
    private RetrievalDenial? Judge(RetrievalRequest request, RetrievalOptions options, RunBudget run)
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

        if (options.MaxCallsPerRun > 0 && run.Allowed >= options.MaxCallsPerRun)
        {
            return new RetrievalDenial(
                ToolErrorCodes.RetrievalBudgetExhausted,
                $"This run has used its {options.MaxCallsPerRun} retrieval calls.",
                "Use what you have already been told, and verify it with build.");
        }

        // The character budget is the call cap's worth of full-size answers, so it only exists
        // when there is a call cap. It used to read Math.Max(1, MaxCallsPerRun), which turned
        // MaxCallsPerRun = 0 - the documented way to lift the call limit - into a budget of one
        // answer, refusing every call after the first with a message about a limit nobody set.
        if (options.MaxCallsPerRun > 0 &&
            run.CharsReturned >= options.MaxResultChars * options.MaxCallsPerRun)
        {
            return new RetrievalDenial(
                ToolErrorCodes.RetrievalBudgetExhausted,
                $"This run has returned {run.CharsReturned:N0} characters of retrieval, which is its " +
                $"budget of {options.MaxCallsPerRun} answers at {options.MaxResultChars:N0} characters.",
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
            run.CallsSinceApplied >= options.MaxCallsWithoutAppliedChange &&
            AppliedThisRun() == run.AppliedAtLastCall)
        {
            return new RetrievalDenial(
                ToolErrorCodes.RetrievalBudgetExhausted,
                $"{run.CallsSinceApplied} retrieval calls have produced no change to the workspace.",
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

    /// <summary>
    /// This run's budget, created on first sight. Called under <see cref="_gate"/>.
    /// <para>
    /// The oldest is evicted past <see cref="MaximumTrackedRuns"/>. A run whose budget is evicted
    /// starts again, which is the same behaviour the single-slot version had for every run and is
    /// acceptable here only because it takes sixty-four concurrent runs to reach.
    /// </para>
    /// </summary>
    private RunBudget Current()
    {
        string runId = RunContext.Current.RunId;
        if (_runs.TryGetValue(runId, out RunBudget? existing))
        {
            return existing;
        }

        if (_order.Count >= MaximumTrackedRuns)
        {
            _runs.Remove(_order[0]);
            _order.RemoveAt(0);
        }

        RunBudget fresh = new() { AppliedAtLastCall = AppliedThisRun() };
        _runs[runId] = fresh;
        _order.Add(runId);
        return fresh;
    }

    private static void Block(RunBudget run, RetrievalDenial denial) =>
        run.Blocked[denial.Code] = run.Blocked.GetValueOrDefault(denial.Code) + 1;

    /// <summary>What one run has spent.</summary>
    private sealed class RunBudget
    {
        public int Allowed { get; set; }

        public int CharsReturned { get; set; }

        public int CallsSinceApplied { get; set; }

        public int AppliedAtLastCall { get; set; }

        public Dictionary<string, int> Blocked { get; } = new(StringComparer.Ordinal);
    }
}
