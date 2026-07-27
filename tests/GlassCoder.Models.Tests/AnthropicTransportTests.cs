using GlassCoder.Models.Configuration;
using GlassCoder.TestSupport;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace GlassCoder.Models.Tests;

/// <summary>
/// The Anthropic transport (workplan task 37): the client shape that lets <c>critic-remote</c>
/// address <c>/v1/messages</c> rather than an OpenAI-compatible gateway.
/// <para>
/// Exercised against a real socket for the same reason the OpenAI seam is: what these tests
/// protect is the wire - the paths, the headers, and above all what is <em>not</em> sent, since
/// current Anthropic models reject sampling parameters outright.
/// </para>
/// </summary>
public sealed class AnthropicTransportTests
{
    [Fact]
    public async Task The_factory_speaks_v1_messages_for_an_anthropic_role()
    {
        using FakeAnthropicServer server = new();
        server.EnqueueText("The critique.");
        using ChatClientFactory factory = Factory(server, apiKey: "sk-ant-test");

        IChatClient client = factory.GetClient("critic-remote");
        ChatResponse response = await client.GetResponseAsync(
            [
                new ChatMessage(ChatRole.System, "You are a reviewer whose job is to REFUTE a change."),
                new ChatMessage(ChatRole.User, "Can you refute this change?"),
            ],
            // Temperature 0 is the critic panel's habit, and the right one for a local critic.
            // The transport must drop it rather than forward it - see the wire assertions below.
            new ChatOptions { Temperature = 0f });

        response.Text.ShouldBe("The critique.");

        System.Text.Json.JsonElement request = server.Request(0);
        request.GetProperty("model").GetString().ShouldBe("claude-opus-5");
        request.GetProperty("max_tokens").GetInt32().ShouldBeGreaterThan(0);
        request.TryGetProperty("system", out _).ShouldBeTrue("the system message must ride the system field");
        request.TryGetProperty("temperature", out _)
            .ShouldBeFalse("current Anthropic models reject sampling parameters with a 400");
        request.TryGetProperty("top_p", out _).ShouldBeFalse();

        server.ApiKeys[0].ShouldBe("sk-ant-test");
        server.Versions[0].ShouldNotBeNull("every request must carry anthropic-version");
    }

    [Fact]
    public async Task A_refusal_is_an_empty_answer_not_an_exception()
    {
        // The classifiers declining is a successful HTTP 200 with stop_reason "refusal" and no
        // content. For the critic panel that must read as "the critic returned nothing" - a
        // failure to judge and a non-vote - never as a crash and never as an acceptance.
        using FakeAnthropicServer server = new();
        server.EnqueueRefusal();
        using ChatClientFactory factory = Factory(server);

        ChatResponse response = await factory.GetClient("critic-remote")
            .GetResponseAsync([new ChatMessage(ChatRole.User, "Judge this.")]);

        string.IsNullOrWhiteSpace(response.Text).ShouldBeTrue();
    }

    [Fact]
    public async Task Usage_is_read_from_the_wire()
    {
        using FakeAnthropicServer server = new();
        server.EnqueueText("ok");
        using ChatClientFactory factory = Factory(server);

        ChatResponse response = await factory.GetClient("critic-remote")
            .GetResponseAsync([new ChatMessage(ChatRole.User, "Judge this.")]);

        response.Usage.ShouldNotBeNull();
        response.Usage.InputTokenCount.ShouldBe(11);
        response.Usage.OutputTokenCount.ShouldBe(7);
    }

    [Fact]
    public async Task The_connection_check_probes_an_anthropic_endpoint_end_to_end()
    {
        using FakeAnthropicServer server = new();
        server.EnqueueText("pong");
        using ModelConnectionProbe probe = new();

        ConnectionCheckResult result = await probe.CheckAsync("critic-remote", Role(server, apiKey: "sk-ant-test"));

        result.Outcome.ShouldBe(ConnectionCheckOutcome.Ok);
        result.ServedModels.ShouldContain("claude-opus-5");
        result.Steps.Select(step => step.Name).ShouldBe(["Settings", "Server", "Alias", "Completion"]);
        result.Steps[^1].Detail.ShouldContain("pong");

        // Anthropic-style auth is the x-api-key header pair, not a bearer token.
        server.ApiKeys.ShouldContain("sk-ant-test");
        server.Versions.ShouldAllBe(version => version != null);
    }

    private static ChatClientFactory Factory(FakeAnthropicServer server, string? apiKey = null)
    {
        ModelsOptions options = new() { DefaultRole = "critic-remote" };
        options.Roles["critic-remote"] = Role(server, apiKey);
        return new ChatClientFactory(Options.Create(options));
    }

    private static ModelRoleOptions Role(FakeAnthropicServer server, string? apiKey = null) =>
        new()
        {
            Transport = ModelTransport.Anthropic,
            Endpoint = server.Endpoint,
            ModelAlias = "claude-opus-5",
            ApiKey = apiKey,
            TimeoutSeconds = 30,
        };
}
