using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GlassCoder.Tools.Execution;

/// <summary>
/// The executor the tools actually depend on: it applies the sandbox policy and then delegates
/// (workplan task 17).
/// <para>
/// When the configured sandbox is unavailable this <em>refuses</em>, unless
/// <see cref="SandboxOptions.AllowUnsandboxedExecution"/> has been set. A downgrade from
/// "containerised, no network" to "your machine, full access" is the kind of thing that is only
/// noticed afterwards, so it has to be asked for - and the fallback is logged as a warning the
/// first time it happens, then at debug. Once per session keeps the fact on the record; thirty
/// repeats of it per run buried the log lines that carried information.
/// </para>
/// </summary>
public sealed class SandboxedCommandExecutor : ICommandExecutor
{
    private readonly DockerCommandExecutor _docker;
    private readonly LocalCommandExecutor _local;
    private readonly SandboxOptions _options;
    private readonly DropboxIgnoreMarker? _ignoreMarker;
    private readonly ILogger<SandboxedCommandExecutor> _logger;
    private int _fallbacks;

    /// <summary>Creates the executor.</summary>
    public SandboxedCommandExecutor(
        DockerCommandExecutor docker,
        LocalCommandExecutor local,
        IOptions<SandboxOptions> options,
        DropboxIgnoreMarker? ignoreMarker = null,
        ILogger<SandboxedCommandExecutor>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        _docker = docker;
        _local = local;
        _options = options.Value;
        _ignoreMarker = ignoreMarker;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<SandboxedCommandExecutor>.Instance;
    }

    /// <inheritdoc />
    public string Sandbox => _options.Mode == SandboxMode.Docker ? _docker.Sandbox : _local.Sandbox;

    /// <inheritdoc />
    public async Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default)
    {
        if (_options.Mode == SandboxMode.Local)
        {
            return _options.AllowUnsandboxedExecution;
        }

        return await _docker.IsAvailableAsync(cancellationToken).ConfigureAwait(false) ||
               _options.AllowUnsandboxedExecution;
    }

    /// <inheritdoc />
    public async Task<CommandResult> ExecuteAsync(CommandRequest request, CancellationToken cancellationToken = default)
    {
        if (_options.Mode == SandboxMode.Local)
        {
            return _options.AllowUnsandboxedExecution
                ? await RunSweptAsync(() => _local.ExecuteAsync(request, cancellationToken)).ConfigureAwait(false)
                : Refuse("Sandbox mode is Local but GlassCoder:Sandbox:AllowUnsandboxedExecution is false.");
        }

        if (await _docker.IsAvailableAsync(cancellationToken).ConfigureAwait(false))
        {
            return await RunSweptAsync(() => _docker.ExecuteAsync(request, cancellationToken)).ConfigureAwait(false);
        }

        if (!_options.AllowUnsandboxedExecution)
        {
            return Refuse(
                "Docker is not reachable, and running repository code on the host is not permitted. " +
                "Start Docker, or set GlassCoder:Sandbox:AllowUnsandboxedExecution to true to accept the risk.");
        }

        int fallbacks = Interlocked.Increment(ref _fallbacks);
        if (fallbacks == 1)
        {
            _logger.LogWarning(
                "Docker is unavailable; running on the host because AllowUnsandboxedExecution is set. " +
                "This executes repository code with the harness's own privileges. Further fallbacks " +
                "this session are logged at debug.");
        }
        else
        {
            _logger.LogDebug("Docker is unavailable; running on the host (fallback #{Count} this session)", fallbacks);
        }

        return await RunSweptAsync(() => _local.ExecuteAsync(request, cancellationToken)).ConfigureAwait(false);
    }

    /// <summary>
    /// Runs a command between two Dropbox ignore sweeps: before, so output that already exists
    /// is marked ahead of the SDK touching it, and after, so folders the command itself created
    /// are marked before the sync client picks them up. The Docker branch sweeps too - the
    /// container writes through a bind mount, and the folders it leaves behind are host folders.
    /// </summary>
    private async Task<CommandResult> RunSweptAsync(Func<Task<CommandResult>> execute)
    {
        _ignoreMarker?.EnsureWorkspaceMarked();
        CommandResult result = await execute().ConfigureAwait(false);
        _ignoreMarker?.EnsureWorkspaceMarked();
        return result;
    }

    private CommandResult Refuse(string reason)
    {
        _logger.LogError("Refusing to execute: {Reason}", reason);
        return CommandResult.Unavailable(reason, Sandbox);
    }
}
