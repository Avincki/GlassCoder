using GlassCoder.Tools.Processes;
using Microsoft.Extensions.Options;

namespace GlassCoder.Tools.Execution;

/// <summary>
/// Runs commands directly on the host through <see cref="IProcessRunner"/>.
/// </summary>
/// <remarks>
/// This is the unsandboxed path and it stays behind an explicit opt-in
/// (<see cref="SandboxOptions.AllowUnsandboxedExecution"/>). It exists because a developer
/// machine without Docker still needs to be able to run the harness deliberately - not because
/// running a model's build script on the host is ever the safe default.
/// </remarks>
public sealed class LocalCommandExecutor : ICommandExecutor
{
    private readonly IProcessRunner _runner;
    private readonly SandboxOptions _options;

    /// <summary>Creates the executor.</summary>
    public LocalCommandExecutor(IProcessRunner runner, IOptions<SandboxOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _runner = runner;
        _options = options.Value;
    }

    /// <inheritdoc />
    public string Sandbox => "host";

    /// <inheritdoc />
    public Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);

    /// <inheritdoc />
    public async Task<CommandResult> ExecuteAsync(CommandRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        ProcessRunResult result = await _runner.RunAsync(
            new ProcessRunRequest(request.FileName, request.Arguments)
            {
                WorkingDirectory = request.WorkingDirectory,
                Timeout = request.Timeout ?? TimeSpan.FromSeconds(_options.CommandTimeoutSeconds),
            },
            cancellationToken).ConfigureAwait(false);

        return new CommandResult(
            result.ExitCode,
            result.StandardOutput,
            result.StandardError,
            result.Duration,
            result.TimedOut,
            Sandbox);
    }
}
