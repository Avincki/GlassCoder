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
        observation.Data!.StartLine.ShouldBe(3);
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
        observation.Data!.Verified.ShouldBeFalse();
        File.ReadAllText(path).ShouldContain("=> ;");
    }

    [Fact]
    public async Task A_non_csharp_file_is_edited_without_a_compile_check()
    {
        string path = _workspace.WriteFile("src/notes.md", "# Notes\nold line\n");

        ToolObservation<EditFileResult> observation = await Tool().EditFileAsync("src/notes.md", "old line", "new line");

        observation.Ok.ShouldBeTrue(observation.Error?.Message);
        observation.Data!.Verified.ShouldBeFalse();
        File.ReadAllText(path).ShouldContain("new line");
    }
}
