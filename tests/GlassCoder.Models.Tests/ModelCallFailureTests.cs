using System.Net.Sockets;
using GlassCoder.Models.Configuration;
using GlassCoder.TestSupport;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace GlassCoder.Models.Tests;

/// <summary>
/// The sentence a run leaves behind when a model call fails.
/// <para>
/// The failures that matter here are made by a real client against a real socket for the same
/// reason the connection probe's are: the exception shapes belong to the SDK, not to us, and a
/// hand-built <see cref="Exception"/> would prove only that the classifier reads its own fixture.
/// A closed port really is closed, and a refused request really carries the server's body.
/// </para>
/// </summary>
public sealed class ModelCallFailureTests
{
    [Fact]
    public async Task A_closed_port_is_reported_as_a_server_that_is_not_running()
    {
        int port;
        using (FakeOpenAiServer server = new())
        {
            // Take a port the operating system just handed out, then give it straight back.
            port = server.Port;
        }

        ModelRoleOptions role = new()
        {
            Endpoint = $"http://127.0.0.1:{port}/v1",
            ModelAlias = "worker",
            TimeoutSeconds = 5,
        };

        ModelCallFailure failure = ModelCallFailure.Describe(
            "worker", role, await FailedCallAsync("worker", role), TimeSpan.FromSeconds(3.4));

        failure.Kind.ShouldBe(ModelCallFailureKind.Unreachable);
        failure.Message.ShouldContain(role.Endpoint);
        failure.Message.ShouldContain("nothing is listening");
        failure.Message.ShouldContain("start the server for this role");
        failure.Message.ShouldContain("ConnectionRefused");
        failure.Message.ShouldContain("after 3.4 s");
    }

    [Theory]
    [InlineData(401, ModelCallFailureKind.Unauthorized)]
    [InlineData(404, ModelCallFailureKind.NotFound)]
    [InlineData(429, ModelCallFailureKind.RateLimited)]
    [InlineData(500, ModelCallFailureKind.ServerError)]
    [InlineData(400, ModelCallFailureKind.RequestRejected)]
    public async Task A_server_that_answers_is_classified_by_what_it_answered(int status, ModelCallFailureKind expected)
    {
        using FakeOpenAiServer server = new() { ChatStatusCode = status };
        ModelRoleOptions role = Role(server);

        ModelCallFailure failure = ModelCallFailure.Describe("worker", role, await FailedCallAsync("worker", role));

        failure.Kind.ShouldBe(expected);
        failure.Message.ShouldContain(status.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task A_refused_request_is_quoted_in_the_server_s_own_words()
    {
        // vLLM's shape: the actionable half of a 400 is in the body, never in the status.
        using FakeOpenAiServer server = new() { ChatStatusCode = 400 };
        ModelRoleOptions role = Role(server);

        ModelCallFailure failure = ModelCallFailure.Describe("worker", role, await FailedCallAsync("worker", role));

        failure.Message.ShouldContain("maximum context length is 8192 tokens");
    }

    [Fact]
    public async Task A_404_names_the_alias_that_may_not_be_served()
    {
        using FakeOpenAiServer server = new()
        {
            ChatStatusCode = 404,
            // The OpenAI API's shape, nested one level deeper than vLLM's.
            ChatErrorBody = """{"error":{"message":"The model `worker` does not exist.","type":"invalid_request_error"}}""",
        };

        ModelRoleOptions role = Role(server);

        ModelCallFailure failure = ModelCallFailure.Describe("worker", role, await FailedCallAsync("worker", role));

        failure.Kind.ShouldBe(ModelCallFailureKind.NotFound);
        failure.Message.ShouldContain("'worker' is not an alias this server serves");
        failure.Message.ShouldContain("The model `worker` does not exist.");
    }

    [Fact]
    public void A_timeout_names_the_timeout_it_ran_past()
    {
        ModelRoleOptions role = new() { Endpoint = "http://localhost:8002/v1", ModelAlias = "worker", TimeoutSeconds = 600 };

        ModelCallFailure failure = ModelCallFailure.Describe(
            "worker",
            role,
            new TaskCanceledException("The request was canceled due to the configured timeout.", new TimeoutException()));

        failure.Kind.ShouldBe(ModelCallFailureKind.TimedOut);
        failure.Message.ShouldContain("600s timeout");
    }

    [Fact]
    public void A_host_name_that_stopped_resolving_is_not_a_dead_server()
    {
        ModelRoleOptions role = new() { Endpoint = "http://spark.example.ts.net:8002/v1", ModelAlias = "worker" };

        ModelCallFailure failure = ModelCallFailure.Describe(
            "worker",
            role,
            new HttpRequestException("No such host is known.", new SocketException((int)SocketError.HostNotFound)));

        failure.Kind.ShouldBe(ModelCallFailureKind.NameNotResolved);
        failure.Message.ShouldContain("did not resolve");
        // The distinction the message exists to draw: a name that stopped resolving is the
        // network, and telling somebody to restart their model server would waste their time.
        failure.Message.ShouldNotContain("start the server for this role");
    }

    [Fact]
    public void A_connection_dropped_mid_generation_says_the_call_was_accepted_first()
    {
        ModelRoleOptions role = Role(endpoint: "http://localhost:8002/v1");

        ModelCallFailure failure = ModelCallFailure.Describe(
            "worker",
            role,
            new HttpRequestException("The connection was reset.", new SocketException((int)SocketError.ConnectionReset)));

        failure.Kind.ShouldBe(ModelCallFailureKind.ConnectionDropped);
        failure.Message.ShouldContain("accepted and then dropped");
        failure.Message.ShouldContain("out of memory");
    }

    [Fact]
    public void A_role_with_no_settings_still_names_the_role_rather_than_throwing()
    {
        ModelCallFailure failure = ModelCallFailure.Describe("worker", settings: null, new InvalidOperationException("boom"));

        failure.Kind.ShouldBe(ModelCallFailureKind.Unknown);
        failure.Message.ShouldContain("\"worker\"");
        failure.Message.ShouldContain("(no endpoint configured)");
        failure.Message.ShouldContain("boom");
    }

    /// <summary>Drives a real client at a role until it fails, and hands back what came out.</summary>
    private static async Task<Exception> FailedCallAsync(string role, ModelRoleOptions settings)
    {
        ModelsOptions options = new() { DefaultRole = role };
        options.Roles[role] = settings;

        using ChatClientFactory factory = new(Options.Create(options));

        try
        {
            await factory.GetClient(role).GetResponseAsync([new ChatMessage(ChatRole.User, "ping")]);
        }
        catch (Exception ex)
        {
            return ex;
        }

        throw new InvalidOperationException("The call was expected to fail against this server, and did not.");
    }

    private static ModelRoleOptions Role(FakeOpenAiServer server) => Role(server.Endpoint);

    private static ModelRoleOptions Role(string endpoint) => new()
    {
        Endpoint = endpoint,
        ModelAlias = "worker",
        TimeoutSeconds = 10,
    };
}
