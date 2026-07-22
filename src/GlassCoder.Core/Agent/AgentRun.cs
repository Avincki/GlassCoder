using GlassCoder.Core.Metrics;
using Microsoft.Extensions.AI;

namespace GlassCoder.Core.Agent;

/// <summary>Why the loop stopped.</summary>
public enum AgentStopReason
{
    /// <summary>The model answered without calling a tool: the goal is met as far as it is concerned.</summary>
    Completed,

    /// <summary>The step limit tripped.</summary>
    StepLimit,

    /// <summary>The token budget tripped.</summary>
    TokenLimit,

    /// <summary>The wall-clock limit tripped.</summary>
    TimeLimit,

    /// <summary>The cost budget tripped.</summary>
    CostLimit,

    /// <summary>Too many consecutive tool calls failed to parse or bind.</summary>
    ToolFailureLimit,

    /// <summary>The caller cancelled the run.</summary>
    Cancelled,

    /// <summary>The model call itself failed.</summary>
    ModelError,
}

/// <summary>One task handed to the loop.</summary>
public sealed record AgentRunRequest
{
    /// <summary>Identifies the task, for cross-run comparison in the metrics store.</summary>
    public required string TaskId { get; init; }

    /// <summary>What the agent is asked to do.</summary>
    public required string Goal { get; init; }

    /// <summary>Run identifier. Generated when not supplied.</summary>
    public string RunId { get; init; } = Guid.NewGuid().ToString("n");

    /// <summary>Served role to drive. Falls back to the configured role.</summary>
    public string? Role { get; init; }

    /// <summary>System prompt override. Falls back to the configured prompt.</summary>
    public string? SystemPrompt { get; init; }

    /// <summary>Per-run limit overrides. Falls back to the configured limits.</summary>
    public AgentOptions? Limits { get; init; }
}

/// <summary>Everything one run produced.</summary>
public sealed record AgentRunResult
{
    /// <summary>Run identifier.</summary>
    public required string RunId { get; init; }

    /// <summary>Task identifier.</summary>
    public required string TaskId { get; init; }

    /// <summary>Why the loop stopped.</summary>
    public required AgentStopReason StopReason { get; init; }

    /// <summary>Completed loop iterations.</summary>
    public required int Steps { get; init; }

    /// <summary>The model's last text response, if it produced one.</summary>
    public string? FinalText { get; init; }

    /// <summary>Prompt tokens across the run.</summary>
    public long InputTokens { get; init; }

    /// <summary>Completion tokens across the run.</summary>
    public long OutputTokens { get; init; }

    /// <summary>Total tokens across the run.</summary>
    public long TotalTokens { get; init; }

    /// <summary>Estimated spend from the per-role token prices.</summary>
    public decimal EstimatedCostUsd { get; init; }

    /// <summary>Wall-clock for the run.</summary>
    public required TimeSpan Elapsed { get; init; }

    /// <summary>Tool calls the model issued.</summary>
    public int ToolCallsTotal { get; init; }

    /// <summary>Tool calls that parsed and executed.</summary>
    public int ToolCallsValid { get; init; }

    /// <summary>
    /// Tool-call validity rate - the single best early diagnostic for a weak model
    /// (CLAUDE.md §11). One when no tool call was attempted.
    /// </summary>
    public double ToolCallValidityRate => ToolCallsTotal == 0 ? 1d : (double)ToolCallsValid / ToolCallsTotal;

    /// <summary>The full message history, for the UI and for tests.</summary>
    public required IReadOnlyList<ChatMessage> Messages { get; init; }

    /// <summary>Failure detail when <see cref="StopReason"/> is <see cref="AgentStopReason.ModelError"/>.</summary>
    public string? Error { get; init; }

    /// <summary>
    /// What the run measured (CLAUDE.md §11). Carried on the result so a caller that knows the
    /// oracle verdict - a checkpoint, an ablation arm - can record the same numbers with pass@1
    /// filled in, rather than recomputing them and getting a subtly different answer.
    /// </summary>
    public RunMetrics? Metrics { get; init; }

    /// <summary>Whether the run ended by the model deciding it was done.</summary>
    public bool RanToCompletion => StopReason == AgentStopReason.Completed;
}
