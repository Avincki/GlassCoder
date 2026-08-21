using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using GlassCoder.Core.Configuration;
using GlassCoder.Models;
using GlassCoder.Models.Configuration;
using GlassCoder.Tools.Execution;
using System.IO;
using Microsoft.Extensions.Options;
using GlassCoder.Tools.Retrieval;
using GlassCoder.Wpf.Mvvm;
using GlassCoder.Wpf.Services;
using Microsoft.Extensions.Configuration;

namespace GlassCoder.Wpf.ViewModels;

/// <summary>
/// The settings dialog (CLAUDE.md §13: every endpoint, alias, budget and limit is configuration).
/// <para>
/// It edits the effective configuration - every layer the harness actually bound, including
/// environment variables and any <c>--config</c> arm - and saves it to the per-user settings
/// file. What it saves therefore loses to an environment variable and to an ablation arm, which
/// is the right way round: a saved preference must never quietly redefine what an arm means.
/// </para>
/// </summary>
public sealed class SettingsViewModel : ViewModelBase
{
    private readonly IUserSettingsStore _store;
    private readonly IProjectSettingsStore _projectStore;
    private readonly ISettingsTransfer _transfer;
    private readonly IModelConnectionProbe _probe;
    private readonly IDesktopShell _shell;
    private RoleSettingsViewModel? _selectedRole;
    private string _status;
    private bool _isBusy;

    /// <summary>Creates the view model over the configuration the harness is running on.</summary>
    public SettingsViewModel(
        IConfiguration configuration,
        IUserSettingsStore store,
        IProjectSettingsStore projectStore,
        ISettingsTransfer transfer,
        IModelConnectionProbe probe,
        IDesktopShell shell)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(projectStore);
        ArgumentNullException.ThrowIfNull(transfer);

        _store = store;
        _projectStore = projectStore;
        _transfer = transfer;
        _probe = probe;
        _shell = shell;

        Settings = GlassCoderSettings.ReadFrom(configuration);
        BuildRoles();

        int undecryptable = _store.LoadSecrets().Count(entry => entry.Value is null);
        _status = undecryptable > 0
            ? $"{undecryptable} stored key(s) could not be decrypted on this machine. Enter them again."
            : "Loaded the effective configuration.";

        SaveCommand = new RelayCommand(() => Save(restart: false), () => !IsBusy);
        SaveAndRestartCommand = new RelayCommand(() => Save(restart: true), () => !IsBusy);
        AddRoleCommand = new RelayCommand(AddRole);
        RemoveRoleCommand = new RelayCommand(RemoveRole, () => SelectedRole is not null && Roles.Count > 1);
        AddEndpointCommand = new RelayCommand(AddEndpoint, () => !IsKnownEndpoint(SelectedRole?.Endpoint));
        RemoveEndpointCommand = new RelayCommand(RemoveEndpoint, () => IsKnownEndpoint(SelectedRole?.Endpoint));
        TestAllCommand = new RelayCommand(async () => await TestAllAsync().ConfigureAwait(true), () => !IsBusy);
        OpenFolderCommand = new RelayCommand(() => _shell.OpenFolder(_store.DirectoryPath));
        ResetCommand = new RelayCommand(Reset, () => _store.Exists);
        ExportCommand = new RelayCommand(Export, () => !IsBusy);
        ImportCommand = new RelayCommand(Import, () => !IsBusy);
        SaveToProjectCommand = new RelayCommand(SaveToProject, () => !IsBusy && HasProjectRoot);
        RecordRetrievalToolsCommand = new RelayCommand(
            async () => await RecordRetrievalToolsAsync().ConfigureAwait(true), () => !IsBusy);
    }

    /// <summary>Raised when the dialog should close. The argument is whether anything was saved.</summary>
    public event EventHandler<bool>? CloseRequested;

    /// <summary>
    /// Every configurable section, bound directly by the view. Replaced wholesale by an import,
    /// which is why it is settable at all.
    /// </summary>
    public GlassCoderSettings Settings { get; private set; }

    /// <summary>The served roles.</summary>
    public ObservableCollection<RoleSettingsViewModel> Roles { get; } = [];

    /// <summary>Everything that would stop the harness from starting on these settings.</summary>
    public ObservableCollection<string> ValidationFailures { get; } = [];

    /// <summary>Role names, for the pickers that have to name one.</summary>
    public IReadOnlyList<string> RoleNames => [.. Roles.Select(role => role.Name)];

    /// <summary>
    /// The endpoints every role's picker offers. Curated by the operator, not discovered: adding
    /// one remembers an address worth typing again, removing one forgets it, and neither touches
    /// what any role is currently served by.
    /// </summary>
    public ObservableCollection<string> Endpoints { get; } = [];

    /// <summary>Where commands may run.</summary>
    public IReadOnlyList<SandboxMode> SandboxModes { get; } = [SandboxMode.Docker, SandboxMode.Local];

    /// <summary>The wire formats a role's endpoint can speak (workplan task 37).</summary>
    public IReadOnlyList<ModelTransport> Transports { get; } = [ModelTransport.OpenAI, ModelTransport.Anthropic];

    /// <summary>
    /// How a retrieval call reaches its upstream (workplan tasks 56, 63).
    /// <para>
    /// Replay first because it is the safe one and the one the Lab runs: it serves from the
    /// recorded corpus and fails loudly on a miss rather than quietly reaching the network.
    /// </para>
    /// </summary>
    public IReadOnlyList<RetrievalMode> RetrievalModes { get; } =
        [RetrievalMode.Replay, RetrievalMode.Record, RetrievalMode.Live];

    /// <summary>Serilog levels, lowest first.</summary>
    public IReadOnlyList<string> LogLevels { get; } =
        ["Verbose", "Debug", "Information", "Warning", "Error", "Fatal"];

    /// <summary>The role being edited.</summary>
    public RoleSettingsViewModel? SelectedRole
    {
        get => _selectedRole;
        set => SetProperty(ref _selectedRole, value);
    }

    /// <summary>What the dialog is doing, or what it last did.</summary>
    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    /// <summary>Whether a save or a check is in flight.</summary>
    public bool IsBusy
    {
        get => _isBusy;
        private set => SetProperty(ref _isBusy, value);
    }

    /// <summary>Where the settings are stored, and what protects the keys.</summary>
    public string StorageSummary =>
        $"Saved to {_store.SettingsFilePath}. API keys go to {_store.SecretsFilePath}, " +
        (_store.SecretsAreEncrypted
            ? $"encrypted with {_store.ProtectionScheme} for this Windows account."
            : $"only {_store.ProtectionScheme}-encoded on this platform - prefer an environment variable for keys.");

    /// <summary>The repository these settings would be saved into, when one is known.</summary>
    public string ProjectRoot => Settings.Workspace.RepoRoot ?? string.Empty;

    /// <summary>Whether a real project is selected, rather than the placeholder root.</summary>
    public bool HasProjectRoot => !WorkspaceRootLocator.IsUnset(ProjectRoot);

    /// <summary>
    /// What a project file would do for the current project, and whether one is already in force.
    /// </summary>
    public string ProjectSummary
    {
        get
        {
            if (!HasProjectRoot)
            {
                return "No project is selected, so there is nowhere to write a project file.";
            }

            string path = _projectStore.FilePathFor(ProjectRoot);
            string sections = string.Join(", ", SettingsDocument.ProjectSectionNames);

            return _projectStore.ExistsIn(ProjectRoot)
                ? $"{path} is in force for this project. It carries {sections} and never an API key."
                : $"Save to project writes {path}, carrying {sections} and never an API key. " +
                  "The project then brings its own paths and branches wherever it is cloned.";
        }
    }

    /// <summary>Repository roots the agent may read, one per line. Empty means the repository root.</summary>
    public string ReadablePaths
    {
        get => Join(Settings.Workspace.ReadablePaths);
        set { Replace(Settings.Workspace.ReadablePaths, value); OnPropertyChanged(); }
    }

    /// <summary>Roots the agent may write, one per line. Empty means nothing is writable.</summary>
    public string WritablePaths
    {
        get => Join(Settings.Workspace.WritablePaths);
        set { Replace(Settings.Workspace.WritablePaths, value); OnPropertyChanged(); }
    }

    /// <summary>Globs excluded from every access, one per line.</summary>
    public string DeniedGlobs
    {
        get => Join(Settings.Workspace.DeniedGlobs);
        set { Replace(Settings.Workspace.DeniedGlobs, value); OnPropertyChanged(); }
    }

    /// <summary>Files always loaded into the window, one per line.</summary>
    public string RootContextFiles
    {
        get => Join(Settings.Context.RootContextFiles);
        set { Replace(Settings.Context.RootContextFiles, value); OnPropertyChanged(); }
    }

    /// <summary>Extra directories scanned for reference assemblies, one per line.</summary>
    public string ExtraReferenceDirectories
    {
        get => Join(Settings.Verification.ExtraReferenceDirectories);
        set { Replace(Settings.Verification.ExtraReferenceDirectories, value); OnPropertyChanged(); }
    }

    /// <summary>Environment passed into the container, as <c>NAME=value</c> lines.</summary>
    public string SandboxEnvironment
    {
        get => Join(Settings.Sandbox.Environment);
        set { Replace(Settings.Sandbox.Environment, value); OnPropertyChanged(); }
    }

    /// <summary>Paths the freshness check ignores, one per line.</summary>
    public string TriggerExclusions
    {
        get => Join(Settings.Provenance.TriggerExclusions);
        set { Replace(Settings.Provenance.TriggerExclusions, value); OnPropertyChanged(); }
    }

    /// <summary>Extensions counted as source when judging freshness, one per line.</summary>
    public string SourceExtensions
    {
        get => Join(Settings.Provenance.SourceExtensions);
        set { Replace(Settings.Provenance.SourceExtensions, value); OnPropertyChanged(); }
    }

    /// <summary>Property names always replaced with a redaction marker, one per line.</summary>
    public string RedactedPropertyNames
    {
        get => Join(Settings.Logging.RedactedPropertyNames);
        set { Replace(Settings.Logging.RedactedPropertyNames, value); OnPropertyChanged(); }
    }

    /// <summary>Extra ActivitySource names to subscribe to, one per line.</summary>
    public string AdditionalTelemetrySources
    {
        get => Join(Settings.Telemetry.AdditionalSources);
        set { Replace(Settings.Telemetry.AdditionalSources, value); OnPropertyChanged(); }
    }

    /// <summary>Branches <c>git_push</c> may touch, one per line. Empty means any branch.</summary>
    public string PushableBranches
    {
        get => Join(Settings.Git.PushableBranches);
        set { Replace(Settings.Git.PushableBranches, value); OnPropertyChanged(); }
    }

    /// <summary>Branches never pushed, one per line. Wins over the pushable list.</summary>
    public string ProtectedBranches
    {
        get => Join(Settings.Git.ProtectedBranches);
        set { Replace(Settings.Git.ProtectedBranches, value); OnPropertyChanged(); }
    }

    /// <summary>
    /// Base branch for pull requests. Empty means the repository default, which the options
    /// object spells as null - a text box cannot, so the two are translated here.
    /// </summary>
    public string PullRequestBaseBranch
    {
        get => Settings.Git.PullRequestBaseBranch ?? string.Empty;
        set
        {
            Settings.Git.PullRequestBaseBranch = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            OnPropertyChanged();
        }
    }

    /// <summary>Validates and saves.</summary>
    public RelayCommand SaveCommand { get; }

    /// <summary>Saves, then restarts so the new settings are the ones in force.</summary>
    public RelayCommand SaveAndRestartCommand { get; }

    /// <summary>Adds a served role.</summary>
    public RelayCommand AddRoleCommand { get; }

    /// <summary>Removes the selected role.</summary>
    public RelayCommand RemoveRoleCommand { get; }

    /// <summary>Remembers the endpoint the selected role is pointed at.</summary>
    public RelayCommand AddEndpointCommand { get; }

    /// <summary>Forgets the endpoint the selected role is pointed at.</summary>
    public RelayCommand RemoveEndpointCommand { get; }

    /// <summary>Checks every role against its server.</summary>
    public RelayCommand TestAllCommand { get; }

    /// <summary>Opens the folder the settings live in.</summary>
    public RelayCommand OpenFolderCommand { get; }

    /// <summary>Deletes the saved settings, falling back to <c>appsettings.json</c>.</summary>
    public RelayCommand ResetCommand { get; }

    /// <summary>Writes everything to a portable file, keys included, under a passphrase.</summary>
    public RelayCommand ExportCommand { get; }

    /// <summary>Loads a portable file into the dialog, ready to review and save.</summary>
    public RelayCommand ImportCommand { get; }

    /// <summary>Writes the project-shaped sections into the project itself.</summary>
    public RelayCommand SaveToProjectCommand { get; }

    /// <summary>Records what each enabled retrieval server advertises, so its tools can register.</summary>
    public RelayCommand RecordRetrievalToolsCommand { get; }

    /// <summary>Checks every role in turn, and reports how many worked.</summary>
    /// <summary>
    /// Connects to each enabled retrieval server, asks what it advertises, and writes the answer
    /// to the corpus (workplan tasks 56, 63).
    /// <para>
    /// This exists because switching retrieval on was not enough and the reason was invisible.
    /// Registration reads the recorded tool list, so that a <see cref="RetrievalMode.Replay"/>
    /// run opens no socket at startup - which is the property the Lab depends on, and which
    /// leaves an operator who has just ticked two boxes with tools that never appear. The
    /// alternative was a six-step dance through Record mode and back. One button is better.
    /// </para>
    /// <para>
    /// It records tool <em>lists</em>, not answers. That is what registration needs; the answers
    /// to actual questions still come from whatever mode the run is in.
    /// </para>
    /// </summary>
    public async Task RecordRetrievalToolsAsync()
    {
        RetrievalOptions retrieval = Settings.Retrieval;
        RetrievalServer[] servers = [.. retrieval.EnabledServers()];

        if (!retrieval.Enabled || servers.Length == 0)
        {
            Status = "Switch retrieval on, and at least one server, before recording its tools.";
            return;
        }

        IsBusy = true;
        Status = "Asking the retrieval servers what they offer…";
        try
        {
            string directory = string.IsNullOrWhiteSpace(retrieval.CacheDirectory)
                ? AppPaths.ResolveDataDirectory(RetrievalOptions.DefaultCacheDirectory)
                : AppPaths.ResolveDataDirectory(retrieval.CacheDirectory);

            RetrievalCache cache = new(directory);
            await using McpRetrievalUpstream upstream = new(new StaticOptionsMonitor(retrieval));

            List<string> recorded = [];
            List<string> failed = [];

            foreach (RetrievalServer server in servers)
            {
                IReadOnlyList<RetrievalToolDescriptor> tools =
                    await upstream.ListToolsAsync(server).ConfigureAwait(true);

                if (tools.Count == 0)
                {
                    failed.Add(server.ToString());
                    continue;
                }

                cache.Put(
                    RetrievalCacheKey.From(server, RetrievalCatalog.ToolListKey, null),
                    RetrievalCatalog.Serialize(tools));

                recorded.Add($"{server} ({tools.Count})");
            }

            Status = (recorded.Count, failed.Count) switch
            {
                (0, _) => $"No server answered. Check the endpoints, and any token they need. ({string.Join(", ", failed)})",
                (_, 0) => $"Recorded {string.Join(", ", recorded)}. Save and restart to advertise them.",
                _ => $"Recorded {string.Join(", ", recorded)}; {string.Join(", ", failed)} did not answer.",
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Status = $"Could not write the corpus: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>The unsaved options, for a client built outside the container.</summary>
    private sealed class StaticOptionsMonitor(RetrievalOptions options) : IOptionsMonitor<RetrievalOptions>
    {
        public RetrievalOptions CurrentValue => options;

        public RetrievalOptions Get(string? name) => options;

        public IDisposable? OnChange(Action<RetrievalOptions, string?> listener) => null;
    }

    public async Task TestAllAsync()
    {
        IsBusy = true;
        try
        {
            int failed = 0;
            foreach (RoleSettingsViewModel role in Roles)
            {
                if (await role.CheckAsync().ConfigureAwait(true) == ConnectionCheckOutcome.Failed)
                {
                    failed++;
                }
            }

            Status = failed == 0
                ? string.Create(CultureInfo.InvariantCulture, $"All {Roles.Count} role(s) answered.")
                : string.Create(CultureInfo.InvariantCulture, $"{failed} of {Roles.Count} role(s) did not work.");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void Save(bool restart)
    {
        ValidationFailures.Clear();

        foreach (string failure in CollectRoles())
        {
            ValidationFailures.Add(failure);
        }

        if (ValidationFailures.Count == 0)
        {
            foreach (string failure in Settings.Validate())
            {
                ValidationFailures.Add(failure);
            }
        }

        if (ValidationFailures.Count > 0)
        {
            Status = "Nothing was saved: these settings would stop the harness from starting.";
            return;
        }

        IsBusy = true;
        try
        {
            _store.Save(Settings);
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            Status = $"Could not write {_store.SettingsFilePath}: {ex.Message}";
            return;
        }
        finally
        {
            IsBusy = false;
        }

        if (restart)
        {
            _shell.Restart();
            return;
        }

        CloseRequested?.Invoke(this, true);
    }

    /// <summary>
    /// Writes everything to one file that another machine can read.
    /// <para>
    /// The passphrase is what makes the keys portable. DPAPI ciphertext is bound to this Windows
    /// account, so copying <c>secrets.json</c> anywhere produces keys that decrypt to nothing;
    /// re-encrypting them under something the operator knows is the only way they travel. Empty
    /// means the file is written without them, and there is no third option that writes one in the
    /// clear.
    /// </para>
    /// </summary>
    private void Export()
    {
        if (!Collect())
        {
            return;
        }

        string? path = _shell.PickFileToSave(
            "Export GlassCoder configuration",
            $"GlassCoder configuration|*{_transfer.FileExtension}|All files|*.*",
            "glasscoder" + _transfer.FileExtension,
            HasProjectRoot ? ProjectRoot : _store.DirectoryPath);

        if (path is null)
        {
            return;
        }

        int keys = Roles.Count(role => !string.IsNullOrWhiteSpace(role.Options.ApiKey));
        string? passphrase = _shell.PromptForPassphrase(
            "Protect the exported keys",
            keys > 0
                ? $"This configuration holds {keys} API key(s). A passphrase encrypts them so they can be read on " +
                  "another machine - the stored keys cannot travel on their own, because Windows ties them to this account."
                : "This configuration holds no API keys, so a passphrase changes nothing about what is written.",
            confirm: true);

        if (passphrase is null)
        {
            return;
        }

        IsBusy = true;
        try
        {
            int written = _transfer.Export(Settings, path, passphrase);
            Status = written > 0
                ? $"Exported to {path}, with {written} key(s) encrypted under the passphrase."
                : $"Exported to {path}. No keys were written, so it is safe to share.";
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            Status = $"Could not write {path}: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Loads an exported file into the dialog.
    /// <para>
    /// It populates rather than saves, so what arrives can be looked at before it replaces
    /// anything - and so the keys go back under DPAPI through the ordinary Save, rather than
    /// through a second path that would have to get the same thing right twice.
    /// </para>
    /// </summary>
    private void Import()
    {
        string? path = _shell.PickFileToOpen(
            "Import GlassCoder configuration",
            $"GlassCoder configuration|*{_transfer.FileExtension};*.json|All files|*.*",
            HasProjectRoot ? ProjectRoot : _store.DirectoryPath);

        if (path is null)
        {
            return;
        }

        string? passphrase = null;
        if (_transfer.ContainsKeys(path))
        {
            passphrase = _shell.PromptForPassphrase(
                "Unlock the imported keys",
                "This file's API keys are encrypted. Enter the passphrase they were exported with.",
                confirm: false);

            if (passphrase is null)
            {
                return;
            }
        }

        IsBusy = true;
        try
        {
            ImportedSettings imported = _transfer.Import(path, passphrase);
            Apply(imported.Settings);

            Status = Describe(imported, path);
        }
        catch (SettingsTransferException ex)
        {
            Status = ex.Message;
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            Status = $"Could not read {path}: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private static string Describe(ImportedSettings imported, string path)
    {
        string what = $"Imported {System.IO.Path.GetFileName(path)}.";

        if (imported.KeysWithheld > 0)
        {
            what += $" {imported.KeysRestored} key(s) came across and {imported.KeysWithheld} did not - " +
                    "enter those again.";
        }
        else if (imported.KeysRestored > 0)
        {
            what += $" {imported.KeysRestored} key(s) came across.";
        }

        return what + " The workspace root was left as it is. Nothing is saved until you press Save.";
    }

    /// <summary>
    /// Writes the project-shaped sections into the project, so they travel with it.
    /// <para>
    /// This is what makes a second project usable: the workspace paths, context files, reference
    /// directories and branch rules all name things inside one repository, and a machine-wide copy
    /// of them is right for exactly one of them.
    /// </para>
    /// </summary>
    private void SaveToProject()
    {
        if (!HasProjectRoot || !Collect())
        {
            return;
        }

        IsBusy = true;
        try
        {
            string path = _projectStore.Save(Settings, ProjectRoot);
            OnPropertyChanged(nameof(ProjectSummary));
            Status = $"Wrote {path}. Restart to put it in force; it will follow this project everywhere.";
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            Status = $"Could not write the project file: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Swaps in an imported configuration. The role list is rebuilt because it wraps the old
    /// options objects, and every binding is invalidated at once because the view binds paths
    /// through <see cref="Settings"/> rather than to properties declared here.
    /// </summary>
    private void Apply(GlassCoderSettings settings)
    {
        // The repository root does not come across. It is the one setting that is intrinsically
        // local - a path from the machine the file was exported on almost certainly names nothing
        // here - and importing it would silently re-point the agent at a folder that does not
        // exist. It stays whatever the workspace pane chose.
        settings.Workspace.RepoRoot = Settings.Workspace.RepoRoot;

        Settings = settings;
        BuildRoles();
        ValidationFailures.Clear();

        // An empty name is WPF's "every property changed", which is what an import is.
        OnPropertyChanged(string.Empty);
    }

    private void BuildRoles()
    {
        Roles.Clear();
        foreach ((string name, ModelRoleOptions options) in Settings.Models.Roles)
        {
            Roles.Add(new RoleSettingsViewModel(name, options, _probe, _store.ProtectionScheme));
        }

        SelectedRole = Roles.Count > 0 ? Roles[0] : null;
        BuildEndpoints();
    }

    /// <summary>
    /// Fills the endpoint picker from the saved list, or from the roles themselves when there is
    /// no saved list.
    /// <para>
    /// The seeding is what carries a configuration written before this list existed: every such
    /// file has roles with endpoints and no list, and a picker that opened empty would look like
    /// the endpoints had been lost. An emptied list refills the same way for the same reason -
    /// a blank picker is not a state worth preserving - so forgetting the last remaining endpoint
    /// lasts until the dialog is next opened.
    /// </para>
    /// </summary>
    private void BuildEndpoints()
    {
        Endpoints.Clear();
        foreach (string endpoint in Settings.Models.KnownEndpoints)
        {
            Remember(endpoint);
        }

        if (Endpoints.Count > 0)
        {
            return;
        }

        foreach (RoleSettingsViewModel role in Roles)
        {
            Remember(role.Endpoint);
        }
    }

    /// <summary>Adds an endpoint to the picker unless it is unusable or already there.</summary>
    private void Remember(string? endpoint)
    {
        string trimmed = endpoint?.Trim() ?? string.Empty;
        if (ModelsOptionsValidator.IsUsableEndpoint(trimmed) && !IsKnownEndpoint(trimmed))
        {
            Endpoints.Add(trimmed);
        }
    }

    private bool IsKnownEndpoint(string? endpoint) =>
        !string.IsNullOrWhiteSpace(endpoint) &&
        Endpoints.Contains(endpoint.Trim(), StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Remembers what the selected role is pointed at. The endpoint is checked against the rule
    /// the startup validator applies, so the picker cannot fill up with addresses that would fail
    /// validation the moment a role was pointed at one.
    /// </summary>
    private void AddEndpoint()
    {
        string endpoint = SelectedRole?.Endpoint?.Trim() ?? string.Empty;

        if (!ModelsOptionsValidator.IsUsableEndpoint(endpoint))
        {
            Status = $"'{endpoint}' was not added: an endpoint has to be an absolute http(s) URL.";
            return;
        }

        if (IsKnownEndpoint(endpoint))
        {
            return;
        }

        Endpoints.Add(endpoint);
        Status = $"{endpoint} is now offered to every role. Save to keep it.";
    }

    /// <summary>
    /// Forgets an endpoint. The role stays pointed at it - this list is what the picker offers,
    /// never what a role is served by - so removing the one in front of you costs nothing but the
    /// shortcut back to it.
    /// </summary>
    private void RemoveEndpoint()
    {
        string endpoint = SelectedRole?.Endpoint?.Trim() ?? string.Empty;
        int index = IndexOfEndpoint(endpoint);
        if (index < 0)
        {
            return;
        }

        Endpoints.RemoveAt(index);
        Status = $"{endpoint} is no longer offered. Roles already on it are unchanged.";
    }

    private int IndexOfEndpoint(string endpoint)
    {
        for (int index = 0; index < Endpoints.Count; index++)
        {
            if (string.Equals(Endpoints[index], endpoint, StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }
        }

        return -1;
    }

    /// <summary>
    /// Folds the edited role names back into the settings, reporting any that would collide.
    /// Everything that writes a file does this first, so no file is ever written from a role list
    /// the dialog has not agreed with.
    /// </summary>
    private bool Collect()
    {
        ValidationFailures.Clear();
        foreach (string failure in CollectRoles())
        {
            ValidationFailures.Add(failure);
        }

        if (ValidationFailures.Count == 0)
        {
            return true;
        }

        Status = "Nothing was written: the roles have to be sorted out first.";
        return false;
    }

    /// <summary>
    /// Writes the edited role names back as the dictionary keys the harness addresses, and
    /// reports the two ways that can go wrong before anything is overwritten.
    /// </summary>
    private List<string> CollectRoles()
    {
        // The picker is the edited copy; the settings own the list that gets written. Folded back
        // here because this is the step every writer shares - Save reaches it directly and the
        // export and project paths reach it through Collect - and folding it in one of those
        // instead would have saved a curated list on some buttons and dropped it on others. It is
        // deliberately not folded on each add and remove, so cancelling the dialog still leaves
        // the saved list exactly as it was found: the bargain every other field here makes.
        Settings.Models.KnownEndpoints.Clear();
        foreach (string endpoint in Endpoints)
        {
            Settings.Models.KnownEndpoints.Add(endpoint);
        }

        List<string> failures = [];
        Dictionary<string, ModelRoleOptions> rebuilt = new(StringComparer.OrdinalIgnoreCase);

        foreach (RoleSettingsViewModel role in Roles)
        {
            string name = role.Name?.Trim() ?? string.Empty;
            if (name.Length == 0)
            {
                failures.Add("Every role needs a name - it is the alias the harness addresses it by.");
            }
            else if (!rebuilt.TryAdd(name, role.Options))
            {
                failures.Add($"Role '{name}' is listed more than once.");
            }
        }

        if (failures.Count > 0)
        {
            return failures;
        }

        Settings.Models.Roles.Clear();
        foreach ((string name, ModelRoleOptions options) in rebuilt)
        {
            Settings.Models.Roles[name] = options;
        }

        return failures;
    }

    private void AddRole()
    {
        string name = "role";
        for (int suffix = 2; Roles.Any(role => string.Equals(role.Name, name, StringComparison.OrdinalIgnoreCase)); suffix++)
        {
            name = string.Create(CultureInfo.InvariantCulture, $"role{suffix}");
        }

        ModelRoleOptions options = new()
        {
            // The role in front of the operator, then whatever the picker offers first: a new
            // role is nearly always another alias on a server that is already configured.
            Endpoint = SelectedRole?.Options.Endpoint
                ?? Endpoints.FirstOrDefault()
                ?? "http://localhost:8001/v1",
            ModelAlias = name,
        };

        RoleSettingsViewModel role = new(name, options, _probe, _store.ProtectionScheme);
        Roles.Add(role);
        SelectedRole = role;
        OnPropertyChanged(nameof(RoleNames));
    }

    private void RemoveRole()
    {
        if (SelectedRole is null || Roles.Count <= 1)
        {
            return;
        }

        Roles.Remove(SelectedRole);
        SelectedRole = Roles[0];
        OnPropertyChanged(nameof(RoleNames));
    }

    private void Reset()
    {
        try
        {
            _store.Clear();
            Status = "Saved settings removed. Restart to fall back to appsettings.json.";
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            Status = $"Could not remove the saved settings: {ex.Message}";
        }
    }

    private static string Join(IList<string> values) => string.Join(Environment.NewLine, values);

    private static void Replace(IList<string> values, string? text)
    {
        values.Clear();
        foreach (string line in (text ?? string.Empty).Split('\n'))
        {
            string trimmed = line.Trim();
            if (trimmed.Length > 0)
            {
                values.Add(trimmed);
            }
        }
    }
}
