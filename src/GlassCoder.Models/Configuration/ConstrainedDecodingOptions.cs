namespace GlassCoder.Models.Configuration;

/// <summary>
/// Request-side constrained decoding settings (CLAUDE.md §6, workplan task 6).
/// <para>
/// This is the highest-ROI reliability lever in the harness: it makes a malformed tool call
/// structurally impossible instead of merely unlikely. Every knob here is a request-side
/// property because the enforcement happens in the server's decoder, below the seam.
/// </para>
/// <para>
/// The property <em>names</em> are configurable on purpose. Which key a server honours
/// (<c>guided_json</c>, <c>guided_decoding_backend</c>, <c>strictJsonSchema</c>, ...) drifts
/// between vLLM / SGLang / llama.cpp releases (CLAUDE.md §19), so the harness must never bake
/// one in.
/// </para>
/// </summary>
public sealed class ConstrainedDecodingOptions
{
    /// <summary>Master switch. When false the request passes through untouched.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Ask the server to enforce the generated tool JSON schema exactly (OpenAI "strict"
    /// function calling). This is the setting that removes malformed tool calls.
    /// </summary>
    public bool StrictToolSchemas { get; set; } = true;

    /// <summary>
    /// Request property that turns on strict tool schemas. Defaults to the key the
    /// Microsoft.Extensions.AI OpenAI client understands.
    /// </summary>
    public string StrictToolSchemasPropertyName { get; set; } = "strictJsonSchema";

    /// <summary>
    /// Force the model to emit a tool call on every step. Off by default: with a read-only
    /// Phase 0 tool set the agent would never be able to produce a final answer. Turn it on
    /// only once a terminal tool (for example <c>complete_task</c>) exists.
    /// </summary>
    public bool RequireToolCall { get; set; }

    /// <summary>
    /// Constrain the model to at most one tool call per step, matching the Observe → Think →
    /// Act → Result loop (CLAUDE.md §3.1) one act at a time.
    /// </summary>
    public bool SingleToolCallPerStep { get; set; } = true;

    /// <summary>
    /// Guided-decoding backend to select on the server (for example <c>xgrammar</c> or
    /// <c>outlines</c>). Null leaves the server default in place.
    /// </summary>
    public string? GuidedDecodingBackend { get; set; }

    /// <summary>Request property carrying <see cref="GuidedDecodingBackend"/>.</summary>
    public string GuidedDecodingBackendPropertyName { get; set; } = "guided_decoding_backend";

    /// <summary>
    /// A raw JSON schema the whole response must conform to. Used for structured non-tool
    /// responses (for example a critic verdict); leave null for ordinary tool-calling turns.
    /// </summary>
    public string? GuidedJsonSchema { get; set; }

    /// <summary>Request property carrying <see cref="GuidedJsonSchema"/>.</summary>
    public string GuidedJsonSchemaPropertyName { get; set; } = "guided_json";

    /// <summary>
    /// Also express <see cref="GuidedJsonSchema"/> as a standard OpenAI
    /// <c>response_format: json_schema</c>, for servers that honour that instead of
    /// <c>guided_json</c>.
    /// </summary>
    public bool ApplyGuidedJsonAsResponseFormat { get; set; } = true;

    /// <summary>Schema name sent with <c>response_format: json_schema</c>.</summary>
    public string ResponseFormatSchemaName { get; set; } = "glasscoder_response";

    /// <summary>
    /// Escape hatch for any other server-specific decoding knob. Values that parse as JSON are
    /// sent as JSON; everything else is sent as a string.
    /// </summary>
    public IDictionary<string, string> AdditionalRequestProperties { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal);
}
