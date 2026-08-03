using System;
using System.Collections.Generic;

namespace GlassCoder.Wpf.Highlighting;

/// <summary>A run of text on one line that shares a colour.</summary>
/// <param name="Text">The text, with no line break and no trailing carriage return.</param>
/// <param name="Kind">What to colour it as.</param>
public sealed record HighlightedSpan(string Text, SyntaxTokenKind Kind);

/// <summary>
/// Turns a file's text into coloured spans grouped by line - the shape a viewer with a line
/// number gutter needs, and the reason the scanner's tokens cannot be used directly: a block
/// comment or a verbatim string is one token spanning many lines, and the gutter has to be able
/// to count them.
/// </summary>
public static class HighlightedDocument
{
    /// <summary>
    /// Scans <paramref name="text"/> and slices the result into lines.
    /// <para>
    /// Line breaks are the separators rather than content, so a file of <c>n</c> newlines yields
    /// <c>n + 1</c> lines - the last of them empty when the file ends with a break, which is what
    /// an editor shows for a well-formed text file.
    /// </para>
    /// </summary>
    /// <param name="text">The file's text.</param>
    /// <param name="language">The grammar to colour with.</param>
    public static IReadOnlyList<IReadOnlyList<HighlightedSpan>> Build(string text, SyntaxLanguage language)
    {
        ArgumentNullException.ThrowIfNull(text);

        IReadOnlyList<SyntaxToken> tokens = SyntaxHighlighter.Tokenize(text, language);
        List<IReadOnlyList<HighlightedSpan>> lines = [];
        List<HighlightedSpan> current = [];

        foreach (SyntaxToken token in tokens)
        {
            int end = token.Start + token.Length;
            int at = token.Start;

            while (at < end)
            {
                int breakAt = text.IndexOf('\n', at);
                if (breakAt < 0 || breakAt >= end)
                {
                    Append(current, text, at, end, token.Kind);
                    break;
                }

                Append(current, text, at, breakAt, token.Kind);
                lines.Add(current);
                current = [];
                at = breakAt + 1;
            }
        }

        lines.Add(current);
        return lines;
    }

    private static void Append(
        List<HighlightedSpan> line, string text, int from, int to, SyntaxTokenKind kind)
    {
        // The carriage half of a CRLF is a separator too, and rendering it puts a box glyph at
        // the end of every line of a Windows-authored file.
        if (to > from && text[to - 1] == '\r')
        {
            to--;
        }

        if (to > from)
        {
            line.Add(new HighlightedSpan(text[from..to], kind));
        }
    }
}
