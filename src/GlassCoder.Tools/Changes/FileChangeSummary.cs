namespace GlassCoder.Tools.Changes;

/// <summary>Net lines a file gained and lost over the session.</summary>
/// <param name="LinesAdded">Lines present now that were not there before the first applied change.</param>
/// <param name="LinesRemoved">Lines there before the first applied change that are gone now.</param>
public readonly record struct FileChangeStats(int LinesAdded, int LinesRemoved);

/// <summary>
/// Per-file rollup of the change log (workplan task 39), for surfaces that ask "what happened to
/// this file" rather than "what changes were proposed".
/// <para>
/// Counts are <em>net</em>: the diff from the first applied change's before-text to the last
/// applied change's after-text. Summing per-change counts would double-count a line the agent
/// wrote and then rewrote, and a change that was applied and later reverted leaves
/// <see cref="ChangeStatus.Applied"/> and so drops out on its own. A file whose applied changes
/// cancelled out exactly is still reported, at zero: it was modified this session, and the
/// caller decides whether "touched, net nothing" is worth showing.
/// </para>
/// </summary>
public static class FileChangeSummary
{
    /// <summary>
    /// Net stats for every file with at least one applied change, keyed by the path exactly as
    /// the change log recorded it. Changes must arrive in proposal order, which is what
    /// <see cref="IChangeLog.All"/> returns.
    /// </summary>
    public static IReadOnlyDictionary<string, FileChangeStats> Summarise(IEnumerable<CodeChange> changes)
    {
        ArgumentNullException.ThrowIfNull(changes);

        Dictionary<string, FileChangeStats> stats = new(StringComparer.Ordinal);
        foreach (IGrouping<string, CodeChange> file in changes
            .Where(change => change.Status == ChangeStatus.Applied)
            .GroupBy(change => change.Path, StringComparer.Ordinal))
        {
            stats[file.Key] = Net(file.First(), file.Last());
        }

        return stats;
    }

    /// <summary>
    /// Net stats for one file, or null when it has no applied change - the incremental form,
    /// for a caller reacting to a single <see cref="IChangeLog.Changed"/> event.
    /// </summary>
    public static FileChangeStats? ForPath(IEnumerable<CodeChange> changes, string path)
    {
        ArgumentNullException.ThrowIfNull(changes);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        CodeChange? first = null;
        CodeChange? last = null;
        foreach (CodeChange change in changes)
        {
            if (change.Status != ChangeStatus.Applied ||
                !string.Equals(change.Path, path, StringComparison.Ordinal))
            {
                continue;
            }

            first ??= change;
            last = change;
        }

        return first is null ? null : Net(first, last!);
    }

    private static FileChangeStats Net(CodeChange first, CodeChange last)
    {
        int added = 0;
        int removed = 0;
        foreach (DiffLine line in TextDiff.Compute(first.BeforeText, last.AfterText))
        {
            if (line.Kind == DiffKind.Added)
            {
                added++;
            }
            else if (line.Kind == DiffKind.Removed)
            {
                removed++;
            }
        }

        return new FileChangeStats(added, removed);
    }
}
