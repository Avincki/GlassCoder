using GlassCoder.TestSupport;
using GlassCoder.Tools.Changes;
using GlassCoder.Tools.FileSystem;
using GlassCoder.Tools.Verification;
using Microsoft.Extensions.Options;

namespace GlassCoder.Tools.Tests;

/// <summary>
/// <c>edit_file</c> taking a list of edits (workplan task 46): one logical change is one call.
/// <para>
/// The shape this replaces was one call per hunk, each preceded by a re-read and followed by its
/// own pre-write compile, and a mid-sequence failure left the tree half-changed with no way to
/// say so. What matters here is what that costs and what it guarantees: atomic per file,
/// deliberately not across files, one verification per file rather than one per hunk, and a
/// partial result that names every file that did not change.
/// </para>
/// </summary>
public sealed class MultiFileEditTests : IDisposable
{
    private readonly TempWorkspace _workspace = new();
    private readonly VerificationOptions _verification = new();
    private readonly ChangeLog _changes = new();

    public void Dispose() => _workspace.Dispose();

    [Fact]
    public async Task One_call_changes_three_files()
    {
        // The case from the workplan: a rename spanning three files, which was three calls.
        _workspace.WriteFile("src/A.cs", "class A { int Size => 1; }\n");
        _workspace.WriteFile("src/B.cs", "class B { int Size => 2; }\n");
        _workspace.WriteFile("src/C.cs", "class C { int Size => 3; }\n");

        ToolObservation<EditFileResult> observation = await Tool().EditFilesAsync(
        [
            new FileEdit("src/A.cs", "Size", "Weight"),
            new FileEdit("src/B.cs", "Size", "Weight"),
            new FileEdit("src/C.cs", "Size", "Weight"),
        ]);

        observation.Ok.ShouldBeTrue(observation.Error?.Message);
        observation.Data!.FilesChanged.ShouldBe(3);
        observation.Data.EditsApplied.ShouldBe(3);
        Read("src/A.cs").ShouldContain("Weight");
        Read("src/C.cs").ShouldContain("Weight");
    }

    [Fact]
    public async Task Several_edits_to_one_file_are_one_change_and_one_verification()
    {
        _workspace.WriteFile("src/Proj.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");
        _workspace.WriteFile("src/Pager.cs", "class Pager\n{\n    int First => 1;\n    int Last => 2;\n}\n");

        ToolObservation<EditFileResult> observation = await Tool().EditFilesAsync(
        [
            new FileEdit("src/Pager.cs", "int First => 1;", "int First => 0;"),
            new FileEdit("src/Pager.cs", "int Last => 2;", "int Last => 3;"),
        ]);

        observation.Ok.ShouldBeTrue(observation.Error?.Message);
        observation.Data!.Files.ShouldHaveSingleItem().Edits.ShouldBe(2);
        Read("src/Pager.cs").ShouldContain("First => 0;");
        Read("src/Pager.cs").ShouldContain("Last => 3;");

        _changes.All().Where(c => c.Status == ChangeStatus.Applied).ShouldHaveSingleItem()
            .Path.ShouldBe("src/Pager.cs", "two hunks in one file are one change, not two");
    }

    [Fact]
    public async Task A_later_edit_sees_what_an_earlier_one_wrote()
    {
        _workspace.WriteFile("src/Chain.cs", "int x = 1;\n");

        ToolObservation<EditFileResult> observation = await Tool().EditFilesAsync(
        [
            new FileEdit("src/Chain.cs", "int x = 1;", "int x = 2;"),
            new FileEdit("src/Chain.cs", "int x = 2;", "int x = 3;"),
        ]);

        observation.Ok.ShouldBeTrue(observation.Error?.Message);
        Read("src/Chain.cs").ShouldContain("int x = 3;");
    }

    [Fact]
    public async Task A_file_whose_second_edit_does_not_match_is_left_untouched()
    {
        // Atomic per file. The first hunk matched and would have landed on its own; it must not,
        // because half a rename in a file is worse than none.
        string before = "class A { int Size => 1; int Other => 2; }\n";
        _workspace.WriteFile("src/A.cs", before);

        ToolObservation<EditFileResult> observation = await Tool().EditFilesAsync(
        [
            new FileEdit("src/A.cs", "int Size => 1;", "int Weight => 1;"),
            new FileEdit("src/A.cs", "int Missing => 9;", "int Gone => 9;"),
        ]);

        observation.Ok.ShouldBeFalse();
        observation.Error!.Code.ShouldBe(ToolErrorCodes.NotFound);
        observation.Error.Message.ShouldContain("Edit 2 of 2", Case.Sensitive);
        Read("src/A.cs").ShouldBe(before);
    }

    [Fact]
    public async Task One_file_failing_does_not_stop_the_others_and_is_named()
    {
        // Cross-file atomicity is deliberately not offered: a partly-applied change that says
        // which files landed is more useful than one that silently undoes correct work.
        _workspace.WriteFile("src/Good.cs", "class Good { int Size => 1; }\n");
        _workspace.WriteFile("src/Bad.cs", "class Bad { }\n");

        ToolObservation<EditFileResult> observation = await Tool().EditFilesAsync(
        [
            new FileEdit("src/Good.cs", "Size", "Weight"),
            new FileEdit("src/Bad.cs", "not in the file", "replacement"),
        ]);

        observation.Ok.ShouldBeTrue("something landed, so this is a partial result rather than a failure");
        observation.Data!.FilesChanged.ShouldBe(1);
        Read("src/Good.cs").ShouldContain("Weight");

        FileEditResult bad = observation.Data.Files.Single(f => f.Path == "src/Bad.cs");
        bad.Applied.ShouldBeFalse();
        bad.Error.ShouldContain("was not found");
    }

    [Fact]
    public async Task A_call_where_nothing_landed_is_a_failure()
    {
        // The loop counts failures. A model repeating an edit whose target is not there has to be
        // able to trip the repeated-failure guard rather than loop to the step limit on "ok".
        _workspace.WriteFile("src/A.cs", "class A { }\n");

        ToolObservation<EditFileResult> observation = await Tool().EditFilesAsync(
            [new FileEdit("src/A.cs", "absent", "present")]);

        observation.Ok.ShouldBeFalse();
        observation.Error!.Code.ShouldBe(ToolErrorCodes.NotFound);
    }

    [Fact]
    public async Task Two_spellings_of_one_path_are_one_file()
    {
        // Grouped on the guard's spelling, not the model's. Keyed on the raw string these were
        // two groups, and the second read the text the first had already replaced.
        _workspace.WriteFile("src/Pager.cs", "int a = 1;\nint b = 2;\n");

        ToolObservation<EditFileResult> observation = await Tool().EditFilesAsync(
        [
            new FileEdit("src/Pager.cs", "int a = 1;", "int a = 0;"),
            new FileEdit("./src/Pager.cs", "int b = 2;", "int b = 0;"),
        ]);

        observation.Ok.ShouldBeTrue(observation.Error?.Message);
        observation.Data!.Files.ShouldHaveSingleItem().Edits.ShouldBe(2);
        Read("src/Pager.cs").ShouldContain("int b = 0;");
    }

    [Fact]
    public async Task An_empty_list_is_refused()
    {
        ToolObservation<EditFileResult> observation = await Tool().EditFilesAsync([]);

        observation.Ok.ShouldBeFalse();
        observation.Error!.Code.ShouldBe(ToolErrorCodes.InvalidArgument);
    }

    [Fact]
    public async Task A_human_refusing_one_file_still_lets_the_others_land()
    {
        // Approval is asked per file, not per batch, because the prompt shows a diff and a
        // reviewer must see what they are approving.
        _workspace.WriteFile("src/Keep.cs", "class Keep { int Size => 1; }\n");
        _workspace.WriteFile("src/Deny.cs", "class Deny { int Size => 1; }\n");

        ToolObservation<EditFileResult> observation = await Tool(new RefusingGate("src/Deny.cs")).EditFilesAsync(
        [
            new FileEdit("src/Keep.cs", "Size", "Weight"),
            new FileEdit("src/Deny.cs", "Size", "Weight"),
        ]);

        observation.Ok.ShouldBeTrue(observation.Error?.Message);
        Read("src/Keep.cs").ShouldContain("Weight");
        Read("src/Deny.cs").ShouldContain("Size");
        observation.Data!.Files.Single(f => f.Path == "src/Deny.cs").Applied.ShouldBeFalse();
    }

    private string Read(string relative) =>
        File.ReadAllText(Path.Combine(_workspace.Root, relative.Replace('/', Path.DirectorySeparatorChar)));

    private EditFileTool Tool(IApprovalGate? approval = null)
    {
        IOptions<VerificationOptions> options = Options.Create(_verification);
        Guardrails.PathGuard guard = _workspace.Guard("src");
        return new EditFileTool(
            guard,
            new RoslynCodeAnalyzer(guard, options),
            new DiagnosticSummarizer(options),
            options,
            _changes,
            approval);
    }

    /// <summary>Says no to one named file and yes to everything else.</summary>
    private sealed class RefusingGate(string path) : IApprovalGate
    {
        public bool IsInteractive => true;

        public Task<ApprovalDecision> RequestAsync(CodeChange change, CancellationToken cancellationToken = default) =>
            Task.FromResult(string.Equals(change.Path, path, StringComparison.Ordinal)
                ? ApprovalDecision.Reject("Not that one.")
                : ApprovalDecision.Approve());

        public Task<ApprovalDecision> RequestActionAsync(AgentAction action, CancellationToken cancellationToken = default) =>
            Task.FromResult(ApprovalDecision.Approve());
    }
}
