namespace GlassCoder.Tools.Guardrails;

/// <summary>
/// The workspace the agent is allowed to see and change (CLAUDE.md §7, §13; workplan task 8).
/// <para>
/// <see cref="WritablePaths"/> is empty by default and an empty writable set means <em>nothing
/// is writable</em>. A harness that cannot write is a harmless harness; a harness that writes
/// wherever it likes is not. Opting in is a deliberate configuration act.
/// </para>
/// </summary>
public sealed class WorkspaceOptions
{
    /// <summary>Configuration section these options bind from.</summary>
    public const string SectionName = "GlassCoder:Workspace";

    /// <summary>Repository root. Relative tool paths resolve against this.</summary>
    public string RepoRoot { get; set; } = ".";

    /// <summary>
    /// Roots the agent may read. Entries may be absolute or relative to <see cref="RepoRoot"/>.
    /// Empty means the repository root itself.
    /// </summary>
    public IList<string> ReadablePaths { get; } = [];

    /// <summary>
    /// Roots the agent may write. Entries may be absolute or relative to <see cref="RepoRoot"/>.
    /// Empty means no writes are permitted at all.
    /// </summary>
    public IList<string> WritablePaths { get; } = [];

    /// <summary>
    /// Globs excluded from every access, matched against the repo-relative path with forward
    /// slashes. These are the directories where an agent can only do harm or waste context.
    /// </summary>
    public IList<string> DeniedGlobs { get; } =
    [
        ".git/**",
        "**/bin/**",
        "**/obj/**",
        "**/.vs/**",
        "**/node_modules/**",
    ];

    /// <summary>
    /// Whether a symbolic link or junction may be followed. Off by default: a link is the
    /// simplest way to walk a path allow-list straight out of the workspace.
    /// </summary>
    public bool FollowSymbolicLinks { get; set; }
}
