using GlassCoder.TestSupport;
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

    private GitTool Tool(params string[] writablePaths) =>
        new(_runner, _workspace.Guard(writablePaths), TempWorkspace.Wrap(_options));

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

        functions.Select(f => f.Name).ShouldBe(["git_status", "git_commit"]);
    }

    /// <summary>A runner whose executable does not exist, as on a machine without git.</summary>
    private sealed class ThrowingProcessRunner : IProcessRunner
    {
        public Task<ProcessRunResult> RunAsync(ProcessRunRequest request, CancellationToken cancellationToken = default) =>
            throw new System.ComponentModel.Win32Exception("The system cannot find the file specified");
    }
}
