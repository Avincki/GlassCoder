using System.Text.Json.Serialization;

namespace GlassCoder.Tools;

/// <summary>
/// An observation seen without its payload type - what the harness itself reads back from a
/// finished call.
/// <para>
/// The compaction digest wants exactly the outcome and none of the data, and a generic type it
/// cannot name is a fact it cannot keep: without this view, every ok flag and every refusal
/// reason vanished at the compaction horizon, and the digest's "do not repeat" advice applied
/// as readily to a write that was refused ten times as to one that landed.
/// </para>
/// </summary>
public interface IToolObservation
{
    /// <summary>Whether the tool did what was asked.</summary>
    bool Ok { get; }

    /// <summary>
    /// Whether the operation the tool carried out achieved its purpose. Distinct from
    /// <see cref="Ok"/> for tools that relay a command's refusal as information: a failed
    /// <c>dotnet</c> operation is <c>Ok</c> - the tool did its job of running and reporting it -
    /// but the outcome is a failure, and the progress machinery must see it as one. Run
    /// 4b562c91 sent the same misshapen <c>add_to_solution</c> five times because every relay
    /// read as success to the loop-breakers.
    /// </summary>
    bool OutcomeOk => Ok;

    /// <summary>
    /// Whether this answer came from a cache rather than from doing the work again.
    /// <para>
    /// A fact about the call, not about the result, and the progress machinery is the only
    /// caller that needs it: a verification served from cache re-confirms something the run
    /// already knew, and two in a row with nothing changed between them is a run marking time.
    /// </para>
    /// </summary>
    bool Reused => false;

    /// <summary>Name of the tool that produced this observation.</summary>
    string Tool { get; }

    /// <summary>One line the model can read without parsing the payload.</summary>
    string? Summary { get; }

    /// <summary>What went wrong. Null when <see cref="Ok"/> is true.</summary>
    ToolError? Error { get; }
}

/// <summary>
/// The single object every tool returns (CLAUDE.md §7).
/// <para>
/// Errors are observations, not exceptions. A tool that cannot do its job reports that fact in
/// a shape the model can read and act on; nothing a tool does may throw out of the controller
/// loop.
/// </para>
/// </summary>
/// <typeparam name="TData">Payload type on success. Its JSON schema is generated from the type.</typeparam>
public sealed class ToolObservation<TData> : IToolObservation
{
    /// <summary>Whether the tool did what was asked.</summary>
    [JsonPropertyOrder(0)]
    public required bool Ok { get; init; }

    /// <summary>Name of the tool that produced this observation.</summary>
    [JsonPropertyOrder(1)]
    public required string Tool { get; init; }

    /// <summary>One line the model can read without parsing <see cref="Data"/>.</summary>
    [JsonPropertyOrder(2)]
    public string? Summary { get; init; }

    /// <summary>Result payload. Null when <see cref="Ok"/> is false.</summary>
    [JsonPropertyOrder(3)]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public TData? Data { get; init; }

    /// <summary>What went wrong. Null when <see cref="Ok"/> is true.</summary>
    [JsonPropertyOrder(4)]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ToolError? Error { get; init; }

    /// <summary>
    /// Whether the operation achieved its purpose - see <see cref="IToolObservation.OutcomeOk"/>.
    /// </summary>
    [JsonIgnore]
    public bool OutcomeOk { get; init; } = true;

    /// <summary>
    /// The wire shadow of <see cref="OutcomeOk"/>, present only when false. The AI function
    /// layer serialises every observation to JSON before the registry sees it again, so an
    /// unserialised flag would exist only in unit tests - and a serialised-always flag would
    /// tax every successful call for the rare failed one. Successful observations stay
    /// byte-identical to what they were.
    /// </summary>
    [JsonPropertyName("outcomeOk")]
    [JsonPropertyOrder(5)]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? OutcomeOnWire => OutcomeOk ? null : false;

    /// <summary>Whether this answer was served from a cache - see <see cref="IToolObservation.Reused"/>.</summary>
    [JsonIgnore]
    public bool Reused { get; init; }

    /// <summary>
    /// The wire shadow of <see cref="Reused"/>, on the same terms as <see cref="OutcomeOnWire"/>
    /// and for the same reason: the AI function layer serialises the observation before the
    /// registry sees it again, so a flag that is not on the wire exists only in unit tests.
    /// Present only when true, so nothing changes for the calls that did the work.
    /// </summary>
    [JsonPropertyName("reused")]
    [JsonPropertyOrder(6)]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? ReusedOnWire => Reused ? true : null;
}

/// <summary>A machine-readable failure inside an observation.</summary>
/// <param name="Code">Stable, greppable failure code - see <see cref="ToolErrorCodes"/>.</param>
/// <param name="Message">Human- and model-readable explanation.</param>
/// <param name="Hint">Optional next step that would make the call succeed.</param>
public sealed record ToolError(string Code, string Message, string? Hint = null);

/// <summary>The failure codes tools report. Metrics group by these, so they are stable.</summary>
public static class ToolErrorCodes
{
    /// <summary>An argument was missing, malformed or out of range.</summary>
    public const string InvalidArgument = "invalid_argument";

    /// <summary>The requested path does not exist.</summary>
    public const string NotFound = "not_found";

    /// <summary>Something is already at the path (used by <c>create_file</c>).</summary>
    public const string AlreadyExists = "already_exists";

    /// <summary>The path allow-list guardrail rejected the path (CLAUDE.md §7).</summary>
    public const string PathNotAllowed = "path_not_allowed";

    /// <summary>The target string was absent or ambiguous (used by <c>edit_file</c>).</summary>
    public const string AmbiguousTarget = "ambiguous_target";

    /// <summary>The file is too large, or is not text.</summary>
    public const string Unreadable = "unreadable";

    /// <summary>The operation was cancelled or ran out of time.</summary>
    public const string Timeout = "timeout";

    /// <summary>The tool name was not in the registry.</summary>
    public const string UnknownTool = "unknown_tool";

    /// <summary>A verification rung refused the change before it was applied.</summary>
    public const string VerificationFailed = "verification_failed";

    /// <summary>The sandbox required to run this tool was unavailable.</summary>
    public const string SandboxUnavailable = "sandbox_unavailable";

    /// <summary>A human declined to approve the change.</summary>
    public const string ApprovalRefused = "approval_refused";

    /// <summary>The git executable was missing, or the workspace is not a git repository.</summary>
    public const string GitUnavailable = "git_unavailable";

    /// <summary>Branch policy refused the operation (used by <c>git_push</c>).</summary>
    public const string BranchNotAllowed = "branch_not_allowed";

    /// <summary>A merge or rebase hit conflicts (used by <c>git_sync</c>).</summary>
    public const string MergeConflict = "merge_conflict";

    /// <summary>Retrieval is switched off for this run, or for this server (workplan task 55).</summary>
    public const string RetrievalDisabled = "retrieval_disabled";

    /// <summary>
    /// Nothing in the run indicates the answer is outside the workspace. The default refusal:
    /// retrieval is admitted on evidence, not on the model deciding it is curious.
    /// </summary>
    public const string RetrievalNotIndicated = "retrieval_not_indicated";

    /// <summary>The run's retrieval calls, result characters, or patience for calls that change
    /// nothing, are spent.</summary>
    public const string RetrievalBudgetExhausted = "retrieval_budget_exhausted";

    /// <summary>Replay mode and the corpus has no answer for this call. Never a silent live call.</summary>
    public const string RetrievalCacheMiss = "retrieval_cache_miss";

    /// <summary>An MCP server timed out, refused, or could not be reached.</summary>
    public const string UpstreamUnavailable = "upstream_unavailable";

    /// <summary>An unexpected failure. The loop turns escaped exceptions into this.</summary>
    public const string Unexpected = "unexpected";
}

/// <summary>Factory helpers so tool bodies stay one line at their exit points.</summary>
public static class Observation
{
    /// <summary>
    /// A successful observation carrying <paramref name="data"/>. Pass
    /// <paramref name="outcomeOk"/> false when the tool ran fine but the operation it relayed
    /// refused - a failed dotnet command, say - so the progress machinery counts the failure
    /// while the model still reads an ordinary observation.
    /// </summary>
    public static ToolObservation<TData> Ok<TData>(
        string tool, TData data, string? summary = null, bool outcomeOk = true, bool reused = false) =>
        new() { Ok = true, Tool = tool, Data = data, Summary = summary, OutcomeOk = outcomeOk, Reused = reused };

    /// <summary>A failed observation. Never throw instead of calling this.</summary>
    public static ToolObservation<TData> Fail<TData>(string tool, string code, string message, string? hint = null) =>
        new() { Ok = false, Tool = tool, Summary = message, Error = new ToolError(code, message, hint) };
}
