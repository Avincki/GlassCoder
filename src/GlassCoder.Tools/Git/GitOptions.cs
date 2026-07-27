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

    /// <summary>Remote that <c>git_sync</c> pulls from and <c>git_push</c> pushes to.</summary>
    public string Remote { get; set; } = "origin";

    /// <summary>
    /// Branches <c>git_push</c> may touch. Empty means any branch. The schema the model sees
    /// carries no force flag and no free-form refspec, so this list and
    /// <see cref="ProtectedBranches"/> are the whole policy surface.
    /// </summary>
    public IList<string> PushableBranches { get; } = [];

    /// <summary>
    /// Branches <c>git_push</c> refuses regardless of approval - listing "main" here makes
    /// agent work flow through feature branches. Wins over <see cref="PushableBranches"/>.
    /// </summary>
    public IList<string> ProtectedBranches { get; } = [];

    /// <summary>Hard cap on outgoing commits listed in a push approval or observation.</summary>
    public int MaxListedCommits { get; set; } = 20;

    /// <summary>
    /// GitHub CLI used by <c>create_pull_request</c>. The CLI rather than a REST client on
    /// purpose: <c>gh auth</c> already holds the credentials, so GlassCoder holds no token of
    /// its own - the same bargain the credential manager gets for git itself.
    /// </summary>
    public string GitHubExecutable { get; set; } = "gh";

    /// <summary>Branch pull requests target. Null uses the repository's default branch.</summary>
    public string? PullRequestBaseBranch { get; set; }
}
