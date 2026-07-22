using System.Globalization;
using System.Text;

namespace GlassCoder.Tools.Changes;

/// <summary>What happened to one line.</summary>
public enum DiffKind
{
    /// <summary>Unchanged context.</summary>
    Context,

    /// <summary>Removed from the original.</summary>
    Removed,

    /// <summary>Added by the change.</summary>
    Added,
}

/// <summary>One line of a diff.</summary>
/// <param name="Kind">Added, removed or context.</param>
/// <param name="Text">The line, without its newline.</param>
/// <param name="OldLine">1-based line number in the original, or null when added.</param>
/// <param name="NewLine">1-based line number in the result, or null when removed.</param>
public sealed record DiffLine(DiffKind Kind, string Text, int? OldLine, int? NewLine)
{
    /// <summary>Renders the line the way a unified diff would.</summary>
    public override string ToString() =>
        Kind switch
        {
            DiffKind.Added => $"+{Text}",
            DiffKind.Removed => $"-{Text}",
            _ => $" {Text}",
        };
}

/// <summary>
/// A line-level diff (workplan task 27).
/// <para>
/// Changes are presented before/after because "the agent edited Pager.cs" is not reviewable and
/// a diff is (CLAUDE.md §10). This is a plain longest-common-subsequence diff - the UI needs
/// something correct and dependency-free, not something clever.
/// </para>
/// </summary>
public static class TextDiff
{
    /// <summary>Computes the diff between two texts.</summary>
    /// <param name="before">Original text.</param>
    /// <param name="after">Changed text.</param>
    /// <param name="contextLines">Unchanged lines to keep either side of a change. Negative keeps everything.</param>
    public static IReadOnlyList<DiffLine> Compute(string? before, string? after, int contextLines = 3)
    {
        string[] oldLines = Split(before);
        string[] newLines = Split(after);
        int[,] lengths = LongestCommonSubsequence(oldLines, newLines);

        List<DiffLine> all = [];
        int i = 0;
        int j = 0;

        while (i < oldLines.Length && j < newLines.Length)
        {
            if (string.Equals(oldLines[i], newLines[j], StringComparison.Ordinal))
            {
                all.Add(new DiffLine(DiffKind.Context, oldLines[i], i + 1, j + 1));
                i++;
                j++;
            }
            else if (lengths[i + 1, j] >= lengths[i, j + 1])
            {
                all.Add(new DiffLine(DiffKind.Removed, oldLines[i], i + 1, null));
                i++;
            }
            else
            {
                all.Add(new DiffLine(DiffKind.Added, newLines[j], null, j + 1));
                j++;
            }
        }

        while (i < oldLines.Length)
        {
            all.Add(new DiffLine(DiffKind.Removed, oldLines[i], i + 1, null));
            i++;
        }

        while (j < newLines.Length)
        {
            all.Add(new DiffLine(DiffKind.Added, newLines[j], null, j + 1));
            j++;
        }

        return contextLines < 0 ? all : Trim(all, contextLines);
    }

    /// <summary>Renders a diff as unified-diff text.</summary>
    public static string ToUnifiedText(IEnumerable<DiffLine> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);

        StringBuilder text = new();
        foreach (DiffLine line in lines)
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"{line}");
        }

        return text.ToString();
    }

    /// <summary>The 1-based line range the change touches, or null when nothing changed.</summary>
    public static (int Start, int End)? ChangedRange(IEnumerable<DiffLine> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);

        int start = int.MaxValue;
        int end = 0;

        foreach (DiffLine line in lines)
        {
            if (line.Kind == DiffKind.Context)
            {
                continue;
            }

            int number = line.NewLine ?? line.OldLine ?? 0;
            if (number == 0)
            {
                continue;
            }

            start = Math.Min(start, number);
            end = Math.Max(end, number);
        }

        return end == 0 ? null : (start, end);
    }

    private static string[] Split(string? text) =>
        string.IsNullOrEmpty(text) ? [] : text.ReplaceLineEndings("\n").Split('\n');

    private static int[,] LongestCommonSubsequence(string[] oldLines, string[] newLines)
    {
        int[,] lengths = new int[oldLines.Length + 1, newLines.Length + 1];

        for (int i = oldLines.Length - 1; i >= 0; i--)
        {
            for (int j = newLines.Length - 1; j >= 0; j--)
            {
                lengths[i, j] = string.Equals(oldLines[i], newLines[j], StringComparison.Ordinal)
                    ? lengths[i + 1, j + 1] + 1
                    : Math.Max(lengths[i + 1, j], lengths[i, j + 1]);
            }
        }

        return lengths;
    }

    /// <summary>Keeps changed lines plus a margin, so a one-line edit in a big file reads as one line.</summary>
    private static List<DiffLine> Trim(List<DiffLine> all, int contextLines)
    {
        bool[] keep = new bool[all.Count];

        for (int i = 0; i < all.Count; i++)
        {
            if (all[i].Kind == DiffKind.Context)
            {
                continue;
            }

            int from = Math.Max(0, i - contextLines);
            int to = Math.Min(all.Count - 1, i + contextLines);
            for (int k = from; k <= to; k++)
            {
                keep[k] = true;
            }
        }

        List<DiffLine> trimmed = [];
        for (int i = 0; i < all.Count; i++)
        {
            if (keep[i])
            {
                trimmed.Add(all[i]);
            }
        }

        return trimmed;
    }
}
