using System;
using System.Collections.Generic;

namespace GlassCoder.Wpf.Highlighting;

/// <summary>
/// A small hand-written scanner per grammar, enough to colour a file the way an editor would.
/// <para>
/// Lexical only: it knows a string from a comment from a keyword, and nothing about scope,
/// types or resolution. That is the whole design. A viewer needs to make code readable, not to
/// understand it, and the cost of the honest version is that <c>var</c> in a comment stays a
/// comment while a type name stays plain - which is what every editor did before language
/// servers, and is still perfectly readable.
/// </para>
/// </summary>
public static class SyntaxHighlighter
{
    private static readonly HashSet<string> CSharpKeywords = new(StringComparer.Ordinal)
    {
        "abstract", "as", "base", "bool", "break", "byte", "case", "catch", "char", "checked",
        "class", "const", "continue", "decimal", "default", "delegate", "do", "double", "else",
        "enum", "event", "explicit", "extern", "false", "finally", "fixed", "float", "for",
        "foreach", "goto", "if", "implicit", "in", "int", "interface", "internal", "is", "lock",
        "long", "namespace", "new", "null", "object", "operator", "out", "override", "params",
        "private", "protected", "public", "readonly", "ref", "return", "sbyte", "sealed",
        "short", "sizeof", "stackalloc", "static", "string", "struct", "switch", "this", "throw",
        "true", "try", "typeof", "uint", "ulong", "unchecked", "unsafe", "ushort", "using",
        "virtual", "void", "volatile", "while",

        // Contextual, and coloured unconditionally. Telling "record" the declaration from
        // "record" the variable needs a parser; colouring both is the trade every editor made.
        "add", "alias", "and", "ascending", "args", "async", "await", "by", "descending",
        "dynamic", "equals", "file", "from", "get", "global", "group", "init", "into", "join",
        "let", "nameof", "nint", "not", "notnull", "nuint", "on", "or", "orderby", "partial",
        "record", "remove", "required", "scoped", "select", "set", "unmanaged", "value", "var",
        "when", "where", "with", "yield",
    };

    private static readonly HashSet<string> JsonKeywords = new(StringComparer.Ordinal)
    {
        "true", "false", "null",
    };

    /// <summary>
    /// Classifies <paramref name="text"/>, returning a complete, ordered, non-overlapping cover
    /// of it. Every character belongs to exactly one token, so a renderer can walk the list and
    /// never has to work out what filled the gaps.
    /// </summary>
    /// <param name="text">The file's text.</param>
    /// <param name="language">The grammar to scan with.</param>
    public static IReadOnlyList<SyntaxToken> Tokenize(string text, SyntaxLanguage language)
    {
        ArgumentNullException.ThrowIfNull(text);

        List<SyntaxToken> classified = language switch
        {
            SyntaxLanguage.CSharp => ScanCSharp(text),
            SyntaxLanguage.Xml => ScanXml(text),
            SyntaxLanguage.Json => ScanJson(text),
            SyntaxLanguage.Markdown => ScanMarkdown(text),
            _ => [],
        };

        return Fill(text.Length, classified);
    }

    /// <summary>Inserts <see cref="SyntaxTokenKind.Plain"/> spans wherever the scanner found nothing.</summary>
    private static List<SyntaxToken> Fill(int length, List<SyntaxToken> classified)
    {
        List<SyntaxToken> all = new(classified.Count * 2 + 1);
        int at = 0;

        foreach (SyntaxToken token in classified)
        {
            // An empty line inside a fenced block scans as a zero-length span. Dropping it here
            // keeps the cover free of tokens that would render as empty runs.
            if (token.Length == 0)
            {
                continue;
            }

            if (token.Start > at)
            {
                all.Add(new SyntaxToken(at, token.Start - at, SyntaxTokenKind.Plain));
            }

            all.Add(token);
            at = token.Start + token.Length;
        }

        if (at < length)
        {
            all.Add(new SyntaxToken(at, length - at, SyntaxTokenKind.Plain));
        }

        return all;
    }

    private static List<SyntaxToken> ScanCSharp(string text)
    {
        List<SyntaxToken> tokens = [];
        int i = 0;
        bool atLineStart = true;

        while (i < text.Length)
        {
            char c = text[i];

            if (c == '\n')
            {
                atLineStart = true;
                i++;
                continue;
            }

            if (char.IsWhiteSpace(c))
            {
                i++;
                continue;
            }

            int start = i;

            // A directive only counts as one at the head of a line; "#" elsewhere is an operator
            // in no C# I know of, but anchoring it costs nothing and keeps interpolation holes
            // from being mistaken for one.
            if (atLineStart && c == '#')
            {
                i = ToEndOfLine(text, i);
                tokens.Add(new SyntaxToken(start, i - start, SyntaxTokenKind.Preprocessor));
                continue;
            }

            atLineStart = false;

            if (c == '/' && Peek(text, i + 1) == '/')
            {
                i = ToEndOfLine(text, i);
                tokens.Add(new SyntaxToken(start, i - start, SyntaxTokenKind.Comment));
                continue;
            }

            if (c == '/' && Peek(text, i + 1) == '*')
            {
                i += 2;
                while (i < text.Length && !(text[i] == '*' && Peek(text, i + 1) == '/'))
                {
                    i++;
                }

                i = Math.Min(text.Length, i + 2);
                tokens.Add(new SyntaxToken(start, i - start, SyntaxTokenKind.Comment));
                continue;
            }

            // The verbatim and interpolated prefixes in any order: @", $", $@" and @$".
            if (c is '@' or '$' && StartsQuotedAfterPrefix(text, i, out int quoteAt))
            {
                bool verbatim = text.AsSpan(i, quoteAt - i).Contains('@');
                i = verbatim ? SkipVerbatimString(text, quoteAt) : SkipString(text, quoteAt, '"');
                tokens.Add(new SyntaxToken(start, i - start, SyntaxTokenKind.StringLiteral));
                continue;
            }

            if (c == '"')
            {
                i = SkipString(text, i, '"');
                tokens.Add(new SyntaxToken(start, i - start, SyntaxTokenKind.StringLiteral));
                continue;
            }

            if (c == '\'')
            {
                i = SkipString(text, i, '\'');
                tokens.Add(new SyntaxToken(start, i - start, SyntaxTokenKind.StringLiteral));
                continue;
            }

            if (char.IsAsciiDigit(c))
            {
                i = SkipNumber(text, i);
                tokens.Add(new SyntaxToken(start, i - start, SyntaxTokenKind.Number));
                continue;
            }

            if (char.IsLetter(c) || c == '_')
            {
                i = SkipIdentifier(text, i);
                if (CSharpKeywords.Contains(text[start..i]))
                {
                    tokens.Add(new SyntaxToken(start, i - start, SyntaxTokenKind.Keyword));
                }

                continue;
            }

            i++;
        }

        return tokens;
    }

    private static List<SyntaxToken> ScanXml(string text)
    {
        List<SyntaxToken> tokens = [];
        int i = 0;

        while (i < text.Length)
        {
            if (text[i] != '<')
            {
                i++;
                continue;
            }

            int start = i;

            if (Matches(text, i, "<!--"))
            {
                i = SkipUntil(text, i + 4, "-->");
                tokens.Add(new SyntaxToken(start, i - start, SyntaxTokenKind.Comment));
                continue;
            }

            if (Matches(text, i, "<![CDATA["))
            {
                i = SkipUntil(text, i + 9, "]]>");
                tokens.Add(new SyntaxToken(start, i - start, SyntaxTokenKind.StringLiteral));
                continue;
            }

            if (Matches(text, i, "<?"))
            {
                i = SkipUntil(text, i + 2, "?>");
                tokens.Add(new SyntaxToken(start, i - start, SyntaxTokenKind.Preprocessor));
                continue;
            }

            if (Matches(text, i, "<!"))
            {
                i = SkipUntil(text, i + 2, ">");
                tokens.Add(new SyntaxToken(start, i - start, SyntaxTokenKind.Preprocessor));
                continue;
            }

            // "<" plus an optional "/" plus the element name, coloured as one so a closing tag
            // reads as the same thing as its opening one.
            i++;
            if (Peek(text, i) == '/')
            {
                i++;
            }

            i = SkipXmlName(text, i);
            tokens.Add(new SyntaxToken(start, i - start, SyntaxTokenKind.Tag));

            i = ScanTagBody(text, i, tokens);
        }

        return tokens;
    }

    /// <summary>Attributes and their values, from after the element name to the closing bracket.</summary>
    private static int ScanTagBody(string text, int i, List<SyntaxToken> tokens)
    {
        while (i < text.Length && text[i] != '>')
        {
            char c = text[i];

            if (c is '"' or '\'')
            {
                int start = i;
                i = SkipString(text, i, c);
                tokens.Add(new SyntaxToken(start, i - start, SyntaxTokenKind.StringLiteral));
                continue;
            }

            if (char.IsLetter(c) || c == '_')
            {
                int start = i;
                i = SkipXmlName(text, i);
                tokens.Add(new SyntaxToken(start, i - start, SyntaxTokenKind.Attribute));
                continue;
            }

            i++;
        }

        if (i < text.Length)
        {
            // The ">" belongs with the tag, and so does the "/" of a self-closing one.
            int close = text[i - 1] == '/' ? i - 1 : i;
            tokens.Add(new SyntaxToken(close, i - close + 1, SyntaxTokenKind.Tag));
            i++;
        }

        return i;
    }

    private static List<SyntaxToken> ScanJson(string text)
    {
        List<SyntaxToken> tokens = [];
        int i = 0;

        while (i < text.Length)
        {
            char c = text[i];
            int start = i;

            if (c == '"')
            {
                i = SkipString(text, i, '"');

                // A string is a property name when a colon is the next thing that is not space.
                int after = i;
                while (after < text.Length && char.IsWhiteSpace(text[after]))
                {
                    after++;
                }

                SyntaxTokenKind kind = Peek(text, after) == ':'
                    ? SyntaxTokenKind.Attribute
                    : SyntaxTokenKind.StringLiteral;

                tokens.Add(new SyntaxToken(start, i - start, kind));
                continue;
            }

            if (char.IsAsciiDigit(c) || (c == '-' && char.IsAsciiDigit(Peek(text, i + 1))))
            {
                i = SkipNumber(text, i + 1);
                tokens.Add(new SyntaxToken(start, i - start, SyntaxTokenKind.Number));
                continue;
            }

            if (char.IsLetter(c))
            {
                i = SkipIdentifier(text, i);
                if (JsonKeywords.Contains(text[start..i]))
                {
                    tokens.Add(new SyntaxToken(start, i - start, SyntaxTokenKind.Keyword));
                }

                continue;
            }

            i++;
        }

        return tokens;
    }

    /// <summary>
    /// Markdown, scanned a line at a time: headings, block quotes, fenced blocks and inline
    /// code. Emphasis is left alone on purpose - bold and italic want a font change rather than
    /// a colour, and half-applying them reads worse than not applying them.
    /// </summary>
    private static List<SyntaxToken> ScanMarkdown(string text)
    {
        List<SyntaxToken> tokens = [];
        int i = 0;
        bool fenced = false;

        while (i < text.Length)
        {
            int lineStart = i;
            int lineEnd = ToEndOfLine(text, i);
            i = lineEnd < text.Length ? lineEnd + 1 : lineEnd;

            int content = lineStart;
            while (content < lineEnd && (text[content] == ' ' || text[content] == '\t'))
            {
                content++;
            }

            bool isFence = Matches(text, content, "```") || Matches(text, content, "~~~");
            if (isFence || fenced)
            {
                tokens.Add(new SyntaxToken(lineStart, lineEnd - lineStart, SyntaxTokenKind.StringLiteral));
                if (isFence)
                {
                    fenced = !fenced;
                }

                continue;
            }

            if (content < lineEnd && text[content] == '#')
            {
                tokens.Add(new SyntaxToken(lineStart, lineEnd - lineStart, SyntaxTokenKind.Keyword));
                continue;
            }

            if (content < lineEnd && text[content] == '>')
            {
                tokens.Add(new SyntaxToken(lineStart, lineEnd - lineStart, SyntaxTokenKind.Comment));
                continue;
            }

            ScanInlineCode(text, content, lineEnd, tokens);
        }

        return tokens;
    }

    /// <summary>Backtick spans within one line. An unclosed backtick colours nothing.</summary>
    private static void ScanInlineCode(string text, int from, int to, List<SyntaxToken> tokens)
    {
        int i = from;
        while (i < to)
        {
            if (text[i] != '`')
            {
                i++;
                continue;
            }

            int close = text.IndexOf('`', i + 1);
            if (close < 0 || close >= to)
            {
                return;
            }

            tokens.Add(new SyntaxToken(i, close - i + 1, SyntaxTokenKind.StringLiteral));
            i = close + 1;
        }
    }

    private static char Peek(string text, int at) => at >= 0 && at < text.Length ? text[at] : '\0';

    private static bool Matches(string text, int at, string value) =>
        at >= 0 && at + value.Length <= text.Length && text.AsSpan(at, value.Length).SequenceEqual(value);

    private static int ToEndOfLine(string text, int i)
    {
        int at = text.IndexOf('\n', i);
        return at < 0 ? text.Length : at;
    }

    /// <summary>Advances past <paramref name="terminator"/>, or to the end when it never comes.</summary>
    private static int SkipUntil(string text, int i, string terminator)
    {
        int at = text.IndexOf(terminator, i, StringComparison.Ordinal);
        return at < 0 ? text.Length : at + terminator.Length;
    }

    /// <summary>
    /// A quoted run, honouring backslash escapes and stopping at a newline. The newline guard is
    /// what keeps one unterminated quote from colouring the rest of the file as a string.
    /// </summary>
    private static int SkipString(string text, int i, char quote)
    {
        i++;
        while (i < text.Length && text[i] != '\n')
        {
            if (text[i] == '\\' && i + 1 < text.Length)
            {
                i += 2;
                continue;
            }

            if (text[i] == quote)
            {
                return i + 1;
            }

            i++;
        }

        return i;
    }

    /// <summary>A verbatim string: no escapes, and a doubled quote stays inside it.</summary>
    private static int SkipVerbatimString(string text, int i)
    {
        i++;
        while (i < text.Length)
        {
            if (text[i] != '"')
            {
                i++;
                continue;
            }

            if (Peek(text, i + 1) == '"')
            {
                i += 2;
                continue;
            }

            return i + 1;
        }

        return i;
    }

    /// <summary>Whether a run of <c>@</c> and <c>$</c> at <paramref name="i"/> opens a string.</summary>
    private static bool StartsQuotedAfterPrefix(string text, int i, out int quoteAt)
    {
        int at = i;
        while (at < text.Length && text[at] is '@' or '$')
        {
            at++;
        }

        quoteAt = at;
        return at > i && Peek(text, at) == '"';
    }

    private static int SkipNumber(string text, int i)
    {
        while (i < text.Length &&
               (char.IsLetterOrDigit(text[i]) || text[i] == '_' ||
                (text[i] == '.' && char.IsAsciiDigit(Peek(text, i + 1))) ||
                ((text[i] is '+' or '-') && Peek(text, i - 1) is 'e' or 'E')))
        {
            i++;
        }

        return i;
    }

    private static int SkipIdentifier(string text, int i)
    {
        while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] == '_'))
        {
            i++;
        }

        return i;
    }

    /// <summary>An XML name, which unlike an identifier may carry a namespace, dots and hyphens.</summary>
    private static int SkipXmlName(string text, int i)
    {
        while (i < text.Length &&
               (char.IsLetterOrDigit(text[i]) || text[i] is '_' or '-' or '.' or ':'))
        {
            i++;
        }

        return i;
    }
}
