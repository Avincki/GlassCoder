using GlassCoder.TestSupport;
using GlassCoder.Tools.Changes;
using GlassCoder.Tools.FileSystem;
using GlassCoder.Tools.Verification;
using Microsoft.Extensions.Options;

namespace GlassCoder.Tools.Tests;

/// <summary>
/// The gate against WPF's generated code (run 5c071f37).
/// <para>
/// The markup compiler declares <c>InitializeComponent</c> and every <c>x:Name</c> field in
/// <c>obj/</c> at build time, where the pre-write compile cannot look. Run 5c071f37 refused one
/// correct code-behind ten times over CS0103 for exactly those names - while the build tool kept
/// answering green in between - and the run spent itself to the token limit and shipped a window
/// with no handler. The gate now reads the generated partials when a build has produced them,
/// and stands aside when it has none or stale ones: a gate that cannot know must not gate.
/// </para>
/// </summary>
public sealed class XamlAwareGateTests : IDisposable
{
    private const string WpfProject =
        "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><UseWPF>true</UseWPF></PropertyGroup></Project>";

    private const string Page =
        """
        <Window x:Class="WpfApp.MainWindow"
                xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
            <TextBox x:Name="txtNumber1" />
        </Window>
        """;

    /// <summary>
    /// What MarkupCompilePass1 writes for <see cref="Page"/>, minus the WPF base types the test
    /// runner does not load - the mechanics under test are the partial's fields, not WPF itself.
    /// </summary>
    private const string GeneratedPartial =
        """
        namespace WpfApp
        {
            public partial class MainWindow
            {
                internal FakeTextBox txtNumber1 = new FakeTextBox();
                public void InitializeComponent() { }
            }

            public class FakeTextBox { public string Text = ""; }
        }
        """;

    /// <summary>The file run 5c071f37 could never land: the handler, wired to the named controls.</summary>
    private const string CodeBehind =
        """
        namespace WpfApp
        {
            public partial class MainWindow
            {
                public MainWindow() { InitializeComponent(); }

                private void btnMultiply_Click() { txtNumber1.Text = "1"; }
            }
        }
        """;

    private readonly TempWorkspace _workspace = new();

    public void Dispose() => _workspace.Dispose();

    private RoslynCodeAnalyzer Analyzer() =>
        new(_workspace.Guard("src"), Options.Create(new VerificationOptions()));

    [Fact]
    public async Task A_code_behind_before_the_first_build_is_inconclusive_rather_than_refused()
    {
        _workspace.WriteFile("src/App.csproj", WpfProject);
        _workspace.WriteFile("src/MainWindow.xaml", Page);
        string codeBehind = Path.Combine(_workspace.Root, "src", "MainWindow.xaml.cs");

        DiagnosticReport report = await Analyzer().CheckEditAsync(codeBehind, CodeBehind);

        report.FailureReason.ShouldNotBeNull("InitializeComponent lives in code no build has generated yet");
        report.FailureReason.ShouldContain("MainWindow.xaml");
        report.FailureReason.ShouldContain("build tool");
        report.Diagnostics.ShouldBeEmpty("a check that could not run reports no findings");
    }

    [Fact]
    public async Task A_built_page_lets_its_code_behind_compile_against_the_generated_partial()
    {
        _workspace.WriteFile("src/App.csproj", WpfProject);
        string xaml = _workspace.WriteFile("src/MainWindow.xaml", Page);
        _workspace.WriteFile("src/obj/Debug/net10.0-windows/MainWindow.g.cs", GeneratedPartial);
        File.SetLastWriteTimeUtc(xaml, DateTime.UtcNow.AddHours(-1));   // built after the markup changed
        string codeBehind = Path.Combine(_workspace.Root, "src", "MainWindow.xaml.cs");

        DiagnosticReport report = await Analyzer().CheckEditAsync(codeBehind, CodeBehind);

        report.FailureReason.ShouldBeNull();
        report.Ok.ShouldBeTrue(report.Diagnostics.Count > 0 ? report.Diagnostics[0].ToString() : null);
    }

    [Fact]
    public async Task Markup_newer_than_its_partial_makes_the_compile_inconclusive()
    {
        // The generated file may name controls the .xaml no longer has, or miss ones it gained.
        // Judging the edit against the old markup is the stale-reference defect in a new coat.
        _workspace.WriteFile("src/App.csproj", WpfProject);
        string generated = _workspace.WriteFile("src/obj/Debug/net10.0-windows/MainWindow.g.cs", GeneratedPartial);
        File.SetLastWriteTimeUtc(generated, DateTime.UtcNow.AddHours(-1));
        _workspace.WriteFile("src/MainWindow.xaml", Page);   // written now, after the build above
        string codeBehind = Path.Combine(_workspace.Root, "src", "MainWindow.xaml.cs");

        DiagnosticReport report = await Analyzer().CheckEditAsync(codeBehind, CodeBehind);

        report.FailureReason.ShouldNotBeNull();
        report.FailureReason.ShouldContain("changed after");
        report.Diagnostics.ShouldBeEmpty();
    }

    [Fact]
    public async Task The_generated_partial_is_no_amnesty_for_a_control_the_markup_never_named()
    {
        // Rung 2's whole job survives: a hallucinated control is still a refusal.
        _workspace.WriteFile("src/App.csproj", WpfProject);
        string xaml = _workspace.WriteFile("src/MainWindow.xaml", Page);
        _workspace.WriteFile("src/obj/Debug/net10.0-windows/MainWindow.g.cs", GeneratedPartial);
        File.SetLastWriteTimeUtc(xaml, DateTime.UtcNow.AddHours(-1));
        string codeBehind = Path.Combine(_workspace.Root, "src", "MainWindow.xaml.cs");

        DiagnosticReport report = await Analyzer().CheckEditAsync(
            codeBehind,
            """
            namespace WpfApp
            {
                public partial class MainWindow
                {
                    private void btnMultiply_Click() { txtDoesNotExist.Text = "1"; }
                }
            }
            """);

        report.Ok.ShouldBeFalse();
        report.Diagnostics.ShouldContain(d => d.Id == "CS0103");
    }

    [Fact]
    public async Task A_resource_dictionary_demands_no_partial()
    {
        // No x:Class, no generated code - a theme file must not park the gate forever.
        _workspace.WriteFile("src/App.csproj", WpfProject);
        _workspace.WriteFile(
            "src/Themes/Generic.xaml",
            "<ResourceDictionary xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" />");
        string helper = _workspace.WriteFile("src/Helper.cs", "namespace WpfApp; public class Helper { }");

        DiagnosticReport report = await Analyzer().CheckEditAsync(
            helper, "namespace WpfApp; public class Helper { public int N => 1; }");

        report.FailureReason.ShouldBeNull();
        report.Ok.ShouldBeTrue(report.Diagnostics.Count > 0 ? report.Diagnostics[0].ToString() : null);
    }

    [Fact]
    public async Task A_project_that_does_not_use_wpf_is_gated_exactly_as_before()
    {
        // A stray .xaml in a plain library must not switch the gate off.
        _workspace.WriteFile("src/App.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");
        _workspace.WriteFile("src/Stray.xaml", Page);
        _workspace.WriteFile("src/Program.cs", "namespace Demo; public class P { }");
        string broken = Path.Combine(_workspace.Root, "src", "Broken.cs");

        DiagnosticReport report = await Analyzer().CheckEditAsync(
            broken, "namespace Demo; public class B { public void M() { NoSuchType x = null; } }");

        report.FailureReason.ShouldBeNull();
        report.Ok.ShouldBeFalse();
    }

    [Fact]
    public async Task The_gate_writes_the_file_run_5c071f37_could_not()
    {
        // The end-to-end shape of the defect: the exact create_file that was refused ten times.
        _workspace.WriteFile("src/App.csproj", WpfProject);
        string xaml = _workspace.WriteFile("src/MainWindow.xaml", Page);
        _workspace.WriteFile("src/obj/Debug/net10.0-windows/MainWindow.g.cs", GeneratedPartial);
        File.SetLastWriteTimeUtc(xaml, DateTime.UtcNow.AddHours(-1));

        IOptions<VerificationOptions> verification = Options.Create(new VerificationOptions());
        CreateFileTool tool = new(
            _workspace.Guard("src"),
            new RoslynCodeAnalyzer(_workspace.Guard("src"), verification),
            new DiagnosticSummarizer(verification),
            verification,
            new ChangeLog(),
            new AutoApprovalGate(Options.Create(new ApprovalOptions())));

        ToolObservation<CreateFileResult> observation =
            await tool.CreateFileAsync("src/MainWindow.xaml.cs", CodeBehind);

        observation.Ok.ShouldBeTrue(observation.Error?.Message);
        File.Exists(Path.Combine(_workspace.Root, "src", "MainWindow.xaml.cs")).ShouldBeTrue();
    }
}
