using System.Text.Json;
using GlassCoder.Core.Agent;
using GlassCoder.Core.DependencyInjection;
using GlassCoder.Core.Diagnostics;
using GlassCoder.TestSupport;
using GlassCoder.Tools.Guardrails;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace GlassCoder.Core.Tests;

/// <summary>
/// Full-loop integration across the real HTTP seam (workplan task 32).
/// <para>
/// Everything below the loop is real here: the OpenAI client, the constrained-decoding stage,
/// JSON serialisation, tool-call parsing, the registry, the tools and the guardrail. Only the
/// model is fake - and it is fake at the socket, not at <c>IChatClient</c>, which is the whole
/// point. A test that stubs the interface cannot catch a serialisation change.
/// </para>
/// <para>
/// Set <c>GLASSCODER_SMOKE_ENDPOINT</c> to point these same tests at a real served model.
/// </para>
/// </summary>
public sealed class SeamIntegrationTests : IDisposable
{
    private readonly TempWorkspace _workspace = new();
    private readonly FakeOpenAiServer _server = new();

    public void Dispose()
    {
        _server.Dispose();
        _workspace.Dispose();
    }

    [Fact]
    public async Task The_loop_drives_a_real_http_endpoint_end_to_end()
    {
        _workspace.WriteFile("src/Widget.cs", "public class Widget { }");
        _server.EnqueueToolCall("glob", """{"pattern":"**/*.cs"}""");
        _server.EnqueueText("Found src/Widget.cs.");

        using ServiceProvider provider = BuildProvider();
        IAgentLoop loop = provider.GetRequiredService<IAgentLoop>();

        AgentRunResult result = await loop.RunAsync(
            new AgentRunRequest { TaskId = "seam", Goal = "List the C# files." });

        result.StopReason.ShouldBe(AgentStopReason.Completed);
        result.FinalText.ShouldBe("Found src/Widget.cs.");
        result.ToolCallsTotal.ShouldBe(1);
        result.ToolCallsValid.ShouldBe(1);

        // Usage came back over the wire, not from a stub.
        result.TotalTokens.ShouldBe(50);
    }

    [Fact]
    public async Task Constrained_decoding_settings_reach_the_wire()
    {
        _server.EnqueueText("done");

        using ServiceProvider provider = BuildProvider();
        await provider.GetRequiredService<IAgentLoop>()
            .RunAsync(new AgentRunRequest { TaskId = "seam", Goal = "Say done." });

        JsonElement request = _server.Request(0);
        request.GetProperty("model").GetString().ShouldBe("worker");

        // Tools are advertised with their generated schemas.
        JsonElement tools = request.GetProperty("tools");
        tools.GetArrayLength().ShouldBeGreaterThan(0);

        JsonElement first = tools[0].GetProperty("function");
        first.GetProperty("name").GetString().ShouldNotBeNullOrWhiteSpace();
        first.GetProperty("parameters").GetProperty("type").GetString().ShouldBe("object");
    }

    [Fact]
    public async Task Tool_calls_parse_and_their_observations_go_back_over_the_wire()
    {
        _workspace.WriteFile("src/Widget.cs", "public class Widget { public int Size => 42; }");
        _server.EnqueueToolCall("read_file", """{"path":"src/Widget.cs"}""");
        _server.EnqueueText("It has a Size of 42.");

        using ServiceProvider provider = BuildProvider();
        await provider.GetRequiredService<IAgentLoop>()
            .RunAsync(new AgentRunRequest { TaskId = "seam", Goal = "What is Widget's size?" });

        // The second request must carry the tool result the harness produced.
        JsonElement second = _server.Request(1);
        string body = second.GetProperty("messages").ToString();
        body.ShouldContain("\"tool\"");
        body.ShouldContain("Size => 42");
    }

    [Fact]
    public async Task A_run_over_the_seam_is_reconstructable_from_its_transcript()
    {
        _workspace.WriteFile("src/Widget.cs", "public class Widget { }");
        _server.EnqueueToolCall("glob", """{"pattern":"**/*.cs"}""");
        _server.EnqueueText("Done.");

        using ServiceProvider provider = BuildProvider();
        AgentRunResult result = await provider.GetRequiredService<IAgentLoop>()
            .RunAsync(new AgentRunRequest { TaskId = "seam", Goal = "List files." });

        ITranscriptBus bus = provider.GetRequiredService<ITranscriptBus>();
        bus.Steps.Count.ShouldBe(result.Steps);
        bus.Steps[0].ToolCalls.ShouldHaveSingleItem().Name.ShouldBe("glob");
        bus.Steps[0].TotalTokens.ShouldBe(32);
    }

    [Fact]
    public async Task An_unreachable_endpoint_stops_the_run_without_throwing()
    {
        using ServiceProvider provider = BuildProvider(endpoint: "http://127.0.0.1:1/v1");

        AgentRunResult result = await provider.GetRequiredService<IAgentLoop>()
            .RunAsync(new AgentRunRequest { TaskId = "seam", Goal = "Say hello." });

        result.StopReason.ShouldBe(AgentStopReason.ModelError);
        result.Error.ShouldNotBeNullOrWhiteSpace();
    }

    private ServiceProvider BuildProvider(string? endpoint = null)
    {
        // A real endpoint if one is configured, otherwise the socket-level fake.
        string resolved = endpoint
            ?? Environment.GetEnvironmentVariable("GLASSCODER_SMOKE_ENDPOINT")
            ?? _server.Endpoint;

        IConfigurationRoot configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["GlassCoder:Models:DefaultRole"] = "worker",
                ["GlassCoder:Models:Roles:worker:Endpoint"] = resolved,
                ["GlassCoder:Models:Roles:worker:ModelAlias"] = "worker",
                ["GlassCoder:Models:Roles:worker:TimeoutSeconds"] = "15",
                [$"{WorkspaceOptions.SectionName}:RepoRoot"] = _workspace.Root,
                ["GlassCoder:Agent:MaxSteps"] = "6",
                ["GlassCoder:Telemetry:Enabled"] = "false",
                ["GlassCoder:Metrics:Enabled"] = "false",
                ["GlassCoder:Provenance:Enabled"] = "false",
            })
            .Build();

        ServiceCollection services = new();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddLogging();
        services.AddGlassCoder(configuration);
        return services.BuildServiceProvider();
    }
}
