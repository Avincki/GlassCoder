using GlassCoder.Models.Configuration;
using Microsoft.Extensions.AI;

namespace GlassCoder.Models;

/// <summary>
/// Hands out a fully built <see cref="IChatClient"/> per served role (CLAUDE.md §6).
/// One harness addresses several concurrently-served roles through this one seam.
/// </summary>
public interface IChatClientFactory
{
    /// <summary>Configured role names, in configuration order.</summary>
    IReadOnlyList<string> Roles { get; }

    /// <summary>The role used when a caller does not name one.</summary>
    string DefaultRole { get; }

    /// <summary>Whether the named role is configured.</summary>
    bool ContainsRole(string role);

    /// <summary>
    /// Gets the client for a role. Clients are cached and safe for concurrent use; the caller
    /// must not dispose them.
    /// </summary>
    /// <exception cref="ArgumentException">The role is not configured.</exception>
    IChatClient GetClient(string? role = null);

    /// <summary>Settings behind a role, for cost accounting and transcript metadata.</summary>
    /// <exception cref="ArgumentException">The role is not configured.</exception>
    ModelRoleOptions GetRoleOptions(string? role = null);
}
