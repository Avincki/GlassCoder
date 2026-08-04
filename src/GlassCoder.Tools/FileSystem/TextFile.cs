using System.Text;

namespace GlassCoder.Tools.FileSystem;

/// <summary>
/// Line endings, and the one place that knows a file's are not necessarily the model's.
/// <para>
/// A language model emits <c>\n</c>. A file written by <c>dotnet new</c> on Windows holds
/// <c>\r\n</c>. An ordinal match between the two never succeeds, however carefully the model
/// copies what it was shown - and the failure says "the text to replace was not found", which
/// sends it back to re-read a file it had already read correctly.
/// </para>
/// <para>
/// So matching is done on a normalised copy and mapped back to the original, and replacement
/// text is rewritten to whatever the file already uses. The alternative - demanding the model
/// produce exact carriage returns - is a contract no model reliably honours.
/// </para>
/// </summary>
public static class TextFile
{
    /// <summary>Windows line ending.</summary>
    public const string Crlf = "\r\n";

    /// <summary>Unix line ending.</summary>
    public const string Lf = "\n";

    /// <summary>How a file's lines end, as reported to the model.</summary>
    public static string DescribeEndings(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        (int crlf, int lf) = Count(text);
        return (crlf, lf) switch
        {
            (0, 0) => "none",
            (> 0, 0) => "crlf",
            (0, > 0) => "lf",
            _ => "mixed",
        };
    }

    /// <summary>
    /// The line ending a file mostly uses, and therefore the one anything written into it should
    /// use. Falls back to the platform's for a file with no line breaks at all.
    /// </summary>
    public static string DominantNewLine(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        (int crlf, int lf) = Count(text);
        if (crlf == 0 && lf == 0)
        {
            return Environment.NewLine;
        }

        return crlf >= lf ? Crlf : Lf;
    }

    /// <summary>Rewrites <paramref name="text"/> so every line ends the way <paramref name="newLine"/> says.</summary>
    public static string WithNewLine(string text, string newLine)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(newLine);

        return text.ReplaceLineEndings(newLine);
    }

    /// <summary>
    /// Finds <paramref name="needle"/> in <paramref name="haystack"/>, ignoring how either one
    /// ends its lines, and reports the span in the <em>original</em> haystack.
    /// </summary>
    /// <param name="haystack">The file's real text.</param>
    /// <param name="needle">What the model asked to replace.</param>
    /// <param name="occurrences">How many times it appears - the ambiguity check needs this.</param>
    /// <returns>The span to replace, or null when there is no match.</returns>
    public static Match? Find(string haystack, string needle, out int occurrences)
    {
        ArgumentNullException.ThrowIfNull(haystack);
        ArgumentNullException.ThrowIfNull(needle);

        occurrences = 0;
        if (needle.Length == 0)
        {
            return null;
        }

        // The exact path first. When the model does produce byte-identical text - the common
        // case for a single line with no break in it - this costs one IndexOf and nothing else.
        int exact = haystack.IndexOf(needle, StringComparison.Ordinal);
        if (exact >= 0)
        {
            occurrences = CountOccurrences(haystack, needle);
            return occurrences == 1 ? new Match(exact, needle.Length) : null;
        }

        (string normalisedHaystack, int[] map) = Normalise(haystack);
        string normalisedNeedle = needle.ReplaceLineEndings(Lf);

        occurrences = CountOccurrences(normalisedHaystack, normalisedNeedle);
        if (occurrences != 1)
        {
            return null;
        }

        int start = normalisedHaystack.IndexOf(normalisedNeedle, StringComparison.Ordinal);
        int end = start + normalisedNeedle.Length;

        // map[i] is where normalised character i began in the original, so the end of the span
        // is where the next one begins - or the end of the file when there is no next one.
        int originalStart = map[start];
        int originalEnd = end < map.Length ? map[end] : haystack.Length;

        return new Match(originalStart, originalEnd - originalStart);
    }

    /// <summary>
    /// Collapses every line ending to <c>\n</c>, and records where each surviving character
    /// began in the original so a match can be mapped back.
    /// </summary>
    public static (string Text, int[] Map) Normalise(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        StringBuilder builder = new(text.Length);
        int[] map = new int[text.Length + 1];
        int written = 0;

        for (int i = 0; i < text.Length; i++)
        {
            map[written] = i;
            written++;

            if (text[i] == '\r')
            {
                builder.Append('\n');

                // A lone \r is a line ending too, but \r\n is one ending rather than two.
                if (i + 1 < text.Length && text[i + 1] == '\n')
                {
                    i++;
                }
            }
            else
            {
                builder.Append(text[i]);
            }
        }

        map[written] = text.Length;
        return (builder.ToString(), map[..(written + 1)]);
    }

    /// <summary>How many times <paramref name="needle"/> appears, counting non-overlapping hits.</summary>
    public static int CountOccurrences(string haystack, string needle)
    {
        ArgumentNullException.ThrowIfNull(haystack);
        ArgumentNullException.ThrowIfNull(needle);

        if (needle.Length == 0)
        {
            return 0;
        }

        int count = 0;
        int index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }

    private static (int Crlf, int Lf) Count(string text)
    {
        int crlf = 0;
        int lf = 0;

        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n')
            {
                if (i > 0 && text[i - 1] == '\r')
                {
                    crlf++;
                }
                else
                {
                    lf++;
                }
            }
        }

        return (crlf, lf);
    }

    /// <summary>A span of the original text.</summary>
    /// <param name="Start">Index into the original.</param>
    /// <param name="Length">Length in the original, which may differ from the needle's.</param>
    public readonly record struct Match(int Start, int Length);
}
