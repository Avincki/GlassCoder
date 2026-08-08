namespace GlassCoder.Tools.Retrieval;

/// <summary>Why the model says it needs to look something up. Not free-form curiosity.</summary>
public enum RetrievalReason
{
    /// <summary>A type or member the workspace does not declare and the model does not know.</summary>
    UnknownApi,

    /// <summary>Whether an API exists in the version actually referenced.</summary>
    VersionCheck,

    /// <summary>Whether a symbol exists at all - the cheap check against an invented name.</summary>
    SymbolExists,
}

/// <summary>One prospective call, as the policy sees it.</summary>
/// <param name="Server">Which upstream it would reach.</param>
/// <param name="ToolName">The registered name, for the observation and the metrics.</param>
/// <param name="Reason">What the model says this is for.</param>
public sealed record RetrievalRequest(RetrievalServer Server, string ToolName, RetrievalReason Reason);

/// <summary>A refusal, shaped so the tool can return it verbatim as an observation.</summary>
/// <param name="Code">A stable <see cref="ToolErrorCodes"/> value; metrics group by it.</param>
/// <param name="Message">What was refused and why.</param>
/// <param name="Hint">What would have worked instead, when there is such a thing.</param>
public sealed record RetrievalDenial(string Code, string Message, string? Hint = null);

/// <summary>What one run spent. Read by the metrics recorder (task 61).</summary>
/// <param name="Allowed">Calls the policy admitted.</param>
/// <param name="Blocked">Calls it refused, by error code.</param>
/// <param name="CharsReturned">Characters returned into the conversation.</param>
public sealed record RetrievalStats(
    int Allowed,
    IReadOnlyDictionary<string, int> Blocked,
    int CharsReturned);

/// <summary>
/// Whether anything in this run indicates that external knowledge is genuinely needed.
/// <para>
/// The seam exists now and is answered by nothing until task 59 wires the verification
/// diagnostics into it. That is deliberate: the policy is written against the question, and the
/// answer arrives without the policy changing.
/// </para>
/// </summary>
public interface IRetrievalSignals
{
    /// <summary>True when a diagnostic or a suite flag says the answer is not in the workspace.</summary>
    bool ExternalKnowledgeIndicated { get; }

    /// <summary>What indicated it, for the transcript. Null when nothing did.</summary>
    string? Indication { get; }
}

/// <summary>
/// Nothing indicates anything. The registered default until task 59, which is why
/// <see cref="RetrievalOptions.AllowProactive"/> is the switch that makes a live trial possible
/// before then.
/// </summary>
public sealed class NoRetrievalSignals : IRetrievalSignals
{
    /// <inheritdoc />
    public bool ExternalKnowledgeIndicated => false;

    /// <inheritdoc />
    public string? Indication => null;
}

/// <summary>
/// The gate every retrieval tool passes through before it reaches a cache or a network
/// (workplan task 55).
/// <para>
/// The system prompt is not where this belongs. "Only call when needed" is advice, and run
/// f4ed50e0 answered a refutation by adding two packages it never used while tool-call validity
/// read 0.93 — a model that ignores a hint twice has not been mechanised. Admission is enforced
/// here and counted, so "did the gate hold" is a number rather than an impression.
/// </para>
/// </summary>
public interface IRetrievalPolicy
{
    /// <summary>
    /// Whether this call may run. A false return always sets <paramref name="denial"/>, and the
    /// caller returns it as an observation — never an exception, never a silent no-op.
    /// </summary>
    bool TryAdmit(RetrievalRequest request, out RetrievalDenial? denial);

    /// <summary>Records an admitted call and what it returned, against this run's budget.</summary>
    void RecordCall(RetrievalRequest request, int charsReturned);

    /// <summary>Records a refusal, so blocked-by-code reaches the metrics.</summary>
    void RecordDenial(RetrievalDenial denial);

    /// <summary>What this run has spent so far.</summary>
    RetrievalStats Stats { get; }
}
