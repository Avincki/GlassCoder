using System.Text.Json;
using GlassCoder.Models;
using GlassCoder.Models.Configuration;
using GlassCoder.TestSupport;
using Microsoft.Extensions.AI;

namespace GlassCoder.Models.Tests;

/// <summary>
/// Request-side constrained decoding (workplan task 6). The point of these tests is that the
/// settings reach the wire on <em>every</em> request, not that a caller remembered to add them.
/// </summary>
public sealed class ConstrainedDecodingChatClientTests
{
    private static readonly AIFunction SampleTool = AIFunctionFactory.Create(
        (string path) => path,
        "read_file",
        "Reads a file.");

    [Fact]
    public async Task Strict_tool_schemas_are_requested_whenever_tools_are_present()
    {
        (ConstrainedDecodingChatClient client, FakeChatClient inner) = Build(new ConstrainedDecodingOptions());

        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], WithTools());

        AdditionalPropertiesDictionary properties = inner.Requests[0].Options!.AdditionalProperties!;
        properties["strictJsonSchema"].ShouldBe(true);
    }

    [Fact]
    public async Task Strict_tool_schemas_are_not_requested_when_there_are_no_tools()
    {
        (ConstrainedDecodingChatClient client, FakeChatClient inner) = Build(new ConstrainedDecodingOptions());

        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], new ChatOptions());

        inner.Requests[0].Options!.AdditionalProperties!.ContainsKey("strictJsonSchema").ShouldBeFalse();
    }

    [Fact]
    public async Task The_guided_decoding_backend_is_passed_through_under_its_configured_key()
    {
        ConstrainedDecodingOptions options = new()
        {
            GuidedDecodingBackend = "xgrammar",
            GuidedDecodingBackendPropertyName = "guided_decoding_backend",
        };
        (ConstrainedDecodingChatClient client, FakeChatClient inner) = Build(options);

        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], WithTools());

        inner.Requests[0].Options!.AdditionalProperties!["guided_decoding_backend"].ShouldBe("xgrammar");
    }

    [Fact]
    public async Task A_guided_json_schema_is_sent_raw_and_as_a_response_format()
    {
        ConstrainedDecodingOptions options = new()
        {
            GuidedJsonSchema = """{"type":"object","properties":{"verdict":{"type":"string"}}}""",
        };
        (ConstrainedDecodingChatClient client, FakeChatClient inner) = Build(options);

        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], new ChatOptions());

        ChatOptions sent = inner.Requests[0].Options!;
        sent.AdditionalProperties!["guided_json"].ShouldBeOfType<JsonElement>();
        sent.ResponseFormat.ShouldBeOfType<ChatResponseFormatJson>().Schema.ShouldNotBeNull();
    }

    [Fact]
    public async Task One_tool_call_per_step_is_enforced_by_default()
    {
        (ConstrainedDecodingChatClient client, FakeChatClient inner) = Build(new ConstrainedDecodingOptions());

        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], WithTools());

        inner.Requests[0].Options!.AllowMultipleToolCalls.ShouldBe(false);
    }

    [Fact]
    public async Task Requiring_a_tool_call_is_off_until_it_is_asked_for()
    {
        // A Phase 0 tool set is read-only: force a tool call every step and the agent can never
        // give a final answer.
        (ConstrainedDecodingChatClient defaults, FakeChatClient defaultInner) = Build(new ConstrainedDecodingOptions());
        await defaults.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], WithTools());
        defaultInner.Requests[0].Options!.ToolMode.ShouldBeNull();

        (ConstrainedDecodingChatClient required, FakeChatClient requiredInner) =
            Build(new ConstrainedDecodingOptions { RequireToolCall = true });
        await required.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], WithTools());
        requiredInner.Requests[0].Options!.ToolMode.ShouldBeOfType<RequiredChatToolMode>();
    }

    [Fact]
    public async Task Disabling_constrained_decoding_leaves_the_request_untouched()
    {
        (ConstrainedDecodingChatClient client, FakeChatClient inner) =
            Build(new ConstrainedDecodingOptions { Enabled = false });
        ChatOptions original = WithTools();

        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], original);

        inner.Requests[0].Options.ShouldBeSameAs(original);
    }

    [Fact]
    public async Task Caller_supplied_options_are_never_mutated()
    {
        (ConstrainedDecodingChatClient client, _) = Build(new ConstrainedDecodingOptions());
        ChatOptions caller = WithTools();

        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], caller);

        caller.AdditionalProperties.ShouldBeNull();
        caller.AllowMultipleToolCalls.ShouldBeNull();
    }

    [Fact]
    public async Task Extra_request_properties_are_parsed_as_json_when_they_look_like_json()
    {
        ConstrainedDecodingOptions options = new();
        options.AdditionalRequestProperties["top_k"] = "40";
        options.AdditionalRequestProperties["stop_token_ids"] = "[128009]";
        options.AdditionalRequestProperties["backend_hint"] = "vllm";
        (ConstrainedDecodingChatClient client, FakeChatClient inner) = Build(options);

        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], new ChatOptions());

        AdditionalPropertiesDictionary properties = inner.Requests[0].Options!.AdditionalProperties!;
        ((JsonElement)properties["top_k"]!).GetInt32().ShouldBe(40);
        ((JsonElement)properties["stop_token_ids"]!).ValueKind.ShouldBe(JsonValueKind.Array);
        properties["backend_hint"].ShouldBe("vllm");
    }

    private static ChatOptions WithTools() => new() { Tools = [SampleTool] };

    private static (ConstrainedDecodingChatClient Client, FakeChatClient Inner) Build(ConstrainedDecodingOptions decoding)
    {
        FakeChatClient inner = new(FakeChatClient.Text("ok"));
        ModelRoleOptions role = new()
        {
            Endpoint = "http://localhost:8001/v1",
            ModelAlias = "worker",
            ConstrainedDecoding = decoding,
        };

        return (new ConstrainedDecodingChatClient(inner, role), inner);
    }
}
