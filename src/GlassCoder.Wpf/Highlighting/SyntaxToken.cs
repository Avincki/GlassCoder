using System;
using System.IO;

namespace GlassCoder.Wpf.Highlighting;

/// <summary>
/// What a span of source text is, as far as colouring cares. Deliberately small: these are the
/// categories every editor colour scheme agrees on, so the viewer can stay recognisable without
/// pretending to understand the language semantically.
/// </summary>
public enum SyntaxTokenKind
{
    /// <summary>Anything not otherwise classified - identifiers, operators, whitespace.</summary>
    Plain,

    /// <summary>A reserved word, and the literals <c>true</c>/<c>false</c>/<c>null</c>.</summary>
    Keyword,

    /// <summary>Line, block or markup comment.</summary>
    Comment,

    /// <summary>String and character literals, and fenced code in Markdown.</summary>
    StringLiteral,

    /// <summary>Numeric literal.</summary>
    Number,

    /// <summary>A directive: <c>#if</c> in C#, the <c>&lt;?xml</c> declaration in markup.</summary>
    Preprocessor,

    /// <summary>A markup element name, with its angle brackets.</summary>
    Tag,

    /// <summary>A markup attribute name, or a JSON property name.</summary>
    Attribute,
}

/// <summary>A classified span, as an offset into the text it was scanned from.</summary>
/// <param name="Start">Zero-based offset of the first character.</param>
/// <param name="Length">How many characters the span covers.</param>
/// <param name="Kind">What the span is.</param>
public readonly record struct SyntaxToken(int Start, int Length, SyntaxTokenKind Kind);

/// <summary>The grammars the viewer can colour. Anything else is shown as plain text.</summary>
public enum SyntaxLanguage
{
    /// <summary>No colouring - the file is shown as it is.</summary>
    None,

    /// <summary>C#.</summary>
    CSharp,

    /// <summary>Angle-bracket markup: XML, XAML, HTML and the MSBuild project files.</summary>
    Xml,

    /// <summary>JSON.</summary>
    Json,

    /// <summary>Markdown.</summary>
    Markdown,
}

/// <summary>Picks a grammar from a file name.</summary>
public static class SyntaxLanguageDetector
{
    /// <summary>
    /// The grammar to colour <paramref name="path"/> with, by extension.
    /// <para>
    /// Extension only, with no sniffing of the content. A viewer that guessed would eventually
    /// guess wrong on a file the user knows the type of, and being confidently mis-coloured is
    /// worse than being plain.
    /// </para>
    /// </summary>
    public static SyntaxLanguage FromPath(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        return Path.GetExtension(path).ToUpperInvariant() switch
        {
            ".CS" or ".CSX" => SyntaxLanguage.CSharp,
            ".XAML" or ".XML" or ".HTML" or ".HTM" or ".CSPROJ" or ".PROPS" or ".TARGETS"
                or ".CONFIG" or ".SVG" or ".RESX" => SyntaxLanguage.Xml,
            ".JSON" or ".JSONC" or ".CCGPROJ" => SyntaxLanguage.Json,
            ".MD" or ".MARKDOWN" => SyntaxLanguage.Markdown,
            _ => SyntaxLanguage.None,
        };
    }
}
