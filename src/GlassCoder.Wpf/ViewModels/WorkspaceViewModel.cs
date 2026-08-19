using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using System.Xml;
using System.Xml.Linq;
using GlassCoder.Core.Configuration;
using GlassCoder.Tools.Changes;
using GlassCoder.Tools.Execution;
using GlassCoder.Core.Diagnostics;
using GlassCoder.Core.Verification;
using GlassCoder.Tools.Guardrails;
using GlassCoder.Wpf.Mvvm;
using GlassCoder.Wpf.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileSystemGlobbing;
using Microsoft.Extensions.Options;

namespace GlassCoder.Wpf.ViewModels;

/// <summary>One entry in the workspace tree: a directory with children, or a file.</summary>
public sealed class FileNodeViewModel : ViewModelBase
{
    private bool _isExpanded;
    private bool _isModified;
    private int _linesAdded;
    private int _linesRemoved;

    /// <summary>Creates the node.</summary>
    public FileNodeViewModel(string name, string relativePath, bool isDirectory)
    {
        Name = name;
        RelativePath = relativePath;
        IsDirectory = isDirectory;

        // Open by default. A tree that starts closed shows one row per top-level folder and
        // hides the thing the pane exists to show - which file the run touched. The deny globs
        // already keep bin, obj, .git and node_modules out, so what expands is source.
        _isExpanded = isDirectory;
    }

    /// <summary>File or directory name, as shown.</summary>
    public string Name { get; }

    /// <summary>Repo-relative path with forward slashes - the change log's spelling.</summary>
    public string RelativePath { get; }

    /// <summary>Whether this node can have children.</summary>
    public bool IsDirectory { get; }

    /// <summary>Child nodes, directories first.</summary>
    public ObservableCollection<FileNodeViewModel> Children { get; } = [];

    /// <summary>
    /// Bound two-way: directories start open, the user may close one, and marking a file modified
    /// re-opens the folders above it.
    /// </summary>
    public bool IsExpanded
    {
        get => _isExpanded;
        set => SetProperty(ref _isExpanded, value);
    }

    /// <summary>Whether the session's change log holds an applied change for this file.</summary>
    public bool IsModified
    {
        get => _isModified;
        private set => SetProperty(ref _isModified, value);
    }

    /// <summary>Net lines added this session.</summary>
    public int LinesAdded
    {
        get => _linesAdded;
        private set => SetProperty(ref _linesAdded, value);
    }

    /// <summary>Net lines removed this session.</summary>
    public int LinesRemoved
    {
        get => _linesRemoved;
        private set => SetProperty(ref _linesRemoved, value);
    }

    /// <summary>Marks the file modified with its net counts.</summary>
    public void SetStats(FileChangeStats stats)
    {
        LinesAdded = stats.LinesAdded;
        LinesRemoved = stats.LinesRemoved;
        IsModified = true;
    }

    /// <summary>Unmarks the file - its last applied change was reverted.</summary>
    public void ClearStats()
    {
        IsModified = false;
        LinesAdded = 0;
        LinesRemoved = 0;
    }

    /// <summary>
    /// The node's name, which is also what accessibility reads.
    /// <para>
    /// A TreeViewItem whose header is a data template has no text of its own to expose, so its
    /// automation peer falls back to the bound object's <see cref="object.ToString"/>. Without
    /// this override every node in the workspace tree announces itself to a screen reader - and
    /// to any UI automation driving the app - as "GlassCoder.Wpf.ViewModels.FileNodeViewModel".
    /// </para>
    /// </summary>
    public override string ToString() => Name;
}

/// <summary>
/// The workspace pane (workplan task 39): which folder the agent works on, and what this
/// session has done to the tree - modified files in green with their net line counts.
/// <para>
/// Folder selection is save-and-restart, like every other setting: the choice persists through
/// <see cref="IUserSettingsStore"/> and takes effect when the process restarts, because the
/// path guard, the sandbox mounts and the context all rooted themselves at startup. The tree
/// therefore always shows the workspace the agent is <em>actually</em> in - never a folder it
/// is not.
/// </para>
/// <para>
/// Two sources feed it, and they answer different questions. A <see cref="FileSystemWatcher"/>
/// says what the workspace <em>contains</em>: a file exists in the tree the moment its path
/// exists on disk, whoever made it and whether or not anything has finished writing to it. The
/// change log says what <em>this run</em> did to it, which is the green and the line counts.
/// Before the watcher, the tree only knew about files the harness had recorded a change for -
/// so the three files <c>dotnet new</c> writes were invisible until someone pressed Refresh.
/// </para>
/// </summary>
public sealed class WorkspaceViewModel : ViewModelBase, IDisposable
{
    /// <summary>
    /// Probe child used to decide whether a directory is denied wholesale: if an arbitrary
    /// child matches the deny globs, the globs are of the "everything under here" shape and
    /// the walk can skip the directory instead of enumerating what it will then hide.
    /// </summary>
    private const string DirectoryProbe = "§probe§";

    /// <summary>
    /// How long the sweep waits between its two passes: long enough for a sync client to work
    /// through the delete storm the first pass raised, short enough that a clean never feels
    /// stuck. Off the UI thread, so the wait costs a status line and nothing else.
    /// </summary>
    private static readonly TimeSpan SettleDelay = TimeSpan.FromSeconds(1.5);

    private readonly IChangeLog _changes;
    private readonly IConfiguration _configuration;
    private readonly IUserSettingsStore _store;
    private readonly IDesktopShell _shell;
    private readonly Dispatcher _dispatcher;
    private readonly Matcher? _denied;
    private readonly IReadOnlyList<string> _writableRoots;

    /// <summary>
    /// The solution patterns a run may write at the workspace root, taken from the guard's own
    /// list of writable root files so the two cannot drift apart.
    /// <para>
    /// Solutions only, out of the eight patterns that list holds. A stale solution is the one
    /// leftover that changes what the next run does - a build or a test at the root resolves to
    /// it, finds projects that are no longer there, or finds it empty and reports green over zero
    /// tests. The rest of the list is furniture: a <c>.gitignore</c>, a <c>Directory.Build.props</c>
    /// or a <c>README.md</c> left behind costs the next run nothing, and the README in particular
    /// has been Clean's stated boundary since the button existed.
    /// </para>
    /// </summary>
    private readonly IReadOnlyList<string> _writableRootFiles;
    private readonly DropboxIgnoreMarker? _dropboxMarker;
    private readonly string _rootPrefix;

    /// <summary>
    /// Where retrospectives land, workspace-relative and in the tree's own slash convention, so a
    /// node's path can be tested against it without touching the disk. Taken from the same options
    /// the reviewer writes by, because a hardcoded copy here would be a second answer to a
    /// configured question.
    /// </summary>
    private readonly string _retrospectives;
    private bool _isAgentRunning;
    private readonly Lock _pendingGate = new();
    private readonly HashSet<string> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly FileSystemWatcher? _watcher;
    private readonly string? _watchFailure;
    private Dictionary<string, FileNodeViewModel> _nodesByPath = new(StringComparer.OrdinalIgnoreCase);
    private string _status = string.Empty;
    private string? _pendingRoot;
    private string? _runId;
    private string? _ratedRunId;
    private string? _ratedTaskId;
    private string? _taskId;
    private readonly IStepLogger? _steps;
    private readonly ITranscriptBus? _transcript;
    private bool _isRatingApp;
    private int? _appRating;
    private string _appComment = string.Empty;
    private string _ratedApplication = string.Empty;
    private bool _awaitingRun;
    private bool _drainQueued;
    private bool _isLoading;

    /// <summary>Creates the pane over the active workspace root and subscribes to the change log.</summary>
    public WorkspaceViewModel(
        IPathGuard guard,
        IChangeLog changes,
        IOptions<WorkspaceOptions> workspace,
        IConfiguration configuration,
        IUserSettingsStore store,
        IDesktopShell shell,
        Dispatcher? dispatcher = null,
        DropboxIgnoreMarker? dropboxMarker = null,
        IStepLogger? steps = null,
        ITranscriptBus? transcript = null,
        IOptions<RetrospectiveOptions>? retrospective = null)
    {
        ArgumentNullException.ThrowIfNull(guard);
        ArgumentNullException.ThrowIfNull(workspace);

        _changes = changes;
        _configuration = configuration;
        _store = store;
        _shell = shell;
        _dispatcher = dispatcher ?? Dispatcher.CurrentDispatcher;
        _dropboxMarker = dropboxMarker;
        _steps = steps;
        _transcript = transcript;
        _retrospectives = (retrospective?.Value.OutputDirectory ?? new RetrospectiveOptions().OutputDirectory)
            .Replace('\\', '/')
            .Trim('/');

        RootPath = guard.RepoRoot;
        _rootPrefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(RootPath)) + Path.DirectorySeparatorChar;

        // The tree hides exactly what the agent cannot touch, so the pane and the guardrail
        // never disagree about what the workspace contains.
        if (workspace.Value.DeniedGlobs.Count > 0)
        {
            _denied = new Matcher(StringComparison.OrdinalIgnoreCase);
            _denied.AddIncludePatterns(workspace.Value.DeniedGlobs);
        }

        _writableRoots = [.. workspace.Value.WritablePaths];
        _writableRootFiles =
        [
            .. workspace.Value.WritableRootFiles.Where(pattern =>
                pattern.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) ||
                pattern.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase)),
        ];

        _changes.Changed += OnChanged;

        BrowseCommand = new RelayCommand(Browse, () => !_isLoading);
        RestartCommand = new RelayCommand(_shell.Restart, () => HasPendingRoot);
        RefreshCommand = new RelayCommand(() => _ = RefreshAsync(), () => !_isLoading);
        CleanCommand = new RelayCommand(Clean, () => !_isLoading && !IsAgentRunning);
        RunAppCommand = new RelayCommand(RunApp, () => !_isLoading && !IsAgentRunning);
        SubmitRatingCommand = new RelayCommand(SubmitRating, () => AppRating is not null);
        SkipRatingCommand = new RelayCommand(() =>
        {
            IsRatingApp = false;
            Status = $"{RatedApplication} closed, unrated.";
        });
        OpenFileCommand = new RelayCommand(OpenFile, CanOpenFile);

        (_watcher, _watchFailure) = StartWatching();
        Loaded = RefreshAsync();
    }

    /// <summary>The workspace root the agent is working in.</summary>
    public string RootPath { get; }

    /// <summary>
    /// The first read of the tree. Exposed so a caller that needs the tree populated - a test,
    /// mainly - can wait for the read the constructor started rather than poll for it.
    /// </summary>
    public Task Loaded { get; }

    /// <summary>Top level of the tree: the root folder's contents.</summary>
    public ObservableCollection<FileNodeViewModel> RootNodes { get; } = [];

    /// <summary>A saved root that is not the one in force yet, when there is one.</summary>
    public string? PendingRoot
    {
        get => _pendingRoot;
        private set
        {
            if (SetProperty(ref _pendingRoot, value))
            {
                OnPropertyChanged(nameof(HasPendingRoot));
            }
        }
    }

    /// <summary>Whether a restart is what stands between the user and their chosen folder.</summary>
    public bool HasPendingRoot => PendingRoot is not null;

    /// <summary>What the pane is doing, or what it last did.</summary>
    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    /// <summary>Picks a folder and saves it as the workspace root for the next start.</summary>
    public RelayCommand BrowseCommand { get; }

    /// <summary>Restarts, making the saved folder the root in force.</summary>
    public RelayCommand RestartCommand { get; }

    /// <summary>Re-reads the tree from disk.</summary>
    public RelayCommand RefreshCommand { get; }

    /// <summary>Empties the writable roots, after asking, so the next run starts blank.</summary>
    public RelayCommand CleanCommand { get; }

    /// <summary>Launches the workspace's application on the desktop, live and interactive.</summary>
    public RelayCommand RunAppCommand { get; }

    /// <summary>Records the rating. Disabled until a score is chosen; the comment is optional.</summary>
    public RelayCommand SubmitRatingCommand { get; }

    /// <summary>Closes the strip without recording. A rating nobody wanted to give is noise.</summary>
    public RelayCommand SkipRatingCommand { get; }

    /// <summary>Whether the strip asking how the application looked is open.</summary>
    public bool IsRatingApp
    {
        get => _isRatingApp;
        private set => SetProperty(ref _isRatingApp, value);
    }

    /// <summary>The application the open question is about.</summary>
    public string RatedApplication
    {
        get => _ratedApplication;
        private set => SetProperty(ref _ratedApplication, value);
    }

    /// <summary>
    /// The score, 0 to 5. Null until one is chosen, which is what gates recording - and what
    /// clears the buttons when the strip opens again for the next application.
    /// </summary>
    public int? AppRating
    {
        get => _appRating;
        set => SetProperty(ref _appRating, value);
    }

    /// <summary>What the operator wants to say about it. Optional, and kept verbatim.</summary>
    public string AppComment
    {
        get => _appComment;
        set => SetProperty(ref _appComment, value);
    }

    /// <summary>Opens the double-clicked file in a read-only viewer window.</summary>
    public RelayCommand OpenFileCommand { get; }

    /// <summary>
    /// Raised when a retrospective's folder is double-clicked, carrying its absolute path.
    /// <para>
    /// An event rather than a call, because this pane cannot reach that surface and should not
    /// learn how. It is bound beside the surfaces rather than inside one, and the only outward
    /// seam it has is <see cref="IDesktopShell"/> - which is the operating system, and choosing
    /// which surface is on screen is not the operating system's business. The shell view model
    /// holds both halves and is where they meet (CLAUDE.md §14).
    /// </para>
    /// </summary>
    public event EventHandler<string>? RetrospectiveOpened;

    /// <summary>
    /// Whether a run is in flight. Set by the shell, exactly like the Changes surface's flag:
    /// emptying the folders an agent is mid-way through writing would hand it a workspace that
    /// stopped matching every observation it has made, so Clean stands down for the duration.
    /// </summary>
    public bool IsAgentRunning
    {
        get => _isAgentRunning;
        set => SetProperty(ref _isAgentRunning, value);
    }

    /// <summary>
    /// Clears the marking, and starts counting again for the run about to begin.
    /// <para>
    /// The counts answer "what has this run done to the tree", so the previous run's green has
    /// to come off before this one writes anything - otherwise the first step of a new run is
    /// read against a tree still coloured by the last one. The run id is not knowable here
    /// (the loop mints it), so the pane latches onto the first change the run produces and
    /// ignores everything belonging to any other.
    /// </para>
    /// </summary>
    public void BeginRun()
    {
        foreach (FileNodeViewModel node in _nodesByPath.Values)
        {
            node.ClearStats();
        }

        _runId = null;
        _taskId = null;
        _awaitingRun = true;
    }

    /// <summary>Stops watching the workspace.</summary>
    public void Dispose()
    {
        _changes.Changed -= OnChanged;
        _watcher?.Dispose();
    }

    /// <summary>
    /// Files, and the one kind of directory that has something to open: a retrospective's own
    /// folder.
    /// <para>
    /// This is what keeps folders behaving like folders. The command is bound to a double-click
    /// on the tree item, and an <see cref="System.Windows.Input.InputBinding"/> whose command
    /// cannot execute leaves the event unhandled - so a double-click on a directory falls
    /// through to the TreeView and expands it, as it did before there was a viewer. A
    /// retrospective folder is the exception, and pays for it: double-clicking one shows the
    /// retrospective instead of expanding it, and the chevron still does the expanding.
    /// </para>
    /// </summary>
    private bool CanOpenFile(object? parameter) =>
        parameter is FileNodeViewModel node && (!node.IsDirectory || IsRetrospective(node));

    /// <summary>
    /// Whether this node is one retrospective's own folder - a directory sitting directly inside
    /// the configured retrospectives directory.
    /// <para>
    /// A test on the path's shape rather than on its contents, because this decides whether the
    /// double-click is worth trying and runs every time WPF re-queries the command. Whether the
    /// folder really holds a retrospective is answered by reading it, once, on the way in.
    /// </para>
    /// </summary>
    private bool IsRetrospective(FileNodeViewModel node)
    {
        if (!node.IsDirectory || _retrospectives.Length == 0)
        {
            return false;
        }

        string relative = node.RelativePath;

        return relative.Length > _retrospectives.Length + 1 &&
            relative.StartsWith(_retrospectives, StringComparison.OrdinalIgnoreCase) &&
            relative[_retrospectives.Length] == '/' &&

            // One level down, and no further: the retrospectives directory holds a folder per
            // retrospective, and anything deeper is inside one rather than being one.
            !relative.AsSpan(_retrospectives.Length + 1).Contains('/');
    }

    private void OpenFile(object? parameter)
    {
        if (parameter is not FileNodeViewModel node || (node.IsDirectory && !IsRetrospective(node)))
        {
            return;
        }

        string relative = node.RelativePath.Replace('/', Path.DirectorySeparatorChar);
        string full = Path.GetFullPath(Path.Combine(RootPath, relative));

        // The tree is built from the workspace, so this should always hold. It is checked anyway
        // because the check is one comparison and the alternative is a path built from a node
        // name reaching the file system unexamined.
        if (!IsInsideRoot(full))
        {
            Status = $"'{node.RelativePath}' is outside the workspace.";
            return;
        }

        if (node.IsDirectory)
        {
            RetrospectiveOpened?.Invoke(this, full);
            return;
        }

        _shell.OpenFileViewer(full, node.RelativePath);
    }

    private bool IsInsideRoot(string fullPath)
    {
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(RootPath));
        return fullPath.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private void Browse()
    {
        string? picked = _shell.PickFolder("Select the project folder", RootPath);
        if (picked is null)
        {
            return;
        }

        string chosen = Path.TrimEndingDirectorySeparator(Path.GetFullPath(picked));
        if (string.Equals(chosen, RootPath, StringComparison.OrdinalIgnoreCase))
        {
            PendingRoot = null;
            Status = "That is already the active workspace.";
            return;
        }

        // Persisted the same way the settings dialog persists everything, so the choice
        // survives a restart and loses to the same layers (environment, --config) a saved
        // setting always loses to.
        try
        {
            GlassCoderSettings settings = GlassCoderSettings.ReadFrom(_configuration);
            settings.Workspace.RepoRoot = chosen;
            _store.Save(settings);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Status = $"Could not save the workspace root: {ex.Message}";
            return;
        }

        PendingRoot = chosen;
        Status = "Workspace saved.";
    }

    /// <summary>
    /// Empties the writable roots, and removes any solution a run left at the workspace root, so
    /// the next run starts from the blank workspace the opening message promises.
    /// <para>
    /// The roots and the solution, and nothing else. A run's output lives inside the folders the
    /// guard lets it write - plus, since 2026-08-11, a short list of files at the root, which is
    /// how a <c>MultiplyApp.slnx</c> came to outlive every project it named. Of that list only a
    /// solution changes what the next run does: a build or a test at the root resolves to it and
    /// finds projects that are gone, or finds it empty and reports green over zero tests. A README
    /// or a <c>.gitignore</c> left behind costs the next run nothing and may be the operator's, so
    /// it stays - which is the boundary this button has held since it was written.
    /// </para>
    /// <para>
    /// The solution is named in the confirmation rather than described, because it is the one part
    /// of the sweep that reaches outside the granted folders. Asks first, and deletes what it can
    /// rather than stopping at the first locked file: half a clean plus an honest count beats a
    /// sync client winning by holding one handle.
    /// </para>
    /// </summary>
    private void Clean()
    {
        if (_writableRoots.Count == 0)
        {
            Status = "No writable roots are configured, so there is nothing to clean.";
            return;
        }

        List<FileInfo> rootFiles = RootFilesToClean();
        string names = string.Join(", ", _writableRoots);
        string alsoNamed = rootFiles.Count == 0
            ? string.Empty
            : $"\n\nAnd the solution at the root:\n{string.Join("\n", rootFiles.Select(f => f.Name))}";

        if (!_shell.Confirm(
                "Clean the workspace",
                $"Delete everything inside {names} under:\n{RootPath}{alsoNamed}\n\n" +
                "The folders themselves stay. There is no undo."))
        {
            Status = "Clean cancelled; nothing was deleted.";
            return;
        }

        _ = CleanAsync(names);
    }

    /// <summary>
    /// The solutions at the workspace root, which a run could have written and no other part of
    /// the sweep reaches.
    /// <para>
    /// Read fresh at each call rather than cached: the operator can change the root, and a list
    /// gathered when the pane loaded would name files from a workspace nobody is looking at.
    /// </para>
    /// </summary>
    private List<FileInfo> RootFilesToClean()
    {
        try
        {
            DirectoryInfo root = new(RootPath);
            if (!root.Exists)
            {
                return [];
            }

            return
            [
                .. _writableRootFiles
                    .SelectMany(pattern => root.EnumerateFiles(pattern, SearchOption.TopDirectoryOnly))
                    .DistinctBy(f => f.FullName, StringComparer.OrdinalIgnoreCase)
                    .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase),
            ];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // A root that cannot be listed is a root with nothing to offer here; the folder sweep
            // reports its own failures and this must not pre-empt them.
            return [];
        }
    }

    /// <summary>
    /// The sweep, off the UI thread, then the refresh, then the summary put back. Off the UI
    /// thread because outlasting a held handle takes patience, and patience on the UI thread
    /// is a frozen window. The summary goes back last because the refresh reports its own
    /// progress and file count into <see cref="Status"/>, and "2 could not be" is not a line
    /// to lose. A failed read keeps its error instead: a pane that cannot see the workspace
    /// outranks a tidy summary.
    /// </summary>
    private async Task CleanAsync(string names)
    {
        if (_isLoading)
        {
            return;
        }

        _isLoading = true;
        Status = "Cleaning…";

        int removed;
        List<string> failures;
        try
        {
            (removed, failures) = await Task.Run(SweepRoots).ConfigureAwait(true);
        }
        finally
        {
            _isLoading = false;
        }

        string summary = failures.Count == 0
            ? string.Create(CultureInfo.InvariantCulture, $"Cleaned {names}: {removed} item(s) removed.")
            : string.Create(CultureInfo.InvariantCulture,
                $"Cleaned {names}: {removed} item(s) removed, {failures.Count} could not be. {failures[0]}");
        Status = summary;

        if (await RefreshAsync().ConfigureAwait(true))
        {
            Status = summary;
        }
    }

    /// <summary>
    /// Sweeps every writable root, in up to two passes. Two, because the first pass raises the
    /// storm it then loses to: a mass delete makes the sync client open every folder it just
    /// watched empty, and the folders' own deletions fail against handles the clean itself
    /// provoked - files gone, husks left. So when a pass leaves failures, the sweep waits for
    /// the dust to settle and goes once more; the husks are empty by then and go quietly.
    /// What the second pass still cannot remove is genuinely held, and is reported as such.
    /// </summary>
    private (int Removed, List<string> Failures) SweepRoots()
    {
        int removed = 0;
        List<string> skipped = [];
        List<string> failures = [];

        for (int pass = 1; ; pass++)
        {
            failures = [];

            foreach (string root in _writableRoots)
            {
                string full = Path.GetFullPath(Path.Combine(RootPath, root));
                if (!IsInsideRoot(full))
                {
                    // "." or an absolute path elsewhere. Emptying it would reach the
                    // workspace's own .git or another project entirely, and this button only
                    // deletes what a run could have made. No second pass changes that.
                    if (pass == 1)
                    {
                        skipped.Add($"'{root}' skipped: it is not strictly inside the workspace");
                    }

                    continue;
                }

                try
                {
                    // Recreated when missing, so a clean always leaves the roots the guard
                    // promises.
                    Directory.CreateDirectory(full);
                    removed += Sweep(new DirectoryInfo(full), failures);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    failures.Add($"{root}: {ex.Message}");
                }
            }

            // The root files last, after the folders they refer to are gone: a solution deleted
            // first would leave the projects it named looking like the deliverable for as long as
            // the sweep takes, and a failure here is reported on the same terms as any other.
            foreach (FileInfo file in RootFilesToClean())
            {
                if (TryDelete(file, failures))
                {
                    removed++;
                }
            }

            if (failures.Count == 0 || pass == 2)
            {
                break;
            }

            // A property set is safe from here: bindings marshal PropertyChanged themselves,
            // and narrating the wait beats a pane that looks hung for the duration.
            Status = "Cleaning… some items are still held by another program; waiting for them to be let go.";
            Thread.Sleep(SettleDelay);
        }

        skipped.AddRange(failures);
        return (removed, skipped);
    }

    /// <summary>
    /// Empties a directory from the leaves up, one entry at a time, and reports how many it
    /// removed. Bottom-up rather than <c>Delete(recursive: true)</c>, because the framework's
    /// recursion abandons a whole subtree at the first file it cannot delete - and in a
    /// Dropbox-synced workspace there usually is one, held for hashing or copied read-only
    /// into build output. One stubborn file used to keep every subfolder around it alive;
    /// swept leaf-first it costs itself and the folders directly above it, nothing more.
    /// </summary>
    private int Sweep(DirectoryInfo directory, List<string> failures)
    {
        FileSystemInfo[] entries;
        try
        {
            entries = directory.GetFileSystemInfos();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            failures.Add($"{Describe(directory)}: {ex.Message}");
            return 0;
        }

        int removed = 0;
        foreach (FileSystemInfo entry in entries)
        {
            if (entry is DirectoryInfo child)
            {
                int failuresBefore = failures.Count;
                removed += Sweep(child, failures);

                // A folder whose sweep left something behind is not empty; asking anyway
                // would only bury the real failure under a "directory is not empty".
                if (failures.Count == failuresBefore && TryDelete(child, failures))
                {
                    removed++;
                }
            }
            else if (TryDelete(entry, failures))
            {
                removed++;
            }
        }

        return removed;
    }

    /// <summary>
    /// Deletes one file or one emptied folder, insisting a little: read-only comes off first,
    /// because delete refuses such files and refusing is not what their attribute means here,
    /// and a couple of spaced retries outlast the moment a sync client holds an entry. An
    /// entry that vanished mid-attempt was the point of the exercise, so it counts.
    /// </summary>
    private bool TryDelete(FileSystemInfo entry, List<string> failures)
    {
        const int Attempts = 3;

        for (int attempt = 1; ; attempt++)
        {
            try
            {
                if ((entry.Attributes & FileAttributes.ReadOnly) != 0)
                {
                    entry.Attributes &= ~FileAttributes.ReadOnly;
                }

                entry.Delete();
                return true;
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt == Attempts)
                {
                    failures.Add($"{Describe(entry)}: {ex.Message}");
                    return false;
                }

                Thread.Sleep(50 * attempt);
            }
        }
    }

    /// <summary>The entry's workspace-relative path, so a deep failure says where it lives.</summary>
    private string Describe(FileSystemInfo entry) => ToRelative(entry.FullName) ?? entry.Name;

    /// <summary>
    /// Launches the workspace's application on the desktop - the live check the ladder cannot
    /// do. The rungs prove the tree compiles and its tests pass; whether the window opens and
    /// its dialogues behave is only answerable by running it where windows exist, which is the
    /// host desktop and never the sandbox. Detached through the shell: the app is the
    /// operator's to drive and to close.
    /// </summary>
    /// <summary>The wire name the rating is recorded under, so a transcript can be grepped for it.</summary>
    private const string OperatorRatingTool = "operator_rating";

    /// <summary>The top of the scale, carried in the record so a reader never has to assume it.</summary>
    private const int MaximumRating = 5;

    private void RunApp()
    {
        List<string> applications = FindApplicationProjects();
        if (applications.Count == 0)
        {
            Status = "No application to run: no project under the workspace sets OutputType Exe or WinExe.";
            return;
        }

        // dotnet run is about to build on the host, creating bin and obj outside the sandbox
        // seam that normally marks them - so the sweep runs here first, and the first build
        // cannot race the sync client.
        _dropboxMarker?.EnsureWorkspaceMarked();

        string project = applications[0];
        string name = ToRelative(project) ?? Path.GetFileName(project);

        // The rating question is asked when the app closes, because that is the only moment the
        // operator can honestly answer it - and because the screen is the one oracle the ladder
        // and the critics do not have. Everything else in a run is judged by something; what the
        // window actually looked like is judged by nobody unless a person says so.
        string? failure = _shell.LaunchApp(project, () => _dispatcher.BeginInvoke(() => AskForRating(name)));

        if (failure is not null)
        {
            Status = $"Could not launch '{name}': {failure}";
            return;
        }

        _ratedRunId = _runId;
        _ratedTaskId = _taskId;

        Status = applications.Count == 1
            ? $"Launched {name}. dotnet run builds first, so give it a moment."
            : string.Create(
                CultureInfo.InvariantCulture,
                $"Launched {name}. {applications.Count - 1} other application project(s) found; the first alphabetically is the one running.");
    }

    /// <summary>
    /// Opens the rating strip for the application that has just closed.
    /// <para>
    /// A question rather than a dialog: a modal box on top of whatever the operator turned to
    /// next would be answered to make it go away, and a rating answered to dismiss a box is
    /// worse than no rating at all.
    /// </para>
    /// </summary>
    private void AskForRating(string application)
    {
        RatedApplication = application;
        AppRating = null;
        AppComment = string.Empty;
        IsRatingApp = true;
        Status = $"{application} closed. How did it look?";
    }

    /// <summary>
    /// Records the operator's verdict as a <c>human</c> step, and closes the strip.
    /// <para>
    /// A step rather than a new record type, for the reason the manual commit and push buttons
    /// are steps: it lands in the JSONL transcript and the live view with no new reader, and it
    /// replays with the run it judged. The rating is attributed to the run whose changes the
    /// pane is showing - a verdict on a window is a verdict on the work that built it - and to
    /// no run at all when the operator ran the app without one.
    /// </para>
    /// </summary>
    private void SubmitRating()
    {
        if (AppRating is not { } rating)
        {
            return;
        }

        string comment = (AppComment ?? string.Empty).Trim();
        string rated = _ratedRunId ?? RunContext.Current.RunId;

        _steps?.LogStep(new StepRecord
        {
            RunId = rated,

            // The task the rated run was attempting, latched from its first change. Not
            // RunContext: that is an AsyncLocal the loop sets, and it does not flow to this
            // thread - so it reads "no-task" and every per-task join silently drops the rating.
            TaskId = _ratedTaskId ?? RunContext.Current.TaskId,

            // One past whatever the run reached, which is what the post-run review row already
            // does. The caller cannot know that number; the bus saw every step.
            StepIndex = _transcript?.NextStepIndex(rated) ?? 0,
            Role = "human",
            StartedAt = DateTimeOffset.UtcNow,
            Prompt = [],
            ToolCalls =
            [
                new ToolCallRecord(
                    Guid.NewGuid().ToString("n")[..12],
                    OperatorRatingTool,
                    new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["rating"] = rating,
                        ["outOf"] = MaximumRating,
                        ["application"] = RatedApplication,
                        ["comment"] = comment.Length == 0 ? null : comment,
                    },
                    "Succeeded",
                    Parsed: true,
                    0,
                    null,
                    null,
                    $"Operator rated {RatedApplication} {rating}/{MaximumRating}."),
            ],
            ModelLatencyMs = 0,
            StepLatencyMs = 0,
            Outcome = $"operator rating {rating}/{MaximumRating}",
        });

        Status = comment.Length == 0
            ? $"Recorded {rating}/{MaximumRating} for {RatedApplication}."
            : $"Recorded {rating}/{MaximumRating} for {RatedApplication}, with a comment.";

        IsRatingApp = false;
    }

    /// <summary>
    /// Every project under the workspace that builds an application, sorted so the choice of
    /// which to run is deterministic. Judged by the project file's own OutputType - Exe or
    /// WinExe - read directly, no MSBuild; a library, a test project or an unreadable file is
    /// simply not an application. The deny globs are honoured because publish output under
    /// bin holds copies of project files, and running a copy runs yesterday's app.
    /// </summary>
    private List<string> FindApplicationProjects()
    {
        List<string> found = [];

        try
        {
            foreach (string project in Directory.EnumerateFiles(RootPath, "*.csproj", SearchOption.AllDirectories))
            {
                string full = Path.GetFullPath(project);
                if (ToRelative(full) is not { } relative || IsDenied(relative))
                {
                    continue;
                }

                if (IsApplication(full))
                {
                    found.Add(full);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            // An unreadable workspace has no applications to offer; Status already reports
            // read failures at refresh time.
        }

        found.Sort(StringComparer.OrdinalIgnoreCase);
        return found;
    }

    private static bool IsApplication(string projectFile)
    {
        try
        {
            string? outputType = XDocument.Load(projectFile)
                .Descendants()
                .FirstOrDefault(e => e.Name.LocalName.Equals("OutputType", StringComparison.OrdinalIgnoreCase))
                ?.Value
                .Trim();

            return outputType is not null &&
                (outputType.Equals("Exe", StringComparison.OrdinalIgnoreCase) ||
                 outputType.Equals("WinExe", StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or XmlException)
        {
            return false;
        }
    }

    /// <summary>
    /// Builds the tree off the UI thread, then swaps it in and lays the session's changes over
    /// it. Errors land in <see cref="Status"/>: a workspace pane that cannot read the
    /// workspace is a fact worth showing, not worth crashing over. Says whether the read
    /// completed, so a caller with news of its own knows whether Status is free to carry it.
    /// </summary>
    private async Task<bool> RefreshAsync()
    {
        if (_isLoading)
        {
            return false;
        }

        _isLoading = true;
        Status = "Reading the workspace…";
        try
        {
            Dictionary<string, FileNodeViewModel> index = new(StringComparer.OrdinalIgnoreCase);
            List<FileNodeViewModel> roots = await Task.Run(() => BuildChildren(new DirectoryInfo(RootPath), "", index))
                .ConfigureAwait(true);

            RootNodes.Clear();
            foreach (FileNodeViewModel node in roots)
            {
                RootNodes.Add(node);
            }

            _nodesByPath = index;

            foreach ((string path, FileChangeStats stats) in FileChangeSummary.Summarise(Scope()))
            {
                Apply(path, stats);
            }

            int files = 0;
            foreach (FileNodeViewModel node in index.Values)
            {
                if (!node.IsDirectory)
                {
                    files++;
                }
            }

            Status = _watchFailure is null
                ? string.Create(CultureInfo.InvariantCulture, $"{files} file(s).")
                : string.Create(CultureInfo.InvariantCulture, $"{files} file(s). {_watchFailure}");
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            Status = $"Could not read '{RootPath}': {ex.Message}";
            return false;
        }
        finally
        {
            _isLoading = false;
        }
    }

    /// <summary>Enumerates one directory into sorted nodes, skipping what the deny globs hide.</summary>
    /// <summary>
    /// Makes one node, open or closed as its path deserves.
    /// <para>
    /// Directories open by default, which is what makes the pane show which file a run touched
    /// rather than one row per top-level folder. The retrospectives directory is the exception:
    /// it holds one folder per retrospective ever taken and grows by one every time somebody
    /// presses the button, so left open it is a wall of timestamps between the operator and the
    /// source they came here to watch. It is also the one folder whose children are reached by
    /// double-clicking rather than by reading, so opening it shows nothing that expanding it
    /// would not.
    /// </para>
    /// </summary>
    private FileNodeViewModel Node(string name, string path, bool isDirectory) =>
        new(name, path, isDirectory)
        {
            IsExpanded = isDirectory && !string.Equals(path, _retrospectives, StringComparison.OrdinalIgnoreCase),
        };

    private List<FileNodeViewModel> BuildChildren(
        DirectoryInfo directory, string relative, Dictionary<string, FileNodeViewModel> index)
    {
        List<FileNodeViewModel> directories = [];
        List<FileNodeViewModel> files = [];

        IEnumerable<FileSystemInfo> entries;
        try
        {
            entries = directory.EnumerateFileSystemInfos();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A folder that cannot be read shows as empty rather than taking the pane down.
            return directories;
        }

        foreach (FileSystemInfo entry in entries)
        {
            string path = relative.Length == 0 ? entry.Name : relative + "/" + entry.Name;

            if (entry is DirectoryInfo child)
            {
                if (IsDeniedDirectory(path))
                {
                    continue;
                }

                FileNodeViewModel node = Node(entry.Name, path, isDirectory: true);
                foreach (FileNodeViewModel grandChild in BuildChildren(child, path, index))
                {
                    node.Children.Add(grandChild);
                }

                index[path] = node;
                directories.Add(node);
            }
            else if (_denied is null || !_denied.Match(path).HasMatches)
            {
                FileNodeViewModel node = Node(entry.Name, path, isDirectory: false);
                index[path] = node;
                files.Add(node);
            }
        }

        directories.Sort(CompareNames);
        files.Sort(CompareNames);
        directories.AddRange(files);
        return directories;
    }

    private bool IsDeniedDirectory(string relativePath) =>
        _denied is not null && _denied.Match(relativePath + "/" + DirectoryProbe).HasMatches;

    // ── What the workspace contains ──

    /// <summary>
    /// Starts watching the tree, and reports rather than throws when it cannot.
    /// <para>
    /// Only names are watched, not content: a rename, a create and a delete change the shape of
    /// the tree, and a write does not. The events are treated as "look at this path again"
    /// rather than as facts - the drain asks the file system what is actually there, which is
    /// what makes a create-then-delete, a rename and a half-written file all come out right.
    /// </para>
    /// </summary>
    private (FileSystemWatcher? Watcher, string? Failure) StartWatching()
    {
        FileSystemWatcher? watcher = null;
        try
        {
            watcher = new FileSystemWatcher(RootPath)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName,

                // A build can produce thousands of events in a burst. The default 8 KB buffer
                // overflows, and an overflow costs a full re-read rather than one node.
                InternalBufferSize = 64 * 1024,
            };

            watcher.Created += (_, e) => Queue(e.FullPath);
            watcher.Deleted += (_, e) => Queue(e.FullPath);
            watcher.Renamed += (_, e) =>
            {
                Queue(e.OldFullPath);
                Queue(e.FullPath);
            };

            // Overflow, or the root going away. Either way the incremental picture is no longer
            // trustworthy, and the honest response is to read the tree again.
            watcher.Error += (_, _) => _dispatcher.BeginInvoke(() => _ = RefreshAsync());

            watcher.EnableRaisingEvents = true;
            return (watcher, null);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            // Enabling can fail after construction - the root going away between the two is
            // enough - and a watcher nobody holds is a handle nobody closes.
            watcher?.Dispose();
            return (null, $"Live updates are off ({ex.Message}); use Refresh.");
        }
    }

    /// <summary>
    /// Notes a path to re-examine. Runs on a watcher thread, so it does as little as possible:
    /// the deny check is here rather than in the drain because a build writes thousands of paths
    /// under <c>bin/</c> and <c>obj/</c> and every one of them would otherwise cost a post to
    /// the UI thread.
    /// </summary>
    private void Queue(string fullPath)
    {
        if (ToRelative(fullPath) is not { } relative || IsDenied(relative))
        {
            return;
        }

        bool post;
        lock (_pendingGate)
        {
            _pending.Add(relative);
            post = !_drainQueued;
            _drainQueued = true;
        }

        // One drain per burst, at background priority: a `dotnet new` is a dozen events and a
        // checkout is thousands, and neither should be a dozen or thousands of tree edits.
        if (post)
        {
            _dispatcher.BeginInvoke(DispatcherPriority.Background, Drain);
        }
    }

    /// <summary>Reconciles every noted path with what the file system now holds.</summary>
    private void Drain()
    {
        string[] paths;
        lock (_pendingGate)
        {
            paths = [.. _pending];
            _pending.Clear();
            _drainQueued = false;
        }

        foreach (string relative in paths)
        {
            Rescan(relative);
        }
    }

    /// <summary>
    /// Brings one path in line with disk: present as a directory, present as a file, or gone.
    /// </summary>
    private void Rescan(string relative)
    {
        string full = Path.Combine(RootPath, relative.Replace('/', Path.DirectorySeparatorChar));

        if (Directory.Exists(full))
        {
            FileNodeViewModel node = EnsureNode(relative, isFile: false);

            // A directory moved in from outside arrives as one event for the folder and none for
            // what is in it, so a folder that shows empty would be a lie. Reading it here uses
            // the same walk, sort and deny filter the initial load uses.
            if (node.Children.Count == 0)
            {
                foreach (FileNodeViewModel child in BuildChildren(new DirectoryInfo(full), relative, _nodesByPath))
                {
                    node.Children.Add(child);
                }
            }
        }
        else if (File.Exists(full))
        {
            EnsureNode(relative, isFile: true);
        }
        else
        {
            Remove(relative);
        }
    }

    /// <summary>Whether the deny globs hide this path, as either a file or a directory.</summary>
    private bool IsDenied(string relative) =>
        _denied is not null && (_denied.Match(relative).HasMatches || IsDeniedDirectory(relative));

    /// <summary>The repo-relative path with forward slashes, or null when it is not under the root.</summary>
    private string? ToRelative(string fullPath) =>
        fullPath.StartsWith(_rootPrefix, StringComparison.OrdinalIgnoreCase)
            ? fullPath[_rootPrefix.Length..].Replace(Path.DirectorySeparatorChar, '/')
            : null;

    // ── What this run did to it ──

    private void OnChanged(object? sender, CodeChange change)
    {
        // Tools raise this from whatever thread they are on, but the Changes surface raises it
        // from the UI thread when a human applies or reverts - and there the hop only means the
        // tree lags its own window by a dispatcher turn.
        if (_dispatcher.CheckAccess())
        {
            Record(change);
        }
        else
        {
            _dispatcher.BeginInvoke(() => Record(change));
        }
    }

    /// <summary>Marks, unmarks or ignores one change, according to which run it belongs to.</summary>
    private void Record(CodeChange change)
    {
        if (_awaitingRun)
        {
            _runId = change.RunId;

            // Latched with the run id, from the same change, because the alternative is
            // RunContext - and that is an AsyncLocal set inside the loop, so on this thread it
            // reads the "no-task" placeholder. A rating naming a real run and a nonexistent task
            // is dropped by every per-task join that would want it.
            _taskId = change.TaskId;
            _awaitingRun = false;
        }
        else if (_runId is not null && !string.Equals(change.RunId, _runId, StringComparison.Ordinal))
        {
            // An earlier run's change - a revert from the Changes surface, most likely. It is
            // real, and it is not this run's arithmetic.
            return;
        }

        FileChangeStats? stats = FileChangeSummary.ForPath(Scope(), change.Path);
        if (stats is null)
        {
            if (_nodesByPath.TryGetValue(change.Path, out FileNodeViewModel? node))
            {
                node.ClearStats();
            }

            return;
        }

        Apply(change.Path, stats.Value);
    }

    /// <summary>
    /// The changes the counts are drawn from: this run's, once there is one. Before the first
    /// run of the session that is every change; between <see cref="BeginRun"/> and the run's
    /// first change it is none, which is what makes the tree go plain the moment Run is pressed.
    /// </summary>
    private IReadOnlyList<CodeChange> Scope()
    {
        if (_awaitingRun)
        {
            return [];
        }

        IReadOnlyList<CodeChange> all = _changes.All();
        return _runId is null
            ? all
            : [.. all.Where(change => string.Equals(change.RunId, _runId, StringComparison.Ordinal))];
    }

    /// <summary>
    /// Marks the file and opens the folders above it, so a change is never folded away.
    /// <para>
    /// Only when the file is on disk. The change log colours the tree; what exists is the
    /// watcher's to say. A delete's change stays Applied - and the loop re-raises it after the
    /// step, attaching the ladder's summary - so marking without this check would recreate the
    /// node the watcher just removed: a green row for a file that is gone. Creation is
    /// unaffected, because every tool writes before it records Applied.
    /// </para>
    /// </summary>
    private void Apply(string path, FileChangeStats stats)
    {
        string full = Path.Combine(RootPath, path.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(full))
        {
            return;
        }

        EnsureNode(path, isFile: true).SetStats(stats);

        for (int cut = path.LastIndexOf('/'); cut >= 0; cut = path.LastIndexOf('/'))
        {
            path = path[..cut];
            if (_nodesByPath.TryGetValue(path, out FileNodeViewModel? ancestor))
            {
                ancestor.IsExpanded = true;
            }
        }
    }

    /// <summary>
    /// Returns the node for a path, creating it and every folder above it that is missing. This
    /// is what lets a file appear in the tree the moment it exists, rather than at the next
    /// full read.
    /// </summary>
    private FileNodeViewModel EnsureNode(string path, bool isFile)
    {
        string[] segments = path.Split('/');
        ObservableCollection<FileNodeViewModel> siblings = RootNodes;
        string relative = string.Empty;
        FileNodeViewModel? node = null;

        for (int i = 0; i < segments.Length; i++)
        {
            bool last = i == segments.Length - 1;
            relative = relative.Length == 0 ? segments[i] : relative + "/" + segments[i];

            if (!_nodesByPath.TryGetValue(relative, out node))
            {
                node = Node(segments[i], relative, isDirectory: !last || !isFile);
                _nodesByPath[relative] = node;
                Insert(siblings, node);
            }

            siblings = node.Children;
        }

        return node!;
    }

    /// <summary>Drops a node and everything under it - the path is gone from the workspace.</summary>
    private void Remove(string path)
    {
        if (!_nodesByPath.TryGetValue(path, out FileNodeViewModel? node))
        {
            return;
        }

        int cut = path.LastIndexOf('/');
        ObservableCollection<FileNodeViewModel> siblings =
            cut >= 0 && _nodesByPath.TryGetValue(path[..cut], out FileNodeViewModel? parent)
                ? parent.Children
                : RootNodes;

        siblings.Remove(node);

        // The subtree goes with it. An index still holding nodes nothing can reach would let a
        // recreated file adopt the stats of the one that was deleted.
        string prefix = path + "/";
        List<string> gone = [path];
        foreach (string key in _nodesByPath.Keys)
        {
            if (key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                gone.Add(key);
            }
        }

        foreach (string key in gone)
        {
            _nodesByPath.Remove(key);
        }
    }

    /// <summary>Inserts keeping the build order: directories first, then names.</summary>
    private static void Insert(ObservableCollection<FileNodeViewModel> siblings, FileNodeViewModel node)
    {
        int at = 0;
        while (at < siblings.Count &&
               (siblings[at].IsDirectory == node.IsDirectory
                   ? CompareNames(siblings[at], node) <= 0
                   : siblings[at].IsDirectory))
        {
            at++;
        }

        siblings.Insert(at, node);
    }

    private static int CompareNames(FileNodeViewModel left, FileNodeViewModel right) =>
        string.Compare(left.Name, right.Name, StringComparison.OrdinalIgnoreCase);
}
