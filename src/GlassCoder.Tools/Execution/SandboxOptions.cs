namespace GlassCoder.Tools.Execution;

/// <summary>Where a command is allowed to run.</summary>
public enum SandboxMode
{
    /// <summary>Inside a container with the repository mounted and the network dropped.</summary>
    Docker,

    /// <summary>Directly on the host. Requires <see cref="SandboxOptions.AllowUnsandboxedExecution"/>.</summary>
    Local,
}

/// <summary>
/// Sandbox policy for anything that executes repository code (CLAUDE.md §8.4, workplan task 17).
/// <para>
/// The premise: <b>a build is arbitrary code execution.</b> An MSBuild target, a source
/// generator, a test's static constructor - each is code the agent just wrote or fetched,
/// running with the harness's privileges. "Run the build" is exactly as dangerous as "run
/// bash", and is treated the same way here.
/// </para>
/// </summary>
public sealed class SandboxOptions
{
    /// <summary>Configuration section these options bind from.</summary>
    public const string SectionName = "GlassCoder:Sandbox";

    /// <summary>Where commands run.</summary>
    public SandboxMode Mode { get; set; } = SandboxMode.Docker;

    /// <summary>Image used for containerised runs. Must contain the .NET SDK.</summary>
    public string Image { get; set; } = "mcr.microsoft.com/dotnet/sdk:10.0";

    /// <summary>Mount point of the repository inside the container.</summary>
    public string ContainerWorkspacePath { get; set; } = "/workspace";

    /// <summary>Docker endpoint. Null uses the platform default (named pipe or unix socket).</summary>
    public string? DockerEndpoint { get; set; }

    /// <summary>
    /// Whether the container gets a network at all. Off by default - a build that needs the
    /// internet is a build that can exfiltrate the repository.
    /// </summary>
    public bool AllowNetwork { get; set; }

    /// <summary>
    /// Whether a command that declares it needs to restore packages may have the network for
    /// that run only. This is the one legitimate exception in CLAUDE.md §8.4.
    /// </summary>
    public bool AllowNetworkForRestore { get; set; } = true;

    /// <summary>Memory ceiling for the container, in bytes. Zero leaves it unlimited.</summary>
    public long MemoryBytes { get; set; } = 4L * 1024 * 1024 * 1024;

    /// <summary>Default per-command timeout.</summary>
    public int CommandTimeoutSeconds { get; set; } = 600;

    /// <summary>
    /// Whether commands may run directly on the host. Off by default: without this, a machine
    /// with no Docker daemon gets a clear refusal instead of silently running a model's build
    /// script against the developer's own filesystem.
    /// </summary>
    public bool AllowUnsandboxedExecution { get; set; }

    /// <summary>
    /// Whether the container is hardened: read-only root filesystem, all Linux capabilities
    /// dropped, no privilege escalation, and a bounded process count (workplan task 35).
    /// <para>
    /// The mounted workspace stays writable - the agent has to be able to edit and build - so
    /// hardening constrains what a build can do to the <em>container</em>, not to the repository
    /// it was given. Writable scratch space is provided as tmpfs rather than by unlocking the
    /// root filesystem.
    /// </para>
    /// </summary>
    public bool HardenContainer { get; set; } = true;

    /// <summary>Maximum processes inside the container. Zero leaves it unlimited.</summary>
    public long ProcessLimit { get; set; } = 512;

    /// <summary>Environment variables passed into the container as <c>NAME=value</c>.</summary>
    public IList<string> Environment { get; } =
    [
        "DOTNET_CLI_TELEMETRY_OPTOUT=1",
        "DOTNET_NOLOGO=1",
        "DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1",
        // A read-only root filesystem has nowhere to put the default package cache.
        "NUGET_PACKAGES=/workspace/.glasscoder/nuget",
        "HOME=/tmp",
    ];
}
