namespace GlassCoder.Core.Diagnostics;

/// <summary>
/// One message as it was sent to, or received from, the model.
/// </summary>
/// <param name="Role">Chat role: system, user, assistant or tool.</param>
/// <param name="Text">Message text, after redaction and truncation.</param>
/// <param name="ToolCallNames">Names of any tool calls carried by the message.</param>
public sealed record TranscriptMessage(string Role, string? Text, IReadOnlyList<string>? ToolCallNames = null);

/// <summary>One tool call within a step, with everything needed to explain what happened.</summary>
/// <param name="CallId">Correlation id the model issued.</param>
/// <param name="Name">Tool name as called.</param>
/// <param name="Arguments">Arguments as supplied by the model.</param>
/// <param name="Status">Registry status: succeeded, failed, unknown_tool, invalid_arguments, faulted.</param>
/// <param name="Parsed">Whether the call parsed and executed - the numerator of tool-call validity.</param>
/// <param name="DurationMs">Wall-clock spent inside the tool.</param>
/// <param name="Result">The observation returned to the model, after redaction and truncation.</param>
/// <param name="Error">Failure detail, when there is one.</param>
public sealed record ToolCallRecord(
    string CallId,
    string Name,
    IReadOnlyDictionary<string, object?>? Arguments,
    string Status,
    bool Parsed,
    double DurationMs,
    string? Result,
    string? Error);

/// <summary>
/// The per-step log schema (CLAUDE.md §9, workplan task 5).
/// <para>
/// This is the contract that makes a run reconstructable as a transcript from the logs alone.
/// One of these is written per loop iteration, as a single JSONL record, and the transcript
/// view, the metrics harness and the ablation runner all read this and nothing else.
/// </para>
/// </summary>
public sealed record StepRecord
{
    /// <summary>Identifies the run this step belongs to.</summary>
    public required string RunId { get; init; }

    /// <summary>Identifies the task being attempted, for cross-run comparison.</summary>
    public required string TaskId { get; init; }

    /// <summary>0-based index of this step within the run.</summary>
    public required int StepIndex { get; init; }

    /// <summary>Served role that produced the response.</summary>
    public required string Role { get; init; }

    /// <summary>Model id the server reported, when it reports one.</summary>
    public string? ModelId { get; init; }

    /// <summary>When the step started.</summary>
    public required DateTimeOffset StartedAt { get; init; }

    /// <summary>The full prompt as sent for this step.</summary>
    public required IReadOnlyList<TranscriptMessage> Prompt { get; init; }

    /// <summary>The model's text response, if any.</summary>
    public string? ResponseText { get; init; }

    /// <summary>Every tool call issued in this step, in order.</summary>
    public required IReadOnlyList<ToolCallRecord> ToolCalls { get; init; }

    /// <summary>Request properties attached by constrained decoding, so an arm is auditable.</summary>
    public IReadOnlyDictionary<string, object?>? RequestProperties { get; init; }

    /// <summary>Prompt tokens reported for this call.</summary>
    public long? InputTokens { get; init; }

    /// <summary>Completion tokens reported for this call.</summary>
    public long? OutputTokens { get; init; }

    /// <summary>Total tokens reported for this call.</summary>
    public long? TotalTokens { get; init; }

    /// <summary>Wall-clock spent in the model call.</summary>
    public required double ModelLatencyMs { get; init; }

    /// <summary>Wall-clock for the whole step, including tool execution.</summary>
    public required double StepLatencyMs { get; init; }

    /// <summary>Finish reason the server reported.</summary>
    public string? FinishReason { get; init; }

    /// <summary>Estimated size of the window that was sent, for context-budget analysis.</summary>
    public int? EstimatedContextTokens { get; init; }

    /// <summary>Whether older turns were compacted before this step was sent.</summary>
    public bool ContextCompacted { get; init; }

    /// <summary>What the loop did next: continued, completed, or the limit that stopped it.</summary>
    public required string Outcome { get; init; }

    /// <summary>Error detail when the step failed.</summary>
    public string? Error { get; init; }
}
