using GlassCoder.TestSupport;
using GlassCoder.Tools.Changes;
using GlassCoder.Tools.FileSystem;
using GlassCoder.Tools.Verification;
using Microsoft.Extensions.Options;

namespace GlassCoder.Tools.Tests;

/// <summary>
/// The refusal loop-breaker (run 5c071f37).
/// <para>
/// The WPF blind spot that cost that run is fixed where it lived, but the next blind spot will
/// present identically: the gate refusing the same file with the same errors forever, each
/// refusal costing a step and teaching nothing. So the gate now concedes a fixed argument -
/// after <see cref="VerificationOptions.MaxIdenticalRefusals"/> identical refusals of one file,
/// the write goes through with a warning and the build tool adjudicates. The cost of any future
/// blind spot is thereby capped at the limit, instead of at the token budget.
/// </para>
/// </summary>
public sealed class RefusalLoopBreakerTests : IDisposable
{
    private const string BrokenContent =
        "namespace Demo; public class B { public void M() { NoSuchType x = null; } }";

    private const string OtherBrokenContent =
        "namespace Demo; public class B { public void M() { EntirelyOtherType x = null; } }";

    private readonly TempWorkspace _workspace = new();

    public RefusalLoopBreakerTests()
    {
        _workspace.WriteFile("src/App.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");
        _workspace.WriteFile("src/Program.cs", "namespace Demo; public class P { }");
    }

    public void Dispose() => _workspace.Dispose();

    [Fact]
    public async Task The_same_refusal_over_and_over_eventually_stands_aside()
    {
        (CreateFileTool create, _) = Tools(maxIdenticalRefusals: 2);

        ToolObservation<CreateFileResult> first = await create.CreateFileAsync("src/Broken.cs", BrokenContent);
        ToolObservation<CreateFileResult> second = await create.CreateFileAsync("src/Broken.cs", BrokenContent);
        ToolObservation<CreateFileResult> third = await create.CreateFileAsync("src/Broken.cs", BrokenContent);

        first.Ok.ShouldBeFalse();
        first.Error!.Code.ShouldBe(ToolErrorCodes.VerificationFailed);
        second.Ok.ShouldBeFalse();
        second.Error!.Message.ShouldContain("2 times");   // the countdown belongs in the refusal

        third.Ok.ShouldBeTrue(third.Error?.Message);
        third.Data!.Diagnostics.ShouldNotBeNull();
        third.Data.Diagnostics.ShouldContain("build tool");
        File.Exists(Path.Combine(_workspace.Root, "src", "Broken.cs")).ShouldBeTrue();
    }

    [Fact]
    public async Task A_different_error_restarts_the_count()
    {
        // The concession is for an argument the gate keeps losing the same way. Content that
        // fails differently is the model exploring, and the gate's answer is still information.
        (CreateFileTool create, _) = Tools(maxIdenticalRefusals: 2);

        await create.CreateFileAsync("src/Broken.cs", BrokenContent);
        await create.CreateFileAsync("src/Broken.cs", BrokenContent);
        ToolObservation<CreateFileResult> different = await create.CreateFileAsync("src/Broken.cs", OtherBrokenContent);

        different.Ok.ShouldBeFalse();
        different.Error!.Code.ShouldBe(ToolErrorCodes.VerificationFailed);
    }

    [Fact]
    public async Task A_write_that_lands_wipes_the_slate()
    {
        (CreateFileTool create, _) = Tools(maxIdenticalRefusals: 2);

        await create.CreateFileAsync("src/Broken.cs", BrokenContent);
        await create.CreateFileAsync("src/Broken.cs", "namespace Demo; public class B { }");
        await create.CreateFileAsync("src/Broken.cs", BrokenContent, overwrite: true);
        ToolObservation<CreateFileResult> fourth = await create.CreateFileAsync(
            "src/Broken.cs", BrokenContent, overwrite: true);

        // Refusals 1, then (after the good write reset the count) 1 and 2 again: still refused.
        fourth.Ok.ShouldBeFalse();
        fourth.Error!.Code.ShouldBe(ToolErrorCodes.VerificationFailed);
    }

    [Fact]
    public async Task A_syntax_error_is_never_conceded()
    {
        // Rung 1 needs no references and has no blind spots - a file that cannot parse is wrong
        // in itself, and writing it can never be the harness's idea.
        (CreateFileTool create, _) = Tools(maxIdenticalRefusals: 1);
        const string unparsable = "namespace Demo; public class B { public void M( }";

        await create.CreateFileAsync("src/Broken.cs", unparsable);
        await create.CreateFileAsync("src/Broken.cs", unparsable);
        ToolObservation<CreateFileResult> third = await create.CreateFileAsync("src/Broken.cs", unparsable);

        third.Ok.ShouldBeFalse();
        third.Error!.Code.ShouldBe(ToolErrorCodes.VerificationFailed);
    }

    [Fact]
    public async Task Zero_keeps_refusing_without_limit()
    {
        (CreateFileTool create, _) = Tools(maxIdenticalRefusals: 0);

        for (int attempt = 0; attempt < 5; attempt++)
        {
            (await create.CreateFileAsync("src/Broken.cs", BrokenContent)).Ok.ShouldBeFalse();
        }
    }

    [Fact]
    public async Task Edit_file_concedes_the_same_way()
    {
        (_, EditFileTool edit) = Tools(maxIdenticalRefusals: 2);
        _workspace.WriteFile("src/Target.cs", "namespace Demo; public class T { public int N => 1; }");

        for (int attempt = 0; attempt < 2; attempt++)
        {
            ToolObservation<EditFileResult> refused = await edit.EditFileAsync(
                "src/Target.cs", oldText: "public int N => 1;", newText: "public int N => Missing;");
            refused.Ok.ShouldBeFalse();
        }

        ToolObservation<EditFileResult> conceded = await edit.EditFileAsync(
            "src/Target.cs", oldText: "public int N => 1;", newText: "public int N => Missing;");

        conceded.Ok.ShouldBeTrue(conceded.Error?.Message);
        conceded.Data!.Files[0].Diagnostics.ShouldNotBeNull();
        conceded.Data.Files[0].Diagnostics.ShouldContain("build tool");
        File.ReadAllText(Path.Combine(_workspace.Root, "src", "Target.cs")).ShouldContain("Missing");
    }

    [Fact]
    public async Task Create_and_edit_share_one_count_for_one_file()
    {
        // Run 5c071f37 alternated create_file and edit_file against the same code-behind; a
        // per-tool count would have let the pair refuse forever in turns.
        (CreateFileTool create, EditFileTool edit) = Tools(maxIdenticalRefusals: 2);
        _workspace.WriteFile("src/Target.cs", "namespace Demo; public class T { }");
        const string broken = "namespace Demo; public class T { public void M() { NoSuchType x = null; } }";

        (await create.CreateFileAsync("src/Target.cs", broken, overwrite: true)).Ok.ShouldBeFalse();
        (await edit.EditFileAsync(
            "src/Target.cs",
            oldText: "namespace Demo; public class T { }",
            newText: broken)).Ok.ShouldBeFalse();
        ToolObservation<CreateFileResult> conceded = await create.CreateFileAsync(
            "src/Target.cs", broken, overwrite: true);

        conceded.Ok.ShouldBeTrue(conceded.Error?.Message);
    }

    private (CreateFileTool Create, EditFileTool Edit) Tools(int maxIdenticalRefusals)
    {
        IOptions<VerificationOptions> verification = Options.Create(
            new VerificationOptions { MaxIdenticalRefusals = maxIdenticalRefusals });
        RoslynCodeAnalyzer analyzer = new(_workspace.Guard("src"), verification);
        DiagnosticSummarizer summarizer = new(verification);
        ChangeLog changes = new();
        AutoApprovalGate approval = new(Options.Create(new ApprovalOptions()));
        VerificationRefusalTracker refusals = new();

        return (
            new CreateFileTool(
                _workspace.Guard("src"), analyzer, summarizer, verification, changes, approval, refusals: refusals),
            new EditFileTool(
                _workspace.Guard("src"), analyzer, summarizer, verification, changes, approval, refusals: refusals));
    }
}
