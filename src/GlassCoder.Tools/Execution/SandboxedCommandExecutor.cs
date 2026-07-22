using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GlassCoder.Tools.Execution;

/// <summary>
/// The executor the tools actually depend on: it applies the sandbox policy and then delegates
/// (workplan task 17).
/// <para>
/// When the configured sandbox is unavailable this <em>refuses</em> rather than quietly falling
/// back to the host. A silent downgrade from "containerised, no network" to "your machine, full
/// access" is the kind of default that is only noticed afterwards.
/// </para>
/// </summary>
public sealed class SandboxedCommandExecutor : ICommandExecutor
{
    private readonly DockerCommandExecutor _docker;
    private readonly LocalCommandExecutor _local;
    private readonly SandboxOptions _options;
    private readonly ILogger<SandboxedCommandExecutor> _logger;

    /// <summary>Creates the executor.</summary>
    public SandboxedCommandExecutor(
        DockerCommandExecutor docker,
        LocalCommandExecutor local,
        IOptions<SandboxOptions> options,
        ILogger<SandboxedCommandExecutor>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        _docker = docker;
        _local = local;
        _options = options.Value;
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
                ? await _local.ExecuteAsync(request, cancellationToken).ConfigureAwait(false)
                : Refuse("Sandbox mode is Local but GlassCoder:Sandbox:AllowUnsandboxedExecution is false.");
        }

        if (await _docker.IsAvailableAsync(cancellationToken).ConfigureAwait(false))
        {
            return await _docker.ExecuteAsync(request, cancellationToken).ConfigureAwait(false);
        }

        if (!_options.AllowUnsandboxedExecution)
        {
            return Refuse(
                "Docker is not reachable, and running repository code on the host is not permitted. " +
                "Start Docker, or set GlassCoder:Sandbox:AllowUnsandboxedExecution to true to accept the risk.");
        }

        _logger.LogWarning(
            "Docker is unavailable; running on the host because AllowUnsandboxedExecution is set. " +
            "This executes repository code with the harness's own privileges.");

        return await _local.ExecuteAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private CommandResult Refuse(string reason)
    {
        _logger.LogError("Refusing to execute: {Reason}", reason);
        return CommandResult.Unavailable(reason, Sandbox);
    }
}
