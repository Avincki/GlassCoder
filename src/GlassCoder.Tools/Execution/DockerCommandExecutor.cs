using System.Diagnostics;
using Docker.DotNet;
using Docker.DotNet.Models;
using GlassCoder.Tools.Guardrails;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GlassCoder.Tools.Execution;

/// <summary>
/// Runs commands inside a container with the repository mounted and, by default, no network
/// (CLAUDE.md §8.4, workplan task 17).
/// </summary>
public sealed class DockerCommandExecutor : ICommandExecutor, IDisposable
{
    private readonly SandboxOptions _options;
    private readonly IPathGuard _guard;
    private readonly ILogger<DockerCommandExecutor> _logger;
    private readonly Lazy<IDockerClient> _client;
    private bool _disposed;

    /// <summary>Creates the executor.</summary>
    public DockerCommandExecutor(
        IOptions<SandboxOptions> options,
        IPathGuard guard,
        ILogger<DockerCommandExecutor>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        _options = options.Value;
        _guard = guard;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<DockerCommandExecutor>.Instance;
        _client = new Lazy<IDockerClient>(CreateClient, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <inheritdoc />
    public string Sandbox => "docker";

    /// <inheritdoc />
    public async Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await _client.Value.System.PingAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is DockerApiException or HttpRequestException or IOException or TimeoutException)
        {
            _logger.LogDebug(ex, "Docker is not reachable");
            return false;
        }
    }

    /// <inheritdoc />
    public async Task<CommandResult> ExecuteAsync(CommandRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ObjectDisposedException.ThrowIf(_disposed, this);

        long start = Stopwatch.GetTimestamp();
        CreateContainerParameters parameters;
        try
        {
            parameters = DockerRunSpec.Create(request, _options, _guard.RepoRoot);
        }
        catch (ArgumentException ex)
        {
            return CommandResult.Unavailable(ex.Message, Sandbox);
        }

        _logger.LogInformation(
            "Running {FileName} {Arguments} in {Image} (network {Network})",
            request.FileName,
            string.Join(' ', request.Arguments),
            parameters.Image,
            parameters.HostConfig.NetworkMode);

        string? containerId = null;
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(request.Timeout ?? TimeSpan.FromSeconds(_options.CommandTimeoutSeconds));

        try
        {
            CreateContainerResponse created = await _client.Value.Containers
                .CreateContainerAsync(parameters, timeout.Token).ConfigureAwait(false);
            containerId = created.ID;

            await _client.Value.Containers
                .StartContainerAsync(containerId, new ContainerStartParameters(), timeout.Token).ConfigureAwait(false);

            ContainerWaitResponse wait = await _client.Value.Containers
                .WaitContainerAsync(containerId, timeout.Token).ConfigureAwait(false);

            using MultiplexedStream logs = await _client.Value.Containers.GetContainerLogsAsync(
                containerId,
                tty: false,
                new ContainerLogsParameters { ShowStdout = true, ShowStderr = true, Follow = false },
                timeout.Token).ConfigureAwait(false);

            (string stdout, string stderr) = await logs.ReadOutputToEndAsync(timeout.Token).ConfigureAwait(false);

            return new CommandResult(
                (int)wait.StatusCode,
                stdout,
                stderr,
                Stopwatch.GetElapsedTime(start),
                TimedOut: false,
                Sandbox);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new CommandResult(-1, string.Empty, string.Empty, Stopwatch.GetElapsedTime(start), true, Sandbox);
        }
        catch (Exception ex) when (ex is DockerApiException or HttpRequestException or IOException)
        {
            _logger.LogWarning(ex, "Containerised command failed to run");
            return CommandResult.Unavailable(ex.Message, Sandbox);
        }
        finally
        {
            if (containerId is not null)
            {
                await RemoveAsync(containerId).ConfigureAwait(false);
            }
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_client.IsValueCreated)
        {
            _client.Value.Dispose();
        }
    }

    private DockerClient CreateClient() =>
        (string.IsNullOrWhiteSpace(_options.DockerEndpoint)
            ? new DockerClientConfiguration()
            : new DockerClientConfiguration(new Uri(_options.DockerEndpoint)))
        .CreateClient();

    private async Task RemoveAsync(string containerId)
    {
        try
        {
            // Not cancellable on purpose: a container left running after a cancelled command
            // is a leak, and the token that cancelled the command is already cancelled.
            await _client.Value.Containers.RemoveContainerAsync(
                containerId,
                new ContainerRemoveParameters { Force = true },
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is DockerApiException or HttpRequestException or IOException)
        {
            _logger.LogWarning(ex, "Could not remove container {ContainerId}", containerId);
        }
    }
}
