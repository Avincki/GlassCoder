using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Threading;
using GlassCoder.Core.Configuration;
using GlassCoder.Tools.Changes;
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
    }

    /// <summary>File or directory name, as shown.</summary>
    public string Name { get; }

    /// <summary>Repo-relative path with forward slashes - the change log's spelling.</summary>
    public string RelativePath { get; }

    /// <summary>Whether this node can have children.</summary>
    public bool IsDirectory { get; }

    /// <summary>Child nodes, directories first.</summary>
    public ObservableCollection<FileNodeViewModel> Children { get; } = [];

    /// <summary>Bound two-way, so marking a file modified can expand the folders above it.</summary>
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
/// </summary>
public sealed class WorkspaceViewModel : ViewModelBase
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
    private Dictionary<string, FileNodeViewModel> _nodesByPath = new(StringComparer.OrdinalIgnoreCase);
    private string _status = string.Empty;
    private string? _pendingRoot;
    private bool _isLoading;

    /// <summary>Creates the pane over the active workspace root and subscribes to the change log.</summary>
    public WorkspaceViewModel(
        IPathGuard guard,
        IChangeLog changes,
        IOptions<WorkspaceOptions> workspace,
        IConfiguration configuration,
        IUserSettingsStore store,
        IDesktopShell shell,
        Dispatcher? dispatcher = null)
    {
        ArgumentNullException.ThrowIfNull(guard);
        ArgumentNullException.ThrowIfNull(workspace);

        _changes = changes;
        _configuration = configuration;
        _store = store;
        _shell = shell;
        _dispatcher = dispatcher ?? Dispatcher.CurrentDispatcher;

        RootPath = guard.RepoRoot;

        // The tree hides exactly what the agent cannot touch, so the pane and the guardrail
        // never disagree about what the workspace contains.
        if (workspace.Value.DeniedGlobs.Count > 0)
        {
            _denied = new Matcher(StringComparison.OrdinalIgnoreCase);
            _denied.AddIncludePatterns(workspace.Value.DeniedGlobs);
        }

        _changes.Changed += OnChanged;

        BrowseCommand = new RelayCommand(Browse, () => !_isLoading);
        RestartCommand = new RelayCommand(_shell.Restart, () => HasPendingRoot);
        RefreshCommand = new RelayCommand(() => _ = RefreshAsync(), () => !_isLoading);
        OpenFileCommand = new RelayCommand(OpenFile, CanOpenFile);

        _ = RefreshAsync();
    }

    /// <summary>The workspace root the agent is working in.</summary>
    public string RootPath { get; }

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

    /// <summary>Opens the double-clicked file in a read-only viewer window.</summary>
    public RelayCommand OpenFileCommand { get; }

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

            foreach ((string path, FileChangeStats stats) in FileChangeSummary.Summarise(_changes.All()))
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

            Status = string.Create(CultureInfo.InvariantCulture, $"{files} file(s).");
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

    private void OnChanged(object? sender, CodeChange change) =>
        _dispatcher.BeginInvoke(() =>
        {
            FileChangeStats? stats = FileChangeSummary.ForPath(_changes.All(), change.Path);
            if (stats is null)
            {
                if (_nodesByPath.TryGetValue(change.Path, out FileNodeViewModel? node))
                {
                    node.ClearStats();
                }

                return;
            }

            Apply(change.Path, stats.Value);
        });

    /// <summary>
    /// Marks the file and expands the folders above it, creating nodes on the way for a file
    /// the agent brought into being after the tree was read.
    /// </summary>
    private void Apply(string path, FileChangeStats stats)
    {
        string[] segments = path.Split('/');
        ObservableCollection<FileNodeViewModel> siblings = RootNodes;
        string relative = string.Empty;

        for (int i = 0; i < segments.Length; i++)
        {
            bool isFile = i == segments.Length - 1;
            relative = relative.Length == 0 ? segments[i] : relative + "/" + segments[i];

            if (!_nodesByPath.TryGetValue(relative, out FileNodeViewModel? node))
            {
                node = new FileNodeViewModel(segments[i], relative, isDirectory: !isFile);
                _nodesByPath[relative] = node;
                Insert(siblings, node);
            }

            if (isFile)
            {
                node.SetStats(stats);
            }
            else
            {
                node.IsExpanded = true;
                siblings = node.Children;
            }
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
