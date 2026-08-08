using GlassCoder.TestSupport;
using GlassCoder.Tools.Changes;
using GlassCoder.Tools.FileSystem;
using GlassCoder.Tools.Verification;
using Microsoft.Extensions.Options;

namespace GlassCoder.Tools.Tests;

/// <summary>
/// The refusal carries the diagnosis, not just the error (run 05e1bedb).
/// <para>
/// Steps 4, 9 and 10 of that run were the same refused test file: first for referencing a type
/// no project owned, then twice for guessing at a namespace the type never had. Each refusal
/// quoted the compiler accurately and withheld what the workspace knew - which file declares
/// the type, which project owns it, and that the type sits in the global namespace. Three
/// guesses at ~12 seconds each, ended by giving up on the using directive rather than by being
/// told the answer.
/// </para>
/// </summary>
public sealed class SymbolHintTests
{
    // ── The three shapes from the run ──

    [Fact]
    public void A_type_in_an_unreferenced_project_names_the_missing_reference()
    {
        using TempWorkspace workspace = new();
        workspace.WriteFile("src/Lib/Lib.csproj", Project());
        workspace.WriteFile("src/Lib/Thing.cs", "namespace LibNs;\n\npublic class Thing { }\n");
        workspace.WriteFile("src/App/App.csproj", Project());

        string hint = SymbolHints.Describe(
            [NotFound("Thing")],
            Path.Combine(workspace.Root, "src", "App", "User.cs"),
            workspace.Root);

        hint.ShouldContain("'Thing' is declared in src/Lib/Thing.cs");
        hint.ShouldContain("project src/Lib/Lib.csproj");
        hint.ShouldContain("namespace 'LibNs'");
        hint.ShouldContain("does not reference that project");
        hint.ShouldContain("add_reference");
    }

    [Fact]
    public void A_type_in_the_global_namespace_says_no_using_applies()
    {
        // Steps 9 and 10, exactly: the type is visible - the reference exists - and the model
        // is guessing at a namespace that was never there. CS0138's own hint ("consider a
        // 'using static'") sent it to a second wrong guess.
        using TempWorkspace workspace = new();
        workspace.WriteFile("src/ArrayProcessor/ArrayProcessor.csproj", Project());
        workspace.WriteFile("src/ArrayProcessor/ArrayProcessor.cs", "public static class ArrayProcessor { }\n");
        workspace.WriteFile(
            "tests/ArrayProcessorTests/ArrayProcessorTests.csproj",
            Project(reference: "..\\..\\src\\ArrayProcessor\\ArrayProcessor.csproj"));

        string hint = SymbolHints.Describe(
            [new CodeDiagnostic(
                "CS0138",
                CodeSeverity.Error,
                "A 'using namespace' directive can only be applied to namespaces; 'ArrayProcessor' is a type not a namespace. Consider a 'using static' directive instead")],
            Path.Combine(workspace.Root, "tests", "ArrayProcessorTests", "ArrayProcessorTests.cs"),
            workspace.Root);

        hint.ShouldContain("'ArrayProcessor' is declared in src/ArrayProcessor/ArrayProcessor.cs");
        hint.ShouldContain("global namespace");
        hint.ShouldContain("use the name directly");
        hint.ShouldNotContain("does not reference", customMessage: "the reference exists; naming it as missing would send the model backwards");
    }

    [Fact]
    public void A_type_owned_by_no_project_names_the_orphan()
    {
        // Step 4: the type existed only as a loose file in src/, so no reference could ever
        // reach it. The fix is a project, and the refusal is where that has to be said.
        using TempWorkspace workspace = new();
        workspace.WriteFile("src/ArrayProcessor.cs", "public static class ArrayProcessor { }\n");
        workspace.WriteFile("tests/T/T.csproj", Project());

        string hint = SymbolHints.Describe(
            [NotFound("ArrayProcessor")],
            Path.Combine(workspace.Root, "tests", "T", "Tests.cs"),
            workspace.Root);

        hint.ShouldContain("'ArrayProcessor' is declared in src/ArrayProcessor.cs");
        hint.ShouldContain("no project contains");
        hint.ShouldContain("dotnet_project");
    }

    // ── The edges ──

    [Fact]
    public void A_type_in_the_same_project_is_a_namespace_fact_not_a_reference_errand()
    {
        using TempWorkspace workspace = new();
        workspace.WriteFile("src/Lib/Lib.csproj", Project());
        workspace.WriteFile("src/Lib/Thing.cs", "namespace LibNs;\n\npublic class Thing { }\n");

        string hint = SymbolHints.Describe(
            [NotFound("Thing")],
            Path.Combine(workspace.Root, "src", "Lib", "Other.cs"),
            workspace.Root);

        hint.ShouldContain("in this same project");
        hint.ShouldContain("namespace 'LibNs'");
        hint.ShouldNotContain("add_reference");
    }

    [Fact]
    public void A_name_declared_nowhere_earns_no_hint()
    {
        // A typo'd type or a local variable: the compiler's message is already the whole truth,
        // and a hint built on nothing would point somewhere wrong.
        using TempWorkspace workspace = new();
        workspace.WriteFile("src/App/App.csproj", Project());

        SymbolHints.Describe(
            [NotFound("NoSuchThing")],
            Path.Combine(workspace.Root, "src", "App", "User.cs"),
            workspace.Root).ShouldBeEmpty();
    }

    [Fact]
    public void A_qualified_guess_is_resolved_by_its_simple_name()
    {
        // Step 10's shape: CS0426 quoting 'ArrayProcessor.ArrayProcessor' - the model invented
        // the qualifier, and the declaration only knows the simple name.
        using TempWorkspace workspace = new();
        workspace.WriteFile("src/ArrayProcessor/ArrayProcessor.csproj", Project());
        workspace.WriteFile("src/ArrayProcessor/ArrayProcessor.cs", "public static class ArrayProcessor { }\n");

        string hint = SymbolHints.Describe(
            [new CodeDiagnostic(
                "CS0426",
                CodeSeverity.Error,
                "The type name 'ArrayProcessor' does not exist in the type 'ArrayProcessor'")],
            Path.Combine(workspace.Root, "src", "ArrayProcessor", "Other.cs"),
            workspace.Root);

        hint.ShouldContain("'ArrayProcessor' is declared in src/ArrayProcessor/ArrayProcessor.cs");
    }

    [Fact]
    public void An_unrelated_diagnostic_is_left_alone()
    {
        using TempWorkspace workspace = new();
        workspace.WriteFile("src/Lib/Lib.csproj", Project());
        workspace.WriteFile("src/Lib/Thing.cs", "namespace LibNs;\n\npublic class Thing { }\n");

        SymbolHints.Describe(
            [new CodeDiagnostic("CS1002", CodeSeverity.Error, "; expected")],
            Path.Combine(workspace.Root, "src", "Lib", "Other.cs"),
            workspace.Root).ShouldBeEmpty();
    }

    // ── Names only a referenced assembly declares (run a408b61b) ──

    /// <summary>
    /// Run a408b61b: 'FactAttribute' is nowhere in the workspace - it lives in xunit.core.dll,
    /// which the failing compilation itself referenced - so a hint that only reads sources had
    /// nothing to say, and the model chased the "assembly reference" half of CS0246 through
    /// three package adds and two green builds.
    /// </summary>
    [Fact]
    public void A_name_no_source_declares_is_answered_from_the_reference_lookup()
    {
        using TempWorkspace workspace = new();
        workspace.WriteFile("src/App/App.csproj", Project());
        List<string> asked = [];

        string hint = SymbolHints.Describe(
            [NotFound("FactAttribute")],
            Path.Combine(workspace.Root, "src", "App", "Tests.cs"),
            workspace.Root,
            identifiers =>
            {
                asked.AddRange(identifiers);
                return new Dictionary<string, ReferencedSymbol>
                {
                    ["FactAttribute"] = new("Xunit", "xunit.core"),
                };
            });

        asked.ShouldBe(["FactAttribute"], customMessage: "the lookup gets exactly the names the sources could not answer");
        hint.ShouldContain("namespace 'Xunit'");
        hint.ShouldContain("using Xunit;");
        hint.ShouldContain("<Using Include=\"Xunit\" />");
    }

    [Fact]
    public void A_name_a_source_declares_never_reaches_the_reference_lookup()
    {
        // The workspace declaration is the richer answer - it knows the project and whether the
        // reference exists - so metadata is only consulted for what the sources cannot place.
        using TempWorkspace workspace = new();
        workspace.WriteFile("src/App/App.csproj", Project());
        workspace.WriteFile("src/App/Thing.cs", "namespace AppNs;\n\npublic class Thing { }\n");
        bool consulted = false;

        SymbolHints.Describe(
            [NotFound("Thing")],
            Path.Combine(workspace.Root, "src", "App", "User.cs"),
            workspace.Root,
            _ =>
            {
                consulted = true;
                return new Dictionary<string, ReferencedSymbol>();
            });

        consulted.ShouldBeFalse();
    }

    // ── Through the gate ──

    [Fact]
    public async Task A_refused_write_carries_the_hint_in_the_message_the_model_reads()
    {
        // The unit above proves the sentence; this proves it lands where run 05e1bedb needed
        // it - in the create_file refusal itself.
        using TempWorkspace workspace = new();
        workspace.WriteFile("src/App/App.csproj", Project());
        workspace.WriteFile("src/App/Program.cs", "public class P { }");
        workspace.WriteFile("src/Lib/Lib.csproj", Project());
        workspace.WriteFile("src/Lib/Thing.cs", "namespace LibNs;\n\npublic class Thing { }\n");

        IOptions<VerificationOptions> verification = Options.Create(new VerificationOptions());
        CreateFileTool tool = new(
            workspace.Guard("src"),
            new RoslynCodeAnalyzer(workspace.Guard("src"), verification),
            new DiagnosticSummarizer(verification),
            verification,
            new ChangeLog(),
            new AutoApprovalGate(Options.Create(new ApprovalOptions())));

        ToolObservation<CreateFileResult> observation = await tool.CreateFileAsync(
            "src/App/User.cs",
            "public class User { public Thing T { get; set; } }");

        observation.Ok.ShouldBeFalse();
        observation.Error!.Message.ShouldContain("declared in src/Lib/Thing.cs");
        observation.Error.Message.ShouldContain("does not reference that project");
    }

    /// <summary>
    /// The whole a408b61b chain, on a framework type: a name no source declares, sitting in an
    /// assembly the gate's own compilation references, must be named with its namespace in the
    /// refusal the model reads - not left to the compiler's "using directive or assembly
    /// reference?" coin flip.
    /// </summary>
    [Fact]
    public async Task A_refusal_names_the_namespace_a_referenced_assembly_declares()
    {
        using TempWorkspace workspace = new();
        workspace.WriteFile("src/App/App.csproj", Project());
        workspace.WriteFile("src/App/Program.cs", "public class P { }");

        IOptions<VerificationOptions> verification = Options.Create(new VerificationOptions());
        CreateFileTool tool = new(
            workspace.Guard("src"),
            new RoslynCodeAnalyzer(workspace.Guard("src"), verification),
            new DiagnosticSummarizer(verification),
            verification,
            new ChangeLog(),
            new AutoApprovalGate(Options.Create(new ApprovalOptions())));

        ToolObservation<CreateFileResult> observation = await tool.CreateFileAsync(
            "src/App/User.cs",
            "public class User { public StringBuilder Buffer { get; } = new(); }");

        observation.Ok.ShouldBeFalse();
        observation.Error!.Message.ShouldContain("namespace 'System.Text'");
        observation.Error.Message.ShouldContain("using System.Text;");
    }

    private static CodeDiagnostic NotFound(string name) => new(
        "CS0246",
        CodeSeverity.Error,
        $"The type or namespace name '{name}' could not be found (are you missing a using directive or an assembly reference?)");

    private static string Project(string? reference = null) =>
        $"""
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup><TargetFramework>net6.0</TargetFramework></PropertyGroup>
          {(reference is null ? string.Empty : $"""<ItemGroup><ProjectReference Include="{reference}" /></ItemGroup>""")}
        </Project>
        """;
}
