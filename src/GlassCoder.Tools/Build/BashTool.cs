using System.ComponentModel;
using GlassCoder.Tools.Execution;
using GlassCoder.Tools.Guardrails;
using GlassCoder.Tools.Registry;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GlassCoder.Tools.Build;

/// <summary>Result payload of <c>bash</c>.</summary>
/// <param name="Command">The command that was run.</param>
/// <param name="ExitCode">Its exit code.</param>
/// <param name="Output">Combined stdout and stderr, tail-trimmed.</param>
/// <param name="DurationMs">Wall-clock.</param>
/// <param name="Sandbox">Where it ran.</param>
public sealed record BashResult(
    [property: Description("The command that was run.")] string Command,
    [property: Description("Exit code from the command.")] int ExitCode,
    [property: Description("Combined stdout and stderr.")] string Output,
    [property: Description("Wall-clock milliseconds the command took.")] double DurationMs,
    [property: Description("Where the command ran: docker or host.")] string Sandbox);

/// <summary>
/// <c>bash</c> - added last, and only behind the sandbox (CLAUDE.md §7, §8.4; workplan task 34).
/// <para>
/// This tool is exactly as privileged as <c>build</c>, which is to say completely. It is
/// deliberately the last capability the harness gains, it runs through the same container seam
/// with the same network-dropped default, and it refuses outright when that seam is
/// unavailable - there is no "just this once" path onto the host.
/// </para>
/// <para>
/// The path allow-list still applies to the working directory. It cannot constrain what the
/// command does once running, which is precisely why the container is not optional.
/// </para>
/// </summary>
public sealed class BashTool : IToolSet
{
    private const string ToolName = "bash";
    private const int MaxOutputCharacters = 8000;

    private readonly ICommandExecutor _executor;
    private readonly IPathGuard _guard;
    private readonly SandboxOptions _sandbox;
    private readonly ILogger<BashTool> _logger;

    /// <summary>Creates the tool.</summary>
    public BashTool(
        ICommandExecutor executor,
        IPathGuard guard,
        IOptions<SandboxOptions> sandbox,
        ILogger<BashTool>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(sandbox);

        _executor = executor;
        _guard = guard;
        _sandbox = sandbox.Value;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<BashTool>.Instance;
    }

    /// <summary>Runs a shell command inside the sandbox.</summary>
    [GlassCoderTool(ToolName, Order = 70)]
    [Description("Run a shell command inside the sandboxed container. Use the dedicated tools instead where "
        + "one exists - read_file, grep, glob, edit_file, build and run_tests are safer and give better "
        + "observations. Reach for this only when nothing else fits.")]
    public async Task<ToolObservation<BashResult>> RunAsync(
        [Description("The shell command to run, for example 'git status --short'.")]
        string command,
        [Description("Directory to run in, relative to the repository root. Use '.' for the repository root.")]
        string workingDirectory = ".",
        [Description("Whether this command legitimately needs network access.")]
        bool requiresNetwork = false,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return Observation.Fail<BashResult>(ToolName, ToolErrorCodes.InvalidArgument, "command is required.");
        }

        PathGuardResult verdict = _guard.Resolve(workingDirectory, PathAccess.Read);
        if (!verdict.Allowed || verdict.FullPath is null)
        {
            return Observation.Fail<BashResult>(ToolName, ToolErrorCodes.PathNotAllowed, verdict.Reason!);
        }

        if (!Directory.Exists(verdict.FullPath))
        {
            return Observation.Fail<BashResult>(
                ToolName,
                ToolErrorCodes.NotFound,
                $"'{verdict.RelativePath}' is not a directory.");
        }

        if (_sandbox.Mode != SandboxMode.Docker && !_sandbox.AllowUnsandboxedExecution)
        {
            return Observation.Fail<BashResult>(
                ToolName,
                ToolErrorCodes.SandboxUnavailable,
                "bash is only available inside the container sandbox.",
                "Set GlassCoder:Sandbox:Mode to Docker, or use the dedicated tools.");
        }

        _logger.LogWarning("Running an arbitrary shell command in {Sandbox}: {Command}", _executor.Sandbox, command);

        CommandResult result = await _executor.ExecuteAsync(
            new CommandRequest("/bin/sh", ["-c", command])
            {
                WorkingDirectory = verdict.FullPath,
                RequiresNetwork = requiresNetwork,
                Timeout = TimeSpan.FromSeconds(_sandbox.CommandTimeoutSeconds),
            },
            cancellationToken).ConfigureAwait(false);

        if (result.FailureReason is not null)
        {
            return Observation.Fail<BashResult>(
                ToolName,
                ToolErrorCodes.SandboxUnavailable,
                result.FailureReason,
                "bash runs arbitrary code, so it will not be run outside the sandbox.");
        }

        if (result.TimedOut)
        {
            return Observation.Fail<BashResult>(
                ToolName,
                ToolErrorCodes.Timeout,
                $"The command exceeded {_sandbox.CommandTimeoutSeconds} seconds and was stopped.");
        }

        BashResult payload = new(
            command,
            result.ExitCode,
            Tail(result.CombinedOutput),
            result.Duration.TotalMilliseconds,
            result.Sandbox);

        // A non-zero exit is an outcome the agent should reason about, not a tool failure.
        return Observation.Ok(
            ToolName,
            payload,
            result.Succeeded ? "Command succeeded." : $"Command exited with {result.ExitCode}.");
    }

    private static string Tail(string output) =>
        output.Length <= MaxOutputCharacters
            ? output
            : string.Concat("… [earlier output trimmed]\n", output.AsSpan(output.Length - MaxOutputCharacters));
}
