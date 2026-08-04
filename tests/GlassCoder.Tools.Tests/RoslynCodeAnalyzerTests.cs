using GlassCoder.TestSupport;
using GlassCoder.Tools.Verification;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
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
    public async Task Rung_two_copes_with_a_file_whose_directory_does_not_exist_yet()
    {
        // create_file checks its content before anything reaches disk, so the walk up to the
        // project has to step over directories that are not there. Enumerating one throws.
        _workspace.WriteFile("proj/Proj.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");
        _workspace.WriteFile("proj/Widget.cs", "namespace Demo; public sealed class Widget { public int Size => 1; }");
        string unborn = Path.Combine(_workspace.Root, "proj", "deep", "nested", "Caller.cs");

        DiagnosticReport report = await Analyzer().CheckEditAsync(
            unborn,
            "namespace Demo; public sealed class Caller { public int Use(Widget w) => w.Size; }");

        report.Ok.ShouldBeTrue(report.Diagnostics.Count > 0 ? report.Diagnostics[0].ToString() : null);
        report.FailureReason.ShouldBeNull("the project is two levels up and should still be found");
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

    // ── Stale references ──

    private const string ReferencingProject =
        "<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup><ProjectReference Include=\"..\\lib\\Lib.csproj\" /></ItemGroup></Project>";

    [Fact]
    public async Task Rung_two_is_inconclusive_when_a_reference_is_older_than_its_sources()
    {
        // Run e8f9186a: a library gained a parameter, and every edit fixing the call sites in
        // its test project was refused with "no overload takes 2 arguments" - judged against
        // the library's last-built DLL, which the test project could not rebuild until the very
        // edit being refused had landed. Stale evidence must not gate.
        _workspace.WriteFile("lib/Lib.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");
        _workspace.WriteFile("lib/Exported.cs", "namespace LibNs; public static class Exported { }");
        _workspace.WriteFile("app/App.csproj", ReferencingProject);
        string caller = _workspace.WriteFile("app/Caller.cs", "namespace AppNs; public class Caller { }");

        string dll = EmitLibrary("app/bin/Debug/Lib.dll");
        File.SetLastWriteTimeUtc(dll, DateTime.UtcNow.AddHours(-1));   // built before the source above

        DiagnosticReport report = await Analyzer().CheckEditAsync(
            caller, "namespace AppNs; public class Caller { public int N => 1; }");

        report.FailureReason.ShouldNotBeNull();
        report.FailureReason.ShouldContain("Lib");
        report.Diagnostics.ShouldBeEmpty();
    }

    [Fact]
    public async Task Rung_two_still_gates_when_the_reference_is_current()
    {
        // The staleness check must not become a blanket amnesty for every project that has a
        // reference: a DLL newer than every source it was built from is trustworthy evidence.
        _workspace.WriteFile("lib/Lib.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");
        string libSource = _workspace.WriteFile(
            "lib/Exported.cs", "namespace LibNs; public static class Exported { public static int One => 1; }");
        _workspace.WriteFile("app/App.csproj", ReferencingProject);
        string caller = _workspace.WriteFile("app/Caller.cs", "namespace AppNs; public class Caller { }");

        File.SetLastWriteTimeUtc(libSource, DateTime.UtcNow.AddHours(-1));
        EmitLibrary("app/bin/Debug/Lib.dll");   // written now, after every source it was built from

        DiagnosticReport report = await Analyzer().CheckEditAsync(
            caller, "namespace AppNs; public class Caller { public int N => LibNs.Exported.One; }");

        report.FailureReason.ShouldBeNull();
        report.Ok.ShouldBeTrue(report.Diagnostics.Count > 0 ? report.Diagnostics[0].ToString() : null);
    }

    /// <summary>
    /// A real assembly, because the reference scavenger only counts DLLs it can load. Its
    /// content mirrors <c>lib/Exported.cs</c> the way a built output would.
    /// </summary>
    private string EmitLibrary(string relativePath)
    {
        string full = Path.Combine(_workspace.Root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);

        CSharpCompilation compilation = CSharpCompilation.Create(
            "Lib",
            [CSharpSyntaxTree.ParseText(
                "namespace LibNs { public static class Exported { public static int One => 1; } }")],
            [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        using FileStream output = File.Create(full);
        compilation.Emit(output).Success.ShouldBeTrue();
        return full;
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
