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

    public RunBudget(AgentOptions limits, ModelRoleOptions role, TimeProvider time)
    {
        _limits = limits;
        _role = role;
        _time = time;
        _startTimestamp = time.GetTimestamp();
    }

    public int Steps { get; private set; }

    public long InputTokens { get; private set; }

    public long OutputTokens { get; private set; }

    public long TotalTokens { get; private set; }

    public int ToolCallsTotal { get; private set; }

    public int ToolCallsValid { get; private set; }

    public int ConsecutiveInvalidToolCalls { get; private set; }

    public TimeSpan Elapsed => _time.GetElapsedTime(_startTimestamp);

    /// <summary>
    /// Spend on the run, at the driving role's prices.
    /// <para>
    /// This prices every token at one role's rate, which is correct only while a run addresses
    /// one role - true today, and not true the moment the critique rung runs inside the loop on a
    /// paid critic. Charging that critic at a local worker's rate of zero would make
    /// <see cref="AgentOptions.MaxCostUsd"/> a budget that cannot trip. Whoever wires rung 6 into
    /// the loop owes this method a second price table; <see cref="Verification.CritiqueResult"/>
    /// already carries the panel's own cost, computed from the critic role's prices.
    /// </para>
    /// </summary>
    public decimal EstimatedCostUsd =>
        ((decimal)InputTokens / 1_000_000m * _role.InputCostPerMillionTokens) +
        ((decimal)OutputTokens / 1_000_000m * _role.OutputCostPerMillionTokens);

    /// <summary>The limit that has tripped, or null when the loop may continue.</summary>
    public AgentStopReason? Exhausted()
    {
        if (Steps >= _limits.MaxSteps)
        {
            return AgentStopReason.StepLimit;
        }

        if (_limits.MaxTotalTokens > 0 && TotalTokens >= _limits.MaxTotalTokens)
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
