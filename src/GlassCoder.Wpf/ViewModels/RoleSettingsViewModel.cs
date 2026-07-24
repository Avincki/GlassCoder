using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using GlassCoder.Models;
using GlassCoder.Models.Configuration;
using GlassCoder.Wpf.Mvvm;

namespace GlassCoder.Wpf.ViewModels;

/// <summary>
/// One served role in the settings dialog: its endpoint, its alias, its key, and the button that
/// finds out whether the three of them actually work together (CLAUDE.md §6).
/// </summary>
/// <remarks>
/// The view model edits the real <see cref="ModelRoleOptions"/> instance in place, which is what
/// lets the check test what is on screen rather than what the harness started with. The one
/// value it wraps is the API key, because a key needs masking and a plain two-way binding to a
/// text box would put it on screen - and in a screenshot - by default.
/// </remarks>
public sealed class RoleSettingsViewModel : ViewModelBase
{
    private readonly IModelConnectionProbe _probe;
    private readonly string _protectionScheme;
    private string _name;
    private bool _isKeyVisible;
    private bool _isChecking;
    private ConnectionCheckOutcome? _outcome;
    private string _checkSummary = "Not checked yet.";

    /// <summary>Creates the view model over a role's live settings.</summary>
    /// <param name="name">Role name - the key it is addressed by.</param>
    /// <param name="options">The settings this role will run on.</param>
    /// <param name="probe">Runs the connection check.</param>
    /// <param name="protectionScheme">How a saved key is protected at rest, for display.</param>
    public RoleSettingsViewModel(
        string name,
        ModelRoleOptions options,
        IModelConnectionProbe probe,
        string protectionScheme)
    {
        ArgumentNullException.ThrowIfNull(options);

        _name = name;
        _probe = probe;
        _protectionScheme = protectionScheme;
        Options = options;

        // A key already on file stays masked; an empty one starts visible, because the next
        // thing that happens to it is a paste.
        _isKeyVisible = string.IsNullOrEmpty(options.ApiKey);

        TestCommand = new RelayCommand(async () => await CheckAsync().ConfigureAwait(true), () => !IsChecking);
        ToggleKeyVisibilityCommand = new RelayCommand(() => IsKeyVisible = !IsKeyVisible);
        ClearKeyCommand = new RelayCommand(() => ApiKey = null, () => !string.IsNullOrEmpty(ApiKey));
    }

    /// <summary>The settings being edited, live.</summary>
    public ModelRoleOptions Options { get; }

    /// <summary>Steps of the last check, in order.</summary>
    public ObservableCollection<ConnectionCheckStep> Steps { get; } = [];

    /// <summary>Role name. Renaming here renames the key the harness addresses.</summary>
    public string Name
    {
        get => _name;
        set => SetProperty(ref _name, value);
    }

    /// <summary>The API key, or null when this role has none.</summary>
    public string? ApiKey
    {
        get => Options.ApiKey;
        set
        {
            string? trimmed = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            if (string.Equals(Options.ApiKey, trimmed, StringComparison.Ordinal))
            {
                return;
            }

            Options.ApiKey = trimmed;
            OnPropertyChanged();
            OnPropertyChanged(nameof(MaskedApiKey));
            OnPropertyChanged(nameof(KeyStatus));
        }
    }

    /// <summary>The key as it is shown while hidden: enough to recognise, not enough to use.</summary>
    public string MaskedApiKey
    {
        get
        {
            string? key = Options.ApiKey;
            if (string.IsNullOrEmpty(key))
            {
                return string.Empty;
            }

            return key.Length <= 8
                ? new string('•', key.Length)
                : string.Concat(key.AsSpan(0, 3), new string('•', Math.Min(16, key.Length - 7)), key.AsSpan(key.Length - 4));
        }
    }

    /// <summary>Where this role's key comes from and what protects it.</summary>
    public string KeyStatus
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(Options.ApiKeyEnvironmentVariable))
            {
                string? fromEnvironment = Environment.GetEnvironmentVariable(Options.ApiKeyEnvironmentVariable);
                if (!string.IsNullOrWhiteSpace(fromEnvironment))
                {
                    return $"The environment variable {Options.ApiKeyEnvironmentVariable} currently supplies this key " +
                           "and takes precedence over anything stored here.";
                }

                return $"The environment variable {Options.ApiKeyEnvironmentVariable} is named but not set.";
            }

            return string.IsNullOrEmpty(Options.ApiKey)
                ? "No key. Local servers ignore it; hosted endpoints will refuse the call."
                : $"Saved to secrets.json, protected with {_protectionScheme}. It is never written to settings.json.";
        }
    }

    /// <summary>Whether the key is shown in the clear.</summary>
    public bool IsKeyVisible
    {
        get => _isKeyVisible;
        set => SetProperty(ref _isKeyVisible, value);
    }

    /// <summary>Whether a check is running.</summary>
    public bool IsChecking
    {
        get => _isChecking;
        private set => SetProperty(ref _isChecking, value);
    }

    /// <summary>How the last check went, or null when there has not been one.</summary>
    public ConnectionCheckOutcome? Outcome
    {
        get => _outcome;
        private set => SetProperty(ref _outcome, value);
    }

    /// <summary>One line describing the last check.</summary>
    public string CheckSummary
    {
        get => _checkSummary;
        private set => SetProperty(ref _checkSummary, value);
    }

    /// <summary>Extra request properties, as <c>name=value</c> lines.</summary>
    public string AdditionalRequestProperties
    {
        get => JoinPairs(Options.AdditionalRequestProperties);
        set
        {
            ReplacePairs(Options.AdditionalRequestProperties, value);
            OnPropertyChanged();
        }
    }

    /// <summary>Extra constrained-decoding properties, as <c>name=value</c> lines.</summary>
    public string DecodingRequestProperties
    {
        get => JoinPairs(Options.ConstrainedDecoding.AdditionalRequestProperties);
        set
        {
            ReplacePairs(Options.ConstrainedDecoding.AdditionalRequestProperties, value);
            OnPropertyChanged();
        }
    }

    /// <summary>Checks this role's settings against the server.</summary>
    public RelayCommand TestCommand { get; }

    /// <summary>Shows or hides the key.</summary>
    public RelayCommand ToggleKeyVisibilityCommand { get; }

    /// <summary>Forgets the key.</summary>
    public RelayCommand ClearKeyCommand { get; }

    /// <summary>
    /// The role name. This is what the list reports as its item name to accessibility tools and
    /// UI automation, which otherwise get the view model's type name.
    /// </summary>
    public override string ToString() => Name;

    /// <summary>Runs the connection check and records what it found.</summary>
    public async Task<ConnectionCheckOutcome> CheckAsync(CancellationToken cancellationToken = default)
    {
        IsChecking = true;
        CheckSummary = "Checking…";
        Outcome = null;
        Steps.Clear();

        try
        {
            ConnectionCheckResult result = await _probe
                .CheckAsync(Name, Options, cancellationToken)
                .ConfigureAwait(true);

            foreach (ConnectionCheckStep step in result.Steps)
            {
                Steps.Add(step);
            }

            Outcome = result.Outcome;
            CheckSummary = result.Summary;
            return result.Outcome;
        }
        catch (OperationCanceledException)
        {
            Outcome = null;
            CheckSummary = "Check cancelled.";
            return ConnectionCheckOutcome.Warning;
        }
        catch (Exception ex)
        {
            // A check that throws is a check that failed; the dialog says so rather than
            // taking the application down with it.
            Outcome = ConnectionCheckOutcome.Failed;
            CheckSummary = $"The check itself failed: {ex.Message}";
            return ConnectionCheckOutcome.Failed;
        }
        finally
        {
            IsChecking = false;
        }
    }

    private static string JoinPairs(IDictionary<string, string> values)
    {
        List<string> lines = [];
        foreach ((string key, string value) in values)
        {
            lines.Add($"{key}={value}");
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static void ReplacePairs(IDictionary<string, string> target, string? text)
    {
        target.Clear();
        foreach (string line in (text ?? string.Empty).Split('\n'))
        {
            string trimmed = line.Trim();
            int separator = trimmed.IndexOf('=', StringComparison.Ordinal);
            if (trimmed.Length == 0 || separator <= 0)
            {
                continue;
            }

            target[trimmed[..separator].Trim()] = trimmed[(separator + 1)..].Trim();
        }
    }
}
