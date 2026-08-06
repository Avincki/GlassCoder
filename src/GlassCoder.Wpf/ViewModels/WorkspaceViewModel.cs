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

    private readonly IChangeLog _changes;
    private readonly IConfiguration _configuration;
    private readonly IUserSettingsStore _store;
    private readonly IDesktopShell _shell;
    private readonly Dispatcher _dispatcher;
    private readonly Matcher? _denied;
    private readonly IReadOnlyList<string> _writableRoots;
    private readonly DropboxIgnoreMarker? _dropboxMarker;
    private readonly string _rootPrefix;
    private bool _isAgentRunning;
    private readonly Lock _pendingGate = new();
    private readonly HashSet<string> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly FileSystemWatcher? _watcher;
    private readonly string? _watchFailure;
    private Dictionary<string, FileNodeViewModel> _nodesByPath = new(StringComparer.OrdinalIgnoreCase);
    private string _status = string.Empty;
    private string? _pendingRoot;
    private string? _runId;
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
        DropboxIgnoreMarker? dropboxMarker = null)
    {
        ArgumentNullException.ThrowIfNull(guard);
        ArgumentNullException.ThrowIfNull(workspace);

        _changes = changes;
        _configuration = configuration;
        _store = store;
        _shell = shell;
        _dispatcher = dispatcher ?? Dispatcher.CurrentDispatcher;
        _dropboxMarker = dropboxMarker;

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

        _changes.Changed += OnChanged;

        BrowseCommand = new RelayCommand(Browse, () => !_isLoading);
        RestartCommand = new RelayCommand(_shell.Restart, () => HasPendingRoot);
        RefreshCommand = new RelayCommand(() => _ = RefreshAsync(), () => !_isLoading);
        CleanCommand = new RelayCommand(Clean, () => !_isLoading && !IsAgentRunning);
        RunAppCommand = new RelayCommand(RunApp, () => !_isLoading && !IsAgentRunning);
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

    /// <summary>Opens the double-clicked file in a read-only viewer window.</summary>
    public RelayCommand OpenFileCommand { get; }

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
        _awaitingRun = true;
    }

    /// <summary>Stops watching the workspace.</summary>
    public void Dispose()
    {
        _changes.Changed -= OnChanged;
        _watcher?.Dispose();
    }

    /// <summary>
    /// Only files, never directories.
    /// <para>
    /// This is what keeps folders behaving like folders. The command is bound to a double-click
    /// on the tree item, and an <see cref="System.Windows.Input.InputBinding"/> whose command
    /// cannot execute leaves the event unhandled - so a double-click on a directory falls
    /// through to the TreeView and expands it, as it did before there was a viewer.
    /// </para>
    /// </summary>
    private static bool CanOpenFile(object? parameter) =>
        parameter is FileNodeViewModel { IsDirectory: false };

    private void OpenFile(object? parameter)
    {
        if (parameter is not FileNodeViewModel node || node.IsDirectory)
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
    /// Empties the writable roots so the next run starts from the blank workspace the opening
    /// message promises. Only those roots: a run's output lives inside the folders the guard
    /// lets it write and nowhere else, so a clean that reached further would be deleting things
    /// no run ever made - a README beside them, or the workspace's own .git. Asks first, and
    /// deletes what it can rather than stopping at the first locked file: half a clean plus an
    /// honest count beats a sync client winning by holding one handle.
    /// </summary>
    private void Clean()
    {
        if (_writableRoots.Count == 0)
        {
            Status = "No writable roots are configured, so there is nothing to clean.";
            return;
        }

        string names = string.Join(", ", _writableRoots);
        if (!_shell.Confirm(
                "Clean the workspace",
                $"Delete everything inside {names} under:\n{RootPath}\n\n" +
                "The folders themselves stay. There is no undo."))
        {
            Status = "Clean cancelled; nothing was deleted.";
            return;
        }

        int removed = 0;
        List<string> failures = [];

        foreach (string root in _writableRoots)
        {
            string full = Path.GetFullPath(Path.Combine(RootPath, root));
            if (!IsInsideRoot(full))
            {
                // "." or an absolute path elsewhere. Emptying it would reach the workspace's own
                // .git or another project entirely, and this button only deletes what a run
                // could have made.
                failures.Add($"'{root}' skipped: it is not strictly inside the workspace");
                continue;
            }

            try
            {
                // Recreated when missing, so a clean always leaves the roots the guard promises.
                Directory.CreateDirectory(full);

                foreach (FileSystemInfo entry in new DirectoryInfo(full).EnumerateFileSystemInfos())
                {
                    try
                    {
                        if (entry is DirectoryInfo directory)
                        {
                            directory.Delete(recursive: true);
                        }
                        else
                        {
                            entry.Delete();
                        }

                        removed++;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        failures.Add($"{entry.Name}: {ex.Message}");
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failures.Add($"{root}: {ex.Message}");
            }
        }

        Status = failures.Count == 0
            ? string.Create(CultureInfo.InvariantCulture, $"Cleaned {names}: {removed} item(s) removed.")
            : string.Create(CultureInfo.InvariantCulture,
                $"Cleaned {names}: {removed} item(s) removed, {failures.Count} could not be. {failures[0]}");

        _ = RefreshAsync();
    }

    /// <summary>
    /// Launches the workspace's application on the desktop - the live check the ladder cannot
    /// do. The rungs prove the tree compiles and its tests pass; whether the window opens and
    /// its dialogues behave is only answerable by running it where windows exist, which is the
    /// host desktop and never the sandbox. Detached through the shell: the app is the
    /// operator's to drive and to close.
    /// </summary>
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
        string? failure = _shell.LaunchApp(project);

        if (failure is not null)
        {
            Status = $"Could not launch '{name}': {failure}";
            return;
        }

        Status = applications.Count == 1
            ? $"Launched {name}. dotnet run builds first, so give it a moment."
            : string.Create(
                CultureInfo.InvariantCulture,
                $"Launched {name}. {applications.Count - 1} other application project(s) found; the first alphabetically is the one running.");
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
    /// workspace is a fact worth showing, not worth crashing over.
    /// </summary>
    private async Task RefreshAsync()
    {
        if (_isLoading)
        {
            return;
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
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            Status = $"Could not read '{RootPath}': {ex.Message}";
        }
        finally
        {
            _isLoading = false;
        }
    }

    /// <summary>Enumerates one directory into sorted nodes, skipping what the deny globs hide.</summary>
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

                FileNodeViewModel node = new(entry.Name, path, isDirectory: true);
                foreach (FileNodeViewModel grandChild in BuildChildren(child, path, index))
                {
                    node.Children.Add(grandChild);
                }

                index[path] = node;
                directories.Add(node);
            }
            else if (_denied is null || !_denied.Match(path).HasMatches)
            {
                FileNodeViewModel node = new(entry.Name, path, isDirectory: false);
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
                node = new FileNodeViewModel(segments[i], relative, isDirectory: !last || !isFile);
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
