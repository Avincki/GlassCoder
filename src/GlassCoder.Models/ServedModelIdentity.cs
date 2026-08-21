using GlassCoder.Models.Configuration;
using Microsoft.Extensions.Options;

namespace GlassCoder.Models;

/// <summary>
/// What is actually behind a role's alias, remembered for as long as the process runs.
/// <para>
/// The alias is the only thing the harness addresses, and that stays true (CLAUDE.md §19). But it
/// is not enough to <em>report</em> with: an OpenAI-compatible server echoes the alias back as the
/// model id, so a run served by <c>worker</c> recorded that it was produced by <c>worker</c>, and
/// a retrospective comparing two checkpoints could not tell them apart. Only <c>/v1/models</c>
/// knows which weights are loaded, and nothing in a run was asking it.
/// </para>
/// </summary>
public interface IServedModelIdentity
{
    /// <summary>
    /// The checkpoint behind a role's alias, or null when the server did not say - which includes
    /// every case where it could not be asked. Never throws: not knowing what answered must not
    /// be able to stop a run that is otherwise fine.
    /// </summary>
    /// <param name="role">The role to resolve. Unknown roles resolve to null.</param>
    /// <param name="cancellationToken">Cancels the lookup.</param>
    Task<string?> ResolveAsync(string role, CancellationToken cancellationToken = default);
}

/// <summary>
/// The default <see cref="IServedModelIdentity"/>: one model-list call per role, then memory.
/// <para>
/// Cached for the life of the process rather than re-asked per run, because this is a fact about
/// the server rather than about the run, and a run must not pay for it more than once. The cost of
/// that choice is a server restarted onto different weights mid-session, which keeps reporting the
/// checkpoint it had when first asked - restart the harness after repointing an endpoint, which is
/// what the settings dialog already tells you to do for the endpoint itself.
/// </para>
/// </summary>
public sealed class ServedModelIdentity : IServedModelIdentity
{
    /// <summary>
    /// Ceiling on one lookup. Short on purpose: this runs on the way into a run, and a slow or
    /// unreachable model list must cost the run a couple of seconds and not its own timeout, which
    /// is measured in minutes.
    /// </summary>
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(5);

    private readonly IServedModelDirectory _directory;
    private readonly ModelsOptions _options;

    // Null is a real answer and is cached like any other: a server that does not report a
    // checkpoint must be asked once, not once per step.
    private readonly Dictionary<string, string?> _resolved = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Creates the resolver over the container's directory and configured roles.</summary>
    /// <param name="directory">The model list to ask.</param>
    /// <param name="options">The configured roles, for each one's endpoint and alias.</param>
    public ServedModelIdentity(IServedModelDirectory directory, IOptions<ModelsOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);

        _directory = directory;
        _options = options.Value;
    }

    /// <inheritdoc />
    public async Task<string?> ResolveAsync(string role, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(role) || !_options.Roles.TryGetValue(role, out ModelRoleOptions? settings))
        {
            return null;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_resolved.TryGetValue(role, out string? remembered))
            {
                return remembered;
            }

            ServedModelList list = await _directory
                .ListAsync(settings, Budget, cancellationToken)
                .ConfigureAwait(false);

            // Identity, not Checkpoint: a server started without an alias reports the checkpoint
            // as the alias, and saying "worker is served by worker" is the sentence this class
            // exists to stop being written.
            string? identity = list.Find(settings.ModelAlias)?.Identity;

            _resolved[role] = identity;
            return identity;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The lookup's own budget ran out. Not cached: the next run may find the server awake.
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }
}
