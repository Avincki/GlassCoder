using GlassCoder.Core.Provenance;
using GlassCoder.Tools.Planning;

namespace GlassCoder.Core.Diagnostics;

/// <summary>
/// The run-level companion to <see cref="StepRecord"/> (workplan task 11).
/// <para>
/// The steps alone describe what happened; this says what the run <em>was</em> - the goal, the
/// prompt it started from, and how it ended. Together they are what "a run is fully
/// reconstructable from the logs alone" means (CLAUDE.md §9).
/// </para>
/// </summary>
public sealed record RunRecord
{
    /// <summary>Run identifier, shared with every <see cref="StepRecord"/> of the run.</summary>
    public required string RunId { get; init; }

    /// <summary>Task identifier, for cross-run comparison.</summary>
    public required string TaskId { get; init; }

    /// <summary>Served role that drove the run.</summary>
    public required string Role { get; init; }

    /// <summary>The goal the agent was given.</summary>
    public string? Goal { get; init; }

    /// <summary>The system prompt in force, without which a transcript cannot be replayed.</summary>
    public string? SystemPrompt { get; init; }

    /// <summary>When the run started.</summary>
    public required DateTimeOffset StartedAt { get; init; }

    /// <summary>When the run stopped.</summary>
    public required DateTimeOffset CompletedAt { get; init; }

    /// <summary>Why the loop stopped.</summary>
    public required string StopReason { get; init; }

    /// <summary>Completed loop iterations.</summary>
    public required int Steps { get; init; }

    /// <summary>The model's closing text, when it produced one.</summary>
    public string? FinalText { get; init; }

    /// <summary>Prompt tokens across the run.</summary>
    public required long InputTokens { get; init; }

    /// <summary>Completion tokens across the run.</summary>
    public required long OutputTokens { get; init; }

    /// <summary>Total tokens across the run.</summary>
    public required long TotalTokens { get; init; }

    /// <summary>Estimated spend from the per-role token prices.</summary>
    public required decimal EstimatedCostUsd { get; init; }

    /// <summary>Wall-clock for the run.</summary>
    public required double ElapsedMs { get; init; }

    /// <summary>Tool calls the model issued.</summary>
    public required int ToolCallsTotal { get; init; }

    /// <summary>Tool calls that parsed and executed.</summary>
    public required int ToolCallsValid { get; init; }

    /// <summary>Failure detail when the run ended in an error.</summary>
    public string? Error { get; init; }

    /// <summary>What produced this run, and whether its context was fresh (workplan task 35).</summary>
    public ProvenanceStamp? Provenance { get; init; }

    /// <summary>The plan as it stood when the run ended (workplan task 24).</summary>
    public IReadOnlyList<TodoItem>? Todos { get; init; }
}
