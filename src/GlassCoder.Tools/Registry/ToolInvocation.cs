namespace GlassCoder.Tools.Registry;

/// <summary>
/// Outcome of executing one tool call. <see cref="ToolCallStatus.Succeeded"/> and
/// <see cref="ToolCallStatus.Failed"/> both mean the call parsed and ran - the difference is
/// whether the tool could do the job. Only the remaining statuses are invalid calls, which is
/// exactly the denominator of the tool-call validity rate (CLAUDE.md §11).
/// </summary>
public enum ToolCallStatus
{
    /// <summary>Parsed, executed, and the observation reports success.</summary>
    Succeeded,

    /// <summary>Parsed and executed; the observation reports a handled failure.</summary>
    Failed,

    /// <summary>The model called a tool that is not registered.</summary>
    UnknownTool,

    /// <summary>Arguments did not bind to the tool's generated schema.</summary>
    InvalidArguments,

    /// <summary>The tool threw. A defect in the tool - it should have returned an observation.</summary>
    Faulted,
}

/// <summary>One executed tool call, as recorded in the transcript (CLAUDE.md §9).</summary>
public sealed record ToolInvocation
{
    /// <summary>Correlation id the model used, echoed back on the result content.</summary>
    public required string CallId { get; init; }

    /// <summary>Tool name as the model called it.</summary>
    public required string ToolName { get; init; }

    /// <summary>Whether the call parsed, executed, and what it reported.</summary>
    public required ToolCallStatus Status { get; init; }

    /// <summary>The observation object handed back to the model.</summary>
    public required object? Result { get; init; }

    /// <summary>Wall-clock spent inside the tool.</summary>
    public required TimeSpan Duration { get; init; }

    /// <summary>Arguments as the model supplied them.</summary>
    public IReadOnlyDictionary<string, object?>? Arguments { get; init; }

    /// <summary>Failure detail for the statuses that carry one.</summary>
    public string? ErrorMessage { get; init; }

    /// <summary>
    /// The observation's own one-line account of what happened, success or not. Distinct from
    /// <see cref="Status"/>, which says only whether the call itself ran.
    /// </summary>
    public string? Summary { get; init; }

    /// <summary>
    /// Whether the operation the tool relayed achieved its purpose. False for
    /// failure-as-information observations - a <c>dotnet</c> command that exited non-zero
    /// behind a <see cref="ToolCallStatus.Succeeded"/> call - so the progress machinery can
    /// count what the model is told is a failure (run 4b562c91: five identical soft-failed
    /// <c>add_to_solution</c> calls, invisible to every loop-breaker).
    /// </summary>
    public bool OutcomeOk { get; init; } = true;

    /// <summary>
    /// Whether the answer came from a cache instead of the work being done again. Carried up for
    /// the progress machinery, which is the only caller that can tell a re-confirmation from a
    /// verification.
    /// </summary>
    public bool ServedFromCache { get; init; }

    /// <summary>Whether this call counts as valid for the tool-call validity rate.</summary>
    public bool IsValid => Status is ToolCallStatus.Succeeded or ToolCallStatus.Failed;
}
