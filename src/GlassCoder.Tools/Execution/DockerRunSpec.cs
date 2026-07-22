using Docker.DotNet.Models;

namespace GlassCoder.Tools.Execution;

/// <summary>
/// Builds the container specification for one command (workplan task 17).
/// <para>
/// Separated from the Docker client on purpose: the security-relevant decisions - what is
/// mounted, whether there is a network, where the command runs - are made here, as a pure
/// function of the request and the policy, and can therefore be unit-tested without a daemon.
/// A sandbox whose rules can only be verified by running it is not a sandbox anyone should
/// trust.
/// </para>
/// </summary>
public static class DockerRunSpec
{
    /// <summary>Network mode that gives a container no network at all.</summary>
    public const string NoNetwork = "none";

    /// <summary>Network mode that gives a container the default bridge network.</summary>
    public const string BridgeNetwork = "bridge";

    /// <summary>Builds the create-container parameters for a request.</summary>
    /// <param name="request">The command to run.</param>
    /// <param name="options">Sandbox policy.</param>
    /// <param name="repoRoot">Absolute host path mounted as the workspace.</param>
    public static CreateContainerParameters Create(CommandRequest request, SandboxOptions options, string repoRoot)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(repoRoot);

        string workspace = string.IsNullOrWhiteSpace(options.ContainerWorkspacePath)
            ? "/workspace"
            : options.ContainerWorkspacePath;

        return new CreateContainerParameters
        {
            Image = options.Image,
            Cmd = [request.FileName, .. request.Arguments],
            WorkingDir = ToContainerPath(request.WorkingDirectory, repoRoot, workspace),
            Env = [.. options.Environment],
            HostConfig = new HostConfig
            {
                Binds = [$"{TrimSeparator(repoRoot)}:{workspace}"],
                NetworkMode = ResolveNetworkMode(request, options),
                Memory = options.MemoryBytes,
                AutoRemove = false,
            },
        };
    }

    /// <summary>
    /// Decides whether this run gets a network. Denied by default; granted only when the
    /// command declares it needs a restore <em>and</em> policy allows that exception.
    /// </summary>
    public static string ResolveNetworkMode(CommandRequest request, SandboxOptions options)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(options);

        if (options.AllowNetwork)
        {
            return BridgeNetwork;
        }

        return request.RequiresNetwork && options.AllowNetworkForRestore ? BridgeNetwork : NoNetwork;
    }

    /// <summary>Maps a host path under the repository root to its path inside the container.</summary>
    public static string ToContainerPath(string? hostPath, string repoRoot, string workspace)
    {
        if (string.IsNullOrWhiteSpace(hostPath))
        {
            return workspace;
        }

        string relative = Path.GetRelativePath(repoRoot, Path.GetFullPath(hostPath));
        if (relative is "." or "")
        {
            return workspace;
        }

        // A path that escapes the mount cannot be expressed inside the container, and silently
        // running in the wrong directory would be worse than refusing.
        if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative))
        {
            throw new ArgumentException(
                $"'{hostPath}' is outside the mounted workspace '{repoRoot}'.",
                nameof(hostPath));
        }

        return $"{workspace}/{relative.Replace('\\', '/')}";
    }

    private static string TrimSeparator(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
}
