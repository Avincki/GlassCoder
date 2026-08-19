using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using GlassCoder.Core.Agent;
using GlassCoder.Core.Verification;
using GlassCoder.Models;
using GlassCoder.Models.Configuration;
using GlassCoder.Tools.Registry;
using GlassCoder.Wpf.Mvvm;
using GlassCoder.Wpf.Services;
using Microsoft.Extensions.Options;

namespace GlassCoder.Wpf.ViewModels;

/// <summary>
/// The shell (workplan task 25): navigation between the three first-class surfaces, and the one
/// control that starts a run.
/// </summary>
/// <remarks>
/// The three surfaces are not an arbitrary choice of screens - they are the three first-class
/// requirements from CLAUDE.md made visible: the transcript (§9), the changes (§10) and the
/// metrics (§11).
/// </remarks>
public sealed class MainWindowViewModel : ViewModelBase, IDisposable
{
    /// <summary>
    /// Ceiling on one header lookup, whatever the role's own timeout is. Four seconds because
    /// this runs while somebody is waiting for a window: a closed port answers instantly, and
    /// anything slower than this is a server that will not be ready for the run either.
    /// </summary>
    private static readonly TimeSpan ModelQueryTimeout = TimeSpan.FromSeconds(4);

    private readonly IAgentLoop _loop;
    private readonly ISettingsDialog _settings;
    private readonly IAboutDialog _about;
    private readonly ICriticPanel _critics;
    private readonly IRunReviewer _reviewer;
    private readonly CritiqueOptions _critique;
    private readonly IServedModelDirectory _directory;
    private readonly ModelsOptions _models;
    private readonly AgentOptions _agent;
    private readonly IUiStateStore? _uiState;
    private bool _describingModels;
    private string _modelsCheckedAt = string.Empty;
    private object? _currentView;
    private string _selectedSurface = "Transcript";
    private string _goal = string.Empty;
    private string? _selectedRecentGoal;
    private string _status = "Ready.";
    private bool _isRunning;
    private bool _useRemoteCritic;
    private RunReview? _review;
    private string? _reviewedGoal;
    private int _reviewedAttempt;
    private CancellationTokenSource? _cancellation;
    private readonly Dispatcher _dispatcher;
    private string? _limitPrompt;
    private TaskCompletionSource<bool>? _limitDecision;

    /// <summary>Creates the shell.</summary>
    public MainWindowViewModel(
        IAgentLoop loop,
        TranscriptViewModel transcript,
        ChangesViewModel changes,
        MetricsViewModel metrics,
        RetrospectiveViewModel retrospective,
        WorkspaceViewModel workspace,
        ISettingsDialog settings,
        IAboutDialog about,
        ICriticPanel critics,
        IRunReviewer reviewer,
        IOptions<CritiqueOptions> critique,
        IServedModelDirectory directory,
        IOptions<ModelsOptions> models,
        IOptions<AgentOptions> agent,
        IUiStateStore? uiState = null,
        LimitExtensionGate? limitGate = null)
    {
        ArgumentNullException.ThrowIfNull(critique);
        ArgumentNullException.ThrowIfNull(models);
        ArgumentNullException.ThrowIfNull(agent);

        _loop = loop;
        _settings = settings;
        _about = about;
        _critics = critics;
        _reviewer = reviewer;
        _critique = critique.Value;
        _directory = directory;
        _models = models.Value;
        _agent = agent.Value;
        _uiState = uiState;
        _dispatcher = Dispatcher.CurrentDispatcher;

        // The loop pauses on this question from a background thread; the banner answers it
        // from the UI thread. Assigned here because the shell is what owns a surface to ask on.
        if (limitGate is not null)
        {
            limitGate.Handler = OnLimitReachedAsync;
        }

        Transcript = transcript;
        Changes = changes;
        Metrics = metrics;
        Retrospective = retrospective;
        Workspace = workspace;
        _currentView = transcript;

        // The tree can see the retrospectives it wrote; the surface can read one. Only the shell
        // holds both, so this is where a double-click in one becomes a report in the other.
        Workspace.RetrospectiveOpened += OnRetrospectiveOpened;

        // The goals of recent runs, newest first, for the picker - and the newest of them in the
        // box, so a repeated test run is a press of Run rather than a paste. Still just a
        // pre-fill: the box is editable and empty on a first-ever start.
        ReadRecentGoals();
        _goal = RecentGoals.Count > 0 ? RecentGoals[0] : string.Empty;

        RunCommand = new RelayCommand(async () => await RunAsync().ConfigureAwait(true), () => !IsRunning);
        CancelCommand = new RelayCommand(() => _cancellation?.Cancel(), () => IsRunning);
        SettingsCommand = new RelayCommand(OpenSettings, () => !IsRunning);
        AboutCommand = new RelayCommand(() => _about.Show());
        RetryCommand = new RelayCommand(async () => await RetryAsync().ConfigureAwait(true), () => CanRetry);
        DismissReviewCommand = new RelayCommand(() => Review = null, () => Review is not null);
        ExtendLimitCommand = new RelayCommand(() => ResolveLimit(true), () => HasLimitPrompt);
        StopAtLimitCommand = new RelayCommand(() => ResolveLimit(false), () => HasLimitPrompt);
        RecheckModelsCommand = new RelayCommand(
            async () => await DescribeModelsAsync().ConfigureAwait(true),
            () => !_describingModels);

        // Status opens at "Ready." and nothing more. It used to open with the whole tool list,
        // which wrapped to two lines of a one-line surface and was gone the moment anything
        // happened - a startup banner squatting on the space reserved for run state. The
        // inventory lives in About now, where it can say what each tool is for and which ones
        // this configuration switched off (workplan task 64).
    }

    /// <summary>The live transcript surface.</summary>
    public TranscriptViewModel Transcript { get; }

    /// <summary>The change-visibility surface.</summary>
    public ChangesViewModel Changes { get; }

    /// <summary>The metrics and ablation surface.</summary>
    public MetricsViewModel Metrics { get; }

    /// <summary>The workspace pane on the right of the shell (workplan task 39).</summary>
    public WorkspaceViewModel Workspace { get; }

    /// <summary>The look back at a finished run (workplan task 67).</summary>
    public RetrospectiveViewModel Retrospective { get; }

    /// <summary>Names of the surfaces, for the navigation list.</summary>
    public IReadOnlyList<string> Surfaces { get; } = ["Transcript", "Changes", "Metrics", "Retrospective"];

    /// <summary>Which surface is selected.</summary>
    public string SelectedSurface
    {
        get => _selectedSurface;
        set
        {
            if (SetProperty(ref _selectedSurface, value))
            {
                CurrentView = value switch
                {
                    "Changes" => Changes,
                    "Metrics" => Metrics,
                    "Retrospective" => Retrospective,
                    _ => Transcript,
                };

                if (value == "Metrics")
                {
                    Metrics.Reload();
                }
            }
        }
    }

    /// <summary>The view model bound to the content area.</summary>
    public object? CurrentView
    {
        get => _currentView;
        private set => SetProperty(ref _currentView, value);
    }

    /// <summary>The goal to run.</summary>
    public string Goal
    {
        get => _goal;
        set => SetProperty(ref _goal, value);
    }

    /// <summary>
    /// The goals of recent runs, newest first, as offered by the picker above the goal box.
    /// </summary>
    public ObservableCollection<string> RecentGoals { get; } = [];

    /// <summary>Whether there is any history to pick from - false on a first-ever start.</summary>
    public bool HasRecentGoals => RecentGoals.Count > 0;

    /// <summary>
    /// The goal picked from the history, which becomes the contents of the goal box.
    /// <para>
    /// Picking replaces the box, it never merges with it: the box is cleared and then filled, so
    /// a half-typed goal cannot end up spliced onto a remembered one. The picker then returns to
    /// no selection, which is what lets the same entry be picked twice - and is honest besides,
    /// because the moment the box is edited the picker no longer describes what will run.
    /// </para>
    /// </summary>
    public string? SelectedRecentGoal
    {
        get => _selectedRecentGoal;
        set
        {
            if (!SetProperty(ref _selectedRecentGoal, value) || string.IsNullOrEmpty(value))
            {
                return;
            }

            Goal = string.Empty;
            Goal = value;

            // Posted, not assigned. A source change raised inside a binding's own target-to-source
            // push is ignored by the binding engine, so clearing the field here would leave the
            // combo box still showing a selection this property no longer holds - and a control
            // whose selection never changes raises nothing when the same row is picked again, so
            // the second pick would silently do nothing. Posting lets the transfer finish first.
            _dispatcher.BeginInvoke(() =>
            {
                _selectedRecentGoal = null;
                OnPropertyChanged(nameof(SelectedRecentGoal));
            });
        }
    }

    /// <summary>What the shell is doing.</summary>
    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    /// <summary>Whether a run is in flight.</summary>
    public bool IsRunning
    {
        get => _isRunning;
        private set
        {
            if (SetProperty(ref _isRunning, value))
            {
                OnPropertyChanged(nameof(CanRetry));

                // The manual git buttons stand down mid-run: committing a tree the agent has not
                // finished changing would record work in progress as if it were finished. Clean
                // stands down for the twin reason - emptying folders mid-run would pull the
                // workspace out from under the agent. The retrospective stands down because it
                // judges a finished run, and the run it holds is not the one in flight.
                Changes.IsAgentRunning = value;
                Workspace.IsAgentRunning = value;
                Retrospective.IsAgentRunning = value;
            }
        }
    }

    /// <summary>
    /// Whether this run is judged by the second-opinion critic rather than the local one.
    /// <para>
    /// Read once, when Run is pressed. A critic that could be swapped mid-run would make the run
    /// two arms and its metrics unattributable, so the choice belongs to the request.
    /// </para>
    /// </summary>
    public bool UseRemoteCritic
    {
        get => _useRemoteCritic;
        set
        {
            if (SetProperty(ref _useRemoteCritic, value))
            {
                // Ticking the box changes which models the run will address, so it changes what
                // the header band is describing. This is also the only moment a hosted critic is
                // asked anything: querying it at startup would send the key to a vendor on every
                // launch, whether or not the second opinion was ever wanted.
                _ = DescribeModelsAsync();
            }
        }
    }

    /// <summary>
    /// What this run will actually talk to: one row per role in the roster, with whatever the
    /// server said is behind each alias (workplan task 77).
    /// <para>
    /// The roster is the roles the run will address, not every role in the configuration - the
    /// agent's, and the critic's when critique is on. A settings mirror would be a second, lossier
    /// copy of a dialog that already exists; this answers the narrower question the header is the
    /// right place for, which is what happens when Run is pressed.
    /// </para>
    /// </summary>
    public ObservableCollection<ModelIdentityViewModel> Models { get; } = [];

    /// <summary>
    /// When the band was last filled in. Shown rather than implied, because the answer is a
    /// snapshot: a server restarted onto different weights leaves the band asserting the old ones
    /// until it is asked again.
    /// </summary>
    public string ModelsCheckedAt
    {
        get => _modelsCheckedAt;
        private set => SetProperty(ref _modelsCheckedAt, value);
    }

    /// <summary>Asks the roster again. The answer is a snapshot, so refreshing it is a button.</summary>
    public RelayCommand RecheckModelsCommand { get; }

    /// <summary>Whether the second-opinion critic can be offered at all.</summary>
    public bool RemoteCriticAvailable =>
        !string.IsNullOrWhiteSpace(_critique.RemoteRole) && _critics.CanCritique(_critique.RemoteRole);

    /// <summary>
    /// Why the checkbox is on or off. A disabled control that does not say why is a bug report
    /// waiting to happen, and "no API key" and "critique is switched off" are different fixes.
    /// </summary>
    public string RemoteCriticTooltip =>
        string.IsNullOrWhiteSpace(_critique.RemoteRole)
            ? "No second-opinion critic is configured. Set Critique:RemoteRole to a served role."
            : RemoteCriticAvailable
                ? $"Judge this run with the '{_critique.RemoteRole}' critic instead of '{_critique.Role}'. " +
                  "Chosen before the run and recorded in the transcript."
                : $"The '{_critique.RemoteRole}' critic cannot be reached: critique is switched off, " +
                  "or the role is missing its API key. Both are fixed in Settings.";

    /// <summary>The second opinion on the last run, when there is one.</summary>
    public RunReview? Review
    {
        get => _review;
        private set
        {
            if (SetProperty(ref _review, value))
            {
                OnPropertyChanged(nameof(HasReview));
                OnPropertyChanged(nameof(ReviewHeadline));
                OnPropertyChanged(nameof(ReviewSummary));
                OnPropertyChanged(nameof(CanRetry));
            }
        }
    }

    /// <summary>Whether a review is on screen.</summary>
    public bool HasReview => Review is not null;

    /// <summary>The one-line verdict, with what the second opinion cost to get.</summary>
    public string ReviewHeadline
    {
        get
        {
            if (Review is not { } review)
            {
                return string.Empty;
            }

            // "The panel could not be reached" and "the panel had nothing to say" are different
            // facts, and only one of them is about the code.
            string verdict = review.Inconclusive ? "The critics could not be reached"
                : !review.Reviewed ? "Not reviewed"
                : review.Refuted ? "The critic refutes this run"
                : "The critic accepts this run";

            // A review that never called a model has no role, latency or cost to report.
            if (!review.Reviewed && !review.Inconclusive)
            {
                return verdict;
            }

            return string.Create(CultureInfo.InvariantCulture,
                $"{verdict} — {review.Role} · {review.DurationMs:F0} ms · ${review.EstimatedCostUsd:F4}");
        }
    }

    /// <summary>
    /// What the panel said - the tally line, then one line per critic. The votes were recorded
    /// from the start (workplan task 37) and never shown; the dissenting reason in a 2/3 verdict
    /// is precisely the line a human wants from a second opinion.
    /// </summary>
    public string ReviewSummary
    {
        get
        {
            if (Review is not { } review)
            {
                return string.Empty;
            }

            if (review.Critique is not { } critique || critique.Votes.Count == 0)
            {
                return review.Summary;
            }

            System.Text.StringBuilder text = new(review.Summary);
            foreach (CritiqueVerdict vote in critique.Votes)
            {
                string verdict = !vote.Available ? "unreachable"
                    : vote.Refuted ? $"REFUTED {vote.Confidence:F2}"
                    : $"accepted {vote.Confidence:F2}";

                text.AppendLine();
                text.Append(CultureInfo.InvariantCulture, $"[{vote.Lens ?? "critic"} · {verdict}] {vote.Reason}");
            }

            return text.ToString();
        }
    }

    /// <summary>
    /// Whether there is a refutation worth acting on. The button is the only thing that starts a
    /// retry: a critic that could re-run the agent itself would be choosing when the worker gets
    /// another attempt, and pass@1 measured over attempts a model granted is not pass@1.
    /// </summary>
    public bool CanRetry => !IsRunning && (Review?.SuggestsRetry ?? false);

    /// <summary>Starts a run.</summary>
    public RelayCommand RunCommand { get; }

    /// <summary>Cancels the run in flight.</summary>
    public RelayCommand CancelCommand { get; }

    /// <summary>Opens the settings dialog.</summary>
    public RelayCommand SettingsCommand { get; }

    /// <summary>Opens the About box. Available during a run - it changes nothing.</summary>
    public RelayCommand AboutCommand { get; }

    /// <summary>Runs the task again, carrying the critic's findings into the new attempt.</summary>
    public RelayCommand RetryCommand { get; }

    /// <summary>Clears the review from the screen without acting on it.</summary>
    public RelayCommand DismissReviewCommand { get; }

    /// <summary>
    /// The limit question the paused loop is waiting on, or null when there is none. The run
    /// makes no model calls while this is showing - the banner is the loop's next step.
    /// </summary>
    public string? LimitPrompt
    {
        get => _limitPrompt;
        private set
        {
            if (SetProperty(ref _limitPrompt, value))
            {
                OnPropertyChanged(nameof(HasLimitPrompt));
            }
        }
    }

    /// <summary>Whether the limit banner is on screen.</summary>
    public bool HasLimitPrompt => LimitPrompt is not null;

    /// <summary>Extends the tripped ceiling by one more allotment and resumes the run.</summary>
    public RelayCommand ExtendLimitCommand { get; }

    /// <summary>Declines the extension; the run stops with the ordinary limit outcome.</summary>
    public RelayCommand StopAtLimitCommand { get; }

    /// <summary>
    /// The loop's question, marshalled onto the UI thread as a banner. The returned task is
    /// what the paused loop awaits; cancelling the run answers it with "stop", so Cancel keeps
    /// working while the banner is up.
    /// </summary>
    private Task<bool> OnLimitReachedAsync(RunLimitReached limit, CancellationToken cancellationToken)
    {
        TaskCompletionSource<bool> decision = new(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationTokenRegistration registration = cancellationToken.Register(() =>
        {
            decision.TrySetResult(false);
            _dispatcher.BeginInvoke(() => LimitPrompt = null);
        });
        _ = decision.Task.ContinueWith(_ => registration.Dispose(), TaskScheduler.Default);

        _dispatcher.BeginInvoke(() =>
        {
            _limitDecision = decision;
            string unit = limit.Reason == AgentStopReason.StepLimit ? "steps" : "tokens";
            LimitPrompt = string.Create(CultureInfo.InvariantCulture,
                $"{(limit.Reason == AgentStopReason.StepLimit ? "Step" : "Token")} limit reached: " +
                $"{limit.Used:N0} of {limit.Ceiling:N0} {unit}. Extend by another {limit.Allotment:N0} and continue?");
        });

        return decision.Task;
    }

    private void ResolveLimit(bool extend)
    {
        _limitDecision?.TrySetResult(extend);
        _limitDecision = null;
        LimitPrompt = null;
        if (extend)
        {
            Status = "Limit extended - the run continues.";
        }
    }

    /// <summary>
    /// Fills the header band in: one directory lookup per role in the roster, in parallel.
    /// <para>
    /// Deliberately not <see cref="IModelConnectionProbe"/>. That check ends in a real completion,
    /// which is right for a button somebody pressed and wrong for anything that runs unasked - it
    /// would write a prompt into the server's logs and metrics on every launch, and on a cold
    /// server it would take most of a minute to do it.
    /// </para>
    /// </summary>
    /// <remarks>
    /// Nothing in here may throw. A model server that is not running is the ordinary state at
    /// startup, and a band that says "not available" is this working rather than failing.
    /// </remarks>
    public async Task DescribeModelsAsync(CancellationToken cancellationToken = default)
    {
        if (_describingModels)
        {
            return;
        }

        _describingModels = true;

        try
        {
            List<(ModelIdentityViewModel Row, ModelRoleOptions Settings)> rows = [];

            // Rebuilt rather than patched: the roster itself changes when the second opinion is
            // ticked, so the rows are a function of it and not a list to keep in sync with it.
            Models.Clear();
            ModelsCheckedAt = string.Empty;

            foreach ((string role, ModelRoleOptions settings) in Roster())
            {
                ModelIdentityViewModel row = new(role, settings.ModelAlias);
                Models.Add(row);
                rows.Add((row, settings));
            }

            await Task.WhenAll(rows.Select(entry => DescribeRoleAsync(entry.Row, entry.Settings, cancellationToken)))
                .ConfigureAwait(true);

            ModelsCheckedAt = $"checked {DateTimeOffset.Now.ToString("HH:mm", CultureInfo.CurrentCulture)}";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The band is a readout, not a gate. Whatever went wrong here, the window opens.
            ModelsCheckedAt = "could not be checked";
        }
        finally
        {
            _describingModels = false;
        }
    }

    /// <summary>Cancels and releases the run in flight, if any.</summary>
    public void Dispose()
    {
        Workspace.RetrospectiveOpened -= OnRetrospectiveOpened;

        // The retrospective watches the transcript bus for finished runs, which outlives it.
        Retrospective.Dispose();

        _cancellation?.Cancel();
        _cancellation?.Dispose();
        _cancellation = null;
    }

    /// <summary>
    /// Shows a retrospective the operator double-clicked in the tree.
    /// <para>
    /// The surface is brought forward either way, including when the folder turns out to hold
    /// nothing readable. A double-click is a question, and the answer - reports, or a line saying
    /// there are none - is on that surface; leaving the operator where they were would answer it
    /// somewhere they are not looking.
    /// </para>
    /// </summary>
    private void OnRetrospectiveOpened(object? sender, string directory)
    {
        Retrospective.ShowSaved(directory);
        SelectedSurface = "Retrospective";
    }

    /// <summary>
    /// Opens the dialog, and says plainly what a save did and did not do: every section is bound
    /// once at startup through <c>IOptions&lt;T&gt;</c>, so the run in this process keeps the
    /// settings it started with.
    /// </summary>
    private void OpenSettings()
    {
        if (_settings.Show())
        {
            Status = "Settings saved. Restart GlassCoder for them to take effect.";
        }
    }

    /// <summary>
    /// The roles this run will address, in the order they act: the agent's, the critic's when
    /// critique is on, and the second opinion's only once it has been asked for.
    /// <para>
    /// Roles the configuration does not define are skipped rather than reported. The harness
    /// already refuses to start on an agent or critique role that is missing
    /// (<c>GlassCoderSettings.Validate</c>), so a gap here would be a row that can only ever say
    /// the same thing startup already said louder.
    /// </para>
    /// </summary>
    private List<(string Role, ModelRoleOptions Settings)> Roster()
    {
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        List<(string, ModelRoleOptions)> roster = [];

        void Add(string? role)
        {
            if (!string.IsNullOrWhiteSpace(role) &&
                seen.Add(role) &&
                _models.Roles.TryGetValue(role, out ModelRoleOptions? settings))
            {
                roster.Add((role, settings));
            }
        }

        Add(_agent.Role);

        if (_critique.Enabled)
        {
            Add(_critique.Role);
        }

        if (UseRemoteCritic)
        {
            Add(_critique.RemoteRole);
        }

        return roster;
    }

    /// <summary>Asks one role's endpoint what it serves, and hands the row the answer.</summary>
    private async Task DescribeRoleAsync(
        ModelIdentityViewModel row,
        ModelRoleOptions settings,
        CancellationToken cancellationToken)
    {
        // A role that declares it needs a key and has none would fail on the wire in a way that
        // reads as "the server is down". Said plainly instead, without the call.
        if (!settings.IsUsable)
        {
            row.Unusable();
            return;
        }

        row.Describe(
            settings,
            await _directory.ListAsync(settings, ModelQueryTimeout, cancellationToken).ConfigureAwait(true));
    }

    /// <summary>
    /// Re-reads the picker's list from the store. The store owns the order and the cap, so the
    /// list is replaced wholesale rather than nudged here - two places deciding what "most
    /// recent" means is how a restart starts disagreeing with the session that preceded it.
    /// </summary>
    private void ReadRecentGoals()
    {
        RecentGoals.Clear();
        foreach (string goal in _uiState?.RecentGoals ?? [])
        {
            RecentGoals.Add(goal);
        }

        OnPropertyChanged(nameof(HasRecentGoals));
    }

    private Task RunAsync() => RunAsync(Goal, attempt: 1);

    /// <summary>
    /// Runs the task again with the critic's findings appended to the goal.
    /// <para>
    /// This is a new run against the same task, not a continuation of the last one - a fresh run
    /// id, and an attempt number the metrics store can filter on. That is what keeps pass@1
    /// meaning "solved on the first attempt" once retries exist (CLAUDE.md §11).
    /// </para>
    /// </summary>
    private Task RetryAsync()
    {
        if (Review is not { } review || _reviewedGoal is null)
        {
            return Task.CompletedTask;
        }

        // Put the composed goal in the box before it runs, so the box shows what actually runs
        // rather than the pre-retry text - with a five-line box that difference is now visible.
        // It stays there, editable, after the run.
        string composed = RunReviewer.ComposeRetryGoal(_reviewedGoal, review);
        Goal = composed;
        return RunAsync(composed, _reviewedAttempt + 1);
    }

    private async Task RunAsync(string goal, int attempt)
    {
        if (string.IsNullOrWhiteSpace(goal) || IsRunning)
        {
            return;
        }

        // Saved at the moment it becomes what runs - not on every keystroke, and before the run
        // rather than after it, so a crash mid-run still leaves the goal for the next start. What
        // is remembered is what ran, which is why a retry's composed goal is remembered too.
        if (_uiState is not null)
        {
            _uiState.RememberGoal(goal);
            ReadRecentGoals();
        }

        IsRunning = true;
        Review = null;
        Status = attempt > 1 ? $"Running attempt {attempt}…" : "Running…";
        SelectedSurface = "Transcript";

        // The tree's green says "this run touched it", so the last run's green comes off before
        // this one writes anything. A retry is a new run for the same reason it gets a new run
        // id: what attempt two changed is not what attempt one changed.
        Workspace.BeginRun();

        // Read once, here: from this point the run is one arm, whatever the checkbox does next.
        string? criticRole = UseRemoteCritic ? _critique.RemoteRole : null;

        _cancellation = new CancellationTokenSource();
        try
        {
            AgentRunResult result = await _loop.RunAsync(
                new AgentRunRequest
                {
                    TaskId = "desktop",
                    Goal = goal,
                    CriticRole = criticRole,
                    Attempt = attempt,
                },
                _cancellation.Token).ConfigureAwait(true);

            string headline = string.Create(CultureInfo.InvariantCulture,
                $"{result.StopReason} after {result.Steps} steps · {result.TotalTokens} tokens · " +
                $"tool-call validity {result.ToolCallValidityRate:P0}");

            // A stop reason on its own is a category, not an explanation: "ModelError" and
            // "Stalled" both leave the reader to go and find the log. When the run knows why it
            // stopped, the status bar says why - it wraps, so there is room for the sentence.
            Status = result.Error is null ? headline : $"{headline}{Environment.NewLine}{result.Error}";

            await ReviewAsync(result, goal, attempt).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            Status = "Cancelled.";
        }
        catch (Exception ex)
        {
            Status = $"Failed: {ex.Message}";
        }
        finally
        {
            _cancellation?.Dispose();
            _cancellation = null;
            IsRunning = false;
        }
    }

    /// <summary>
    /// Asks the critic what it makes of the finished run. A review that fails is reported and
    /// nothing more: the run already happened, and a second opinion that could not be obtained
    /// must not read as a run that failed.
    /// </summary>
    private async Task ReviewAsync(AgentRunResult result, string goal, int attempt)
    {
        if (!_reviewer.CanReview(result.CriticRole))
        {
            return;
        }

        Status += " · asking for a second opinion…";

        try
        {
            RunReview review = await _reviewer.ReviewAsync(result, CancellationToken.None).ConfigureAwait(true);
            _reviewedGoal = goal;
            _reviewedAttempt = attempt;
            Review = review;
        }
        catch (Exception ex)
        {
            Status += $" · the review failed: {ex.Message}";
        }
    }
}
