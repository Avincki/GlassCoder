using GlassCoder.Models;
using GlassCoder.Models.Configuration;
using GlassCoder.TestSupport;
using Microsoft.Extensions.Options;

namespace GlassCoder.Models.Tests;

/// <summary>
/// What is behind a role's alias, and why anything asks.
/// <para>
/// An OpenAI-compatible server echoes back the alias it was asked for, so a run served by
/// <c>worker</c> recorded that <c>worker</c> produced it. True, and useless to a retrospective
/// comparing two checkpoints on one alias - which is the comparison the whole model surface
/// exists to support. Only <c>/v1/models</c> knows which weights are loaded.
/// </para>
/// </summary>
public sealed class ServedModelIdentityTests
{
    [Fact]
    public async Task The_checkpoint_behind_the_alias_is_what_comes_back()
    {
        using FakeOpenAiServer server = new() { ServedCheckpoint = "org/Qwen3.8-27B-NVFP4" };

        (await Identity(server).ResolveAsync("worker")).ShouldBe("org/Qwen3.8-27B-NVFP4");
    }

    [Fact]
    public async Task A_server_that_names_the_alias_and_nothing_else_resolves_to_nothing()
    {
        // Started without an alias, so it reports the checkpoint as the alias. Answering "worker"
        // here would put "worker: worker" back in the report this exists to keep it out of.
        using FakeOpenAiServer server = new() { ServedCheckpoint = "worker" };

        (await Identity(server).ResolveAsync("worker")).ShouldBeNull();
    }

    [Fact]
    public async Task A_server_that_volunteers_no_checkpoint_resolves_to_nothing()
    {
        using FakeOpenAiServer server = new() { ServedCheckpoint = null };

        (await Identity(server).ResolveAsync("worker")).ShouldBeNull();
    }

    [Fact]
    public async Task The_answer_is_asked_for_once_and_remembered()
    {
        // A run stamps every step with this. Asking per step would put a model-list call between
        // every thought the agent has.
        using FakeOpenAiServer server = new() { ServedCheckpoint = "org/Qwen3.8-27B-NVFP4" };
        IServedModelIdentity identity = Identity(server);

        await identity.ResolveAsync("worker");
        await identity.ResolveAsync("worker");
        await identity.ResolveAsync("worker");

        server.Paths.Count(path => path.EndsWith("/models", StringComparison.Ordinal)).ShouldBe(1);
    }

    [Fact]
    public async Task Not_knowing_is_remembered_too()
    {
        // Null is a real answer. Re-asking a server that has already said it does not know would
        // be the same call, every step, for the rest of the process.
        using FakeOpenAiServer server = new() { ServedCheckpoint = null };
        IServedModelIdentity identity = Identity(server);

        await identity.ResolveAsync("worker");
        await identity.ResolveAsync("worker");

        server.Paths.Count(path => path.EndsWith("/models", StringComparison.Ordinal)).ShouldBe(1);
    }

    [Fact]
    public async Task A_role_nothing_configures_is_answered_without_asking_anything()
    {
        using FakeOpenAiServer server = new() { ServedCheckpoint = "org/Qwen3.8-27B-NVFP4" };

        (await Identity(server).ResolveAsync("nobody")).ShouldBeNull();
        server.Paths.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_server_that_cannot_be_reached_costs_the_run_nothing_but_the_answer()
    {
        // Not knowing which model answered must never be able to stop a run that is otherwise
        // fine, so the failure is an absent name and not an exception.
        ModelsOptions options = new();
        options.Roles["worker"] = new ModelRoleOptions
        {
            // Reserved for documentation examples, so nothing is listening and nothing can be.
            Endpoint = "http://192.0.2.1:9/v1",
            ModelAlias = "worker",
        };

        IServedModelIdentity identity = new ServedModelIdentity(
            new ServedModelDirectory(), Options.Create(options));

        (await identity.ResolveAsync("worker")).ShouldBeNull();
    }

    private static IServedModelIdentity Identity(FakeOpenAiServer server)
    {
        ModelsOptions options = new();
        options.Roles["worker"] = new ModelRoleOptions
        {
            Endpoint = server.Endpoint,
            ModelAlias = "worker",
        };

        return new ServedModelIdentity(new ServedModelDirectory(), Options.Create(options));
    }
}
