using System.Text.Json;
using GlassCoder.Core.Agent;
using GlassCoder.Core.DependencyInjection;
using GlassCoder.TestSupport;
using GlassCoder.Tools.Guardrails;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace GlassCoder.Core.Tests;

/// <summary>
/// What one step actually puts on the wire, measured at the socket rather than estimated.
/// <para>
/// This exists because the tool schemas are not free and are not visible. They are generated
/// from method signatures, they are re-sent in full on <em>every</em> request, and they do not
/// appear in the context assembler's estimate - a step whose assembled context is 128 tokens
/// still sends over 2,700, and the difference is almost entirely schema. On a local model that
/// is prefill paid once per step, for the whole run.
/// </para>
/// <para>
/// So the budget is asserted rather than assumed. A new tool, or a description that grows into
/// prose, should have to pass a test that says out loud what it costs.
/// </para>
/// </summary>
public sealed class PromptBudgetTests : IDisposable
{
    /// <summary>
    /// Ceiling on the advertised tool schemas, in characters, with every tool enabled.
    /// <para>
    /// Measured at 10,291 when this was written - thirteen tools, of which <c>update_todos</c>
    /// alone is 1,356. The headroom is deliberate but finite: it is room for a tool or two, not
    /// licence for the schemas to keep growing. If this fails, the question to ask is not "what
    /// should the number be" but "is this tool worth 200 tokens on every step of every run".
    /// </para>
    /// <para>
    /// Raised once, to 14,000, at 13,547 measured across seventeen tools. Four arrived together
    /// (tasks 44, 49, 50): <c>list_projects</c> 446, <c>dotnet_project</c> 1,359,
    /// <c>file_operation</c> 820, <c>list_changes</c> 413. That is more than the "tool or two"
    /// the original headroom was for, so the number was not raised until the question above had
    /// been answered honestly: every one of those descriptions was cut first, which took the
    /// total from 14,448 - <c>dotnet_project</c> alone was 1,818 and the single most expensive
    /// tool in the harness, ahead of <c>update_todos</c>.
    /// </para>
    /// <para>
    /// It also decided a design. An outside review proposed nine new tools; they were folded into
    /// four schemas instead, because at roughly 300 tokens each the flat version would have cost
    /// about 2,700 tokens on every request - against a step-0 conversation of roughly 130.
    /// Capability belongs on the tools that already exist. The next tool to be added should trim
    /// something, not raise this again: the schemas are 95.9% of a step-0 request as it stands.
    /// </para>
    /// </summary>
    private const int ToolSchemaCharacterBudget = 14000;

    /// <summary>Roughly the divisor the context assembler uses, so the two speak the same units.</summary>
    private const double CharactersPerToken = 4.0;

    private readonly TempWorkspace _workspace = new();
    private readonly FakeOpenAiServer _server = new();
    private readonly ITestOutputHelper _output;

    public PromptBudgetTests(ITestOutputHelper output) => _output = output;

    public void Dispose()
    {
        _server.Dispose();
        _workspace.Dispose();
    }

    /// <summary>
    /// The schemas stay inside their budget, and the breakdown is printed either way - a number
    /// that only appears on failure is a number nobody looks at until it is already a problem.
    /// </summary>
    [Fact]
    public async Task The_tool_schemas_stay_within_their_budget()
    {
        JsonElement request = await CaptureFirstRequestAsync(git: true);

        JsonElement tools = request.GetProperty("tools");
        int toolChars = tools.GetRawText().Length;
        int totalChars = request.GetRawText().Length;

        _output.WriteLine($"request {totalChars} chars (~{totalChars / CharactersPerToken:F0} tokens)");
        _output.WriteLine(
            $"  tools {toolChars} chars (~{toolChars / CharactersPerToken:F0} tokens), " +
            $"{100.0 * toolChars / totalChars:F1}% of the request");

        foreach (JsonElement tool in tools.EnumerateArray())
        {
            string name = tool.GetProperty("function").GetProperty("name").GetString() ?? "?";
            _output.WriteLine($"    {name,-22} {tool.GetRawText().Length,6} chars");
        }

        toolChars.ShouldBeLessThanOrEqualTo(
            ToolSchemaCharacterBudget,
            "the tool schemas are re-sent on every model call - growing them slows every step of every run");
    }

    /// <summary>
    /// Turning the git tools off is a real prefill saving, and this says how much. Not an
    /// argument for turning them off - it is what makes the trade visible when a run is slow and
    /// the workspace is not a repository anyway.
    /// </summary>
    [Fact]
    public async Task Disabling_the_git_tools_measurably_shortens_every_request()
    {
        JsonElement without = await CaptureFirstRequestAsync(git: false);
        int withoutChars = without.GetProperty("tools").GetRawText().Length;

        // A second server and workspace, because the first pair has already served its one reply.
        using FakeOpenAiServer server = new();
        using TempWorkspace workspace = new();
        JsonElement with = await CaptureFirstRequestAsync(git: true, server, workspace);
        int withChars = with.GetProperty("tools").GetRawText().Length;

        int saved = withChars - withoutChars;
        _output.WriteLine(
            $"git tools cost {saved} chars (~{saved / CharactersPerToken:F0} tokens) on every request");

        saved.ShouldBeGreaterThan(0);
        withoutChars.ShouldBeLessThan(withChars);
    }

    /// <summary>
    /// The configured output ceiling reaches the request. Asserted at the socket because the
    /// option existed and was plumbed long before anything set it - the gap was the config file,
    /// and a setting nobody sets looks identical to one that does not work.
    /// </summary>
    [Fact]
    public async Task The_configured_output_ceiling_reaches_the_wire()
    {
        JsonElement request = await CaptureFirstRequestAsync(git: false, maxOutputTokens: 1024);

        // max_completion_tokens, not max_tokens: the OpenAI client sends the current spelling,
        // and asserting the old one would pass against a field the server ignores.
        request.TryGetProperty("max_completion_tokens", out JsonElement ceiling).ShouldBeTrue();
        ceiling.GetInt32().ShouldBe(1024);
    }

    private async Task<JsonElement> CaptureFirstRequestAsync(
        bool git,
        FakeOpenAiServer? server = null,
        TempWorkspace? workspace = null,
        int? maxOutputTokens = null)
    {
        server ??= _server;
        workspace ??= _workspace;

        workspace.WriteFile("src/Widget.cs", "public class Widget { }");
        server.EnqueueText("done");

        using ServiceProvider provider = BuildProvider(git, server.Endpoint, workspace.Root, maxOutputTokens);
        IAgentLoop loop = provider.GetRequiredService<IAgentLoop>();
        await loop.RunAsync(new AgentRunRequest { TaskId = "budget", Goal = "List the C# files." });

        return JsonDocument.Parse(server.Requests[0]).RootElement.Clone();
    }

    private static ServiceProvider BuildProvider(bool git, string endpoint, string root, int? maxOutputTokens)
    {
        Dictionary<string, string?> settings = new()
        {
            ["GlassCoder:Git:Enabled"] = git ? "true" : "false",
            ["GlassCoder:Models:DefaultRole"] = "worker",
            ["GlassCoder:Models:Roles:worker:Endpoint"] = endpoint,
            ["GlassCoder:Models:Roles:worker:ModelAlias"] = "worker",
            ["GlassCoder:Models:Roles:worker:TimeoutSeconds"] = "15",
            [$"{WorkspaceOptions.SectionName}:RepoRoot"] = root,
            ["GlassCoder:Agent:MaxSteps"] = "2",
            ["GlassCoder:Telemetry:Enabled"] = "false",
            ["GlassCoder:Metrics:Enabled"] = "false",
            ["GlassCoder:Provenance:Enabled"] = "false",
        };

        if (maxOutputTokens is { } ceiling)
        {
            settings["GlassCoder:Models:Roles:worker:MaxOutputTokens"] =
                ceiling.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        IConfigurationRoot configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings)
            .Build();

        ServiceCollection services = new();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddLogging();
        services.AddGlassCoder(configuration);
        return services.BuildServiceProvider();
    }
}
