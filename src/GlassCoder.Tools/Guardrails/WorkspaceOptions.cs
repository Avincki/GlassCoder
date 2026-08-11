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
    /// File names a run may write <em>at the repository root only</em>, even when the root itself
    /// is not in <see cref="WritablePaths"/>.
    /// <para>
    /// A repository's own furniture belongs at the root and nowhere else, and the shipped writable
    /// set is <c>src</c> and <c>tests</c>. Run <c>46231701</c> met the consequence: task 73
    /// correctly refused a solution below the root, the root was not writable, so its own advice
    /// was to skip the solution - and under that configuration "no solution at all" was the only
    /// reachable state. <c>dotnet test</c> from the root had no target, <c>bin</c> and <c>obj</c>
    /// sat unignored in a synced tree, and <c>ProjectLocator</c>'s root-solution branch had been
    /// unreachable code for months.
    /// </para>
    /// <para>
    /// Deliberately file names, not another writable path. Opening the root would let a run
    /// scatter source files across it, which is the mess a src/tests split exists to prevent.
    /// Patterns take <c>*</c> and <c>?</c> and are matched against the file name alone, so no
    /// entry here can reach into a subdirectory.
    /// </para>
    /// <para>
    /// What is <em>not</em> on the list is as deliberate: no <c>.editorconfig</c> and no
    /// <c>NuGet.config</c>. One can switch analyzers off and the other can move where packages
    /// come from, and neither is furniture a run needs to build what it was asked for.
    /// <c>Directory.Build.props</c> is admitted with that risk noted - it is how a multi-project
    /// workspace shares a target framework, which is a thing runs legitimately need.
    /// </para>
    /// </summary>
    public IList<string> WritableRootFiles { get; } =
    [
        "*.sln",
        "*.slnx",
        ".gitignore",
        "Directory.Build.props",
        "Directory.Build.targets",
        "Directory.Packages.props",
        "global.json",
        "README.md",
    ];

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

    /// <summary>
    /// Whether build-output folders under the workspace (bin, obj and friends) are marked with
    /// the <c>com.dropbox.ignored</c> stream around every sandboxed command. On by default and
    /// a no-op unless the workspace actually lives inside a Dropbox folder - where an unmarked
    /// obj is a sync client racing every build for the same files.
    /// </summary>
    public bool ExcludeBuildOutputFromDropbox { get; set; } = true;
}
