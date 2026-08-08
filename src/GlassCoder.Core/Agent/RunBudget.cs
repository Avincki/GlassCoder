using GlassCoder.Models.Configuration;
using Microsoft.Extensions.AI;

namespace GlassCoder.Core.Agent;

/// <summary>
/// Tracks what a run has spent and answers one question: may the loop take another step?
/// <para>
/// Budgets, limits and graceful give-up are part of the loop rather than an afterthought
/// (CLAUDE.md §18), so they live in one small object the loop consults once per iteration.
/// </para>
/// </summary>
internal sealed class RunBudget
{
    private readonly AgentOptions _limits;
    private readonly ModelRoleOptions _role;
    private readonly TimeProvider _time;
    private readonly long _startTimestamp;
    private decimal _criticSpendUsd;
    private int _extraSteps;
    private long _extraTokens;

    public RunBudget(AgentOptions limits, ModelRoleOptions role, TimeProvider time)
    {
        _limits = limits;
        _role = role;
        _time = time;
        _startTimestamp = time.GetTimestamp();
    }

    public int Steps { get; private set; }

    /// <summary>The step ceiling this run currently has, operator extensions included.</summary>
    public int MaxSteps => _limits.MaxSteps + _extraSteps;

    /// <summary>The token ceiling this run currently has, operator extensions included.</summary>
    public long MaxTotalTokens => _limits.MaxTotalTokens + _extraTokens;

    /// <summary>What one step-limit extension adds: the configured limit.</summary>
    public int StepAllotment => _limits.MaxSteps;

    /// <summary>What one token-limit extension adds: the configured limit.</summary>
    public long TokenAllotment => _limits.MaxTotalTokens;

    /// <summary>How many steps are left before <see cref="AgentStopReason.StepLimit"/> trips.</summary>
    public int StepsRemaining => Math.Max(0, MaxSteps - Steps);

    /// <summary>
    /// Whether the run is close enough to its step ceiling that the agent should be told.
    /// <para>
    /// A quarter of the budget, floored at three. The failure this exists for is an agent that
    /// spends its last steps re-checking work instead of finishing it - which it cannot avoid
    /// while the ceiling is invisible to it.
    /// </para>
    /// </summary>
    public bool IsRunningOutOfSteps => MaxSteps > 0 && StepsRemaining <= Math.Max(3, MaxSteps / 4);

    public long InputTokens { get; private set; }

    public long OutputTokens { get; private set; }

    public long TotalTokens { get; private set; }

    public int ToolCallsTotal { get; private set; }

    public int ToolCallsValid { get; private set; }

    public int ConsecutiveInvalidToolCalls { get; private set; }

    public TimeSpan Elapsed => _time.GetElapsedTime(_startTimestamp);

    /// <summary>
    /// Spend on the run: the worker's tokens at the driving role's prices, plus whatever the
    /// critique rung spent, at the critic role's own prices.
    /// <para>
    /// The critic's spend arrives pre-priced (<see cref="Verification.CritiqueResult.EstimatedCostUsd"/>)
    /// rather than as tokens, because pricing another role's tokens at this role's rate would
    /// make a hosted critic read as free and <see cref="AgentOptions.MaxCostUsd"/> a budget that
    /// cannot trip. Critic tokens are deliberately absent from the token counts:
    /// <see cref="AgentOptions.MaxTotalTokens"/> guards the worker's context window, which the
    /// critic never occupies.
    /// </para>
    /// </summary>
    public decimal EstimatedCostUsd =>
        ((decimal)InputTokens / 1_000_000m * _role.InputCostPerMillionTokens) +
        ((decimal)OutputTokens / 1_000_000m * _role.OutputCostPerMillionTokens) +
        _criticSpendUsd;

    /// <summary>Adds spend already priced at another role's rates - the critique rung's, today.</summary>
    public void AddCriticSpend(decimal costUsd) => _criticSpendUsd += costUsd;

    /// <summary>
    /// Raises the tripped ceiling by one more allotment of its configured size - the operator's
    /// answer to a limit that fired three steps from done. Only the step and token ceilings
    /// extend; time and cost stay where configuration put them.
    /// </summary>
    public void Extend(AgentStopReason reason)
    {
        if (reason == AgentStopReason.StepLimit)
        {
            _extraSteps += _limits.MaxSteps;
        }
        else if (reason == AgentStopReason.TokenLimit)
        {
            _extraTokens += _limits.MaxTotalTokens;
        }
    }

    /// <summary>The limit that has tripped, or null when the loop may continue.</summary>
    public AgentStopReason? Exhausted()
    {
        if (Steps >= MaxSteps)
        {
            return AgentStopReason.StepLimit;
        }

        if (_limits.MaxTotalTokens > 0 && TotalTokens >= MaxTotalTokens)
        {
            return AgentStopReason.TokenLimit;
        }

        if (_limits.MaxWallClockSeconds > 0 && Elapsed >= TimeSpan.FromSeconds(_limits.MaxWallClockSeconds))
        {
            return AgentStopReason.TimeLimit;
        }

        if (_limits.MaxCostUsd is { } maxCost && EstimatedCostUsd >= maxCost)
        {
            return AgentStopReason.CostLimit;
        }

        if (_limits.MaxConsecutiveInvalidToolCalls > 0 &&
            ConsecutiveInvalidToolCalls >= _limits.MaxConsecutiveInvalidToolCalls)
        {
            return AgentStopReason.ToolFailureLimit;
        }

        return null;
    }

    public void CountStep() => Steps++;

    public void AddUsage(UsageDetails? usage)
    {
        if (usage is null)
        {
            return;
        }

        InputTokens += usage.InputTokenCount ?? 0;
        OutputTokens += usage.OutputTokenCount ?? 0;
        TotalTokens += usage.TotalTokenCount ?? ((usage.InputTokenCount ?? 0) + (usage.OutputTokenCount ?? 0));
    }

    public void CountToolCall(bool valid)
    {
        ToolCallsTotal++;
        if (valid)
        {
            ToolCallsValid++;
            ConsecutiveInvalidToolCalls = 0;
        }
        else
        {
            ConsecutiveInvalidToolCalls++;
        }
    }
}
