using GlassCoder.Models.Configuration;
using GlassCoder.TestSupport;

namespace GlassCoder.Models.Tests;

/// <summary>
/// The one <c>GET</c> behind the shell's header band (workplan task 77).
/// <para>
/// Against a real socket, like the probe's tests and for the same reason: what is being asserted
/// is how a server's answer is read, and a faked reader would only assert that the fake agrees
/// with itself. The four outcomes are tested separately because the whole point of carrying an
/// outcome rather than an empty list is that a caller can tell them apart.
/// </para>
/// </summary>
public sealed class ServedModelDirectoryTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task A_server_that_volunteers_a_checkpoint_has_it_read_back()
    {
        using FakeOpenAiServer server = new()
        {
            ServedCheckpoint = "RedHatAI/Qwen3-Coder-Next-NVFP4",
            ServedContextTokens = 262_144,
        };
        using ServedModelDirectory directory = new();

        ServedModelList list = await directory.ListAsync(Role(server), Timeout);

        list.Outcome.ShouldBe(ServedModelListOutcome.Listed);
        ServedModel served = list.Find("worker").ShouldNotBeNull();
        served.Checkpoint.ShouldBe("RedHatAI/Qwen3-Coder-Next-NVFP4");
        served.MaxContextTokens.ShouldBe(262_144);
        served.Identity.ShouldBe("RedHatAI/Qwen3-Coder-Next-NVFP4");
    }

    /// <summary>
    /// <c>root</c> is not part of the OpenAI shape - vLLM and SGLang volunteer it and llama.cpp's
    /// server does not - so a list without it has to parse rather than fail. The alias is still
    /// served; the harness simply cannot say by what.
    /// </summary>
    [Fact]
    public async Task A_server_that_reports_no_checkpoint_still_lists_its_aliases()
    {
        using FakeOpenAiServer server = new();
        using ServedModelDirectory directory = new();

        ServedModelList list = await directory.ListAsync(Role(server), Timeout);

        list.Outcome.ShouldBe(ServedModelListOutcome.Listed);
        ServedModel served = list.Find("worker").ShouldNotBeNull();
        served.Checkpoint.ShouldBeNull();
        served.Identity.ShouldBeNull();
    }

    /// <summary>
    /// A server started without <c>--served-model-name</c> answers with the checkpoint as its own
    /// alias. Reporting "worker is served by worker" would be a line that says nothing twice.
    /// </summary>
    [Fact]
    public void An_alias_that_is_its_own_checkpoint_has_no_separate_identity()
    {
        ServedModel served = new("Qwen/Qwen2.5-7B-Instruct", Checkpoint: "Qwen/Qwen2.5-7B-Instruct");

        served.Identity.ShouldBeNull();
    }

    /// <summary>The Anthropic list carries a readable name instead of a checkpoint path.</summary>
    [Fact]
    public void A_display_name_is_preferred_to_a_checkpoint()
    {
        ServedModel served = new("claude-opus-5", DisplayName: "Claude Opus 5");

        served.Identity.ShouldBe("Claude Opus 5");
    }

    [Fact]
    public async Task A_rejected_key_is_not_the_same_as_an_absent_list()
    {
        using FakeOpenAiServer server = new() { ModelsStatusCode = 401 };
        using ServedModelDirectory directory = new();

        ServedModelList list = await directory.ListAsync(Role(server), Timeout);

        list.Outcome.ShouldBe(ServedModelListOutcome.Unauthorized);
        list.StatusCode.ShouldBe(401);
    }

    [Fact]
    public async Task A_server_with_no_model_list_is_refused_rather_than_unreachable()
    {
        using FakeOpenAiServer server = new() { ModelsStatusCode = 404 };
        using ServedModelDirectory directory = new();

        ServedModelList list = await directory.ListAsync(Role(server), Timeout);

        list.Outcome.ShouldBe(ServedModelListOutcome.Refused);
        list.Models.ShouldBeEmpty();
    }

    /// <summary>
    /// The ordinary state at startup, and the one that must not throw: the window opens before the
    /// model server does at least as often as the other way round.
    /// </summary>
    [Fact]
    public async Task A_closed_port_is_reported_rather_than_thrown()
    {
        int port;
        using (FakeOpenAiServer server = new())
        {
            // Take a port the operating system just handed out, then give it straight back.
            port = server.Port;
        }

        using ServedModelDirectory directory = new();

        ServedModelList list = await directory.ListAsync(
            new ModelRoleOptions { Endpoint = $"http://127.0.0.1:{port}/v1", ModelAlias = "worker" },
            TimeSpan.FromSeconds(5));

        list.Outcome.ShouldBe(ServedModelListOutcome.Unreachable);
        list.Url.ToString().ShouldContain($"{port}/v1/models");
        list.Error.ShouldNotBeNullOrWhiteSpace();
    }

    /// <summary>
    /// The check the settings dialog already ran gains the checkpoint without gaining a step: one
    /// parser feeds both, so a server that names its weights names them in both places.
    /// </summary>
    [Fact]
    public async Task The_connection_check_reports_the_checkpoint_it_was_told()
    {
        using FakeOpenAiServer server = new()
        {
            ServedCheckpoint = "RedHatAI/Qwen3-Coder-Next-NVFP4",
            ServedContextTokens = 262_144,
        };
        server.EnqueueText("pong");
        using ModelConnectionProbe probe = new();

        ConnectionCheckResult result = await probe.CheckAsync("worker", Role(server));

        result.Outcome.ShouldBe(ConnectionCheckOutcome.Ok);
        ConnectionCheckStep alias = result.Steps.First(step => step.Name == "Alias");
        alias.Detail.ShouldContain("RedHatAI/Qwen3-Coder-Next-NVFP4");
        alias.Detail.ShouldContain("262,144");
    }

    private static ModelRoleOptions Role(FakeOpenAiServer server) => new()
    {
        Endpoint = server.Endpoint,
        ModelAlias = "worker",
        TimeoutSeconds = 10,
    };
}
