using GlassCoder.TestSupport;
using GlassCoder.Tools.Changes;
using GlassCoder.Tools.FileSystem;
using GlassCoder.Tools.Verification;
using Microsoft.Extensions.Options;

namespace GlassCoder.Tools.Tests;

/// <summary>
/// <c>create_file</c>: the only route a new file has into the workspace, and the guarantees it
/// must not weaken on the way - never overwrite, always through the guard, the change log, the
/// pre-write check and the approval gate.
/// </summary>
public sealed class CreateFileToolTests : IDisposable
{
    private const string Project = "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>";

    private const string ImplicitUsingsProject =
        "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><ImplicitUsings>enable</ImplicitUsings></PropertyGroup></Project>";

    private readonly TempWorkspace _workspace = new();
    private readonly VerificationOptions _verification = new();
    private readonly ChangeLog _changes = new();

    public void Dispose() => _workspace.Dispose();

    private CreateFileTool Tool(IApprovalGate? approval = null)
    {
        IOptions<VerificationOptions> options = Options.Create(_verification);
        Guardrails.PathGuard guard = _workspace.Guard("src");
        return new CreateFileTool(
            guard,
            new RoslynCodeAnalyzer(guard, options),
            new DiagnosticSummarizer(options),
            options,
            _changes,
            approval);
    }

    [Fact]
    public async Task A_new_file_is_written_with_its_full_contents()
    {
        ToolObservation<CreateFileResult> observation = await Tool()
            .CreateFileAsync("src/Notes.md", "# Notes\nfirst line\n");

        observation.Ok.ShouldBeTrue(observation.Error?.Message);
        File.ReadAllText(Path.Combine(_workspace.Root, "src", "Notes.md")).ShouldBe("# Notes\nfirst line\n");
        observation.Data!.Path.ShouldBe("src/Notes.md");
        observation.Summary.ShouldContain("Created src/Notes.md");
    }

    [Fact]
    public async Task Missing_parent_directories_are_created()
    {
        ToolObservation<CreateFileResult> observation = await Tool()
            .CreateFileAsync("src/deep/nested/Thing.cs", "namespace Demo; public sealed class Thing { }");

        observation.Ok.ShouldBeTrue(observation.Error?.Message);
        File.Exists(Path.Combine(_workspace.Root, "src", "deep", "nested", "Thing.cs")).ShouldBeTrue();
    }

    [Fact]
    public async Task An_existing_file_is_never_overwritten()
    {
        // Creation and modification stay separate verbs: an upserting create tool would be a hole
        // straight through edit_file's exact-and-unique guarantee.
        string path = _workspace.WriteFile("src/Thing.cs", "namespace Demo; public sealed class Thing { }");

        ToolObservation<CreateFileResult> observation = await Tool()
            .CreateFileAsync("src/Thing.cs", "namespace Demo; public sealed class Thing { public int N => 1; }");

        observation.Ok.ShouldBeFalse();
        observation.Error!.Code.ShouldBe(ToolErrorCodes.AlreadyExists);
        observation.Error.Hint.ShouldContain("edit_file");
        File.ReadAllText(path).ShouldBe("namespace Demo; public sealed class Thing { }");
    }

    [Fact]
    public async Task A_directory_at_the_path_is_reported_rather_than_written_through()
    {
        _workspace.CreateDirectory("src/Thing.cs");

        ToolObservation<CreateFileResult> observation = await Tool().CreateFileAsync("src/Thing.cs", "text");

        observation.Ok.ShouldBeFalse();
        observation.Error!.Code.ShouldBe(ToolErrorCodes.AlreadyExists);
    }

    [Fact]
    public async Task A_path_outside_the_writable_set_is_rejected()
    {
        ToolObservation<CreateFileResult> observation = await Tool().CreateFileAsync("docs/Sneaky.cs", "class X { }");

        observation.Ok.ShouldBeFalse();
        observation.Error!.Code.ShouldBe(ToolErrorCodes.PathNotAllowed);
        File.Exists(Path.Combine(_workspace.Root, "docs", "Sneaky.cs")).ShouldBeFalse();
    }

    [Fact]
    public async Task Traversal_out_of_the_writable_set_is_rejected()
    {
        ToolObservation<CreateFileResult> observation = await Tool()
            .CreateFileAsync("src/../escaped/Sneaky.cs", "class X { }");

        observation.Ok.ShouldBeFalse();
        observation.Error!.Code.ShouldBe(ToolErrorCodes.PathNotAllowed);
        Directory.Exists(Path.Combine(_workspace.Root, "escaped")).ShouldBeFalse(
            "a refused path must not leave a directory behind either");
    }

    [Fact]
    public async Task Content_that_will_not_parse_is_refused_before_it_is_written()
    {
        _workspace.WriteFile("src/Proj.csproj", Project);

        ToolObservation<CreateFileResult> observation = await Tool()
            .CreateFileAsync("src/Thing.cs", "namespace Demo; public sealed class Thing { public int N => ; }");

        observation.Ok.ShouldBeFalse();
        observation.Error!.Code.ShouldBe(ToolErrorCodes.VerificationFailed);
        observation.Error.Message.ShouldContain("Syntax check of the new file failed.");
        File.Exists(Path.Combine(_workspace.Root, "src", "Thing.cs")).ShouldBeFalse();
    }

    [Fact]
    public async Task Content_that_would_not_compile_is_refused_before_it_is_written()
    {
        _workspace.WriteFile("src/Proj.csproj", Project);
        _workspace.WriteFile("src/Widget.cs", "namespace Demo; public sealed class Widget { public int Size => 1; }");

        ToolObservation<CreateFileResult> observation = await Tool()
            .CreateFileAsync("src/Caller.cs", "namespace Demo; public sealed class Caller { public int Use(Widget w) => w.Weight; }");

        observation.Ok.ShouldBeFalse();
        observation.Error!.Code.ShouldBe(ToolErrorCodes.VerificationFailed);
        observation.Error.Message.ShouldContain("CS1061");
        File.Exists(Path.Combine(_workspace.Root, "src", "Caller.cs")).ShouldBeFalse();
    }

    [Fact]
    public async Task A_file_using_System_is_created_when_the_project_has_implicit_usings()
    {
        // The regression this tool would otherwise hit hardest: a new file is the one place fresh
        // System references appear, and the SDK's global usings are not on disk to be found.
        _workspace.WriteFile("src/Proj.csproj", ImplicitUsingsProject);

        ToolObservation<CreateFileResult> observation = await Tool().CreateFileAsync(
            "src/DoubleSorter.cs",
            """
            namespace Demo;

            public static class DoubleSorter
            {
                public static double[] Ascending(double[] values)
                {
                    ArgumentNullException.ThrowIfNull(values);

                    double[] sorted = [.. values];
                    Array.Sort(sorted);
                    return sorted;
                }
            }
            """);

        observation.Ok.ShouldBeTrue(observation.Error?.Message);
        observation.Data!.Verified.ShouldBeTrue();
        File.Exists(Path.Combine(_workspace.Root, "src", "DoubleSorter.cs")).ShouldBeTrue();
    }

    [Fact]
    public async Task Pre_existing_errors_never_block_a_creation()
    {
        // The file being created is often the fix for what is broken - most obviously when the
        // spec already calls a class that does not exist yet.
        _workspace.WriteFile("src/Proj.csproj", Project);
        _workspace.WriteFile("src/Broken.cs", "namespace Demo; public sealed class Broken { public int X => Missing.Value; }");

        ToolObservation<CreateFileResult> observation = await Tool()
            .CreateFileAsync("src/Fine.cs", "namespace Demo; public sealed class Fine { public int Y => 1; }");

        observation.Ok.ShouldBeTrue(observation.Error?.Message);
        File.Exists(Path.Combine(_workspace.Root, "src", "Fine.cs")).ShouldBeTrue();
    }

    [Fact]
    public async Task A_non_csharp_file_is_created_without_a_compile_check()
    {
        ToolObservation<CreateFileResult> observation = await Tool()
            .CreateFileAsync("src/notes.txt", "not C# at all { { {");

        observation.Ok.ShouldBeTrue(observation.Error?.Message);
        observation.Data!.Verified.ShouldBeFalse();
    }

    [Fact]
    public async Task Verification_can_be_switched_off()
    {
        _workspace.WriteFile("src/Proj.csproj", Project);
        _verification.VerifyEditsBeforeWrite = false;

        ToolObservation<CreateFileResult> observation = await Tool()
            .CreateFileAsync("src/Thing.cs", "namespace Demo; public sealed class Thing { public int N => ; }");

        observation.Ok.ShouldBeTrue(observation.Error?.Message);
        observation.Data!.Verified.ShouldBeFalse();
    }

    [Fact]
    public async Task An_empty_file_is_allowed()
    {
        ToolObservation<CreateFileResult> observation = await Tool().CreateFileAsync("src/placeholder.txt", string.Empty);

        observation.Ok.ShouldBeTrue(observation.Error?.Message);
        observation.Data!.Lines.ShouldBe(0);
    }

    [Fact]
    public async Task A_creation_is_recorded_as_a_pure_addition()
    {
        await Tool().CreateFileAsync("src/Thing.cs", "namespace Demo;\n\npublic sealed class Thing { }\n");

        CodeChange change = _changes.All().ShouldHaveSingleItem();
        change.Tool.ShouldBe("create_file");
        change.Status.ShouldBe(ChangeStatus.Applied);
        change.BeforeText.ShouldBeEmpty();
        change.Diff().ShouldAllBe(line => line.Kind == DiffKind.Added);
        change.Range().ShouldNotBeNull();
    }

    [Fact]
    public async Task A_refused_creation_is_still_recorded()
    {
        // A surface that only ever sees successful writes cannot show what the agent tried to do.
        _workspace.WriteFile("src/Proj.csproj", Project);

        await Tool().CreateFileAsync("src/Thing.cs", "namespace Demo; public sealed class Thing { public int N => ; }");

        CodeChange change = _changes.All().ShouldHaveSingleItem();
        change.Status.ShouldBe(ChangeStatus.Rejected);
        change.Note.ShouldBe("Verification refused the edit.");
        change.VerificationSummary.ShouldNotBeNull();
    }

    [Fact]
    public async Task A_rejected_approval_writes_nothing()
    {
        ToolObservation<CreateFileResult> observation = await Tool(new RefusingGate())
            .CreateFileAsync("src/Thing.cs", "namespace Demo; public sealed class Thing { }");

        observation.Ok.ShouldBeFalse();
        observation.Error!.Code.ShouldBe(ToolErrorCodes.ApprovalRefused);
        observation.Error.Message.ShouldBe("A reviewer rejected this change.");
        File.Exists(Path.Combine(_workspace.Root, "src", "Thing.cs")).ShouldBeFalse();
        _changes.All().ShouldHaveSingleItem().Status.ShouldBe(ChangeStatus.Rejected);
    }

    private sealed class RefusingGate : IApprovalGate
    {
        public bool IsInteractive => true;

        public Task<ApprovalDecision> RequestAsync(CodeChange change, CancellationToken cancellationToken = default) =>
            Task.FromResult(ApprovalDecision.Reject("A reviewer rejected this change."));
    }
}
