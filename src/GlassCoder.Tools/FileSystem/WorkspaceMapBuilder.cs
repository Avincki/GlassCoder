using System.Globalization;
using System.Text;
using GlassCoder.Tools.Guardrails;

namespace GlassCoder.Tools.FileSystem;

/// <summary>
/// Renders the workspace as one bounded block for the start of a run: every file the guard
/// allows, then the contents of the smallest ones while the budget lasts.
/// <para>
/// The context policy is retrieval-first (CLAUDE.md §12), and this is its one deliberate
/// exception. Run 48a7af6a spent six of its twenty-five steps on glob, list_projects and
/// read_file over a five-file workspace, then re-read two of those files when they fell out of
/// the window - all of it answerable at step 0 for a fraction of one step's tokens. A large
/// repository degrades gracefully to a capped listing; the agent still retrieves on demand,
/// it just no longer spends steps discovering that the tree is small.
/// </para>
/// </summary>
public sealed class WorkspaceMapBuilder
{
    /// <summary>Listing cap. Past this the map is orientation, not inventory, and says so.</summary>
    private const int MaxListedFiles = 200;

    /// <summary>Matcher visits allowed while listing. Generous: guard-denied hits (bin, obj)
    /// spend visits without producing entries.</summary>
    private const int MaxVisitedFiles = 5000;

    /// <summary>Largest file the inline pass will consider. Anything bigger is what
    /// read_file's windowing is for.</summary>
    private const int MaxInlineFileBytes = 16_384;

    private readonly IPathGuard _guard;

    /// <summary>Creates the builder.</summary>
    public WorkspaceMapBuilder(IPathGuard guard)
    {
        _guard = guard;
    }

    /// <summary>
    /// Builds the map, spending at most <paramref name="characterBudget"/> characters. Returns
    /// an empty string when there is nothing to show or no budget to show it in.
    /// </summary>
    public string Build(int characterBudget, CancellationToken cancellationToken = default)
    {
        if (characterBudget <= 0 || !Directory.Exists(_guard.RepoRoot))
        {
            return string.Empty;
        }

        List<(string RelativePath, string FullPath, long Bytes)> files = [];
        bool listingCapped = false;
        try
        {
            foreach (string full in WorkspaceFiles.Enumerate(
                _guard, _guard.RepoRoot, "**/*", MaxVisitedFiles, cancellationToken))
            {
                if (files.Count == MaxListedFiles)
                {
                    listingCapped = true;
                    break;
                }

                long bytes;
                try
                {
                    bytes = new FileInfo(full).Length;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    continue;
                }

                files.Add((_guard.ToRelativePath(full), full, bytes));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            return string.Empty;
        }

        if (files.Count == 0)
        {
            return string.Empty;
        }

        CultureInfo culture = CultureInfo.InvariantCulture;
        StringBuilder map = new();
        map.AppendLine("Workspace map, generated at run start. Inlined contents are a snapshot; your edits change them.");

        foreach ((string relative, _, long bytes) in files.OrderBy(f => f.RelativePath, StringComparer.OrdinalIgnoreCase))
        {
            map.AppendLine(culture, $"  {relative} ({bytes:N0} bytes)");
            if (map.Length >= characterBudget)
            {
                return string.Concat(
                    map.ToString().AsSpan(0, Math.Min(map.Length, characterBudget)),
                    "\n… [workspace map truncated - glob lists the rest]");
            }
        }

        if (listingCapped)
        {
            map.AppendLine(culture, $"  … more files beyond the first {MaxListedFiles} - glob lists the rest.");
        }

        // Smallest first: the budget inlines the most files that way, and small files are the
        // ones whose retrieval least deserves a whole step.
        int omitted = 0;
        foreach ((string relative, string full, long bytes) in files.OrderBy(f => f.Bytes))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (bytes > MaxInlineFileBytes || WorkspaceFiles.IsBinary(full))
            {
                omitted++;
                continue;
            }

            string content;
            try
            {
                content = File.ReadAllText(full);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                omitted++;
                continue;
            }

            string section = $"\n--- {relative} ---\n{content.TrimEnd()}\n";
            if (map.Length + section.Length > characterBudget)
            {
                omitted++;
                continue;
            }

            map.Append(section);
        }

        if (omitted > 0)
        {
            map.AppendLine(culture,
                $"\n{omitted} file(s) are listed above without contents - read_file retrieves them.");
        }

        return map.ToString().TrimEnd();
    }
}
