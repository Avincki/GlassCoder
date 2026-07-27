namespace GlassCoder.Tools.Git;

/// <summary>
/// Git policy (workplan task 40).
/// <para>
/// Git runs on the host, not in the sandbox: the container has no network and no credentials,
/// and both are the point of the later push step. What makes that defensible is that nothing
/// here executes repository code — every invocation is a fixed argument list through
/// <see cref="Processes.IProcessRunner"/>, and hooks are off by default because a pre-commit
/// hook is arbitrary code execution with the harness's privileges.
/// </para>
/// </summary>
public sealed class GitOptions
{
    /// <summary>Configuration section these options bind from.</summary>
    public const string SectionName = "GlassCoder:Git";

    /// <summary>
    /// Whether the git tools are registered at all. Off by default, like <c>bash</c>: version
    /// control actions are an opt-in capability, not part of the base tool set.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>Git executable to launch. A bare name resolves through PATH.</summary>
    public string GitExecutable { get; set; } = "git";

    /// <summary>Per-command timeout. Git is run with prompts disabled, so a hang is a defect.</summary>
    public int CommandTimeoutSeconds { get; set; } = 120;

    /// <summary>
    /// Whether commit hooks may run. Off by default: a hook is code the agent may just have
    /// written, executing on the host outside the sandbox.
    /// </summary>
    public bool AllowHooks { get; set; }

    /// <summary>
    /// Provenance trailer appended to every commit message as its own paragraph. Empty disables
    /// it. Agent commits should be recognisable as agent commits (CLAUDE.md §17, phase 6).
    /// </summary>
    public string CommitTrailer { get; set; } = "Co-Authored-By: GlassCoder <agent@glasscoder.invalid>";

    /// <summary>Hard cap on file paths listed in one observation. The counts stay truthful.</summary>
    public int MaxListedFiles { get; set; } = 100;
}
