using System.ComponentModel;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace GlassCoder.Tools.Verification;

/// <summary>One declaration found in a source file.</summary>
/// <param name="Path">Repo-relative file holding the declaration.</param>
/// <param name="Line">1-based line the declaration starts on.</param>
/// <param name="EndLine">1-based line it ends on, so a caller can read exactly this much.</param>
/// <param name="Kind">What sort of declaration: class, method, property and so on.</param>
/// <param name="Name">The declared name, without its container.</param>
/// <param name="Signature">The declaration itself, with no body.</param>
/// <param name="Depth">How deeply nested it is, for rendering.</param>
public sealed record SourceSymbol(
    [property: Description("Repo-relative file holding the declaration.")] string Path,
    [property: Description("1-based line the declaration starts on.")] int Line,
    [property: Description("1-based line it ends on. Pass this range to read_file for the body.")] int EndLine,
    [property: Description("What sort of declaration: class, method, property, field and so on.")] string Kind,
    [property: Description("The declared name, without its container.")] string Name,
    [property: Description("The declaration with no body.")] string Signature,
    [property: Description("How deeply nested the declaration is.")] int Depth);

/// <summary>
/// Structure read out of C# source, for navigating by shape rather than by text (workplan
/// task 47).
/// <para>
/// This needs only the syntax tree, and that is the whole point. A declaration is in the file
/// whether or not its dependencies were ever built, so none of this inherits the
/// reference-resolution problem that made the pre-write compile answer <c>Inconclusive</c>
/// (task 45) and that keeps <c>find_references</c> unbuilt (task 48). There is no failure mode
/// here where a confident answer is wrong.
/// </para>
/// </summary>
public static class CodeStructure
{
    /// <summary>Parse options matching the analyzer's, so the two never disagree about a file.</summary>
    private static readonly CSharpParseOptions ParseOptions =
        new(LanguageVersion.Preview, DocumentationMode.None);

    /// <summary>Parses text and returns its declarations, outermost first, in source order.</summary>
    public static IReadOnlyList<SourceSymbol> Outline(string relativePath, string text, int max = int.MaxValue) =>
        Outline(relativePath, CSharpSyntaxTree.ParseText(text, ParseOptions, path: relativePath), max);

    /// <summary>Returns the declarations in an already-parsed tree.</summary>
    public static IReadOnlyList<SourceSymbol> Outline(string relativePath, SyntaxTree tree, int max = int.MaxValue)
    {
        ArgumentNullException.ThrowIfNull(tree);

        SourceText source = tree.GetText();
        List<SourceSymbol> symbols = [];

        foreach (SyntaxNode node in tree.GetRoot().DescendantNodes(descendIntoTrivia: false))
        {
            if (node is not MemberDeclarationSyntax member)
            {
                continue;
            }

            foreach (string name in Names(member))
            {
                if (symbols.Count >= max)
                {
                    return symbols;
                }

                symbols.Add(new SourceSymbol(
                    relativePath,
                    source.Lines.GetLinePosition(member.SpanStart).Line + 1,
                    source.Lines.GetLinePosition(member.Span.End).Line + 1,
                    Kind(member),
                    name,
                    Signature(member),
                    Depth(member)));
            }
        }

        return symbols;
    }

    /// <summary>
    /// Renders an outline for a model to read: the line number, then the declaration, indented by
    /// nesting. Line numbers are the payload - they are what turns "orient in this file" into one
    /// <c>read_file</c> call with a range rather than a read of the whole thing.
    /// </summary>
    public static string Render(IReadOnlyList<SourceSymbol> symbols)
    {
        ArgumentNullException.ThrowIfNull(symbols);

        StringBuilder text = new();
        foreach (SourceSymbol symbol in symbols)
        {
            text.Append(symbol.Line.ToString(System.Globalization.CultureInfo.InvariantCulture).PadLeft(5))
                .Append("  ")
                .Append(' ', symbol.Depth * 2)
                .AppendLine(symbol.Signature);
        }

        return text.ToString().TrimEnd();
    }

    /// <summary>
    /// The names a declaration introduces - all of them, because one field declaration can
    /// declare several and a search that missed the second would be silently incomplete.
    /// </summary>
    private static IEnumerable<string> Names(MemberDeclarationSyntax member)
    {
        switch (member)
        {
            case BaseNamespaceDeclarationSyntax ns:
                yield return ns.Name.ToString();
                break;
            case BaseTypeDeclarationSyntax type:
                yield return type.Identifier.ValueText;
                break;
            case DelegateDeclarationSyntax @delegate:
                yield return @delegate.Identifier.ValueText;
                break;
            case MethodDeclarationSyntax method:
                yield return method.Identifier.ValueText;
                break;
            case ConstructorDeclarationSyntax constructor:
                yield return constructor.Identifier.ValueText;
                break;
            case DestructorDeclarationSyntax destructor:
                yield return destructor.Identifier.ValueText;
                break;
            case OperatorDeclarationSyntax @operator:
                yield return "operator " + @operator.OperatorToken.ValueText;
                break;
            case ConversionOperatorDeclarationSyntax:
                yield return "operator";
                break;
            case PropertyDeclarationSyntax property:
                yield return property.Identifier.ValueText;
                break;
            case IndexerDeclarationSyntax:
                yield return "this[]";
                break;
            case EventDeclarationSyntax @event:
                yield return @event.Identifier.ValueText;
                break;
            case EventFieldDeclarationSyntax eventField:
                foreach (VariableDeclaratorSyntax variable in eventField.Declaration.Variables)
                {
                    yield return variable.Identifier.ValueText;
                }

                break;
            case FieldDeclarationSyntax field:
                foreach (VariableDeclaratorSyntax variable in field.Declaration.Variables)
                {
                    yield return variable.Identifier.ValueText;
                }

                break;
            case EnumMemberDeclarationSyntax enumMember:
                yield return enumMember.Identifier.ValueText;
                break;
        }
    }

    private static string Kind(MemberDeclarationSyntax member) => member switch
    {
        BaseNamespaceDeclarationSyntax => "namespace",
        RecordDeclarationSyntax record =>
            record.ClassOrStructKeyword.IsKind(SyntaxKind.StructKeyword) ? "record struct" : "record",
        ClassDeclarationSyntax => "class",
        StructDeclarationSyntax => "struct",
        InterfaceDeclarationSyntax => "interface",
        EnumDeclarationSyntax => "enum",
        DelegateDeclarationSyntax => "delegate",
        ConstructorDeclarationSyntax => "constructor",
        DestructorDeclarationSyntax => "destructor",
        OperatorDeclarationSyntax or ConversionOperatorDeclarationSyntax => "operator",
        MethodDeclarationSyntax => "method",
        PropertyDeclarationSyntax => "property",
        IndexerDeclarationSyntax => "indexer",
        EventDeclarationSyntax or EventFieldDeclarationSyntax => "event",
        EnumMemberDeclarationSyntax => "enum member",
        _ => "field",
    };

    /// <summary>
    /// The declaration without its body, whitespace collapsed. Taking the source between the
    /// start and whatever opens the body handles every member shape the same way - block bodies,
    /// expression bodies, accessor lists, positional records and bare semicolons alike - rather
    /// than reassembling a signature from parts and getting one member kind subtly wrong.
    /// </summary>
    private static string Signature(MemberDeclarationSyntax member)
    {
        int start = member.SpanStart;
        int end = Math.Max(start, BodyStart(member));
        return Collapse(member.SyntaxTree.GetText().ToString(TextSpan.FromBounds(start, end)));
    }

    private static int BodyStart(MemberDeclarationSyntax member)
    {
        switch (member)
        {
            case FileScopedNamespaceDeclarationSyntax scoped:
                return scoped.SemicolonToken.SpanStart;
            case NamespaceDeclarationSyntax block:
                return block.OpenBraceToken.SpanStart;
            case BaseTypeDeclarationSyntax type when type.OpenBraceToken.IsKind(SyntaxKind.OpenBraceToken):
                return type.OpenBraceToken.SpanStart;
            case BaseMethodDeclarationSyntax method:
                return method.Body?.SpanStart ?? method.ExpressionBody?.SpanStart ?? method.Span.End;
            case PropertyDeclarationSyntax property:
                return property.AccessorList?.SpanStart ?? property.ExpressionBody?.SpanStart ?? property.Span.End;
            case IndexerDeclarationSyntax indexer:
                return indexer.AccessorList?.SpanStart ?? indexer.ExpressionBody?.SpanStart ?? indexer.Span.End;
            case EventDeclarationSyntax { AccessorList: { } accessors }:
                return accessors.SpanStart;
            default:
                return member.Span.End;
        }
    }

    private static int Depth(SyntaxNode node)
    {
        int depth = 0;
        for (SyntaxNode? parent = node.Parent; parent is not null; parent = parent.Parent)
        {
            if (parent is BaseTypeDeclarationSyntax or BaseNamespaceDeclarationSyntax)
            {
                depth++;
            }
        }

        return depth;
    }

    /// <summary>Folds a multi-line declaration onto one line - an outline is a list, not a listing.</summary>
    private static string Collapse(string text)
    {
        StringBuilder collapsed = new(text.Length);
        bool space = false;

        foreach (char character in text)
        {
            if (char.IsWhiteSpace(character))
            {
                space = collapsed.Length > 0;
                continue;
            }

            if (space)
            {
                collapsed.Append(' ');
                space = false;
            }

            collapsed.Append(character);
        }

        return collapsed.ToString();
    }
}
