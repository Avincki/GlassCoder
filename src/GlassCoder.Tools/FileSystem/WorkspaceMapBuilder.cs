using System.Globalization;
using System.Text;
using GlassCoder.Tools.Guardrails;
using Microsoft.Extensions.Options;

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

    /// <summary>
    /// Directory-visit ceiling for the walk. A backstop, not a budget: build output is pruned
    /// by name before it costs a visit, so real trees rarely come near this - and hitting it
    /// is said out loud, never silent (a glob-based walk once spent its whole visit budget on
    /// guard-denied bin and obj entries and truncated the listing without a word).
    /// </summary>
    private const int MaxVisitedDirectories = 2_000;

    /// <summary>
    /// Directory names never descended into, matching the ignore sweep and the guard's denied
    /// globs: disposable output whose listing would be noise. Dot-directories are pruned by
    /// rule below.
    /// </summary>
    private static readonly string[] PrunedDirectoryNames = ["bin", "obj", "node_modules", ".vs"];

    /// <summary>Largest file the inline pass will consider. Anything bigger is what
    /// read_file's windowing is for.</summary>
    private const int MaxInlineFileBytes = 16_384;

    /// <summary>Characters a section header and its newlines add beyond the content itself.</summary>
    private const int SectionOverhead = 16;

    private readonly IPathGuard _guard;
    private readonly WorkspaceOptions? _options;

    /// <summary>Creates the builder. The options carry the writable roots the map announces.</summary>
    public WorkspaceMapBuilder(IPathGuard guard, IOptions<WorkspaceOptions>? options = null)
    {
        _guard = guard;
        _options = options?.Value;
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

        // A hand-rolled walk rather than a glob: the matcher visits everything and lets the
        // guard veto afterwards, which spends the visit budget on bin and obj junk and ends
        // silently when it runs out. Pruning by name first means the ceiling is about real
        // tree size, and reaching either cap sets the flag - a capped listing always says so.
        List<(string RelativePath, string FullPath, long Bytes)> files = [];
        bool listingCapped = false;
        int visited = 0;
        Stack<string> pending = new();
        pending.Push(_guard.RepoRoot);

        while (pending.Count > 0 && !listingCapped)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string current = pending.Pop();
            if (++visited > MaxVisitedDirectories)
            {
                listingCapped = true;
                break;
            }

            foreach (string full in SafeEnumerateFiles(current))
            {
                if (files.Count == MaxListedFiles)
                {
                    listingCapped = true;
                    break;
                }

                PathGuardResult verdict = _guard.Resolve(full, PathAccess.Read);
                if (!verdict.Allowed || verdict.FullPath is null)
                {
                    continue;
                }

                long bytes;
                try
                {
                    bytes = new FileInfo(verdict.FullPath).Length;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    continue;
                }

                files.Add((_guard.ToRelativePath(verdict.FullPath), verdict.FullPath, bytes));
            }

            foreach (string child in SafeEnumerateDirectories(current))
            {
                string name = Path.GetFileName(child);
                if (!name.StartsWith('.') && !PrunedDirectoryNames.Contains(name, StringComparer.OrdinalIgnoreCase))
                {
                    pending.Push(child);
                }
            }
        }

        if (files.Count == 0)
        {
            // An empty workspace is exactly when orientation matters most: run 21f25fea aimed
            // its first scaffold at the unwritable root, was refused, and never recovered. The
            // one fact that prevents that has to be on the table before the first call.
            return ("The workspace is empty - there are no files yet. " + WritableRoots()).TrimEnd();
        }

        CultureInfo culture = CultureInfo.InvariantCulture;
        StringBuilder map = new();
        map.AppendLine("Workspace map, generated at run start. Inlined contents are a snapshot; your edits change them.");
        string writable = WritableRoots();
        if (writable.Length > 0)
        {
            map.AppendLine(writable);
        }

        foreach ((string relative, _, long bytes) in files.OrderBy(f => f.RelativePath, StringComparer.OrdinalIgnoreCase))
        {
            map.AppendLine(culture, $"  {relative} ({bytes:N0} bytes)");
            if (map.Length >= characterBudget)
            {
                return string.Concat(
                    map.ToString().AsSpan(0, characterBudget),
                    "\n… [workspace map truncated - glob lists the rest]");
            }
        }

        if (listingCapped)
        {
            map.AppendLine("  … not every file is listed - glob lists the rest.");
        }

        // Smallest first: the budget inlines the most files that way, and small files are the
        // ones whose retrieval least deserves a whole step. Ascending size also means the
        // first file that cannot fit ends the pass - everything after it is at least as big -
        // and the size check comes before the read, so a full budget stops costing disk.
        // Bytes over-estimate characters for UTF-8, so a fit predicted here is a real fit.
        List<(string RelativePath, string FullPath, long Bytes)> bySize = [.. files.OrderBy(f => f.Bytes)];
        int omitted = 0;
        for (int index = 0; index < bySize.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            (string relative, string full, long bytes) = bySize[index];

            if (bytes > MaxInlineFileBytes ||
                map.Length + bytes + relative.Length + SectionOverhead > characterBudget)
            {
                omitted += bySize.Count - index;
                break;
            }

            if (WorkspaceFiles.IsBinary(full))
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

            // No culture-sensitive holes here, so the plain Append is the right overload.
            map.Append($"\n--- {relative} ---\n{content.TrimEnd()}\n");
        }

        if (omitted > 0)
        {
            map.AppendLine(culture,
                $"\n{omitted} file(s) are listed above without contents - read_file retrieves them.");
        }

        return map.ToString().TrimEnd();
    }

    private static IEnumerable<string> SafeEnumerateFiles(string directory)
    {
        try
        {
            return Directory.EnumerateFiles(directory).ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            return [];
        }
    }

    private static IEnumerable<string> SafeEnumerateDirectories(string directory)
    {
        try
        {
            return Directory.EnumerateDirectories(directory).ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            return [];
        }
    }

    /// <summary>
    /// The writable roots, said plainly: everything outside them is refused, and a model that
    /// learns this from a refusal has already spent the step the sentence would have saved.
    /// </summary>
    private string WritableRoots()
    {
        if (_options is null)
        {
            return string.Empty;
        }

        return _options.WritablePaths.Count == 0
            ? "No paths are writable: this run cannot create or change files."
            : $"Writable roots: {string.Join(", ", _options.WritablePaths)}. Create files and projects inside these; " +
              "everything else is read-only.";
    }
}
