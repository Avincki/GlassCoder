using GlassCoder.Models.Configuration;
using GlassCoder.TestSupport;

namespace GlassCoder.Models.Tests;

/// <summary>
/// The connection check behind the settings dialog's "does this work?" button.
/// <para>
/// Exercised against a real socket rather than a faked <see cref="Microsoft.Extensions.AI.IChatClient"/>:
/// the whole value of the check is that it crosses the seam, so a test that stops short of the
/// wire would prove nothing about it.
/// </para>
/// </summary>
public sealed class ModelConnectionProbeTests
{
    [Fact]
    public async Task A_working_endpoint_passes_every_step()
    {
        using FakeOpenAiServer server = new();
        server.EnqueueText("pong");
        using ModelConnectionProbe probe = new();

        ConnectionCheckResult result = await probe.CheckAsync("worker", Role(server, apiKey: "sk-test-key"));

        result.Outcome.ShouldBe(ConnectionCheckOutcome.Ok);
        result.Succeeded.ShouldBeTrue();
        result.ServedModels.ShouldContain("worker");
        result.Steps.Select(step => step.Name).ShouldBe(["Settings", "Server", "Alias", "Completion"]);
        result.Steps[^1].Detail.ShouldContain("pong");
    }

    [Fact]
    public async Task The_configured_key_is_sent_to_the_server()
    {
        using FakeOpenAiServer server = new();
        server.EnqueueText("pong");
        using ModelConnectionProbe probe = new();

        await probe.CheckAsync("worker", Role(server, apiKey: "sk-test-key"));

        server.AuthorizationHeaders.ShouldAllBe(header => header == "Bearer sk-test-key");
    }

    [Fact]
    public async Task A_rejected_key_is_reported_as_a_rejected_key()
    {
        using FakeOpenAiServer server = new() { ModelsStatusCode = 401 };
        using ModelConnectionProbe probe = new();

        ConnectionCheckResult result = await probe.CheckAsync("worker", Role(server, apiKey: "sk-wrong"));

        result.Outcome.ShouldBe(ConnectionCheckOutcome.Failed);
        result.Summary.ShouldContain("rejected the API key");
        // It stops there: asking for a completion with a key the server already refused would
        // only produce a second, less specific failure.
        result.Steps.ShouldNotContain(step => step.Name == "Completion");
    }

    [Fact]
    public async Task An_alias_the_server_does_not_serve_is_a_warning_not_a_failure()
    {
        using FakeOpenAiServer server = new();
        server.ServedModels.Clear();
        server.ServedModels.Add("qwen3-coder-30b");
        server.EnqueueText("pong");
        using ModelConnectionProbe probe = new();

        ModelRoleOptions role = Role(server);
        role.ModelAlias = "worker";

        ConnectionCheckResult result = await probe.CheckAsync("worker", role);

        result.Outcome.ShouldBe(ConnectionCheckOutcome.Warning);
        result.Succeeded.ShouldBeTrue();
        result.Summary.ShouldContain("qwen3-coder-30b");
    }

    [Fact]
    public async Task A_server_without_a_model_list_still_passes_on_the_completion()
    {
        using FakeOpenAiServer server = new() { ModelsStatusCode = 404 };
        server.EnqueueText("pong");
        using ModelConnectionProbe probe = new();

        ConnectionCheckResult result = await probe.CheckAsync("worker", Role(server));

        result.Succeeded.ShouldBeTrue();
        result.Steps.ShouldContain(step => step.Name == "Completion" && step.Outcome == ConnectionCheckOutcome.Ok);
    }

    [Fact]
    public async Task A_misconfigured_role_fails_before_anything_touches_the_network()
    {
        using ModelConnectionProbe probe = new();

        ConnectionCheckResult result = await probe.CheckAsync(
            "worker",
            new ModelRoleOptions { Endpoint = "localhost:8001", ModelAlias = "worker" });

        result.Outcome.ShouldBe(ConnectionCheckOutcome.Failed);
        result.Steps.Count.ShouldBe(1);
        result.Steps[0].Name.ShouldBe("Settings");
        result.Summary.ShouldContain("absolute http(s) URI");
    }

    [Fact]
    public async Task An_endpoint_with_nothing_listening_says_so()
    {
        int port;
        using (FakeOpenAiServer server = new())
        {
            // Take a port the operating system just handed out, then give it straight back.
            port = server.Port;
        }

        using ModelConnectionProbe probe = new();

        ConnectionCheckResult result = await probe.CheckAsync(
            "worker",
            new ModelRoleOptions
            {
                Endpoint = $"http://127.0.0.1:{port}/v1",
                ModelAlias = "worker",
                TimeoutSeconds = 5,
            });

        result.Outcome.ShouldBe(ConnectionCheckOutcome.Failed);
        result.Summary.ShouldContain("Could not reach");
    }

    private static ModelRoleOptions Role(FakeOpenAiServer server, string? apiKey = null) => new()
    {
        Endpoint = server.Endpoint,
        ModelAlias = "worker",
        ApiKey = apiKey,
        TimeoutSeconds = 10,
    };
}
