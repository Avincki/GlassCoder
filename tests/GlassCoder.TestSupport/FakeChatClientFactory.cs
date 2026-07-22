using GlassCoder.Models;
using GlassCoder.Models.Configuration;
using Microsoft.Extensions.AI;

namespace GlassCoder.TestSupport;

/// <summary>Hands the loop a <see cref="FakeChatClient"/> for every role.</summary>
public sealed class FakeChatClientFactory : IChatClientFactory
{
    private readonly IChatClient _client;
    private readonly ModelRoleOptions _roleOptions;

    /// <summary>Creates the factory over one fake client.</summary>
    public FakeChatClientFactory(IChatClient client, ModelRoleOptions? roleOptions = null)
    {
        _client = client;
        _roleOptions = roleOptions ?? new ModelRoleOptions { Endpoint = "http://localhost/v1", ModelAlias = "worker" };
    }

    /// <inheritdoc />
    public IReadOnlyList<string> Roles => [ModelRoles.Worker];

    /// <inheritdoc />
    public string DefaultRole => ModelRoles.Worker;

    /// <inheritdoc />
    public bool ContainsRole(string role) => true;

    /// <inheritdoc />
    public IChatClient GetClient(string? role = null) => _client;

    /// <inheritdoc />
    public ModelRoleOptions GetRoleOptions(string? role = null) => _roleOptions;
}
