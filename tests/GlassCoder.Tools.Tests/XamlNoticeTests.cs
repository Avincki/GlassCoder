using GlassCoder.TestSupport;
using GlassCoder.Tools.Changes;
using GlassCoder.Tools.FileSystem;
using GlassCoder.Tools.Guardrails;
using GlassCoder.Tools.Verification;
using Microsoft.Extensions.Options;

namespace GlassCoder.Tools.Tests;

/// <summary>
/// The XAML notices (GrokReview 2026-08-08 ui-layout; runs ea9a1f66, 216360bf).
/// <para>
/// The clip risk: ea9a1f66's operator saw the result field outside the dialog because a fixed
/// Height="300" window held five rows of content - green build, green tests, invisible defect.
/// The test-project note: 216360bf copied the app's markup into the test project chasing
/// XAML-parsing layout tests that can never load, and the copy outlived the deleted tests.
/// Notices, never refusals: rendering is a judgement no compiler backs.
/// </para>
/// </summary>
public sealed class XamlNoticeTests : IDisposable
{
    private readonly TempWorkspace _workspace = new();

    public void Dispose() => _workspace.Dispose();

    /// <summary>A path whose ancestors hold no project file, so only the content speaks.</summary>
    private string AppXaml => Path.Combine(_workspace.Root, "src", "App", "MainWindow.xaml");

    private const string ShortDenseWindow =
        """
        <Window xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" Height="300" Width="400">
          <Grid>
            <Grid.RowDefinitions>
              <RowDefinition /><RowDefinition /><RowDefinition /><RowDefinition />
            </Grid.RowDefinitions>
            <TextBox /><TextBox /><Button Content="Multiply" /><TextBlock /><TextBlock />
          </Grid>
        </Window>
        """;

    [Fact]
    public void A_short_fixed_window_with_dense_content_earns_the_layout_note()
    {
        string notice = XamlNotices.Describe(AppXaml, ShortDenseWindow);

        notice.ShouldContain("Layout note");
        notice.ShouldContain("Height=300");
        notice.ShouldContain("Run app");
    }

    [Fact]
    public void SizeToContent_silences_the_layout_note()
    {
        string sized = ShortDenseWindow.Replace(
            "Height=\"300\"", "Height=\"300\" SizeToContent=\"Height\"", StringComparison.Ordinal);

        XamlNotices.Describe(AppXaml, sized).ShouldBeEmpty();
    }

    [Fact]
    public void A_tall_window_earns_no_note()
    {
        string tall = ShortDenseWindow.Replace("Height=\"300\"", "Height=\"600\"", StringComparison.Ordinal);

        XamlNotices.Describe(AppXaml, tall).ShouldBeEmpty();
    }

    [Fact]
    public void Sparse_content_earns_no_note()
    {
        const string sparse =
            """
            <Window xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" Height="300">
              <TextBlock Text="hello" />
            </Window>
            """;

        XamlNotices.Describe(AppXaml, sparse).ShouldBeEmpty();
    }

    [Fact]
    public void Non_xaml_paths_are_ignored()
    {
        XamlNotices.Describe("src/App/Program.cs", ShortDenseWindow).ShouldBeEmpty();
    }

    [Fact]
    public void A_xaml_file_in_a_test_project_earns_the_test_project_note()
    {
        using TempWorkspace workspace = new();
        workspace.WriteFile(
            "tests/App.Tests/App.Tests.csproj",
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup><PackageReference Include="xunit" Version="2.9.3" /></ItemGroup>
            </Project>
            """);
        string xaml = Path.Combine(workspace.Root, "tests", "App.Tests", "MainWindow.xaml");

        string notice = XamlNotices.Describe(xaml, "<Window xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" />");

        notice.ShouldContain("test project");
        notice.ShouldContain("markup belongs to the app project");
    }

    [Fact]
    public async Task The_notice_lands_in_the_create_file_summary_the_model_reads()
    {
        using TempWorkspace workspace = new();
        workspace.WriteFile("src/App/App.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");

        IOptions<VerificationOptions> verification = Options.Create(new VerificationOptions());
        CreateFileTool tool = new(
            workspace.Guard("src"),
            new RoslynCodeAnalyzer(workspace.Guard("src"), verification),
            new DiagnosticSummarizer(verification),
            verification,
            new ChangeLog(),
            new AutoApprovalGate(Options.Create(new ApprovalOptions())));

        ToolObservation<CreateFileResult> observation =
            await tool.CreateFileAsync("src/App/MainWindow.xaml", ShortDenseWindow);

        observation.Ok.ShouldBeTrue(observation.Error?.Message);
        observation.Summary.ShouldContain("Layout note");
    }
}
