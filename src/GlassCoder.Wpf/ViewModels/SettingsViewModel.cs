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
    private readonly IModelConnectionProbe _probe;
    private readonly IDesktopShell _shell;
    private RoleSettingsViewModel? _selectedRole;
    private string _status;
    private bool _isBusy;

    /// <summary>Creates the view model over the configuration the harness is running on.</summary>
    public SettingsViewModel(
        IConfiguration configuration,
        IUserSettingsStore store,
        IModelConnectionProbe probe,
        IDesktopShell shell)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(store);

        _store = store;
        _probe = probe;
        _shell = shell;

        Settings = GlassCoderSettings.ReadFrom(configuration);

        foreach ((string name, ModelRoleOptions options) in Settings.Models.Roles)
        {
            Roles.Add(new RoleSettingsViewModel(name, options, probe, store.ProtectionScheme));
        }

        _selectedRole = Roles.Count > 0 ? Roles[0] : null;

        int undecryptable = _store.LoadSecrets().Count(entry => entry.Value is null);
        _status = undecryptable > 0
            ? $"{undecryptable} stored key(s) could not be decrypted on this machine. Enter them again."
            : "Loaded the effective configuration.";

        SaveCommand = new RelayCommand(() => Save(restart: false), () => !IsBusy);
        SaveAndRestartCommand = new RelayCommand(() => Save(restart: true), () => !IsBusy);
        AddRoleCommand = new RelayCommand(AddRole);
        RemoveRoleCommand = new RelayCommand(RemoveRole, () => SelectedRole is not null && Roles.Count > 1);
        TestAllCommand = new RelayCommand(async () => await TestAllAsync().ConfigureAwait(true), () => !IsBusy);
        OpenFolderCommand = new RelayCommand(() => _shell.OpenFolder(_store.DirectoryPath));
        ResetCommand = new RelayCommand(Reset, () => _store.Exists);
    }

    /// <summary>Raised when the dialog should close. The argument is whether anything was saved.</summary>
    public event EventHandler<bool>? CloseRequested;

    /// <summary>Every configurable section, bound directly by the view.</summary>
    public GlassCoderSettings Settings { get; }

    /// <summary>The served roles.</summary>
    public ObservableCollection<RoleSettingsViewModel> Roles { get; } = [];

    /// <summary>Everything that would stop the harness from starting on these settings.</summary>
    public ObservableCollection<string> ValidationFailures { get; } = [];

    /// <summary>Role names, for the pickers that have to name one.</summary>
    public IReadOnlyList<string> RoleNames => [.. Roles.Select(role => role.Name)];

    /// <summary>Where commands may run.</summary>
    public IReadOnlyList<SandboxMode> SandboxModes { get; } = [SandboxMode.Docker, SandboxMode.Local];

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

    /// <summary>Validates and saves.</summary>
    public RelayCommand SaveCommand { get; }

    /// <summary>Saves, then restarts so the new settings are the ones in force.</summary>
    public RelayCommand SaveAndRestartCommand { get; }

    /// <summary>Adds a served role.</summary>
    public RelayCommand AddRoleCommand { get; }

    /// <summary>Removes the selected role.</summary>
    public RelayCommand RemoveRoleCommand { get; }

    /// <summary>Checks every role against its server.</summary>
    public RelayCommand TestAllCommand { get; }

    /// <summary>Opens the folder the settings live in.</summary>
    public RelayCommand OpenFolderCommand { get; }

    /// <summary>Deletes the saved settings, falling back to <c>appsettings.json</c>.</summary>
    public RelayCommand ResetCommand { get; }

    /// <summary>Checks every role in turn, and reports how many worked.</summary>
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
    /// Writes the edited role names back as the dictionary keys the harness addresses, and
    /// reports the two ways that can go wrong before anything is overwritten.
    /// </summary>
    private List<string> CollectRoles()
    {
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
            Endpoint = SelectedRole?.Options.Endpoint ?? "http://localhost:8001/v1",
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
