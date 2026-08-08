using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using GlassCoder.Core.Agent;
using GlassCoder.Core.Verification;
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
    private readonly IAgentLoop _loop;
    private readonly IToolRegistry _tools;
    private readonly ISettingsDialog _settings;
    private readonly IAboutDialog _about;
    private readonly ICriticPanel _critics;
    private readonly IRunReviewer _reviewer;
    private readonly CritiqueOptions _critique;
    private readonly IUiStateStore? _uiState;
    private object? _currentView;
    private string _selectedSurface = "Transcript";
    private string _goal = string.Empty;
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
        IToolRegistry tools,
        TranscriptViewModel transcript,
        ChangesViewModel changes,
        MetricsViewModel metrics,
        WorkspaceViewModel workspace,
        ISettingsDialog settings,
        IAboutDialog about,
        ICriticPanel critics,
        IRunReviewer reviewer,
        IOptions<CritiqueOptions> critique,
        IUiStateStore? uiState = null,
        LimitExtensionGate? limitGate = null)
    {
        ArgumentNullException.ThrowIfNull(critique);

        _loop = loop;
        _tools = tools;
        _settings = settings;
        _about = about;
        _critics = critics;
        _reviewer = reviewer;
        _critique = critique.Value;
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
        Workspace = workspace;
        _currentView = transcript;

        // The last run's goal, so a repeated test run is a press of Run rather than a paste.
        // Still just a pre-fill: the box is editable and empty on a first-ever start.
        string? saved = _uiState?.LastGoal;
        _goal = string.IsNullOrWhiteSpace(saved) ? string.Empty : saved;

        RunCommand = new RelayCommand(async () => await RunAsync().ConfigureAwait(true), () => !IsRunning);
        CancelCommand = new RelayCommand(() => _cancellation?.Cancel(), () => IsRunning);
        SettingsCommand = new RelayCommand(OpenSettings, () => !IsRunning);
        AboutCommand = new RelayCommand(() => _about.Show());
        RetryCommand = new RelayCommand(async () => await RetryAsync().ConfigureAwait(true), () => CanRetry);
        DismissReviewCommand = new RelayCommand(() => Review = null, () => Review is not null);
        ExtendLimitCommand = new RelayCommand(() => ResolveLimit(true), () => HasLimitPrompt);
        StopAtLimitCommand = new RelayCommand(() => ResolveLimit(false), () => HasLimitPrompt);

        Status = string.Create(CultureInfo.InvariantCulture,
            $"Ready. {_tools.Functions.Count} tools: {string.Join(", ", ToolNames)}");
    }

    /// <summary>The live transcript surface.</summary>
    public TranscriptViewModel Transcript { get; }

    /// <summary>The change-visibility surface.</summary>
    public ChangesViewModel Changes { get; }

    /// <summary>The metrics and ablation surface.</summary>
    public MetricsViewModel Metrics { get; }

    /// <summary>The workspace pane on the right of the shell (workplan task 39).</summary>
    public WorkspaceViewModel Workspace { get; }

    /// <summary>Names of the surfaces, for the navigation list.</summary>
    public IReadOnlyList<string> Surfaces { get; } = ["Transcript", "Changes", "Metrics"];

    /// <summary>Tool names, as advertised to the model.</summary>
    public IReadOnlyList<string> ToolNames
    {
        get
        {
            List<string> names = [];
            foreach (Microsoft.Extensions.AI.AIFunction function in _tools.Functions)
            {
                names.Add(function.Name);
            }

            return names;
        }
    }

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
                // workspace out from under the agent.
                Changes.IsAgentRunning = value;
                Workspace.IsAgentRunning = value;
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
        set => SetProperty(ref _useRemoteCritic, value);
    }

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

    /// <summary>Cancels and releases the run in flight, if any.</summary>
    public void Dispose()
    {
        _cancellation?.Cancel();
        _cancellation?.Dispose();
        _cancellation = null;
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
        // rather than after it, so a crash mid-run still leaves the goal for the next start.
        if (_uiState is not null)
        {
            _uiState.LastGoal = goal;
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

            Status = string.Create(CultureInfo.InvariantCulture,
                $"{result.StopReason} after {result.Steps} steps · {result.TotalTokens} tokens · " +
                $"tool-call validity {result.ToolCallValidityRate:P0}");

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
