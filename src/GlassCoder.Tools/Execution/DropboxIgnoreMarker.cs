using System.Text.Json;
using GlassCoder.Tools.Guardrails;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GlassCoder.Tools.Execution;

/// <summary>
/// Marks build-output folders under the workspace with the <c>com.dropbox.ignored</c> NTFS
/// stream, so a workspace that lives inside Dropbox keeps its bin and obj on disk without
/// syncing them (https://help.dropbox.com/sync/ignored-files).
/// <para>
/// The launcher already does this for the folder it launches - but the harness builds into a
/// workspace the launcher never sees, and it creates bin and obj <em>mid-run</em>, after any
/// launch-time sweep. The 2026-08-06 runs each lost three steps to builds that failed while
/// Dropbox held a lock on files it should never have been syncing. So the harness marks its
/// own output: the sweep runs around every sandboxed command, and project directories get
/// their bin and obj pre-created and marked before the first build can race the sync client.
/// </para>
/// <para>
/// Everything here is best-effort and must never fail a command: a workspace outside Dropbox,
/// a non-NTFS volume, a folder that vanishes mid-sweep - all of it degrades to "not marked",
/// which is exactly where the workspace stood before this class existed.
/// </para>
/// </summary>
public sealed class DropboxIgnoreMarker
{
    /// <summary>Appended raw: the Path helpers reject or mangle the stream colon.</summary>
    private const string IgnoreStreamSuffix = ":com.dropbox.ignored";

    /// <summary>Walk ceiling - a backstop against a pathological tree, not a budget.</summary>
    private const int MaxDirectoriesVisited = 10_000;

    /// <summary>
    /// Folder names that are disposable output, aligned with the path guard's denied globs
    /// (minus <c>.git</c>, which is data, not output). Dropbox ignores everything inside an
    /// ignored folder, so these are marked and never descended into.
    /// </summary>
    private static readonly string[] TargetNames = ["bin", "obj", "node_modules", ".vs"];

    /// <summary>Project extensions whose directories get bin and obj pre-created and marked.</summary>
    private static readonly string[] ProjectPatterns = ["*.csproj", "*.fsproj", "*.vbproj"];

    /// <summary>What gets pre-created beside a project file, so the first build finds it marked.</summary>
    private static readonly string[] PrecreatedOutputNames = ["bin", "obj"];

    private static readonly string[] RootPathKeys = ["path", "root_path"];

    private static readonly EnumerationOptions ChildDirectoryOptions = new()
    {
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.ReparsePoint,
    };

    private readonly IPathGuard _guard;
    private readonly WorkspaceOptions _options;
    private readonly ILogger<DropboxIgnoreMarker> _logger;
    private readonly IReadOnlyList<string>? _dropboxRootsOverride;
    private readonly Lazy<bool> _active;
    private bool _warnedAboutFailures;

    /// <summary>Creates the marker. The override of Dropbox roots exists for tests.</summary>
    public DropboxIgnoreMarker(
        IPathGuard guard,
        IOptions<WorkspaceOptions> options,
        ILogger<DropboxIgnoreMarker>? logger = null,
        IReadOnlyList<string>? dropboxRootsOverride = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        _guard = guard;
        _options = options.Value;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<DropboxIgnoreMarker>.Instance;
        _dropboxRootsOverride = dropboxRootsOverride;
        _active = new Lazy<bool>(ResolveActive);
    }

    /// <summary>
    /// Sweeps the workspace once: marks every target folder, and pre-creates marked bin and
    /// obj beside every project file so the first build writes into folders Dropbox is
    /// already ignoring. Cheap when there is nothing to do; a no-op outside Dropbox.
    /// </summary>
    public void EnsureWorkspaceMarked()
    {
        if (!_active.Value)
        {
            return;
        }

        int marked = 0;
        int failed = 0;
        Exception? firstFailure = null;

        try
        {
            int visited = 0;
            Stack<string> pending = new();
            pending.Push(_guard.RepoRoot);

            while (pending.Count > 0)
            {
                string current = pending.Pop();
                if (++visited > MaxDirectoriesVisited)
                {
                    _logger.LogWarning(
                        "Stopped the Dropbox ignore sweep under {Root} after {Count} directories",
                        _guard.RepoRoot, MaxDirectoriesVisited);
                    break;
                }

                if (HoldsAProject(current))
                {
                    foreach (string output in PrecreatedOutputNames)
                    {
                        Mark(Path.Combine(current, output), create: true, ref marked, ref failed, ref firstFailure);
                    }
                }

                foreach (string child in SafeEnumerateDirectories(current))
                {
                    string name = Path.GetFileName(child);
                    if (TargetNames.Contains(name, StringComparer.OrdinalIgnoreCase))
                    {
                        // Dropbox ignores everything inside an ignored folder; no need to descend.
                        Mark(child, create: false, ref marked, ref failed, ref firstFailure);
                    }
                    else if (!name.StartsWith('.'))
                    {
                        pending.Push(child);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            firstFailure ??= ex;
            failed++;
        }

        if (marked > 0)
        {
            _logger.LogInformation(
                "Marked {Count} build-output folder(s) under {Root} as ignored by Dropbox", marked, _guard.RepoRoot);
        }

        if (failed > 0 && !_warnedAboutFailures)
        {
            // Once: on a volume without streams this would otherwise repeat for every command.
            _warnedAboutFailures = true;
            _logger.LogWarning(
                firstFailure,
                "Could not mark {Count} folder(s) as Dropbox-ignored under {Root}; further failures are not logged",
                failed, _guard.RepoRoot);
        }
    }

    private static void Mark(string directory, bool create, ref int marked, ref int failed, ref Exception? firstFailure)
    {
        try
        {
            if (!Directory.Exists(directory))
            {
                if (!create)
                {
                    return;
                }

                Directory.CreateDirectory(directory);
            }

            string streamPath = directory + IgnoreStreamSuffix;
            if (IsAlreadyIgnored(streamPath))
            {
                return;
            }

            File.WriteAllText(streamPath, "1");
            marked++;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            firstFailure ??= ex;
            failed++;
        }
    }

    private static bool IsAlreadyIgnored(string streamPath)
    {
        try
        {
            return File.ReadAllText(streamPath).Trim() == "1";
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return false;
        }
    }

    private static bool HoldsAProject(string directory)
    {
        foreach (string pattern in ProjectPatterns)
        {
            try
            {
                if (Directory.EnumerateFiles(directory, pattern).Any())
                {
                    return true;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Unreadable is answered with "no project seen", not with a failed sweep.
            }
        }

        return false;
    }

    private static IEnumerable<string> SafeEnumerateDirectories(string root)
    {
        try
        {
            // Materialized so enumeration faults (a directory deleted mid-sweep) surface here.
            return Directory.EnumerateDirectories(root, "*", ChildDirectoryOptions).ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private bool ResolveActive()
    {
        if (!_options.ExcludeBuildOutputFromDropbox || !OperatingSystem.IsWindows())
        {
            return false;
        }

        string root;
        try
        {
            root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(_guard.RepoRoot));
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or NotSupportedException)
        {
            return false;
        }

        foreach (string dropboxRoot in _dropboxRootsOverride ?? LoadDropboxRoots())
        {
            string trimmed = Path.TrimEndingDirectorySeparator(dropboxRoot);
            if (root.Equals(trimmed, StringComparison.OrdinalIgnoreCase)
                || (root.Length > trimmed.Length
                    && root.StartsWith(trimmed, StringComparison.OrdinalIgnoreCase)
                    && root[trimmed.Length] == Path.DirectorySeparatorChar))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The synced roots, from the Dropbox client's own info.json. Empty when absent.</summary>
    private IReadOnlyList<string> LoadDropboxRoots()
    {
        string[] candidates =
        [
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Dropbox", "info.json"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Dropbox", "info.json"),
        ];

        string? infoPath = candidates.FirstOrDefault(File.Exists);
        if (infoPath is null)
        {
            return [];
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(infoPath));
            List<string> roots = [];
            foreach (JsonProperty account in document.RootElement.EnumerateObject())
            {
                if (account.Value.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                foreach (string key in RootPathKeys)
                {
                    if (account.Value.TryGetProperty(key, out JsonElement value)
                        && value.ValueKind == JsonValueKind.String
                        && value.GetString() is { Length: > 0 } rootPath)
                    {
                        roots.Add(rootPath);
                    }
                }
            }

            return roots;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not read the Dropbox roots from {Path}; Dropbox ignore is inactive", infoPath);
            return [];
        }
    }
}
