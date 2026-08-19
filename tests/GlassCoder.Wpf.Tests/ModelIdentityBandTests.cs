using System.Windows.Threading;
using GlassCoder.Core.DependencyInjection;
using GlassCoder.Models;
using GlassCoder.Models.Configuration;
using GlassCoder.TestSupport;
using GlassCoder.Wpf.DependencyInjection;
using GlassCoder.Wpf.ViewModels;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace GlassCoder.Wpf.Tests;

/// <summary>
/// The header band that names what a run will actually talk to (workplan task 77).
/// <para>
/// The rows are asserted as the strings a person reads, not as the fields behind them, because
/// the whole feature is a sentence on a window. A row that holds the right checkpoint and renders
/// as "worker · " has failed at the only thing it does.
/// </para>
/// </summary>
public sealed class ModelIdentityBandTests
{
    /// <summary>
    /// The roster is the roles the run will address - the agent's and, with critique on, the
    /// critic's - and each is named by what its own server said, not by what the other's did.
    /// </summary>
    [Fact]
    public void The_band_names_every_role_the_run_will_address()
    {
        using FakeOpenAiServer worker = new()
        {
            ServedCheckpoint = "RedHatAI/Qwen3-Coder-Next-NVFP4",
            ServedContextTokens = 262_144,
        };
        using FakeOpenAiServer critic = new() { ServedCheckpoint = "microsoft/phi-4", ServedContextTokens = 16_384 };
        critic.ServedModels.Clear();
        critic.ServedModels.Add("critic");

        List<string> rows = Describe(worker, critic, critiqueEnabled: true);

        rows.Count.ShouldBe(2);
        rows[0].ShouldBe("worker · RedHatAI/Qwen3-Coder-Next-NVFP4 · 262,144-token context");
        rows[1].ShouldBe("critic · microsoft/phi-4 · 16,384-token context");
    }

    /// <summary>
    /// Critique off means the critic never runs, so naming it in a band about this run would be
    /// describing something that will not happen.
    /// </summary>
    [Fact]
    public void A_critic_that_will_not_run_is_not_in_the_band()
    {
        using FakeOpenAiServer worker = new() { ServedCheckpoint = "RedHatAI/Qwen3-Coder-Next-NVFP4" };
        using FakeOpenAiServer critic = new();

        List<string> rows = Describe(worker, critic, critiqueEnabled: false);

        rows.Count.ShouldBe(1);
        rows[0].ShouldBe("worker · RedHatAI/Qwen3-Coder-Next-NVFP4");
    }

    /// <summary>
    /// The state this has to survive: the window opens before the model server does. It says so
    /// and names the endpoint, because that is the one thing needed to fix it.
    /// </summary>
    [Fact]
    public void A_server_that_is_not_running_says_so_and_says_where()
    {
        string endpoint;
        using (FakeOpenAiServer closed = new())
        {
            endpoint = closed.Endpoint;
        }

        ModelIdentityViewModel row = new("worker", "worker");
        ModelRoleOptions settings = new() { Endpoint = endpoint, ModelAlias = "worker" };

        row.Describe(settings, new ServedModelList(
            ServedModelListOutcome.Unreachable, [], new Uri(endpoint + "/models"), Error: "closed"));

        row.Outcome.ShouldBe(ConnectionCheckOutcome.Warning);
        row.Description.ShouldBe($"not available at {endpoint}");
    }

    /// <summary>
    /// Reachable and mute is not the same as down. Both leave the checkpoint unknown, and they
    /// have different fixes, so they get different sentences.
    /// </summary>
    [Fact]
    public void A_server_that_reports_no_checkpoint_is_not_reported_as_unavailable()
    {
        ModelIdentityViewModel row = new("worker", "worker");

        row.Describe(
            new ModelRoleOptions { Endpoint = "http://localhost:8002/v1", ModelAlias = "worker" },
            new ServedModelList(
                ServedModelListOutcome.Listed,
                [new ServedModel("worker", MaxContextTokens: 32_768)],
                new Uri("http://localhost:8002/v1/models")));

        row.Outcome.ShouldBe(ConnectionCheckOutcome.Ok);
        row.Description.ShouldBe("served, checkpoint not reported · 32,768-token context");
    }

    /// <summary>An alias nothing serves names what is served instead, so the fix is on screen.</summary>
    [Fact]
    public void An_alias_the_server_does_not_serve_names_what_it_does()
    {
        ModelIdentityViewModel row = new("worker", "worker");

        row.Describe(
            new ModelRoleOptions { Endpoint = "http://localhost:8002/v1", ModelAlias = "worker" },
            new ServedModelList(
                ServedModelListOutcome.Listed,
                [new ServedModel("qwen3-coder-30b")],
                new Uri("http://localhost:8002/v1/models")));

        row.Outcome.ShouldBe(ConnectionCheckOutcome.Warning);
        row.Description.ShouldBe("'worker' is not served; this endpoint serves qwen3-coder-30b");
    }

    /// <summary>
    /// A hosted role with no key is said plainly rather than called over the wire, where it would
    /// come back looking like a server that is down.
    /// </summary>
    [Fact]
    public void A_role_with_no_key_is_never_asked()
    {
        ModelIdentityViewModel row = new("remote-critic", "claude-opus-5");

        row.Unusable();

        row.Outcome.ShouldBe(ConnectionCheckOutcome.Warning);
        row.Description.ShouldBe("no API key configured, so it was not asked");
    }

    /// <summary>
    /// Builds the shell over two fake servers, fills the band, and returns each row as it reads.
    /// </summary>
    private static List<string> Describe(
        FakeOpenAiServer worker,
        FakeOpenAiServer critic,
        bool critiqueEnabled)
    {
        using TempWorkspace workspace = new();

        return UiThread.Run(dispatcher =>
        {
            using ServiceProvider provider = Build(dispatcher, workspace.Root, worker, critic, critiqueEnabled);
            MainWindowViewModel shell = provider.GetRequiredService<MainWindowViewModel>();

            Task filling = shell.DescribeModelsAsync();

            // The dispatcher has a queue but no loop, so anything the lookups post to it sits
            // there until something pumps - see UiThread.Pump.
            UiThread.Pump(dispatcher, () => filling.IsCompleted, TimeSpan.FromSeconds(20)).ShouldBeTrue();
            filling.GetAwaiter().GetResult();

            shell.ModelsCheckedAt.ShouldStartWith("checked ");

            return shell.Models.Select(row => $"{row.Role} · {row.Description}").ToList();
        });
    }

    private static ServiceProvider Build(
        Dispatcher dispatcher,
        string repoRoot,
        FakeOpenAiServer worker,
        FakeOpenAiServer critic,
        bool critiqueEnabled)
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["GlassCoder:Workspace:RepoRoot"] = repoRoot,
                ["GlassCoder:Models:Roles:worker:Endpoint"] = worker.Endpoint,
                ["GlassCoder:Models:Roles:worker:ModelAlias"] = "worker",
                ["GlassCoder:Models:Roles:critic:Endpoint"] = critic.Endpoint,
                ["GlassCoder:Models:Roles:critic:ModelAlias"] = "critic",
                ["GlassCoder:Critique:Enabled"] = critiqueEnabled ? "true" : "false",
                ["GlassCoder:Critique:Role"] = "critic",
                ["GlassCoder:Git:Enabled"] = "false",
                ["GlassCoder:Telemetry:Enabled"] = "false",
                ["GlassCoder:Metrics:Directory"] = System.IO.Path.Combine(repoRoot, "metrics"),
            })
            .Build();

        ServiceCollection services = new();
        services.AddSingleton(configuration);
        services.AddLogging();
        services.AddGlassCoder(configuration);
        services.AddGlassCoderDesktop(dispatcher);

        return services.BuildServiceProvider();
    }
}
