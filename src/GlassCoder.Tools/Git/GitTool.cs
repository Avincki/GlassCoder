using System.ComponentModel;
using GlassCoder.Tools.Guardrails;
using GlassCoder.Tools.Processes;
using GlassCoder.Tools.Registry;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GlassCoder.Tools.Git;

/// <summary>Result payload of <c>git_status</c>.</summary>
/// <param name="Branch">Current branch.</param>
/// <param name="Upstream">Tracked upstream, when set.</param>
/// <param name="Ahead">Commits ahead of the upstream.</param>
/// <param name="Behind">Commits behind the upstream.</param>
/// <param name="Staged">Files with staged changes.</param>
/// <param name="Unstaged">Files with unstaged changes.</param>
/// <param name="Untracked">Untracked files.</param>
/// <param name="Conflicted">Files with unresolved merge conflicts.</param>
/// <param name="Files">Changed paths with their state codes, capped.</param>
/// <param name="Clean">Whether the tree has no changes at all.</param>
public sealed record GitStatusResult(
    [property: Description("Current branch, or '(detached)'.")] string Branch,
    [property: Description("Upstream branch the current branch tracks, when one is set.")] string? Upstream,
    [property: Description("Commits ahead of the upstream.")] int Ahead,
    [property: Description("Commits behind the upstream.")] int Behind,
    [property: Description("Files with staged changes.")] int Staged,
    [property: Description("Files with unstaged changes.")] int Unstaged,
    [property: Description("Untracked files.")] int Untracked,
    [property: Description("Files with unresolved merge conflicts.")] int Conflicted,
    [property: Description("Changed paths with their two-letter index/worktree state, '??' for untracked. "
        + "Capped; the counts are the truth.")]
    IReadOnlyList<string> Files,
    [property: Description("True when the working tree has no changes at all.")] bool Clean);

/// <summary>Result payload of <c>git_commit</c>.</summary>
/// <param name="Sha">SHA of the new commit.</param>
/// <param name="Branch">Branch the commit landed on.</param>
/// <param name="FilesCommitted">Number of files in the commit.</param>
/// <param name="Files">Paths in the commit, capped.</param>
/// <param name="ExcludedByGuard">Changed files not staged because they are outside the writable set.</param>
public sealed record GitCommitResult(
    [property: Description("SHA of the new commit.")] string Sha,
    [property: Description("Branch the commit landed on.")] string Branch,
    [property: Description("Number of files in the commit.")] int FilesCommitted,
    [property: Description("Paths in the commit, capped.")] IReadOnlyList<string> Files,
    [property: Description("Changed files left unstaged because they are outside the writable path set.")]
    int ExcludedByGuard);

/// <summary>
/// <c>git_status</c> and <c>git_commit</c> - the local, reversible half of version control
/// (workplan task 40). <c>git_sync</c> and <c>git_push</c> arrive in task 41, behind approval.
/// <para>
/// Git runs on the host through <see cref="IProcessRunner"/> rather than in the sandbox: the
/// container has no credentials and no network, and both are the point of the later push step.
/// The balancing constraints are that every invocation is a fixed argument list, hooks are
/// disabled unless configuration says otherwise, prompts are disabled so a missing credential
/// fails fast, and stage-all never reaches outside the path guard's writable set.
/// </para>
/// </summary>
public sealed class GitTool : IToolSet
{
    private const string StatusToolName = "git_status";
    private const string CommitToolName = "git_commit";
    private const int MaxOutputCharacters = 4000;
    private const int StagingBatchSize = 50;

    private readonly IProcessRunner _runner;
    private readonly IPathGuard _guard;
    private readonly GitOptions _options;
    private readonly ILogger<GitTool> _logger;

    /// <summary>Creates the tool.</summary>
    public GitTool(IProcessRunner runner, IPathGuard guard, IOptions<GitOptions> options, ILogger<GitTool>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        _runner = runner;
        _guard = guard;
        _options = options.Value;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<GitTool>.Instance;
    }

    /// <summary>Reports the state of the working tree.</summary>
    [GlassCoderTool(StatusToolName, Order = 80)]
    [Description("Report the state of the git working tree: current branch, ahead/behind counts against the "
        + "upstream, and which files are staged, modified, untracked or conflicted. Read-only; call it "
        + "before git_commit to see what a commit would include.")]
    public async Task<ToolObservation<GitStatusResult>> StatusAsync(CancellationToken cancellationToken = default)
    {
        GitRun run = await RunGitAsync(["status", "--porcelain=v2", "--branch"], cancellationToken).ConfigureAwait(false);
        if (Failed(run))
        {
            return FailFrom<GitStatusResult>(StatusToolName, run);
        }

        StatusSnapshot status = StatusSnapshot.Parse(run.Result!.StandardOutput);
        GitStatusResult payload = new(
            status.Branch,
            status.Upstream,
            status.Ahead,
            status.Behind,
            status.StagedCount,
            status.UnstagedCount,
            status.UntrackedCount,
            status.ConflictedCount,
            Cap(status.Describe()),
            status.Entries.Count == 0);

        return Observation.Ok(StatusToolName, payload, Summarise(payload));
    }

    /// <summary>Records the current changes as a commit.</summary>
    [GlassCoderTool(CommitToolName, Order = 81)]
    [Description("Record the current changes as a git commit on the current branch. Stages every changed "
        + "file inside the writable path set first unless stageAll is false, then commits with the given "
        + "message. Local and reversible; nothing is pushed anywhere.")]
    public async Task<ToolObservation<GitCommitResult>> CommitAsync(
        [Description("Commit message. The first line is the subject; keep it under 72 characters.")]
        string message,
        [Description("Whether to stage every changed and untracked file inside the writable set before "
            + "committing. Set false to commit only what is already staged.")]
        bool stageAll = true,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return Observation.Fail<GitCommitResult>(CommitToolName, ToolErrorCodes.InvalidArgument, "message is required.");
        }

        if (!_guard.HasWritablePaths)
        {
            return Observation.Fail<GitCommitResult>(
                CommitToolName,
                ToolErrorCodes.PathNotAllowed,
                "The workspace has no writable paths, so recording commits is disabled.",
                "Add entries to GlassCoder:Workspace:WritablePaths.");
        }

        GitRun statusRun = await RunGitAsync(["status", "--porcelain=v2", "--branch"], cancellationToken).ConfigureAwait(false);
        if (Failed(statusRun))
        {
            return FailFrom<GitCommitResult>(CommitToolName, statusRun);
        }

        StatusSnapshot status = StatusSnapshot.Parse(statusRun.Result!.StandardOutput);

        int excluded = 0;
        if (stageAll)
        {
            List<string> allowed = [];
            foreach (string candidate in status.StageCandidates())
            {
                if (_guard.Resolve(candidate, PathAccess.Write).Allowed)
                {
                    allowed.Add(candidate);
                }
                else
                {
                    excluded++;
                }
            }

            foreach (string[] batch in allowed.Chunk(StagingBatchSize))
            {
                GitRun addRun = await RunGitAsync(["add", "--", .. batch], cancellationToken).ConfigureAwait(false);
                if (Failed(addRun))
                {
                    return FailFrom<GitCommitResult>(CommitToolName, addRun);
                }
            }
        }

        GitRun stagedRun = await RunGitAsync(["diff", "--cached", "--name-only"], cancellationToken).ConfigureAwait(false);
        if (Failed(stagedRun))
        {
            return FailFrom<GitCommitResult>(CommitToolName, stagedRun);
        }

        string[] stagedFiles = stagedRun.Result!.StandardOutput
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (stagedFiles.Length == 0)
        {
            return Observation.Fail<GitCommitResult>(
                CommitToolName,
                ToolErrorCodes.NotFound,
                excluded > 0
                    ? $"Nothing to commit: the only changed files ({excluded}) are outside the writable path set."
                    : "Nothing to commit: no staged changes were found.",
                "Check git_status first; make an edit, or set stageAll to true.");
        }

        List<string> commitArguments = [];
        if (!_options.AllowHooks)
        {
            // /dev/null is understood by git for Windows too; hooks resolve to nowhere and do not run.
            commitArguments.AddRange(["-c", "core.hooksPath=/dev/null"]);
        }

        commitArguments.AddRange(["commit", "-m", message]);
        if (!string.IsNullOrWhiteSpace(_options.CommitTrailer))
        {
            commitArguments.AddRange(["-m", _options.CommitTrailer]);
        }

        GitRun commitRun = await RunGitAsync(commitArguments, cancellationToken).ConfigureAwait(false);
        if (Failed(commitRun))
        {
            return FailFrom<GitCommitResult>(CommitToolName, commitRun);
        }

        GitRun shaRun = await RunGitAsync(["rev-parse", "HEAD"], cancellationToken).ConfigureAwait(false);
        string sha = Failed(shaRun) ? "(unknown)" : shaRun.Result!.StandardOutput.Trim();

        _logger.LogInformation("Committed {FileCount} file(s) as {Sha} on {Branch}", stagedFiles.Length, sha, status.Branch);

        GitCommitResult payload = new(sha, status.Branch, stagedFiles.Length, Cap(stagedFiles), excluded);
        string summary = $"Committed {stagedFiles.Length} file(s) to {status.Branch} as {Shorten(sha)}."
            + (excluded > 0 ? $" {excluded} changed file(s) outside the writable set were not staged." : string.Empty);

        return Observation.Ok(CommitToolName, payload, summary);
    }

    private async Task<GitRun> RunGitAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        ProcessRunRequest request = new(_options.GitExecutable, arguments)
        {
            WorkingDirectory = _guard.RepoRoot,
            Timeout = TimeSpan.FromSeconds(Math.Max(1, _options.CommandTimeoutSeconds)),
            Environment = new Dictionary<string, string?>
            {
                // A prompt nobody will answer must become a fast failure, not a hung loop step.
                ["GIT_TERMINAL_PROMPT"] = "0",
                ["GCM_INTERACTIVE"] = "never",
                ["GIT_OPTIONAL_LOCKS"] = "0",
            },
        };

        try
        {
            return new GitRun(true, await _runner.RunAsync(request, cancellationToken).ConfigureAwait(false), null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "git could not be launched as {Executable}", _options.GitExecutable);
            return new GitRun(false, null, ex.Message);
        }
    }

    private static bool Failed(GitRun run) => !run.Launched || run.Result!.TimedOut || run.Result.ExitCode != 0;

    private ToolObservation<TData> FailFrom<TData>(string tool, GitRun run)
    {
        if (!run.Launched)
        {
            return Observation.Fail<TData>(
                tool,
                ToolErrorCodes.GitUnavailable,
                $"git could not be started: {run.LaunchFailure}",
                "Install git, or set GlassCoder:Git:GitExecutable to its full path.");
        }

        if (run.Result!.TimedOut)
        {
            return Observation.Fail<TData>(
                tool,
                ToolErrorCodes.Timeout,
                $"git exceeded {_options.CommandTimeoutSeconds} seconds and was stopped.",
                "Prompts are disabled, so a hung credential helper or signing key is the usual cause.");
        }

        string error = Tail(string.IsNullOrWhiteSpace(run.Result.StandardError)
            ? run.Result.StandardOutput
            : run.Result.StandardError).Trim();

        if (error.Contains("not a git repository", StringComparison.OrdinalIgnoreCase))
        {
            return Observation.Fail<TData>(
                tool,
                ToolErrorCodes.GitUnavailable,
                "The workspace is not a git repository.",
                "Run git init in the repository root first.");
        }

        return Observation.Fail<TData>(tool, ToolErrorCodes.Unexpected, $"git exited with {run.Result.ExitCode}: {error}");
    }

    private static string Summarise(GitStatusResult status)
    {
        string position = status.Upstream is null
            ? string.Empty
            : $" ({status.Ahead} ahead, {status.Behind} behind {status.Upstream})";

        return status.Clean
            ? $"Working tree clean on {status.Branch}{position}."
            : $"On {status.Branch}{position}: {status.Staged} staged, {status.Unstaged} unstaged, "
                + $"{status.Untracked} untracked, {status.Conflicted} conflicted.";
    }

    private IReadOnlyList<string> Cap(IReadOnlyList<string> files) =>
        files.Count <= _options.MaxListedFiles ? files : [.. files.Take(_options.MaxListedFiles)];

    private static string Shorten(string sha) => sha.Length > 8 ? sha[..8] : sha;

    private static string Tail(string output) =>
        output.Length <= MaxOutputCharacters
            ? output
            : string.Concat("… [earlier output trimmed]\n", output.AsSpan(output.Length - MaxOutputCharacters));

    /// <summary>One git invocation: either a result, or the reason it never launched.</summary>
    private sealed record GitRun(bool Launched, ProcessRunResult? Result, string? LaunchFailure);

    private enum ChangeKind
    {
        Tracked,
        Untracked,
        Conflicted,
    }

    private sealed record ChangeEntry(string Code, string Path, string? OriginalPath, ChangeKind Kind);

    /// <summary>Parsed <c>git status --porcelain=v2 --branch</c> output.</summary>
    private sealed class StatusSnapshot
    {
        private const string HeadPrefix = "# branch.head ";
        private const string UpstreamPrefix = "# branch.upstream ";
        private const string AheadBehindPrefix = "# branch.ab ";

        /// <summary>Current branch, or <c>(detached)</c>.</summary>
        public string Branch { get; private set; } = "(unknown)";

        /// <summary>Tracked upstream, when one is set.</summary>
        public string? Upstream { get; private set; }

        /// <summary>Commits ahead of the upstream.</summary>
        public int Ahead { get; private set; }

        /// <summary>Commits behind the upstream.</summary>
        public int Behind { get; private set; }

        /// <summary>Every change entry, in output order.</summary>
        public List<ChangeEntry> Entries { get; } = [];

        /// <summary>Files with staged changes.</summary>
        public int StagedCount => Entries.Count(e => e.Kind == ChangeKind.Tracked && e.Code[0] != '.');

        /// <summary>Files with unstaged changes.</summary>
        public int UnstagedCount => Entries.Count(e => e.Kind == ChangeKind.Tracked && e.Code[1] != '.');

        /// <summary>Untracked files.</summary>
        public int UntrackedCount => Entries.Count(e => e.Kind == ChangeKind.Untracked);

        /// <summary>Files with unresolved conflicts.</summary>
        public int ConflictedCount => Entries.Count(e => e.Kind == ChangeKind.Conflicted);

        /// <summary>
        /// Paths <c>stageAll</c> should offer to the guard: worktree modifications and untracked
        /// files. Conflicted paths are deliberately absent - staging one marks the conflict
        /// resolved, and that is a judgement the agent must make explicitly, not a side effect.
        /// </summary>
        public IEnumerable<string> StageCandidates()
        {
            foreach (ChangeEntry entry in Entries)
            {
                if (entry.Kind == ChangeKind.Untracked || (entry.Kind == ChangeKind.Tracked && entry.Code[1] != '.'))
                {
                    yield return entry.Path;
                    if (entry.OriginalPath is not null)
                    {
                        yield return entry.OriginalPath;
                    }
                }
            }
        }

        /// <summary>Human- and model-readable one-liners, one per entry.</summary>
        public IReadOnlyList<string> Describe() =>
            [.. Entries.Select(e => e.OriginalPath is null
                ? $"{e.Code} {e.Path}"
                : $"{e.Code} {e.Path} (from {e.OriginalPath})")];

        /// <summary>Parses porcelain v2 output. Unrecognised lines are skipped, never fatal.</summary>
        public static StatusSnapshot Parse(string output)
        {
            StatusSnapshot snapshot = new();
            foreach (string raw in output.Split('\n'))
            {
                string line = raw.TrimEnd('\r');
                if (line.Length == 0)
                {
                    continue;
                }

                if (line.StartsWith(HeadPrefix, StringComparison.Ordinal))
                {
                    snapshot.Branch = line[HeadPrefix.Length..];
                    continue;
                }

                if (line.StartsWith(UpstreamPrefix, StringComparison.Ordinal))
                {
                    snapshot.Upstream = line[UpstreamPrefix.Length..];
                    continue;
                }

                if (line.StartsWith(AheadBehindPrefix, StringComparison.Ordinal))
                {
                    string[] ab = line[AheadBehindPrefix.Length..].Split(' ');
                    if (ab.Length == 2 && int.TryParse(ab[0], out int ahead) && int.TryParse(ab[1], out int behind))
                    {
                        snapshot.Ahead = Math.Abs(ahead);
                        snapshot.Behind = Math.Abs(behind);
                    }

                    continue;
                }

                if (line[0] == '#')
                {
                    continue;
                }

                // Paths may contain spaces, so each entry type splits with its exact field count.
                switch (line[0])
                {
                    case '1' when line.Split(' ', 9) is { Length: 9 } fields:
                        snapshot.Entries.Add(new ChangeEntry(fields[1], fields[8], null, ChangeKind.Tracked));
                        break;

                    case '2' when line.Split(' ', 10) is { Length: 10 } fields:
                        string[] paths = fields[9].Split('\t');
                        snapshot.Entries.Add(new ChangeEntry(
                            fields[1],
                            paths[0],
                            paths.Length > 1 ? paths[1] : null,
                            ChangeKind.Tracked));
                        break;

                    case 'u' when line.Split(' ', 11) is { Length: 11 } fields:
                        snapshot.Entries.Add(new ChangeEntry(fields[1], fields[10], null, ChangeKind.Conflicted));
                        break;

                    case '?' when line.Split(' ', 2) is { Length: 2 } fields:
                        snapshot.Entries.Add(new ChangeEntry("??", fields[1], null, ChangeKind.Untracked));
                        break;
                }
            }

            return snapshot;
        }
    }
}
