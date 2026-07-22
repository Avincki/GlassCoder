using System.Text.Json;
using GlassCoder.Models.Configuration;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace GlassCoder.Models;

/// <summary>
/// Applies request-side constrained decoding to every call that passes through it
/// (CLAUDE.md §6, workplan task 6).
/// <para>
/// It sits at the top of the client pipeline so the settings are attached before any other
/// stage - including the OpenTelemetry stage - observes the request, which means traces show
/// what was actually sent rather than what the caller asked for.
/// </para>
/// <para>
/// Nothing here parses or repairs model output. The whole point is that the server's decoder
/// cannot emit a non-conforming tool call in the first place: reliability by construction, not
/// by a longer prompt (CLAUDE.md §18).
/// </para>
/// </summary>
public sealed class ConstrainedDecodingChatClient : DelegatingChatClient
{
    private readonly ConstrainedDecodingOptions _constrainedDecoding;
    private readonly Dictionary<string, string> _roleRequestProperties;
    private readonly ILogger _logger;
    private readonly JsonElement? _guidedJsonSchema;

    /// <summary>Creates the stage for one served role.</summary>
    /// <param name="innerClient">The next client in the pipeline.</param>
    /// <param name="role">Settings of the role this pipeline serves.</param>
    /// <param name="logger">Logger used to report an unusable configured schema once, at construction.</param>
    public ConstrainedDecodingChatClient(IChatClient innerClient, ModelRoleOptions role, ILogger? logger = null)
        : base(innerClient)
    {
        ArgumentNullException.ThrowIfNull(role);

        _constrainedDecoding = role.ConstrainedDecoding;
        _roleRequestProperties = new Dictionary<string, string>(role.AdditionalRequestProperties, StringComparer.Ordinal);
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
        _guidedJsonSchema = ParseGuidedSchema(_constrainedDecoding.GuidedJsonSchema, _logger);
    }

    /// <inheritdoc />
    public override Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default) =>
        base.GetResponseAsync(messages, Constrain(options), cancellationToken);

    /// <inheritdoc />
    public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default) =>
        base.GetStreamingResponseAsync(messages, Constrain(options), cancellationToken);

    /// <summary>
    /// Produces the options actually sent over the seam. Exposed for tests and for the
    /// transcript: what was constrained is part of the per-step record.
    /// </summary>
    public ChatOptions? Constrain(ChatOptions? options)
    {
        if (!_constrainedDecoding.Enabled && _roleRequestProperties.Count == 0)
        {
            return options;
        }

        ChatOptions constrained = options?.Clone() ?? new ChatOptions();
        constrained.AdditionalProperties ??= [];

        // Role-level knobs first so a decoding setting always wins over a generic one.
        foreach ((string key, string value) in _roleRequestProperties)
        {
            constrained.AdditionalProperties[key] = RequestPropertyValue.Parse(value);
        }

        if (!_constrainedDecoding.Enabled)
        {
            return constrained;
        }

        bool hasTools = constrained.Tools is { Count: > 0 };

        if (_constrainedDecoding.StrictToolSchemas && hasTools)
        {
            constrained.AdditionalProperties[_constrainedDecoding.StrictToolSchemasPropertyName] = true;
        }

        if (_constrainedDecoding.SingleToolCallPerStep)
        {
            constrained.AllowMultipleToolCalls = false;
        }

        if (_constrainedDecoding.RequireToolCall && hasTools)
        {
            constrained.ToolMode = ChatToolMode.RequireAny;
        }

        if (!string.IsNullOrWhiteSpace(_constrainedDecoding.GuidedDecodingBackend))
        {
            constrained.AdditionalProperties[_constrainedDecoding.GuidedDecodingBackendPropertyName] =
                _constrainedDecoding.GuidedDecodingBackend;
        }

        if (_guidedJsonSchema is { } schema)
        {
            constrained.AdditionalProperties[_constrainedDecoding.GuidedJsonSchemaPropertyName] = schema;

            if (_constrainedDecoding.ApplyGuidedJsonAsResponseFormat && constrained.ResponseFormat is null)
            {
                constrained.ResponseFormat = ChatResponseFormat.ForJsonSchema(
                    schema,
                    _constrainedDecoding.ResponseFormatSchemaName);
            }
        }

        foreach ((string key, string value) in _constrainedDecoding.AdditionalRequestProperties)
        {
            constrained.AdditionalProperties[key] = RequestPropertyValue.Parse(value);
        }

        return constrained;
    }

    private static JsonElement? ParseGuidedSchema(string? schemaJson, ILogger logger)
    {
        if (string.IsNullOrWhiteSpace(schemaJson))
        {
            return null;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(schemaJson);
            return document.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            logger.LogWarning(
                ex,
                "Configured guided-decoding JSON schema is not valid JSON and will be ignored.");
            return null;
        }
    }
}
