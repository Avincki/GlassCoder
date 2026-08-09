using System.Text.Json;
using GlassCoder.TestSupport;
using GlassCoder.Tools;
using GlassCoder.Tools.Changes;
using GlassCoder.Tools.FileSystem;
using GlassCoder.Tools.Guardrails;
using GlassCoder.Tools.Registry;
using GlassCoder.Tools.Verification;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace GlassCoder.Tools.Tests;

/// <summary>
/// Line endings, and the contract between <c>read_file</c> and <c>edit_file</c> (workplan task 45).
/// <para>
/// A run asked to write one function spent 23 of its 30 steps on seventeen consecutive
/// <c>edit_file</c> failures against a seven-line file. The file came from <c>dotnet new</c> and
/// held CRLF; the model emitted LF; the match was ordinal. Nothing the model could do would have
/// fixed it, and the error it was handed - "the text to replace was not found" - sent it back to
/// re-read a file it had already read correctly.
/// </para>
/// </summary>
public sealed class LineEndingTests : IDisposable
{
    private readonly TempWorkspace _workspace = new();

    [Fact]
    public async Task An_edit_written_with_lf_matches_a_file_written_with_crlf()
    {
        // The exact regression. dotnet new writes CRLF on Windows; the model writes \n.
        _workspace.WriteFile("src/Class1.cs", "namespace Lib;\r\n\r\npublic class Class1\r\n{\r\n}\r\n");

        ToolObservation<EditFileResult> observation = await Tool().EditFileAsync(
            "src/Class1.cs",
            "public class Class1\n{\n}",
            "public class Class1\n{\n    public int X => 1;\n}");

        observation.Ok.ShouldBeTrue(observation.Error?.Message);
    }

    [Fact]
    public async Task An_edit_written_with_crlf_matches_a_file_written_with_lf()
    {
        // The same defect in the other direction, which is what a repository written on Linux
        // and edited on Windows would have hit.
        _workspace.WriteFile("src/Class1.cs", "namespace Lib;\n\npublic class Class1\n{\n}\n");

        ToolObservation<EditFileResult> observation = await Tool().EditFileAsync(
            "src/Class1.cs",
            "public class Class1\r\n{\r\n}",
            "public class Class1\r\n{\r\n    public int X => 1;\r\n}");

        observation.Ok.ShouldBeTrue(observation.Error?.Message);
    }

    [Fact]
    public async Task The_file_keeps_the_line_ending_it_already_had()
    {
        // The previous run's one successful edit left a bare \n inside an otherwise-CRLF file.
        // Matching flexibly is only half the fix; writing back consistently is the other half.
        string path = _workspace.WriteFile("src/Class1.cs", "namespace Lib;\r\n\r\npublic class Class1\r\n{\r\n}\r\n");

        await Tool().EditFileAsync(
            "src/Class1.cs",
            "public class Class1\n{\n}",
            "public class Class1\n{\n    public int X => 1;\n}");

        string written = await File.ReadAllTextAsync(path);
        TextFile.DescribeEndings(written).ShouldBe("crlf", "an edit must not leave the file half one and half the other");
    }

    [Fact]
    public async Task A_target_that_appears_twice_is_still_refused_across_line_endings()
    {
        // The ambiguity guard has to survive normalisation, or flexible matching would quietly
        // become "replace the first one you find".
        _workspace.WriteFile("src/Class1.cs", "void A()\r\n{\r\n}\r\nvoid B()\r\n{\r\n}\r\n");

        ToolObservation<EditFileResult> observation = await Tool().EditFileAsync(
            "src/Class1.cs", "{\n}", "{ return; }");

        observation.Ok.ShouldBeFalse();
        observation.Error!.Code.ShouldBe(ToolErrorCodes.AmbiguousTarget);
    }

    [Fact]
    public async Task Text_that_is_genuinely_absent_still_fails_and_says_where()
    {
        // Flexible matching must not become no matching. And the failure now points at the line
        // that differs rather than saying only "not found".
        _workspace.WriteFile("src/Class1.cs", "public class Class1\r\n{\r\n    int X => 1;\r\n}\r\n");

        ToolObservation<EditFileResult> observation = await Tool().EditFileAsync(
            "src/Class1.cs",
            "public class Class1\n{\n        int X => 1;\n}",
            "replacement");

        observation.Ok.ShouldBeFalse();
        observation.Error!.Code.ShouldBe(ToolErrorCodes.NotFound);
        observation.Error.Message.ShouldContain("first 2 line(s) appear");
        observation.Error.Hint.ShouldContain("overwrite: true");

        // A partial match really is a whitespace problem, so this is the one case where reading
        // the file again is the right advice.
        observation.Error.Hint.ShouldContain("Read the file again");
    }

    /// <summary>
    /// The second occurrence of a defect HISTORY parked as "watch rather than fix" (workplan
    /// task 70).
    /// <para>
    /// Runs <c>122e11c6</c> and <c>d5edbc59</c> each spent steps 9-12 on this: an edit whose
    /// target was text the model meant to <em>write</em>, a refusal saying only that it was not
    /// found, a re-read of a file that had been read correctly, and an escape into a whole-file
    /// rewrite - which in the second run is where nine unused using directives came from.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_target_that_is_nowhere_in_the_file_names_the_two_moves_that_work()
    {
        _workspace.WriteFile("src/Class1.cs", "public class Class1\r\n{\r\n    int X => 1;\r\n}\r\n");

        ToolObservation<EditFileResult> observation = await Tool().EditFileAsync(
            "src/Class1.cs",
            "public void Multiply()\n{\n    return a * b;\n}",
            "replacement");

        observation.Ok.ShouldBeFalse();
        observation.Error!.Code.ShouldBe(ToolErrorCodes.NotFound);
        observation.Error.Message.ShouldContain("No line of it appears there");

        observation.Error.Hint.ShouldContain("outline: true");
        observation.Error.Hint.ShouldContain("overwrite: true");

        // And it must not send the model back to re-read: that is the step this fix removes.
        observation.Error.Hint.ShouldNotContain("Read the file again");
    }

    [Fact]
    public async Task A_single_line_target_that_is_absent_gets_the_same_answer()
    {
        // It used to get no diagnosis at all and the whitespace hint - the one explanation a
        // zero match cannot have.
        _workspace.WriteFile("src/Class1.cs", "public class Class1\r\n{\r\n    int X => 1;\r\n}\r\n");

        ToolObservation<EditFileResult> observation = await Tool().EditFileAsync(
            "src/Class1.cs", "int Y => 2;", "int Y => 3;");

        observation.Ok.ShouldBeFalse();
        observation.Error!.Message.ShouldContain("No line of it appears there");
        observation.Error.Hint.ShouldContain("outline: true");
    }

    /// <summary>
    /// Every line present, the block absent. This threw <see cref="IndexOutOfRangeException"/>
    /// before task 70 - the diagnosis reached past the end of its own array - and any target
    /// ending in a newline whose lines all appear somewhere would trigger it.
    /// </summary>
    [Fact]
    public async Task Lines_that_all_appear_but_never_together_say_so_instead_of_throwing()
    {
        _workspace.WriteFile("src/Class1.cs", "int A => 1;\r\nint MIDDLE => 0;\r\nint B => 2;\r\n");

        ToolObservation<EditFileResult> observation = await Tool().EditFileAsync(
            "src/Class1.cs", "int A => 1;\nint B => 2;\n", "replacement");

        observation.Ok.ShouldBeFalse();
        observation.Error!.Code.ShouldBe(ToolErrorCodes.NotFound);
        observation.Error.Message.ShouldContain("not consecutively");
        observation.Error.Hint.ShouldContain("contiguous");
    }

    [Fact]
    public void A_re_read_of_an_unchanged_file_announces_itself_as_one()
    {
        // Not a refusal - the read returns exactly what it would have returned. It just lets the
        // model tell "I have new information" from "I have the same information twice".
        _workspace.WriteFile("src/Class1.cs", "public class Class1 { }");

        ReadFileTool tool = new(
            _workspace.Guard("src"), Options.Create(new ToolsOptions()), new FileReadMemo());

        tool.ReadFile("src/Class1.cs").Summary.ShouldNotContain("has not changed");
        tool.ReadFile("src/Class1.cs").Summary.ShouldContain("has not changed since you last read it");

        // A write between reads makes the next read new again, with nothing having to invalidate.
        _workspace.WriteFile("src/Class1.cs", "public class Class1 { int X => 1; }");
        tool.ReadFile("src/Class1.cs").Summary.ShouldNotContain("has not changed");
    }

    [Fact]
    public void Read_file_returns_what_the_file_holds_and_says_what_that_is()
    {
        _workspace.WriteFile("src/Crlf.cs", "one\r\ntwo\r\n");
        _workspace.WriteFile("src/Lf.cs", "one\ntwo\n");

        ReadFileTool tool = new(_workspace.Guard("src"), Options.Create(new ToolsOptions()));

        ReadFileResult crlf = tool.ReadFile("src/Crlf.cs").Data!;
        ReadFileResult lf = tool.ReadFile("src/Lf.cs").Data!;

        crlf.LineEndings.ShouldBe("crlf");
        crlf.Content.ShouldBe("one\r\ntwo");
        crlf.TotalLines.ShouldBe(2, "a trailing newline terminates the last line rather than starting another");

        // Previously both came back joined with Environment.NewLine, so this file was shown to
        // the model as something it is not.
        lf.LineEndings.ShouldBe("lf");
        lf.Content.ShouldBe("one\ntwo");
    }

    [Fact]
    public void Read_file_flags_lines_it_had_to_clip()
    {
        // A clipped line is the one thing read_file returns that cannot be quoted back, so it
        // has to be called out rather than left to fail later as a mystery.
        _workspace.WriteFile("src/Long.cs", new string('x', 300) + "\nshort\n");
        ToolsOptions options = new() { MaxLineLength = 100 };

        ToolObservation<ReadFileResult> observation =
            new ReadFileTool(_workspace.Guard("src"), Options.Create(options)).ReadFile("src/Long.cs");

        observation.Data!.ClippedLines.ShouldBe(1);
        observation.Summary.ShouldContain("do not quote those to edit_file");
    }

    private EditFileTool Tool()
    {
        IOptions<VerificationOptions> options = Options.Create(new VerificationOptions());
        IPathGuard guard = _workspace.Guard("src");
        return new EditFileTool(guard, new RoslynCodeAnalyzer(guard, options), new DiagnosticSummarizer(options), options);
    }

    public void Dispose() => _workspace.Dispose();
}

/// <summary>
/// Replacing a file wholesale (workplan task 45).
/// <para>
/// <c>create_file</c> refused to overwrite and <c>edit_file</c> needed an exact match, so
/// "replace this generated stub with my implementation" had no working path at all - which is
/// what forced the run into the edit loop it never escaped.
/// </para>
/// </summary>
public sealed class OverwriteTests : IDisposable
{
    private readonly TempWorkspace _workspace = new();

    [Fact]
    public async Task Overwriting_is_refused_unless_it_was_asked_for()
    {
        _workspace.WriteFile("src/Class1.cs", "public class Class1 { }\n");

        ToolObservation<CreateFileResult> observation = await Tool()
            .CreateFileAsync("src/Class1.cs", "public class Class1 { public int X => 1; }\n");

        observation.Ok.ShouldBeFalse();
        observation.Error!.Code.ShouldBe(ToolErrorCodes.AlreadyExists);
        observation.Error.Hint.ShouldContain("overwrite: true");
    }

    [Fact]
    public async Task Overwriting_replaces_the_file_and_records_the_diff()
    {
        string path = _workspace.WriteFile("src/Class1.cs", "public class Class1 { }\n");
        ChangeLog changes = new();

        ToolObservation<CreateFileResult> observation = await Tool(changes)
            .CreateFileAsync("src/Class1.cs", "public class Class1 { public int X => 1; }\n", overwrite: true);

        observation.Ok.ShouldBeTrue(observation.Error?.Message);
        (await File.ReadAllTextAsync(path)).ShouldContain("public int X => 1;");

        // The Changes surface has to show a replacement as the diff it is, not as an addition.
        CodeChange change = changes.All().Single();
        change.Status.ShouldBe(ChangeStatus.Applied);
        change.BeforeText.ShouldBe("public class Class1 { }\n");
    }

    [Fact]
    public async Task An_overwrite_keeps_the_line_ending_the_file_had()
    {
        string path = _workspace.WriteFile("src/Class1.cs", "public class Class1\r\n{\r\n}\r\n");

        await Tool().CreateFileAsync("src/Class1.cs", "public class Class1\n{\n    int X => 1;\n}\n", overwrite: true);

        TextFile.DescribeEndings(await File.ReadAllTextAsync(path)).ShouldBe("crlf");
    }

    private CreateFileTool Tool(ChangeLog? changes = null)
    {
        IOptions<VerificationOptions> options = Options.Create(new VerificationOptions());
        IPathGuard guard = _workspace.Guard("src");
        return new CreateFileTool(
            guard,
            new RoslynCodeAnalyzer(guard, options),
            new DiagnosticSummarizer(options),
            options,
            changes ?? new ChangeLog());
    }

    public void Dispose() => _workspace.Dispose();
}

/// <summary>
/// What the transcript records about a tool call (workplan task 45).
/// </summary>
public sealed class ToolArgumentLoggingTests
{
    [Fact]
    public async Task The_arguments_the_model_sent_are_recorded_as_values()
    {
        // They arrive as JsonElement, whose public surface is its kind rather than its content -
        // so the log used to read {"ValueKind":"String"} and the actual argument was gone. Two
        // diagnoses of a failing run had to infer an argument from bytes on disk because of it.
        ToolRegistry registry = new([new EchoTools()]);

        ToolInvocation invocation = await registry.InvokeAsync(new FunctionCallContent(
            "call-1",
            "echo",
            new Dictionary<string, object?>
            {
                ["text"] = JsonSerializer.Deserialize<JsonElement>("\"line one\\nline two\""),
                ["count"] = JsonSerializer.Deserialize<JsonElement>("3"),
                ["loud"] = JsonSerializer.Deserialize<JsonElement>("true"),
            }));

        invocation.Arguments.ShouldNotBeNull();
        invocation.Arguments["text"].ShouldBe("line one\nline two");
        invocation.Arguments["count"].ShouldBe(3L);
        invocation.Arguments["loud"].ShouldBe(true);
    }

    [Fact]
    public async Task A_call_that_ran_fine_still_reports_what_it_concluded()
    {
        // The ambiguity this closes: Status says whether the *call* ran. A build that compiled
        // nothing returns ok:true - a failed build is a handled outcome, not a tool fault - and
        // logged as "build:Succeeded", which reads to every human as a claim about the build.
        // Nearly missed a real MSB1003 that way.
        ToolRegistry registry = new([new EchoTools()]);

        ToolInvocation invocation = await registry.InvokeAsync(new FunctionCallContent(
            "call-1", "echo", new Dictionary<string, object?> { ["text"] = "hello" }));

        invocation.Status.ShouldBe(ToolCallStatus.Succeeded);
        invocation.ErrorMessage.ShouldBeNull();
        invocation.Summary.ShouldBe("echoed hello");
    }

    [Fact]
    public async Task A_failed_call_carries_how_it_failed()
    {
        // The loop needs this to tell one failure from the same failure again.
        ToolRegistry registry = new([new EchoTools()]);

        ToolInvocation invocation = await registry.InvokeAsync(new FunctionCallContent(
            "call-1", "echo", new Dictionary<string, object?> { ["text"] = "fail" }));

        invocation.Status.ShouldBe(ToolCallStatus.Failed);
        invocation.ErrorMessage.ShouldNotBeNull();
        invocation.ErrorMessage.ShouldContain("invalid_argument");
        invocation.ErrorMessage.ShouldContain("asked to fail");
    }

    private sealed class EchoTools : IToolSet
    {
        [GlassCoderTool("echo")]
        [System.ComponentModel.Description("Echoes its arguments, for tests.")]
        public ToolObservation<string> Echo(
            [System.ComponentModel.Description("What to echo.")] string text,
            [System.ComponentModel.Description("How many times.")] int count = 1,
            [System.ComponentModel.Description("Whether to shout.")] bool loud = false) =>
            text == "fail"
                ? Observation.Fail<string>("echo", ToolErrorCodes.InvalidArgument, "The tool was asked to fail.")
                : Observation.Ok("echo", text, $"echoed {text}");
    }
}
