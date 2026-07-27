using GlassCoder.TestSupport;
using GlassCoder.Tools.Changes;
using GlassCoder.Tools.Git;
using GlassCoder.Tools.Processes;
using GlassCoder.Tools.Registry;
using Microsoft.Extensions.AI;

namespace GlassCoder.Tools.Tests;

/// <summary>
/// <c>git_status</c> and <c>git_commit</c> (workplan task 40): fixed argument lists, the
/// writable set as the staging boundary, hooks and prompts off, and every failure an
/// observation. All over the fake runner - no test touches a real repository.
/// </summary>
public sealed class GitToolTests : IDisposable
{
    private const string StatusOutput = """
        # branch.oid 4a5a5c3
        # branch.head main
        # branch.upstream origin/main
        # branch.ab +2 -1
        1 M. N... 100644 100644 100644 e69de29 abc1234 src/Staged.cs
        1 .M N... 100644 100644 100644 e69de29 e69de29 src/Pager.cs
        u UU N... 100644 100644 100644 100644 abc1234 def5678 fedcba9 src/Conflict.cs
        ? docs/notes.md
        """;

    private readonly TempWorkspace _workspace = new();
    private readonly FakeProcessRunner _runner = new();
    private readonly GitOptions _options = new();

    public void Dispose() => _workspace.Dispose();

    private GitTool Tool(params string[] writablePaths) => Tool(null, writablePaths);

    private GitTool Tool(IApprovalGate? gate, params string[] writablePaths) =>
        new(_runner, _workspace.Guard(writablePaths), TempWorkspace.Wrap(_options), gate);

    [Fact]
    public async Task Status_reports_branch_position_and_counts()
    {
        _runner.Enqueue(0, StatusOutput);

        ToolObservation<GitStatusResult> observation = await Tool().StatusAsync();

        observation.Ok.ShouldBeTrue(observation.Error?.Message);
        observation.Data!.Branch.ShouldBe("main");
        observation.Data.Upstream.ShouldBe("origin/main");
        observation.Data.Ahead.ShouldBe(2);
        observation.Data.Behind.ShouldBe(1);
        observation.Data.Staged.ShouldBe(1);
        observation.Data.Unstaged.ShouldBe(1);
        observation.Data.Untracked.ShouldBe(1);
        observation.Data.Conflicted.ShouldBe(1);
        observation.Data.Clean.ShouldBeFalse();
        observation.Data.Files.ShouldContain("?? docs/notes.md");
        observation.Data.Files.ShouldContain(".M src/Pager.cs");
    }

    [Fact]
    public async Task Status_runs_read_only_with_prompts_disabled_in_the_repo_root()
    {
        _runner.Enqueue(0, StatusOutput);

        await Tool().StatusAsync();

        ProcessRunRequest request = _runner.Requests.ShouldHaveSingleItem();
        request.FileName.ShouldBe("git");
        request.Arguments.ShouldBe(["status", "--porcelain=v2", "--branch"]);
        request.WorkingDirectory.ShouldBe(_workspace.Guard().RepoRoot);
        request.Environment!["GIT_TERMINAL_PROMPT"].ShouldBe("0");
        request.Environment["GCM_INTERACTIVE"].ShouldBe("never");
    }

    [Fact]
    public async Task A_clean_tree_reads_as_clean()
    {
        _runner.Enqueue(0, "# branch.oid 4a5a5c3\n# branch.head main\n");

        ToolObservation<GitStatusResult> observation = await Tool().StatusAsync();

        observation.Ok.ShouldBeTrue();
        observation.Data!.Clean.ShouldBeTrue();
        observation.Summary.ShouldContain("clean");
    }

    [Fact]
    public async Task A_missing_git_executable_is_an_observation_not_an_exception()
    {
        GitTool tool = new(new ThrowingProcessRunner(), _workspace.Guard(), TempWorkspace.Wrap(_options));

        ToolObservation<GitStatusResult> observation = await tool.StatusAsync();

        observation.Ok.ShouldBeFalse();
        observation.Error!.Code.ShouldBe(ToolErrorCodes.GitUnavailable);
        observation.Error.Hint.ShouldContain("GitExecutable");
    }

    [Fact]
    public async Task A_directory_that_is_not_a_repository_reports_git_unavailable()
    {
        _runner.Enqueue(128, "", "fatal: not a git repository (or any of the parent directories): .git");

        ToolObservation<GitStatusResult> observation = await Tool().StatusAsync();

        observation.Ok.ShouldBeFalse();
        observation.Error!.Code.ShouldBe(ToolErrorCodes.GitUnavailable);
        observation.Error.Message.ShouldContain("not a git repository");
        observation.Error.Hint.ShouldContain("git init");
    }

    [Fact]
    public async Task A_timed_out_git_command_reports_timeout()
    {
        _runner.Default = new ProcessRunResult(-1, "", "", TimeSpan.Zero, TimedOut: true);

        ToolObservation<GitStatusResult> observation = await Tool().StatusAsync();

        observation.Ok.ShouldBeFalse();
        observation.Error!.Code.ShouldBe(ToolErrorCodes.Timeout);
    }

    [Fact]
    public async Task Commit_refuses_a_read_only_workspace_without_running_git()
    {
        ToolObservation<GitCommitResult> observation = await Tool().CommitAsync("message");

        observation.Ok.ShouldBeFalse();
        observation.Error!.Code.ShouldBe(ToolErrorCodes.PathNotAllowed);
        _runner.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task Commit_requires_a_message()
    {
        ToolObservation<GitCommitResult> observation = await Tool("src").CommitAsync("   ");

        observation.Ok.ShouldBeFalse();
        observation.Error!.Code.ShouldBe(ToolErrorCodes.InvalidArgument);
        _runner.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task Commit_stages_only_paths_inside_the_writable_set()
    {
        _runner.Enqueue(0, StatusOutput)                       // status
            .Enqueue(0)                                        // add
            .Enqueue(0, "src/Staged.cs\nsrc/Pager.cs\n")       // diff --cached --name-only
            .Enqueue(0)                                        // commit
            .Enqueue(0, "deadbeefdeadbeefdeadbeefdeadbeefdeadbeef\n");

        ToolObservation<GitCommitResult> observation = await Tool("src").CommitAsync("Fix the pager");

        observation.Ok.ShouldBeTrue(observation.Error?.Message);

        ProcessRunRequest add = _runner.Requests[1];
        add.Arguments[0].ShouldBe("add");
        add.Arguments.ShouldContain("src/Pager.cs");
        // Outside the writable set, so never staged - the guard is the staging boundary.
        add.Arguments.ShouldNotContain("docs/notes.md");
        // Staging a conflicted path would silently mark the conflict resolved.
        add.Arguments.ShouldNotContain("src/Conflict.cs");

        observation.Data!.Sha.ShouldBe("deadbeefdeadbeefdeadbeefdeadbeefdeadbeef");
        observation.Data.Branch.ShouldBe("main");
        observation.Data.FilesCommitted.ShouldBe(2);
        observation.Data.ExcludedByGuard.ShouldBe(1);
        observation.Summary.ShouldContain("deadbeef");
        observation.Summary.ShouldContain("outside the writable set");
    }

    [Fact]
    public async Task Commit_disables_hooks_and_appends_the_provenance_trailer()
    {
        _options.CommitTrailer = "Co-Authored-By: GlassCoder <agent@glasscoder.invalid>";
        _runner.Enqueue(0, StatusOutput)
            .Enqueue(0)
            .Enqueue(0, "src/Pager.cs\n")
            .Enqueue(0)
            .Enqueue(0, "deadbeef\n");

        await Tool("src").CommitAsync("Fix the pager");

        ProcessRunRequest commit = _runner.Requests[3];
        commit.Arguments.ShouldBe([
            "-c", "core.hooksPath=/dev/null",
            "commit", "-m", "Fix the pager",
            "-m", "Co-Authored-By: GlassCoder <agent@glasscoder.invalid>",
        ]);
    }

    [Fact]
    public async Task Hooks_and_the_trailer_are_both_configuration()
    {
        _options.AllowHooks = true;
        _options.CommitTrailer = "";
        _runner.Enqueue(0, StatusOutput)
            .Enqueue(0)
            .Enqueue(0, "src/Pager.cs\n")
            .Enqueue(0)
            .Enqueue(0, "deadbeef\n");

        await Tool("src").CommitAsync("Fix the pager");

        _runner.Requests[3].Arguments.ShouldBe(["commit", "-m", "Fix the pager"]);
    }

    [Fact]
    public async Task Commit_with_stage_all_off_touches_only_the_index()
    {
        _runner.Enqueue(0, StatusOutput)
            .Enqueue(0, "src/Staged.cs\n")
            .Enqueue(0)
            .Enqueue(0, "deadbeef\n");

        ToolObservation<GitCommitResult> observation = await Tool("src").CommitAsync("Only the index", stageAll: false);

        observation.Ok.ShouldBeTrue(observation.Error?.Message);
        _runner.Requests.Count.ShouldBe(4);
        _runner.Requests.ShouldNotContain(r => r.Arguments[0] == "add");
        observation.Data!.ExcludedByGuard.ShouldBe(0);
    }

    [Fact]
    public async Task Nothing_to_commit_fails_cleanly()
    {
        _runner.Enqueue(0, "# branch.head main\n")   // clean tree
            .Enqueue(0, "");                          // nothing staged either

        ToolObservation<GitCommitResult> observation = await Tool("src").CommitAsync("message");

        observation.Ok.ShouldBeFalse();
        observation.Error!.Code.ShouldBe(ToolErrorCodes.NotFound);
        observation.Error.Message.ShouldContain("Nothing to commit");
    }

    [Fact]
    public async Task Changes_entirely_outside_the_writable_set_explain_the_refusal()
    {
        _runner.Enqueue(0, "# branch.head main\n? docs/notes.md\n")
            .Enqueue(0, "");

        ToolObservation<GitCommitResult> observation = await Tool("src").CommitAsync("message");

        observation.Ok.ShouldBeFalse();
        observation.Error!.Code.ShouldBe(ToolErrorCodes.NotFound);
        observation.Error.Message.ShouldContain("outside the writable path set");
    }

    [Fact]
    public async Task A_failed_commit_surfaces_the_git_error()
    {
        _runner.Enqueue(0, StatusOutput)
            .Enqueue(0)
            .Enqueue(0, "src/Pager.cs\n")
            .Enqueue(1, "", "fatal: empty ident name not allowed");

        ToolObservation<GitCommitResult> observation = await Tool("src").CommitAsync("message");

        observation.Ok.ShouldBeFalse();
        observation.Error!.Code.ShouldBe(ToolErrorCodes.Unexpected);
        observation.Error.Message.ShouldContain("empty ident name");
    }

    [Fact]
    public void The_git_tools_satisfy_the_tool_contract()
    {
        IReadOnlyList<AIFunction> functions = ToolFunctionFactory.Create([Tool()]);

        functions.Select(f => f.Name).ShouldBe(
            ["git_status", "git_commit", "git_sync", "git_push", "create_pull_request"]);
    }

    [Fact]
    public async Task A_pull_request_is_opened_through_the_gh_cli_after_approval()
    {
        RecordingGate gate = new(approve: true);
        _runner.Enqueue(0, "# branch.head feature/pager\n# branch.upstream origin/feature/pager\n# branch.ab +0 -0\n")
            .Enqueue(0, "https://github.com/x/y/pull/42\n");

        ToolObservation<GitPullRequestResult> observation = await Tool(gate, "src")
            .CreatePullRequestAsync("Fix the pager", "It was off by one.");

        observation.Ok.ShouldBeTrue(observation.Error?.Message);

        ProcessRunRequest pr = _runner.Requests[1];
        pr.FileName.ShouldBe("gh");
        // --flag=value, so a title starting with a dash can never be read as an option.
        pr.Arguments.ShouldBe([
            "pr", "create", "--title=Fix the pager", "--body=It was off by one.", "--head=feature/pager",
        ]);
        observation.Data!.Url.ShouldBe("https://github.com/x/y/pull/42");
        observation.Data.HeadBranch.ShouldBe("feature/pager");
        gate.Action!.Tool.ShouldBe("create_pull_request");
        gate.Action.Detail.ShouldContain("Title: Fix the pager");
    }

    [Fact]
    public async Task A_configured_base_branch_is_passed_to_the_cli()
    {
        _options.PullRequestBaseBranch = "develop";
        _runner.Enqueue(0, "# branch.head feature/pager\n# branch.upstream origin/feature/pager\n# branch.ab +0 -0\n")
            .Enqueue(0, "https://github.com/x/y/pull/42\n");

        await Tool(new RecordingGate(approve: true), "src").CreatePullRequestAsync("Fix the pager");

        _runner.Requests[1].Arguments.ShouldContain("--base=develop");
    }

    [Fact]
    public async Task A_refused_pull_request_never_reaches_the_cli()
    {
        RecordingGate gate = new(approve: false);
        _runner.Enqueue(0, "# branch.head feature/pager\n# branch.upstream origin/feature/pager\n# branch.ab +0 -0\n");

        ToolObservation<GitPullRequestResult> observation = await Tool(gate, "src")
            .CreatePullRequestAsync("Fix the pager");

        observation.Ok.ShouldBeFalse();
        observation.Error!.Code.ShouldBe(ToolErrorCodes.ApprovalRefused);
        _runner.Requests.Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_pull_request_needs_the_branch_pushed_first()
    {
        _runner.Enqueue(0, "# branch.head feature/pager\n");

        ToolObservation<GitPullRequestResult> observation = await Tool(new RecordingGate(approve: true), "src")
            .CreatePullRequestAsync("Fix the pager");

        observation.Ok.ShouldBeFalse();
        observation.Error!.Code.ShouldBe(ToolErrorCodes.NotFound);
        observation.Error.Hint.ShouldContain("git_push");
    }

    [Fact]
    public async Task A_pull_request_refuses_to_describe_unpushed_commits()
    {
        _runner.Enqueue(0, CleanAheadTwo);

        ToolObservation<GitPullRequestResult> observation = await Tool(new RecordingGate(approve: true), "src")
            .CreatePullRequestAsync("Fix the pager");

        observation.Ok.ShouldBeFalse();
        observation.Error!.Code.ShouldBe(ToolErrorCodes.InvalidArgument);
        observation.Error.Message.ShouldContain("2 commit(s) ahead");
    }

    [Fact]
    public async Task A_pull_request_honours_the_branch_policy()
    {
        _options.ProtectedBranches.Add("main");
        RecordingGate gate = new(approve: true);
        _runner.Enqueue(0, "# branch.head main\n# branch.upstream origin/main\n# branch.ab +0 -0\n");

        ToolObservation<GitPullRequestResult> observation = await Tool(gate, "src").CreatePullRequestAsync("Ship it");

        observation.Ok.ShouldBeFalse();
        observation.Error!.Code.ShouldBe(ToolErrorCodes.BranchNotAllowed);
        gate.Action.ShouldBeNull();
    }

    [Fact]
    public async Task An_unauthenticated_gh_cli_says_how_to_fix_it()
    {
        _runner.Enqueue(0, "# branch.head feature/pager\n# branch.upstream origin/feature/pager\n# branch.ab +0 -0\n")
            .Enqueue(1, "", "gh: To get started with GitHub CLI, please run: gh auth login");

        ToolObservation<GitPullRequestResult> observation = await Tool(new RecordingGate(approve: true), "src")
            .CreatePullRequestAsync("Fix the pager");

        observation.Ok.ShouldBeFalse();
        observation.Error!.Hint.ShouldContain("gh auth login");
    }

    [Fact]
    public async Task An_existing_pull_request_is_reported_as_such()
    {
        _runner.Enqueue(0, "# branch.head feature/pager\n# branch.upstream origin/feature/pager\n# branch.ab +0 -0\n")
            .Enqueue(1, "", "a pull request for branch \"feature/pager\" already exists");

        ToolObservation<GitPullRequestResult> observation = await Tool(new RecordingGate(approve: true), "src")
            .CreatePullRequestAsync("Fix the pager");

        observation.Ok.ShouldBeFalse();
        observation.Error!.Code.ShouldBe(ToolErrorCodes.AlreadyExists);
    }

    [Fact]
    public async Task A_missing_gh_cli_is_an_observation_with_install_guidance()
    {
        GitTool tool = new(
            new SelectiveThrowingRunner(_runner, "gh"),
            _workspace.Guard("src"),
            TempWorkspace.Wrap(_options),
            new RecordingGate(approve: true));

        _runner.Enqueue(0, "# branch.head feature/pager\n# branch.upstream origin/feature/pager\n# branch.ab +0 -0\n");

        ToolObservation<GitPullRequestResult> observation = await tool.CreatePullRequestAsync("Fix the pager");

        observation.Ok.ShouldBeFalse();
        observation.Error!.Code.ShouldBe(ToolErrorCodes.GitUnavailable);
        observation.Error.Hint.ShouldContain("gh auth login");
    }

    private const string CleanBehindTwo =
        "# branch.head main\n# branch.upstream origin/main\n# branch.ab +0 -2\n";

    private const string CleanAheadTwo =
        "# branch.head main\n# branch.upstream origin/main\n# branch.ab +2 -0\n";

    [Fact]
    public async Task Sync_rebases_onto_the_upstream_and_reports_the_movement()
    {
        _runner.Enqueue(0, CleanBehindTwo)
            .Enqueue(0, "aaa111\n")                      // rev-parse before
            .Enqueue(0, "Updating aaa111..bbb222\n")     // pull --rebase
            .Enqueue(0, "bbb222\n");                     // rev-parse after

        ToolObservation<GitSyncResult> observation = await Tool("src").SyncAsync();

        observation.Ok.ShouldBeTrue(observation.Error?.Message);
        _runner.Requests[2].Arguments.ShouldBe(["pull", "--rebase", "origin", "main"]);
        observation.Data!.Updated.ShouldBeTrue();
        observation.Data.BeforeSha.ShouldBe("aaa111");
        observation.Data.AfterSha.ShouldBe("bbb222");
        observation.Summary.ShouldContain("Synced main");
    }

    [Fact]
    public async Task Sync_reports_already_up_to_date()
    {
        _runner.Enqueue(0, CleanBehindTwo)
            .Enqueue(0, "aaa111\n")
            .Enqueue(0, "Already up to date.\n")
            .Enqueue(0, "aaa111\n");

        ToolObservation<GitSyncResult> observation = await Tool("src").SyncAsync();

        observation.Ok.ShouldBeTrue();
        observation.Data!.Updated.ShouldBeFalse();
        observation.Summary.ShouldContain("already up to date");
    }

    [Fact]
    public async Task Sync_refuses_a_dirty_tree()
    {
        _runner.Enqueue(0, CleanBehindTwo + "1 .M N... 100644 100644 100644 e69de29 e69de29 src/Pager.cs\n");

        ToolObservation<GitSyncResult> observation = await Tool("src").SyncAsync();

        observation.Ok.ShouldBeFalse();
        observation.Error!.Code.ShouldBe(ToolErrorCodes.InvalidArgument);
        observation.Error.Hint.ShouldContain("git_commit");
        _runner.Requests.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Sync_needs_an_upstream()
    {
        _runner.Enqueue(0, "# branch.head main\n");

        ToolObservation<GitSyncResult> observation = await Tool("src").SyncAsync();

        observation.Ok.ShouldBeFalse();
        observation.Error!.Code.ShouldBe(ToolErrorCodes.NotFound);
        observation.Error.Hint.ShouldContain("git_push");
    }

    [Fact]
    public async Task A_conflicted_sync_aborts_the_rebase_and_reports_the_files()
    {
        _runner.Enqueue(0, CleanBehindTwo)
            .Enqueue(0, "aaa111\n")
            .Enqueue(1, "CONFLICT (content): Merge conflict in src/Pager.cs\n", "error: could not apply abc123");

        ToolObservation<GitSyncResult> observation = await Tool("src").SyncAsync();

        observation.Ok.ShouldBeFalse();
        observation.Error!.Code.ShouldBe(ToolErrorCodes.MergeConflict);
        observation.Error.Message.ShouldContain("src/Pager.cs");
        observation.Error.Message.ShouldContain("aborted");
        // The tree must never be left mid-rebase: the agent has no tool to resolve one.
        _runner.Requests[3].Arguments.ShouldBe(["rebase", "--abort"]);
    }

    [Fact]
    public async Task Sync_requires_a_writable_workspace()
    {
        ToolObservation<GitSyncResult> observation = await Tool().SyncAsync();

        observation.Ok.ShouldBeFalse();
        observation.Error!.Code.ShouldBe(ToolErrorCodes.PathNotAllowed);
        _runner.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task Push_asks_for_approval_and_a_refusal_blocks_it()
    {
        RecordingGate gate = new(approve: false);
        _runner.Enqueue(0, CleanAheadTwo)
            .Enqueue(0, "abc1234 Fix the pager\ndef5678 Add the pager\n");

        ToolObservation<GitPushResult> observation = await Tool(gate, "src").PushAsync();

        observation.Ok.ShouldBeFalse();
        observation.Error!.Code.ShouldBe(ToolErrorCodes.ApprovalRefused);
        _runner.Requests.ShouldNotContain(r => r.Arguments[0] == "push");
        gate.Action!.Title.ShouldContain("2 commit(s)");
        gate.Action.Detail.ShouldContain("main → origin/main");
        gate.Action.Detail.ShouldContain("abc1234 Fix the pager");
    }

    [Fact]
    public async Task An_approved_push_sends_the_branch_to_the_configured_remote()
    {
        RecordingGate gate = new(approve: true);
        _runner.Enqueue(0, CleanAheadTwo)
            .Enqueue(0, "abc1234 Fix the pager\ndef5678 Add the pager\n")
            .Enqueue(0)                                  // push
            .Enqueue(0, "cafe1234\n");                   // rev-parse

        ToolObservation<GitPushResult> observation = await Tool(gate, "src").PushAsync();

        observation.Ok.ShouldBeTrue(observation.Error?.Message);
        _runner.Requests[2].Arguments.ShouldBe(["push", "origin", "main"]);
        observation.Data!.Branch.ShouldBe("main");
        observation.Data.Remote.ShouldBe("origin");
        observation.Data.CommitsPushed.ShouldBe(2);
        observation.Data.SetUpstream.ShouldBeFalse();
        observation.Data.Sha.ShouldBe("cafe1234");
    }

    [Fact]
    public async Task A_first_push_sets_the_upstream()
    {
        RecordingGate gate = new(approve: true);
        _runner.Enqueue(0, "# branch.head main\n")
            .Enqueue(0, "3\n")                           // rev-list --count HEAD
            .Enqueue(0, "abc one\ndef two\nghi three\n") // log
            .Enqueue(0)                                  // push -u
            .Enqueue(0, "cafe1234\n");

        ToolObservation<GitPushResult> observation = await Tool(gate, "src").PushAsync();

        observation.Ok.ShouldBeTrue(observation.Error?.Message);
        _runner.Requests[3].Arguments.ShouldBe(["push", "-u", "origin", "main"]);
        observation.Data!.SetUpstream.ShouldBeTrue();
        observation.Data.CommitsPushed.ShouldBe(3);
        gate.Action!.Detail[0].ShouldContain("first push");
    }

    [Fact]
    public async Task Push_refuses_a_protected_branch_before_anyone_is_asked()
    {
        _options.ProtectedBranches.Add("main");
        RecordingGate gate = new(approve: true);
        _runner.Enqueue(0, CleanAheadTwo);

        ToolObservation<GitPushResult> observation = await Tool(gate, "src").PushAsync();

        observation.Ok.ShouldBeFalse();
        observation.Error!.Code.ShouldBe(ToolErrorCodes.BranchNotAllowed);
        gate.Action.ShouldBeNull();
        _runner.Requests.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Push_enforces_the_branch_allow_list()
    {
        _options.PushableBranches.Add("feature/pager");
        _runner.Enqueue(0, CleanAheadTwo);

        ToolObservation<GitPushResult> observation = await Tool(new RecordingGate(approve: true), "src").PushAsync();

        observation.Ok.ShouldBeFalse();
        observation.Error!.Code.ShouldBe(ToolErrorCodes.BranchNotAllowed);
    }

    [Fact]
    public async Task Nothing_to_push_fails_cleanly()
    {
        _runner.Enqueue(0, "# branch.head main\n# branch.upstream origin/main\n# branch.ab +0 -0\n");

        ToolObservation<GitPushResult> observation = await Tool(new RecordingGate(approve: true), "src").PushAsync();

        observation.Ok.ShouldBeFalse();
        observation.Error!.Code.ShouldBe(ToolErrorCodes.NotFound);
        observation.Error.Message.ShouldContain("Nothing to push");
    }

    [Fact]
    public async Task Push_fails_closed_without_an_interactive_gate()
    {
        // The default gate is AutoApprovalGate over default options: push approval is required
        // and there is nobody to ask, so a bare GitTool cannot push at all.
        _runner.Enqueue(0, CleanAheadTwo)
            .Enqueue(0, "abc1234 Fix the pager\n");

        ToolObservation<GitPushResult> observation = await Tool("src").PushAsync();

        observation.Ok.ShouldBeFalse();
        observation.Error!.Code.ShouldBe(ToolErrorCodes.ApprovalRefused);
        observation.Error.Message.ShouldContain("no way to ask");
        _runner.Requests.ShouldNotContain(r => r.Arguments[0] == "push");
    }

    [Fact]
    public async Task An_authentication_failure_hints_at_the_host_credential_manager()
    {
        _runner.Enqueue(0, CleanAheadTwo)
            .Enqueue(0, "abc1234 Fix the pager\n")
            .Enqueue(128, "", "fatal: Authentication failed for 'https://github.com/x/y.git/'");

        ToolObservation<GitPushResult> observation = await Tool(new RecordingGate(approve: true), "src").PushAsync();

        observation.Ok.ShouldBeFalse();
        observation.Error!.Hint.ShouldContain("credential");
    }

    [Fact]
    public async Task A_non_fast_forward_rejection_hints_at_sync()
    {
        _runner.Enqueue(0, CleanAheadTwo)
            .Enqueue(0, "abc1234 Fix the pager\n")
            .Enqueue(1, "", "! [rejected]  main -> main (fetch first)");

        ToolObservation<GitPushResult> observation = await Tool(new RecordingGate(approve: true), "src").PushAsync();

        observation.Ok.ShouldBeFalse();
        observation.Error!.Hint.ShouldContain("git_sync");
    }

    [Fact]
    public async Task Push_requires_a_writable_workspace()
    {
        ToolObservation<GitPushResult> observation = await Tool(new RecordingGate(approve: true)).PushAsync();

        observation.Ok.ShouldBeFalse();
        observation.Error!.Code.ShouldBe(ToolErrorCodes.PathNotAllowed);
        _runner.Requests.ShouldBeEmpty();
    }

    /// <summary>A runner whose executable does not exist, as on a machine without git.</summary>
    private sealed class ThrowingProcessRunner : IProcessRunner
    {
        public Task<ProcessRunResult> RunAsync(ProcessRunRequest request, CancellationToken cancellationToken = default) =>
            throw new System.ComponentModel.Win32Exception("The system cannot find the file specified");
    }

    /// <summary>A machine that has git but not gh - the common case for the pull-request tool.</summary>
    private sealed class SelectiveThrowingRunner : IProcessRunner
    {
        private readonly IProcessRunner _inner;
        private readonly string _missing;

        public SelectiveThrowingRunner(IProcessRunner inner, string missing)
        {
            _inner = inner;
            _missing = missing;
        }

        public Task<ProcessRunResult> RunAsync(ProcessRunRequest request, CancellationToken cancellationToken = default) =>
            request.FileName == _missing
                ? throw new System.ComponentModel.Win32Exception("The system cannot find the file specified")
                : _inner.RunAsync(request, cancellationToken);
    }

    /// <summary>An interactive gate that records what it was asked and answers by script.</summary>
    private sealed class RecordingGate : IApprovalGate
    {
        private readonly bool _approve;

        public RecordingGate(bool approve) => _approve = approve;

        public AgentAction? Action { get; private set; }

        public bool IsInteractive => true;

        public Task<ApprovalDecision> RequestAsync(CodeChange change, CancellationToken cancellationToken = default) =>
            Task.FromResult(ApprovalDecision.Approve());

        public Task<ApprovalDecision> RequestActionAsync(AgentAction action, CancellationToken cancellationToken = default)
        {
            Action = action;
            return Task.FromResult(_approve
                ? ApprovalDecision.Approve()
                : ApprovalDecision.Reject("A reviewer declined the push."));
        }
    }
}
