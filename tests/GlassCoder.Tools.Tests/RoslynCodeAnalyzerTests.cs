using GlassCoder.TestSupport;
using GlassCoder.Tools.Verification;
using Microsoft.Extensions.Options;

namespace GlassCoder.Tools.Tests;

/// <summary>
/// Rungs 1 and 2 (workplan task 14): a per-file syntax check that is fast enough to run after
/// every edit, and an in-memory compile that can judge an edit before it reaches disk.
/// </summary>
public sealed class RoslynCodeAnalyzerTests : IDisposable
{
    private const string ImplicitUsingsProject =
        "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><ImplicitUsings>enable</ImplicitUsings></PropertyGroup></Project>";

    private readonly TempWorkspace _workspace = new();

    public void Dispose() => _workspace.Dispose();

    private RoslynCodeAnalyzer Analyzer() =>
        new(_workspace.Guard("."), Options.Create(new VerificationOptions()));

    [Fact]
    public void Valid_syntax_passes_rung_one()
    {
        DiagnosticReport report = Analyzer().CheckSyntax("A.cs", "public class A { public int X => 1; }");

        report.Ok.ShouldBeTrue();
        report.ErrorCount.ShouldBe(0);
    }

    [Fact]
    public void A_malformed_edit_is_caught_by_rung_one_with_a_typed_diagnostic()
    {
        DiagnosticReport report = Analyzer().CheckSyntax("A.cs", "public class A { public int X => 1; ");

        report.Ok.ShouldBeFalse();
        report.ErrorCount.ShouldBeGreaterThan(0);
        report.Diagnostics[0].Id.ShouldStartWith("CS");
        report.Diagnostics[0].Line.ShouldBeGreaterThan(0);
    }

    [Fact]
    public void Rung_one_ignores_files_it_does_not_handle()
    {
        RoslynCodeAnalyzer analyzer = Analyzer();

        analyzer.Handles("notes.txt").ShouldBeFalse();
        analyzer.CheckSyntax("notes.txt", "this is not C#").Ok.ShouldBeTrue();
    }

    [Fact]
    public async Task Rung_two_detects_a_hallucinated_api_across_files()
    {
        _workspace.WriteFile("proj/Proj.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");
        _workspace.WriteFile("proj/Widget.cs", "namespace Demo; public sealed class Widget { public int Size => 1; }");
        string caller = _workspace.WriteFile(
            "proj/Caller.cs",
            "namespace Demo; public sealed class Caller { public int Use(Widget w) => w.Size; }");

        // The edit calls a member that does not exist - exactly the failure mode a syntax check
        // cannot see and a full build would take seconds to find.
        DiagnosticReport report = await Analyzer().CheckEditAsync(
            caller,
            "namespace Demo; public sealed class Caller { public int Use(Widget w) => w.Weight; }");

        report.Ok.ShouldBeFalse();
        report.Diagnostics.ShouldContain(d => d.Id == "CS1061");
    }

    [Fact]
    public async Task Rung_two_passes_a_good_edit()
    {
        _workspace.WriteFile("proj/Proj.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");
        _workspace.WriteFile("proj/Widget.cs", "namespace Demo; public sealed class Widget { public int Size => 1; }");
        string caller = _workspace.WriteFile(
            "proj/Caller.cs",
            "namespace Demo; public sealed class Caller { public int Use(Widget w) => w.Size; }");

        DiagnosticReport report = await Analyzer().CheckEditAsync(
            caller,
            "namespace Demo; public sealed class Caller { public int Use(Widget w) => w.Size * 2; }");

        report.Ok.ShouldBeTrue(report.Diagnostics.Count > 0 ? report.Diagnostics[0].ToString() : null);
    }

    [Fact]
    public async Task Rung_two_is_inconclusive_rather_than_wrong_when_there_is_no_project()
    {
        // Reporting "no project" as a compile failure would send the agent hunting a bug that
        // is not in the code.
        string orphan = _workspace.WriteFile("loose/Orphan.cs", "public class Orphan { }");

        DiagnosticReport report = await Analyzer().CheckEditAsync(orphan, "public class Orphan { }");

        report.FailureReason.ShouldNotBeNull();
        report.Diagnostics.ShouldBeEmpty();
    }

    [Fact]
    public async Task Rung_two_honours_implicit_usings()
    {
        // The SDK writes its global usings into obj/, which the deny list excludes - so without
        // synthesising them, every new file touching System would be refused before it was written.
        _workspace.WriteFile("proj/Proj.csproj", ImplicitUsingsProject);
        string caller = _workspace.WriteFile("proj/Caller.cs", "namespace Demo; public sealed class Caller { }");

        DiagnosticReport report = await Analyzer().CheckEditAsync(
            caller,
            """
            namespace Demo;

            public sealed class Caller
            {
                public double[] Sorted(double[] values)
                {
                    ArgumentNullException.ThrowIfNull(values);
                    double[] copy = [.. values];
                    Array.Sort(copy);
                    return copy;
                }
            }
            """);

        report.Ok.ShouldBeTrue(report.Diagnostics.Count > 0 ? report.Diagnostics[0].ToString() : null);
    }

    [Theory]
    [InlineData("enable")]
    [InlineData("true")]
    [InlineData("ENABLE")]
    public async Task Rung_two_accepts_every_spelling_that_switches_implicit_usings_on(string value)
    {
        _workspace.WriteFile(
            "proj/Proj.csproj",
            $"<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><ImplicitUsings>{value}</ImplicitUsings></PropertyGroup></Project>");
        string caller = _workspace.WriteFile("proj/Caller.cs", "namespace Demo; public sealed class Caller { }");

        DiagnosticReport report = await Analyzer().CheckEditAsync(
            caller,
            "namespace Demo; public sealed class Caller { public int N => Array.Empty<int>().Length; }");

        report.Ok.ShouldBeTrue(report.Diagnostics.Count > 0 ? report.Diagnostics[0].ToString() : null);
    }

    [Fact]
    public async Task Rung_two_leaves_implicit_usings_off_when_the_project_does_not_ask_for_them()
    {
        // Switching them on unconditionally would hide a genuinely missing using directive.
        _workspace.WriteFile(
            "proj/Proj.csproj",
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><ImplicitUsings>disable</ImplicitUsings></PropertyGroup></Project>");
        string caller = _workspace.WriteFile("proj/Caller.cs", "namespace Demo; public sealed class Caller { }");

        DiagnosticReport report = await Analyzer().CheckEditAsync(
            caller,
            "namespace Demo; public sealed class Caller { public int N => Array.Empty<int>().Length; }");

        report.Ok.ShouldBeFalse();
        report.Diagnostics.ShouldContain(d => d.Id == "CS0103");
    }

    [Fact]
    public async Task Rung_two_still_catches_a_hallucinated_api_under_implicit_usings()
    {
        // The synthesised usings must not become a blanket amnesty: this is rung 2's whole job.
        _workspace.WriteFile("proj/Proj.csproj", ImplicitUsingsProject);
        string caller = _workspace.WriteFile("proj/Caller.cs", "namespace Demo; public sealed class Caller { }");

        DiagnosticReport report = await Analyzer().CheckEditAsync(
            caller,
            "namespace Demo; public sealed class Caller { public int N => Array.SortDescending([1, 2]); }");

        report.Ok.ShouldBeFalse();
        report.Diagnostics.ShouldContain(d => d.Id == "CS0117" || d.Id == "CS1061");
    }

    [Fact]
    public async Task A_malformed_project_file_does_not_fail_the_compile()
    {
        _workspace.WriteFile("proj/Proj.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup>");
        string caller = _workspace.WriteFile("proj/Caller.cs", "namespace Demo; public sealed class Caller { }");

        DiagnosticReport report = await Analyzer().CheckEditAsync(
            caller,
            "namespace Demo; public sealed class Caller { public int N => 2; }");

        report.Ok.ShouldBeTrue(report.Diagnostics.Count > 0 ? report.Diagnostics[0].ToString() : null);
    }

    [Fact]
    public async Task Rung_two_is_fast_enough_to_run_after_an_edit()
    {
        _workspace.WriteFile("proj/Proj.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");
        for (int i = 0; i < 25; i++)
        {
            _workspace.WriteFile($"proj/Type{i}.cs", $"namespace Demo; public sealed class Type{i} {{ public int N => {i}; }}");
        }

        RoslynCodeAnalyzer analyzer = Analyzer();
        await analyzer.CompileAsync(Path.Combine(_workspace.Root, "proj"));   // warm the reference cache

        DiagnosticReport report = await analyzer.CompileAsync(Path.Combine(_workspace.Root, "proj"));

        report.Ok.ShouldBeTrue();
        report.DurationMs.ShouldBeLessThan(3000);
    }
}
