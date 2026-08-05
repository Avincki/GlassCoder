using GlassCoder.TestSupport;
using GlassCoder.Tools.FileSystem;
using GlassCoder.Tools.Verification;
using Microsoft.Extensions.Options;

namespace GlassCoder.Tools.Tests;

/// <summary>
/// <c>edit_file</c> (workplan task 16): exact and unique or nothing, inside the allow-list or
/// nothing, and compile-checked before anything reaches disk.
/// </summary>
public sealed class EditFileToolTests : IDisposable
{
    private readonly TempWorkspace _workspace = new();
    private readonly VerificationOptions _verification = new();

    public void Dispose() => _workspace.Dispose();

    private EditFileTool Tool()
    {
        IOptions<VerificationOptions> options = Options.Create(_verification);
        Guardrails.PathGuard guard = _workspace.Guard("src");
        return new EditFileTool(guard, new RoslynCodeAnalyzer(guard, options), new DiagnosticSummarizer(options), options);
    }

    [Fact]
    public async Task An_exact_unique_target_is_replaced()
    {
        string path = _workspace.WriteFile("src/Pager.cs", "class Pager\n{\n    int Last => Count - 1;\n}\n");

        ToolObservation<EditFileResult> observation = await Tool()
            .EditFileAsync("src/Pager.cs", "Count - 1", "Count");

        observation.Ok.ShouldBeTrue(observation.Error?.Message);
        File.ReadAllText(path).ShouldContain("int Last => Count;");
        observation.Data!.Files[0].StartLine.ShouldBe(3);
    }

    [Fact]
    public async Task An_absent_target_errors_and_writes_nothing()
    {
        string path = _workspace.WriteFile("src/Pager.cs", "class Pager { }\n");
        string before = File.ReadAllText(path);

        ToolObservation<EditFileResult> observation = await Tool()
            .EditFileAsync("src/Pager.cs", "not in the file", "replacement");

        observation.Ok.ShouldBeFalse();
        observation.Error!.Code.ShouldBe(ToolErrorCodes.NotFound);
        File.ReadAllText(path).ShouldBe(before);
    }

    [Fact]
    public async Task An_ambiguous_target_errors_and_writes_nothing()
    {
        // The dangerous case: an edit that could land in two places would land in the wrong one
        // silently, and the loop would never know.
        string path = _workspace.WriteFile("src/Pager.cs", "int a = 1;\nint b = 1;\n");
        string before = File.ReadAllText(path);

        ToolObservation<EditFileResult> observation = await Tool().EditFileAsync("src/Pager.cs", "= 1;", "= 2;");

        observation.Ok.ShouldBeFalse();
        observation.Error!.Code.ShouldBe(ToolErrorCodes.AmbiguousTarget);
        observation.Error.Message.ShouldContain("2 times");
        observation.Error.Hint.ShouldContain("more surrounding context");
        File.ReadAllText(path).ShouldBe(before);
    }

    [Fact]
    public async Task Replace_all_changes_every_occurrence_in_one_call()
    {
        // The run this is for: five byte-identical call sites, five separate steps, each quoting
        // a whole method to satisfy the uniqueness rule. Asked for explicitly, they are one call.
        string path = _workspace.WriteFile("src/Calls.cs",
            "class Calls\n{\n    int A() => Size * 2;\n    int B() => Size * 2;\n    int C() => Size * 2;\n    int Size => 4;\n}\n");

        ToolObservation<EditFileResult> observation = await Tool()
            .EditFileAsync("src/Calls.cs", "Size * 2", "Size * 3", replaceAll: true);

        observation.Ok.ShouldBeTrue(observation.Error?.Message);
        observation.Data!.EditsApplied.ShouldBe(3);
        observation.Data.FilesChanged.ShouldBe(1);

        string after = File.ReadAllText(path);
        TextFile.CountOccurrences(after, "Size * 3").ShouldBe(3);
        after.ShouldNotContain("Size * 2");
    }

    [Fact]
    public async Task Replace_all_matches_across_line_endings_and_preserves_the_files_own()
    {
        string path = _workspace.WriteFile("src/Crlf.cs",
            "class Crlf\r\n{\r\n    int A => 1;\r\n    int B => 1;\r\n}\r\n");

        // The needle spans a line break and quotes \n; the file holds \r\n.
        ToolObservation<EditFileResult> observation = await Tool()
            .EditFileAsync("src/Crlf.cs", "=> 1;\n", "=> 2;\n", replaceAll: true);

        observation.Ok.ShouldBeTrue(observation.Error?.Message);
        observation.Data!.EditsApplied.ShouldBe(2);

        string after = File.ReadAllText(path);
        TextFile.CountOccurrences(after, "=> 2;").ShouldBe(2);
        TextFile.DescribeEndings(after).ShouldBe("crlf");
    }

    [Fact]
    public async Task Replace_all_with_an_absent_target_errors_and_writes_nothing()
    {
        string path = _workspace.WriteFile("src/Pager.cs", "class Pager { }\n");
        string before = File.ReadAllText(path);

        ToolObservation<EditFileResult> observation = await Tool()
            .EditFileAsync("src/Pager.cs", "not in the file", "replacement", replaceAll: true);

        observation.Ok.ShouldBeFalse();
        observation.Error!.Code.ShouldBe(ToolErrorCodes.NotFound);
        File.ReadAllText(path).ShouldBe(before);
    }

    [Fact]
    public async Task An_ambiguous_target_points_at_replace_all()
    {
        // The refusal is the moment the model learns the flag exists - the information has to be
        // in the message it is already reading.
        _workspace.WriteFile("src/Pager.cs", "int a = 1;\nint b = 1;\n");

        ToolObservation<EditFileResult> observation = await Tool().EditFileAsync("src/Pager.cs", "= 1;", "= 2;");

        observation.Ok.ShouldBeFalse();
        observation.Error!.Hint.ShouldContain("replaceAll");
    }

    [Fact]
    public async Task A_replace_all_edit_composes_with_a_unique_edit_in_one_call()
    {
        string path = _workspace.WriteFile("src/Mix.cs",
            "class Mix\n{\n    int A => 1;\n    int B => 1;\n    int Name => 9;\n}\n");

        ToolObservation<EditFileResult> observation = await Tool().EditFilesAsync(
        [
            new FileEdit("src/Mix.cs", "=> 1;", "=> 2;", ReplaceAll: true),
            new FileEdit("src/Mix.cs", "Name => 9", "Title => 9"),
        ]);

        observation.Ok.ShouldBeTrue(observation.Error?.Message);
        observation.Data!.EditsApplied.ShouldBe(3);

        string after = File.ReadAllText(path);
        TextFile.CountOccurrences(after, "=> 2;").ShouldBe(2);
        after.ShouldContain("Title => 9");
    }

    [Fact]
    public async Task A_top_level_replace_all_fills_in_for_edits_that_omit_it()
    {
        // Same bargain as the top-level path: when the intent arrived at the wrong level,
        // refusing it is a technicality.
        string path = _workspace.WriteFile("src/Fill.cs",
            "class Fill\n{\n    int A => 1;\n    int B => 1;\n}\n");

        ToolObservation<EditFileResult> observation = await Tool().EditFileAsync(
            path: "src/Fill.cs",
            replaceAll: true,
            edits: [new FileEdit(string.Empty, "=> 1;", "=> 3;")]);

        observation.Ok.ShouldBeTrue(observation.Error?.Message);
        observation.Data!.EditsApplied.ShouldBe(2);
        TextFile.CountOccurrences(File.ReadAllText(path), "=> 3;").ShouldBe(2);
    }

    [Fact]
    public async Task A_path_outside_the_writable_set_is_rejected()
    {
        _workspace.WriteFile("docs/README.md", "# docs");

        ToolObservation<EditFileResult> observation = await Tool().EditFileAsync("docs/README.md", "docs", "changed");

        observation.Ok.ShouldBeFalse();
        observation.Error!.Code.ShouldBe(ToolErrorCodes.PathNotAllowed);
        File.ReadAllText(Path.Combine(_workspace.Root, "docs", "README.md")).ShouldBe("# docs");
    }

    [Fact]
    public async Task A_missing_file_errors()
    {
        ToolObservation<EditFileResult> observation = await Tool().EditFileAsync("src/Nope.cs", "a", "b");

        observation.Ok.ShouldBeFalse();
        observation.Error!.Code.ShouldBe(ToolErrorCodes.NotFound);
    }

    [Fact]
    public async Task A_no_op_edit_is_refused()
    {
        _workspace.WriteFile("src/Pager.cs", "class Pager { }\n");

        ToolObservation<EditFileResult> observation = await Tool()
            .EditFileAsync("src/Pager.cs", "class Pager", "class Pager");

        observation.Ok.ShouldBeFalse();
        observation.Error!.Code.ShouldBe(ToolErrorCodes.InvalidArgument);
    }

    [Fact]
    public async Task An_edit_that_breaks_the_syntax_is_refused_before_it_is_written()
    {
        string path = _workspace.WriteFile("src/Proj.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");
        path = _workspace.WriteFile("src/Pager.cs", "public class Pager\n{\n    public int Last => 1;\n}\n");
        string before = File.ReadAllText(path);

        ToolObservation<EditFileResult> observation = await Tool()
            .EditFileAsync("src/Pager.cs", "public int Last => 1;", "public int Last => ;");

        observation.Ok.ShouldBeFalse();
        observation.Error!.Code.ShouldBe(ToolErrorCodes.VerificationFailed);
        File.ReadAllText(path).ShouldBe(before, "nothing may reach disk when a rung refuses the edit");
    }

    [Fact]
    public async Task An_edit_that_introduces_a_compile_error_is_refused()
    {
        _workspace.WriteFile("src/Proj.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");
        _workspace.WriteFile("src/Widget.cs", "namespace Demo; public sealed class Widget { public int Size => 1; }");
        string caller = _workspace.WriteFile(
            "src/Caller.cs",
            "namespace Demo; public sealed class Caller { public int Use(Widget w) => w.Size; }");
        string before = File.ReadAllText(caller);

        ToolObservation<EditFileResult> observation = await Tool()
            .EditFileAsync("src/Caller.cs", "w.Size", "w.Weight");

        observation.Ok.ShouldBeFalse();
        observation.Error!.Code.ShouldBe(ToolErrorCodes.VerificationFailed);
        observation.Error.Message.ShouldContain("CS1061");
        File.ReadAllText(caller).ShouldBe(before);
    }

    [Fact]
    public async Task Pre_existing_errors_never_block_an_edit()
    {
        // The agent is usually editing precisely because the project is broken. Refusing to let
        // it start would be a deadlock.
        _workspace.WriteFile("src/Proj.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");
        _workspace.WriteFile("src/Broken.cs", "namespace Demo; public sealed class Broken { public int X => Missing.Value; }");
        string other = _workspace.WriteFile(
            "src/Other.cs",
            "namespace Demo; public sealed class Other { public int Y => 1; }");

        ToolObservation<EditFileResult> observation = await Tool().EditFileAsync("src/Other.cs", "=> 1;", "=> 2;");

        observation.Ok.ShouldBeTrue(observation.Error?.Message);
        File.ReadAllText(other).ShouldContain("=> 2;");
    }

    [Fact]
    public async Task Verification_can_be_switched_off()
    {
        _workspace.WriteFile("src/Proj.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");
        string path = _workspace.WriteFile("src/Pager.cs", "public class Pager { public int Last => 1; }");
        _verification.VerifyEditsBeforeWrite = false;

        ToolObservation<EditFileResult> observation = await Tool()
            .EditFileAsync("src/Pager.cs", "public int Last => 1;", "public int Last => ;");

        observation.Ok.ShouldBeTrue();
        observation.Data!.Files[0].Verified.ShouldBeFalse();
        File.ReadAllText(path).ShouldContain("=> ;");
    }

    [Fact]
    public async Task A_non_csharp_file_is_edited_without_a_compile_check()
    {
        string path = _workspace.WriteFile("src/notes.md", "# Notes\nold line\n");

        ToolObservation<EditFileResult> observation = await Tool().EditFileAsync("src/notes.md", "old line", "new line");

        observation.Ok.ShouldBeTrue(observation.Error?.Message);
        observation.Data!.Files[0].Verified.ShouldBeFalse();
        File.ReadAllText(path).ShouldContain("new line");
    }
}
