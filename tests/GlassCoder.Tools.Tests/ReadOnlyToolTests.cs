using GlassCoder.TestSupport;
using GlassCoder.Tools.FileSystem;
using GlassCoder.Tools.Guardrails;

namespace GlassCoder.Tools.Tests;

/// <summary>
/// The Phase 0 tool set (workplan task 9). Every tool is checked on both paths: a well-formed
/// observation on success, and a well-formed observation on failure - never an exception.
/// </summary>
public sealed class ReadOnlyToolTests : IDisposable
{
    private readonly TempWorkspace _workspace = new();
    private readonly ToolsOptions _options = new();

    public void Dispose() => _workspace.Dispose();

    private ReadFileTool ReadFileTool() => new(_workspace.Guard(), TempWorkspace.Wrap(_options));

    private GrepTool GrepTool() => new(_workspace.Guard(), TempWorkspace.Wrap(_options));

    private GlobTool GlobTool() => new(_workspace.Guard(), TempWorkspace.Wrap(_options));

    [Fact]
    public void Read_file_returns_content_with_line_numbers()
    {
        _workspace.WriteFile("src/Program.cs", "line one\nline two\nline three\n");

        ToolObservation<ReadFileResult> observation = ReadFileTool().ReadFile("src/Program.cs");

        observation.Ok.ShouldBeTrue();
        observation.Data!.TotalLines.ShouldBe(3);
        observation.Data.StartLine.ShouldBe(1);
        observation.Data.EndLine.ShouldBe(3);
        observation.Data.Truncated.ShouldBeFalse();
        observation.Data.Content.ShouldContain("line two");
    }

    [Fact]
    public void Read_file_honours_the_requested_window_and_reports_truncation()
    {
        _workspace.WriteFile("src/Big.cs", string.Join('\n', Enumerable.Range(1, 100).Select(i => $"line {i}")));

        ToolObservation<ReadFileResult> observation = ReadFileTool().ReadFile("src/Big.cs", startLine: 10, maxLines: 5);

        observation.Ok.ShouldBeTrue();
        observation.Data!.StartLine.ShouldBe(10);
        observation.Data.EndLine.ShouldBe(14);
        observation.Data.Truncated.ShouldBeTrue();
        observation.Data.Content.ShouldStartWith("line 10");
    }

    [Fact]
    public void Read_file_reports_a_missing_file_as_an_observation()
    {
        ToolObservation<ReadFileResult> observation = ReadFileTool().ReadFile("src/Nope.cs");

        observation.Ok.ShouldBeFalse();
        observation.Error!.Code.ShouldBe(ToolErrorCodes.NotFound);
        observation.Data.ShouldBeNull();
    }

    [Fact]
    public void Read_file_rejects_a_path_outside_the_workspace()
    {
        ToolObservation<ReadFileResult> observation = ReadFileTool().ReadFile("../outside.txt");

        observation.Ok.ShouldBeFalse();
        observation.Error!.Code.ShouldBe(ToolErrorCodes.PathNotAllowed);
    }

    [Fact]
    public void Read_file_refuses_a_file_over_the_size_limit()
    {
        _workspace.WriteFile("src/Huge.cs", new string('x', 4096));
        _options.MaxFileBytes = 1024;

        ToolObservation<ReadFileResult> observation = ReadFileTool().ReadFile("src/Huge.cs");

        observation.Ok.ShouldBeFalse();
        observation.Error!.Code.ShouldBe(ToolErrorCodes.Unreadable);
        observation.Error.Hint.ShouldContain("grep");
    }

    [Fact]
    public void Grep_finds_matches_with_file_and_line()
    {
        _workspace.WriteFile("src/A.cs", "class Alpha\n{\n}\n");
        _workspace.WriteFile("src/B.cs", "class Beta\n{\n}\n");

        ToolObservation<GrepResult> observation = GrepTool().Grep(@"class\s+\w+", glob: "**/*.cs");

        observation.Ok.ShouldBeTrue();
        observation.Data!.Matches.Count.ShouldBe(2);
        observation.Data.Matches.ShouldAllBe(m => m.Line == 1);
        observation.Data.Matches.Select(m => m.Path).Order().ShouldBe(["src/A.cs", "src/B.cs"]);
    }

    [Fact]
    public void Grep_reports_an_invalid_pattern_as_an_observation()
    {
        ToolObservation<GrepResult> observation = GrepTool().Grep("class(");

        observation.Ok.ShouldBeFalse();
        observation.Error!.Code.ShouldBe(ToolErrorCodes.InvalidArgument);
    }

    [Fact]
    public void Grep_caps_its_results_and_says_so()
    {
        _workspace.WriteFile("src/Many.cs", string.Join('\n', Enumerable.Repeat("match", 50)));

        ToolObservation<GrepResult> observation = GrepTool().Grep("match", maxResults: 5);

        observation.Ok.ShouldBeTrue();
        observation.Data!.Matches.Count.ShouldBe(5);
        observation.Data.Truncated.ShouldBeTrue();
        observation.Summary.ShouldContain("capped");
    }

    [Fact]
    public void Grep_skips_denied_directories()
    {
        _workspace.WriteFile("src/A.cs", "needle");
        _workspace.WriteFile("src/obj/Generated.cs", "needle");
        _workspace.WriteFile(".git/config", "needle");

        ToolObservation<GrepResult> observation = GrepTool().Grep("needle");

        observation.Data!.Matches.Select(m => m.Path).ShouldBe(["src/A.cs"]);
    }

    [Fact]
    public void Glob_lists_matching_paths_sorted()
    {
        _workspace.WriteFile("src/B.cs", "");
        _workspace.WriteFile("src/A.cs", "");
        _workspace.WriteFile("docs/readme.md", "");

        ToolObservation<GlobResult> observation = GlobTool().Glob("**/*.cs");

        observation.Ok.ShouldBeTrue();
        observation.Data!.Paths.ShouldBe(["src/A.cs", "src/B.cs"]);
        observation.Data.Truncated.ShouldBeFalse();
    }

    [Fact]
    public void Glob_rejects_a_directory_outside_the_workspace()
    {
        ToolObservation<GlobResult> observation = GlobTool().Glob("**/*", path: "..");

        observation.Ok.ShouldBeFalse();
        observation.Error!.Code.ShouldBe(ToolErrorCodes.PathNotAllowed);
    }

    [Fact]
    public void Glob_reports_an_empty_pattern_as_an_observation()
    {
        ToolObservation<GlobResult> observation = GlobTool().Glob("  ");

        observation.Ok.ShouldBeFalse();
        observation.Error!.Code.ShouldBe(ToolErrorCodes.InvalidArgument);
    }

    [Fact]
    public void All_three_tools_share_the_guardrail()
    {
        // A tool that reaches the filesystem without asking the guard first is a defect; this
        // is the cheap check that none of them do.
        IPathGuard guard = _workspace.Guard();
        _workspace.WriteFile("src/A.cs", "needle");

        ReadFileTool read = new(guard, TempWorkspace.Wrap(_options));
        GrepTool grep = new(guard, TempWorkspace.Wrap(_options));
        GlobTool glob = new(guard, TempWorkspace.Wrap(_options));

        read.ReadFile("/etc/passwd").Error!.Code.ShouldBe(ToolErrorCodes.PathNotAllowed);
        grep.Grep("root", path: "/etc").Error!.Code.ShouldBe(ToolErrorCodes.PathNotAllowed);
        glob.Glob("**/*", path: "/etc").Error!.Code.ShouldBe(ToolErrorCodes.PathNotAllowed);
    }
}
