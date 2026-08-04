using GlassCoder.TestSupport;
using GlassCoder.Tools.FileSystem;
using GlassCoder.Tools.Verification;
using Microsoft.Extensions.Options;

namespace GlassCoder.Tools.Tests;

/// <summary>
/// Navigating by structure rather than by text (workplan task 47): the outline behind
/// <c>read_file(outline: true)</c>, and <c>find_symbol</c>.
/// <para>
/// Both read the syntax tree and nothing else, and that is the property worth protecting. A
/// declaration is in the file whether or not the project was ever built, so none of this inherits
/// the reference-resolution problem that makes the pre-write compile answer
/// <c>Inconclusive</c> - which is exactly why <c>find_references</c> is not here (task 48).
/// </para>
/// </summary>
public sealed class CodeStructureTests : IDisposable
{
    private const string Source = """
        namespace Demo;

        public sealed class Widget : IThing
        {
            private readonly int _size;

            public Widget(int size) => _size = size;

            public int Size => _size;

            public int Scale(int by)
            {
                return _size * by;
            }

            public enum Kind
            {
                Round,
                Square,
            }
        }
        """;

    private readonly TempWorkspace _workspace = new();

    public void Dispose() => _workspace.Dispose();

    // ── The outline ──

    [Fact]
    public void An_outline_lists_declarations_with_no_bodies()
    {
        IReadOnlyList<SourceSymbol> symbols = CodeStructure.Outline("src/Widget.cs", Source);

        symbols.Select(s => s.Name).ShouldContain("Widget");
        symbols.Select(s => s.Name).ShouldContain("Scale");
        symbols.Select(s => s.Name).ShouldContain("_size");

        SourceSymbol scale = symbols.Single(s => s.Name == "Scale");
        scale.Kind.ShouldBe("method");
        scale.Signature.ShouldBe("public int Scale(int by)");
        scale.Signature.ShouldNotContain("return", Case.Sensitive, "an outline is a list, not a listing");
    }

    [Fact]
    public void Every_declaration_carries_the_range_to_read_it_back()
    {
        // The line numbers are the payload: they turn "orient in this file" into one ranged read
        // rather than a read of the whole thing.
        IReadOnlyList<SourceSymbol> symbols = CodeStructure.Outline("src/Widget.cs", Source);
        SourceSymbol scale = symbols.Single(s => s.Name == "Scale");

        string[] lines = Source.ReplaceLineEndings("\n").Split('\n');
        lines[scale.Line - 1].ShouldContain("public int Scale(int by)");
        lines[scale.EndLine - 1].Trim().ShouldBe("}");
    }

    [Theory]
    [InlineData("Widget", "class")]
    [InlineData("Kind", "enum")]
    [InlineData("Size", "property")]
    [InlineData("_size", "field")]
    [InlineData("Demo", "namespace")]
    public void Each_declaration_says_what_it_is(string name, string kind)
    {
        CodeStructure.Outline("src/Widget.cs", Source).First(s => s.Name == name).Kind.ShouldBe(kind);
    }

    [Fact]
    public void A_nested_declaration_is_rendered_under_its_container()
    {
        string rendered = CodeStructure.Render(CodeStructure.Outline("src/Widget.cs", Source));

        rendered.ShouldContain("class Widget");
        // The namespace is depth 0, the class depth 1, its members depth 2.
        rendered.Split('\n').First(l => l.Contains("Scale", StringComparison.Ordinal))
            .ShouldContain("      public int Scale");
    }

    [Fact]
    public void One_field_declaration_that_names_several_variables_yields_them_all()
    {
        // A search that missed the second would be silently incomplete.
        IReadOnlyList<SourceSymbol> symbols = CodeStructure.Outline(
            "src/Many.cs", "class Many { private int a, b, c; }");

        symbols.Select(s => s.Name).ShouldBe(["Many", "a", "b", "c"]);
    }

    [Fact]
    public void A_file_that_does_not_compile_still_has_an_outline()
    {
        // The whole point of using the syntax tree: this file references a type that does not
        // exist, and the declarations in it are still facts.
        IReadOnlyList<SourceSymbol> symbols = CodeStructure.Outline(
            "src/Broken.cs", "class Broken { public Missing Thing() { return null; } }");

        symbols.Select(s => s.Name).ShouldBe(["Broken", "Thing"]);
    }

    [Fact]
    public void Reading_a_file_as_an_outline_returns_the_shape_instead_of_the_code()
    {
        _workspace.WriteFile("src/Widget.cs", Source);

        ToolObservation<ReadFileResult> observation =
            new ReadFileTool(_workspace.Guard(), TempWorkspace.Wrap(new ToolsOptions()))
                .ReadFile("src/Widget.cs", outline: true);

        observation.Ok.ShouldBeTrue(observation.Error?.Message);
        observation.Data!.Content.ShouldContain("public int Scale(int by)");
        observation.Data.Content.ShouldNotContain("return _size * by;", Case.Sensitive);
        observation.Summary.ShouldContain("declaration");
    }

    [Fact]
    public void An_outline_of_something_that_is_not_C_sharp_is_refused_rather_than_guessed()
    {
        _workspace.WriteFile("src/notes.md", "# Notes\n");

        ToolObservation<ReadFileResult> observation =
            new ReadFileTool(_workspace.Guard(), TempWorkspace.Wrap(new ToolsOptions()))
                .ReadFile("src/notes.md", outline: true);

        observation.Ok.ShouldBeFalse();
        observation.Error!.Code.ShouldBe(ToolErrorCodes.InvalidArgument);
        observation.Error.Hint.ShouldContain("without outline");
    }

    // ── find_symbol ──

    [Fact]
    public void A_symbol_is_found_by_its_exact_name_with_the_file_and_line()
    {
        _workspace.WriteFile("src/Widget.cs", Source);
        _workspace.WriteFile("src/Caller.cs", "class Caller { int Use(Widget w) => w.Scale(2); }");

        ToolObservation<FindSymbolResult> observation = Tool().FindSymbol("Scale");

        observation.Ok.ShouldBeTrue(observation.Error?.Message);
        SourceSymbol found = observation.Data!.Symbols.ShouldHaveSingleItem();
        found.Path.ShouldBe("src/Widget.cs", "the call site in Caller.cs is a use, not a declaration");
        found.Kind.ShouldBe("method");
    }

    [Fact]
    public void An_exact_match_is_listed_before_a_partial_one()
    {
        // A search for 'Size' should lead with Size, not with SizeOfEverything.
        _workspace.WriteFile("src/A.cs", "class A { public int SizeOfEverything => 1; }");
        _workspace.WriteFile("src/B.cs", "class B { public int Size => 1; }");

        ToolObservation<FindSymbolResult> observation = Tool().FindSymbol("Size");

        observation.Data!.Symbols[0].Name.ShouldBe("Size");
        observation.Data.Total.ShouldBe(2);
    }

    [Fact]
    public void A_name_that_is_declared_nowhere_says_so_rather_than_returning_nothing()
    {
        _workspace.WriteFile("src/Widget.cs", Source);

        ToolObservation<FindSymbolResult> observation = Tool().FindSymbol("Nonexistent");

        observation.Ok.ShouldBeFalse();
        observation.Error!.Code.ShouldBe(ToolErrorCodes.NotFound);
        observation.Error.Hint.ShouldContain("package");
    }

    [Fact]
    public void Generated_output_is_not_searched()
    {
        // The deny globs hide bin and obj from the agent; a symbol search that read them would
        // point it at a copy of its own code.
        _workspace.WriteFile("src/obj/Debug/Widget.g.cs", "class Ghost { }");
        _workspace.WriteFile("src/Widget.cs", Source);

        Tool().FindSymbol("Ghost").Ok.ShouldBeFalse();
    }

    private FindSymbolTool Tool()
    {
        Guardrails.PathGuard guard = _workspace.Guard();
        return new FindSymbolTool(
            guard,
            new RoslynCodeAnalyzer(guard, Options.Create(new VerificationOptions())),
            TempWorkspace.Wrap(new ToolsOptions()));
    }
}
