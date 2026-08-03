using System.Text;
using GlassCoder.Wpf.Highlighting;

namespace GlassCoder.Wpf.Tests;

/// <summary>
/// The file viewer's scanner. Two properties matter more than any single classification: the
/// output covers the text exactly, and one malformed construct cannot recolour the rest of the
/// file. Both are asserted here over every grammar, because both are what the difference between
/// a readable file and a wall of one colour comes down to.
/// </summary>
public sealed class SyntaxHighlighterTests
{
    public static TheoryData<SyntaxLanguage, string> Samples => new()
    {
        { SyntaxLanguage.CSharp, "class A { /* x */ string s = \"hi\"; int n = 1; } // done" },
        { SyntaxLanguage.Xml, "<!-- c --><Grid x:Name=\"g\"><Child /></Grid>" },
        { SyntaxLanguage.Json, "{ \"a\": 1, \"b\": [true, null], \"c\": \"d\" }" },
        { SyntaxLanguage.Markdown, "# Title\n\n> quote\n\n```cs\ncode\n```\n\ntext `inline` more" },
        { SyntaxLanguage.None, "anything at all" },
    };

    /// <summary>
    /// The cover is total and ordered: every character belongs to exactly one token, and the
    /// tokens run start to end without gap or overlap. The renderer walks this list directly, so
    /// a hole in it is a hole in the displayed file.
    /// </summary>
    [Theory]
    [MemberData(nameof(Samples))]
    public void The_tokens_cover_the_text_exactly(SyntaxLanguage language, string text)
    {
        IReadOnlyList<SyntaxToken> tokens = SyntaxHighlighter.Tokenize(text, language);

        int at = 0;
        StringBuilder rebuilt = new();
        foreach (SyntaxToken token in tokens)
        {
            token.Start.ShouldBe(at, "tokens must be contiguous and in order");
            token.Length.ShouldBeGreaterThan(0);
            rebuilt.Append(text.AsSpan(token.Start, token.Length));
            at += token.Length;
        }

        at.ShouldBe(text.Length);
        rebuilt.ToString().ShouldBe(text);
    }

    [Fact]
    public void Csharp_keywords_comments_strings_and_numbers_are_told_apart()
    {
        string text = "public int x = 42; // note";

        KindOf(text, SyntaxLanguage.CSharp, "public").ShouldBe(SyntaxTokenKind.Keyword);
        KindOf(text, SyntaxLanguage.CSharp, "int").ShouldBe(SyntaxTokenKind.Keyword);
        KindOf(text, SyntaxLanguage.CSharp, "42").ShouldBe(SyntaxTokenKind.Number);
        KindOf(text, SyntaxLanguage.CSharp, "// note").ShouldBe(SyntaxTokenKind.Comment);
        KindOf(text, SyntaxLanguage.CSharp, "x").ShouldBe(SyntaxTokenKind.Plain);
    }

    /// <summary>A keyword inside a comment or a string is not a keyword.</summary>
    [Fact]
    public void Keywords_inside_comments_and_strings_stay_where_they_are()
    {
        string text = "// return\n\"class\"\n";

        KindOf(text, SyntaxLanguage.CSharp, "// return").ShouldBe(SyntaxTokenKind.Comment);
        KindOf(text, SyntaxLanguage.CSharp, "\"class\"").ShouldBe(SyntaxTokenKind.StringLiteral);
    }

    /// <summary>
    /// A verbatim string swallows escapes and doubled quotes rather than ending early - the
    /// difference between colouring a Windows path and colouring the rest of the file.
    /// </summary>
    [Fact]
    public void Verbatim_and_interpolated_strings_end_where_they_should()
    {
        KindOf("var p = @\"C:\\x\\y\"; int n = 1;", SyntaxLanguage.CSharp, "@\"C:\\x\\y\"")
            .ShouldBe(SyntaxTokenKind.StringLiteral);
        KindOf("var p = @\"say \"\"hi\"\"\"; int n = 1;", SyntaxLanguage.CSharp, "@\"say \"\"hi\"\"\"")
            .ShouldBe(SyntaxTokenKind.StringLiteral);
        KindOf("var p = $\"a{b}c\"; int n = 1;", SyntaxLanguage.CSharp, "$\"a{b}c\"")
            .ShouldBe(SyntaxTokenKind.StringLiteral);
    }

    /// <summary>
    /// An unterminated quote stops at the newline. Without that guard one stray apostrophe in a
    /// comment would paint every line below it as a string.
    /// </summary>
    [Fact]
    public void An_unclosed_string_does_not_run_past_its_line()
    {
        string text = "var s = \"oops\nint n = 42;";

        KindOf(text, SyntaxLanguage.CSharp, "42").ShouldBe(SyntaxTokenKind.Number);
        KindOf(text, SyntaxLanguage.CSharp, "int").ShouldBe(SyntaxTokenKind.Keyword);
    }

    [Fact]
    public void Markup_tags_attributes_and_values_are_told_apart()
    {
        string text = "<Grid x:Name=\"g\" />";

        KindOf(text, SyntaxLanguage.Xml, "<Grid").ShouldBe(SyntaxTokenKind.Tag);
        KindOf(text, SyntaxLanguage.Xml, "x:Name").ShouldBe(SyntaxTokenKind.Attribute);
        KindOf(text, SyntaxLanguage.Xml, "\"g\"").ShouldBe(SyntaxTokenKind.StringLiteral);
        KindOf(text, SyntaxLanguage.Xml, "/>").ShouldBe(SyntaxTokenKind.Tag);
    }

    /// <summary>A JSON property name is coloured differently from a string value.</summary>
    [Fact]
    public void Json_property_names_are_not_string_values()
    {
        string text = "{ \"name\": \"value\", \"n\": 12, \"ok\": true }";

        KindOf(text, SyntaxLanguage.Json, "\"name\"").ShouldBe(SyntaxTokenKind.Attribute);
        KindOf(text, SyntaxLanguage.Json, "\"value\"").ShouldBe(SyntaxTokenKind.StringLiteral);
        KindOf(text, SyntaxLanguage.Json, "12").ShouldBe(SyntaxTokenKind.Number);
        KindOf(text, SyntaxLanguage.Json, "true").ShouldBe(SyntaxTokenKind.Keyword);
    }

    [Fact]
    public void Markdown_headings_quotes_and_fences_are_told_apart()
    {
        string text = "# Title\n> quote\n```\ncode\n```\nplain\n";

        KindOf(text, SyntaxLanguage.Markdown, "# Title").ShouldBe(SyntaxTokenKind.Keyword);
        KindOf(text, SyntaxLanguage.Markdown, "> quote").ShouldBe(SyntaxTokenKind.Comment);
        KindOf(text, SyntaxLanguage.Markdown, "code").ShouldBe(SyntaxTokenKind.StringLiteral);
        KindOf(text, SyntaxLanguage.Markdown, "plain").ShouldBe(SyntaxTokenKind.Plain);
    }

    /// <summary>An unknown extension is shown, just not coloured.</summary>
    [Fact]
    public void An_unknown_language_yields_one_plain_span()
    {
        IReadOnlyList<SyntaxToken> tokens = SyntaxHighlighter.Tokenize("a b c", SyntaxLanguage.None);

        tokens.Count.ShouldBe(1);
        tokens[0].Kind.ShouldBe(SyntaxTokenKind.Plain);
    }

    [Fact]
    public void Empty_input_produces_no_tokens()
    {
        SyntaxHighlighter.Tokenize(string.Empty, SyntaxLanguage.CSharp).ShouldBeEmpty();
    }

    [Theory]
    [InlineData("Program.cs", SyntaxLanguage.CSharp)]
    [InlineData("MainWindow.xaml", SyntaxLanguage.Xml)]
    [InlineData("GlassCoder.Wpf.csproj", SyntaxLanguage.Xml)]
    [InlineData("appsettings.json", SyntaxLanguage.Json)]
    [InlineData("README.md", SyntaxLanguage.Markdown)]
    [InlineData("notes.txt", SyntaxLanguage.None)]
    [InlineData("noextension", SyntaxLanguage.None)]
    public void The_language_comes_from_the_extension(string path, SyntaxLanguage expected)
    {
        SyntaxLanguageDetector.FromPath(path).ShouldBe(expected);
    }

    /// <summary>
    /// The kind of the single token containing <paramref name="fragment"/>.
    /// <para>
    /// Containing, not starting at. Runs of unclassified text are merged into one plain span, so
    /// the identifier in <c>int x = 42</c> shares its token with the spaces and the equals sign
    /// either side. Asserting that a fragment lies wholly inside one token is the real contract -
    /// it is what stops a fragment being half one colour and half another.
    /// </para>
    /// </summary>
    private static SyntaxTokenKind KindOf(string text, SyntaxLanguage language, string fragment)
    {
        int at = text.IndexOf(fragment, StringComparison.Ordinal);
        at.ShouldBeGreaterThanOrEqualTo(0, $"the sample must contain '{fragment}'");

        foreach (SyntaxToken token in SyntaxHighlighter.Tokenize(text, language))
        {
            if (token.Start <= at && at + fragment.Length <= token.Start + token.Length)
            {
                return token.Kind;
            }
        }

        throw new Xunit.Sdk.XunitException($"'{fragment}' is split across tokens");
    }
}
