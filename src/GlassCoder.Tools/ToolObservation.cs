using System.Text.Json.Serialization;

namespace GlassCoder.Tools;

/// <summary>
/// The single object every tool returns (CLAUDE.md §7).
/// <para>
/// Errors are observations, not exceptions. A tool that cannot do its job reports that fact in
/// a shape the model can read and act on; nothing a tool does may throw out of the controller
/// loop.
/// </para>
/// </summary>
/// <typeparam name="TData">Payload type on success. Its JSON schema is generated from the type.</typeparam>
public sealed class ToolObservation<TData>
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

    /// <summary>An unexpected failure. The loop turns escaped exceptions into this.</summary>
    public const string Unexpected = "unexpected";
}

/// <summary>Factory helpers so tool bodies stay one line at their exit points.</summary>
public static class Observation
{
    /// <summary>A successful observation carrying <paramref name="data"/>.</summary>
    public static ToolObservation<TData> Ok<TData>(string tool, TData data, string? summary = null) =>
        new() { Ok = true, Tool = tool, Data = data, Summary = summary };

    /// <summary>A failed observation. Never throw instead of calling this.</summary>
    public static ToolObservation<TData> Fail<TData>(string tool, string code, string message, string? hint = null) =>
        new() { Ok = false, Tool = tool, Summary = message, Error = new ToolError(code, message, hint) };
}
