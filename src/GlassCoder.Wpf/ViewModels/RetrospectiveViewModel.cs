using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using GlassCoder.Core.Configuration;
using GlassCoder.Core.Diagnostics;
using GlassCoder.Core.Verification;
using GlassCoder.Wpf.Highlighting;
using GlassCoder.Wpf.Mvvm;
using GlassCoder.Wpf.Services;
using Microsoft.Extensions.Options;

namespace GlassCoder.Wpf.ViewModels;

/// <summary>One stage's report, as the surface shows it.</summary>
public sealed class RetrospectiveStageViewModel : ViewModelBase
{
    private bool _isExpanded;

    /// <summary>Wraps a finished stage.</summary>
    /// <param name="stage">What the stage concluded.</param>
    /// <param name="expanded">Whether it opens expanded.</param>
    public RetrospectiveStageViewModel(RetrospectiveStage stage, bool expanded)
    {
        ArgumentNullException.ThrowIfNull(stage);

        Stage = stage;
        _isExpanded = expanded;
        ReportLines = HighlightedDocument.Build(stage.Report, SyntaxLanguage.Markdown);
    }

    /// <summary>The underlying stage.</summary>
    public RetrospectiveStage Stage { get; }

    /// <summary>The stage's name, as a person would say it.</summary>
    public string Title => Stage.Title;

    /// <summary>What answered, how long it took and what it cost.</summary>
    public string Headline => Stage.Reviewed
        ? string.Create(
            CultureInfo.InvariantCulture,
            $"{Stage.Model} · {Stage.DurationMs / 1000:F0} s · ${Stage.CostUsd:F4}")
        : "Not reviewed";

    /// <summary>Why this stage is not usable, when it is not.</summary>
    public string? Failure => Stage.Failure;

    /// <summary>Whether there is a failure to show.</summary>
    public bool HasFailure => !string.IsNullOrWhiteSpace(Stage.Failure);

    /// <summary>The report, coloured as Markdown.</summary>
    public IReadOnlyList<IReadOnlyList<HighlightedSpan>> ReportLines { get; }

    /// <summary>Whether the report is open.</summary>
    public bool IsExpanded
    {
        get => _isExpanded;
        set => SetProperty(ref _isExpanded, value);
    }
}

/// <summary>
/// Shows the finished proposals in their own window (workplan task 67).
/// <para>
/// A seam rather than a direct <c>new Window()</c> for the reason every other dialog here is one:
/// a view model that constructs windows cannot be tested, and "the window opens exactly once per
/// completed retrospective, and never on rehydration" is precisely the behaviour worth a test.
/// </para>
/// </summary>
public interface IRetrospectiveResultDialog
{
    /// <summary>Opens, or brings forward, the proposals window for this retrospective.</summary>
    /// <param name="model">The surface's own view model, so both show one set of ticks.</param>
    void Show(RetrospectiveViewModel model);
}

/// <summary>
/// The retrospective surface (workplan task 67): the fourth first-class view, beside the
/// transcript, the changes and the metrics.
/// <para>
/// The other three show a run as it is. This asks what it was worth - by having headless Claude
/// Code review the code the run produced, then the process that produced it, then what GlassCoder
/// itself should learn from both. The first two are prose to read. The third is a checklist,
/// because it is the only one whose subject is this repository, and the only one a person is
/// meant to leave with work from.
/// </para>
/// </summary>
public sealed class RetrospectiveViewModel : ViewModelBase, IDisposable
{
    /// <summary>How many activity lines the feed keeps. Beyond this the oldest go.</summary>
    private const int MaximumActivityLines = 400;

    private readonly IRetrospectiveReviewer _reviewer;
    private readonly IRetrospectiveWriter _writer;
    private readonly IRetrospectiveResultDialog? _dialog;
    private readonly IDesktopShell? _shell;
    private readonly ITranscriptBus? _transcript;
    private readonly LoggingOptions? _logging;
    private readonly Dispatcher _dispatcher;

    private RetrospectiveRequest? _run;
    private Retrospective? _result;
    private bool _isRunning;
    private bool _isAgentRunning;
    private bool _available;
    private string _tooltip = "Checking whether Claude Code is available…";
    private string _status = "No completed run to look back at yet.";
    private string _instructions = string.Empty;
    private string? _workOrderPath;
    private CancellationTokenSource? _cancellation;

    /// <summary>Creates the surface.</summary>
    /// <param name="reviewer">The three-stage reviewer.</param>
    /// <param name="writer">Where a ticked checklist is written.</param>
    /// <param name="dispatcher">The UI dispatcher. Everything from a background thread crosses it.</param>
    /// <param name="dialog">Opens the proposals window. Null leaves the surface as the only face.</param>
    /// <param name="transcript">Watched for finished runs, so the surface knows what to offer.</param>
    /// <param name="shell">Used only to reveal a written work order.</param>
    public RetrospectiveViewModel(
        IRetrospectiveReviewer reviewer,
        IRetrospectiveWriter writer,
        Dispatcher dispatcher,
        IRetrospectiveResultDialog? dialog = null,
        ITranscriptBus? transcript = null,
        IDesktopShell? shell = null,
        IOptions<LoggingOptions>? logging = null)
    {
        _reviewer = reviewer;
        _writer = writer;
        _dispatcher = dispatcher;
        _dialog = dialog;
        _transcript = transcript;
        _shell = shell;
        _logging = logging?.Value;

        RunCommand = new RelayCommand(
            async () => await RunAsync().ConfigureAwait(true),
            () => CanRun);
        CancelCommand = new RelayCommand(() => _cancellation?.Cancel(), () => IsRunning);
        WriteWorkOrderCommand = new RelayCommand(WriteWorkOrder, () => CanWriteWorkOrder);
        ShowProposalsCommand = new RelayCommand(
            () => _dialog?.Show(this),
            () => _dialog is not null && Recommendations.Count > 0);
        ShowOutputCommand = new RelayCommand(ShowOutput, () => _workOrderPath is not null);

        if (_transcript is not null)
        {
            _transcript.RunRecorded += OnRunRecorded;
        }

        _ = InitialiseAsync();
        _ = RecallLastRunAsync();
    }

    /// <summary>The stage reports, in the order they ran.</summary>
    public ObservableCollection<RetrospectiveStageViewModel> Stages { get; } = [];

    /// <summary>The proposed workplan: what GlassCoder should learn, one tickable item each.</summary>
    public ObservableCollection<ReviewActionViewModel> Recommendations { get; } = [];

    /// <summary>What the CLI is doing right now, newest last.</summary>
    public ObservableCollection<string> Activity { get; } = [];

    /// <summary>Takes the retrospective.</summary>
    public RelayCommand RunCommand { get; }

    /// <summary>Stops the stage in flight. Stages already finished are kept.</summary>
    public RelayCommand CancelCommand { get; }

    /// <summary>Writes the ticked recommendations out as a work order.</summary>
    public RelayCommand WriteWorkOrderCommand { get; }

    /// <summary>Reopens the proposals window.</summary>
    public RelayCommand ShowProposalsCommand { get; }

    /// <summary>Opens the folder the work order was written to.</summary>
    public RelayCommand ShowOutputCommand { get; }

    /// <summary>The run being looked back at, when there is one.</summary>
    public string? RunId => _run?.RunId;

    /// <summary>The run's id, shortened the way the transcript shortens it.</summary>
    public string RunLabel => _run is null
        ? "No run yet"
        : $"Run {(_run.RunId.Length <= 8 ? _run.RunId : _run.RunId[..8])} · {_run.StopReason} · {_run.Steps} steps";

    /// <summary>The goal that run was given, for the header.</summary>
    public string RunGoal => _run?.Goal ?? string.Empty;

    /// <summary>Whether there is a finished run to look back at.</summary>
    public bool HasRun => _run is not null;

    /// <summary>Optional direction for the reviewer - "look at the test quality", say.</summary>
    public string Instructions
    {
        get => _instructions;
        set => SetProperty(ref _instructions, value);
    }

    /// <summary>Whether a retrospective is in flight.</summary>
    public bool IsRunning
    {
        get => _isRunning;
        private set
        {
            if (SetProperty(ref _isRunning, value))
            {
                OnPropertyChanged(nameof(CanRun));
                OnPropertyChanged(nameof(RunLabelForButton));
                OnPropertyChanged(nameof(CanWriteWorkOrder));
            }
        }
    }

    /// <summary>
    /// Whether the agent itself is running. The retrospective stands down while it is: judging a
    /// run that is still changing its own files would review a moving target, and the run whose
    /// id this surface holds is not the one in flight.
    /// </summary>
    public bool IsAgentRunning
    {
        get => _isAgentRunning;
        set
        {
            if (SetProperty(ref _isAgentRunning, value))
            {
                OnPropertyChanged(nameof(CanRun));
                OnPropertyChanged(nameof(Tooltip));
            }
        }
    }

    /// <summary>Whether the button can be pressed, and why it is what it is.</summary>
    public bool CanRun => _available && HasRun && !IsRunning && !IsAgentRunning;

    /// <summary>What the button says: a first look, or another one.</summary>
    public string RunLabelForButton => _result is null ? "Take retrospective" : "Take it again";

    /// <summary>
    /// Why the button is enabled or disabled. A greyed control that does not say why is a bug
    /// report waiting to happen, and every reason here has a different fix.
    /// </summary>
    public string Tooltip
    {
        get
        {
            if (IsAgentRunning)
            {
                return "A run is in flight. The retrospective judges a finished run, so it waits for this one.";
            }

            return !HasRun ? "Finish a run first - there is nothing to look back at yet." : _tooltip;
        }
    }

    /// <summary>What the surface is doing, or what it last did.</summary>
    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    /// <summary>Whether a retrospective is on screen.</summary>
    public bool HasResult => _result is not null;

    /// <summary>What the whole thing cost and how long it took.</summary>
    public string ResultHeadline => _result is not { } result
        ? string.Empty
        : string.Create(
            CultureInfo.InvariantCulture,
            $"{result.Stages.Count} stage(s) · {result.TotalDurationMs / 1000:F0} s · ${result.TotalCostUsd:F4}");

    /// <summary>Whether the work order can be written right now.</summary>
    public bool CanWriteWorkOrder =>
        !IsRunning && _writer.CanWrite && Recommendations.Any(r => r.IsAccepted);

    /// <summary>
    /// Why the work order cannot be written, when it cannot. Names the setting, because the
    /// common case is a machine that was never told where GlassCoder's source lives.
    /// </summary>
    public string WorkOrderTooltip =>
        _writer.UnavailableReason
        ?? "Writes the ticked recommendations as one Markdown work order a Claude Code session can implement.";

    /// <summary>Where the last work order went.</summary>
    public string? WorkOrderPath => _workOrderPath;

    /// <summary>
    /// Notes a finished run and offers it. Public because the shell also hands over the run it
    /// just finished, which is the same fact arriving by a shorter path.
    /// </summary>
    /// <param name="request">The run to look back at.</param>
    public void OfferRun(RetrospectiveRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        bool changed = _run is null || !string.Equals(_run.RunId, request.RunId, StringComparison.Ordinal);
        _run = request;

        OnPropertyChanged(nameof(RunId));
        OnPropertyChanged(nameof(RunLabel));
        OnPropertyChanged(nameof(RunGoal));
        OnPropertyChanged(nameof(HasRun));
        OnPropertyChanged(nameof(CanRun));
        OnPropertyChanged(nameof(Tooltip));

        if (!changed)
        {
            return;
        }

        // A new run's retrospective is not the last one's. Anything already on disk for this run
        // is shown; a run never looked at shows the invitation rather than stale reports.
        Clear();

        Retrospective? existing;
        try
        {
            existing = _reviewer.Load(request.RunId);
        }
        catch (Exception ex)
        {
            // This runs from the transcript bus's own event, marshalled onto the dispatcher, so
            // it has no caller to report to - an exception here would go unhandled on the UI
            // thread and take the window down at the end of a run that had just succeeded.
            Status = $"Could not read the retrospective already on disk: {ex.Message}";
            return;
        }

        if (existing is not null)
        {
            // Rehydration deliberately does not open the window: the operator did not just press
            // anything, and a window arriving at startup is not an answer to a question they asked.
            Apply(existing, announce: false);
            Status = $"A retrospective from {existing.TakenAt.LocalDateTime:g} is on disk for this run.";
        }
        else
        {
            Status = "Ready to look back at this run.";
        }
    }

    private void OnRunRecorded(object? sender, RunRecord record) =>
        _dispatcher.BeginInvoke(() => OfferRun(new RetrospectiveRequest(record.RunId)
        {
            TaskId = record.TaskId,
            Goal = record.Goal,
            StopReason = record.StopReason,
            Steps = record.Steps,
            TotalTokens = record.TotalTokens,
        }));

    /// <summary>
    /// Offers the last run in the durable transcript, so the surface has something to look back
    /// at on a cold start.
    /// <para>
    /// Without this the surface only ever knew about runs that finished in <em>this</em> process,
    /// because <see cref="ITranscriptBus.RunRecorded"/> is the only thing that told it - so
    /// closing the application and reopening it left "No run yet" and a greyed button, with every
    /// run of the last month sitting in the log a few metres away. Looking back at a finished run
    /// is precisely the thing one does after coming back to it.
    /// </para>
    /// <para>
    /// The live bus still wins: if a run finishes while this is reading, it has offered a fresher
    /// run than anything on disk and this leaves it alone.
    /// </para>
    /// </summary>
    private async Task RecallLastRunAsync()
    {
        if (_logging is null)
        {
            return;
        }

        try
        {
            // Off the UI thread: today's log is already megabytes, and this runs during the
            // window's own construction.
            RunRecord? last = await Task.Run(() =>
            {
                string directory = AppPaths.ResolveDataDirectory(_logging.Directory);
                if (!Directory.Exists(directory))
                {
                    return null;
                }

                // Newest file only. Reading the whole rolling set would parse a month of runs to
                // answer a question about the last one.
                string? newest = new DirectoryInfo(directory)
                    .EnumerateFiles("*.jsonl")
                    .OrderByDescending(f => f.LastWriteTimeUtc)
                    .FirstOrDefault()?.FullName;

                if (newest is null)
                {
                    return null;
                }

                // A run record is only written when a run ends, so the last one is the last run
                // that finished - which is the only kind worth looking back at.
                return TranscriptReader.ReadFile(newest)
                    .Select(t => t.Run)
                    .LastOrDefault(run => run is not null);
            }).ConfigureAwait(true);

            if (last is null || _run is not null)
            {
                return;
            }

            OfferRun(new RetrospectiveRequest(last.RunId)
            {
                TaskId = last.TaskId,
                Goal = last.Goal,
                StopReason = last.StopReason,
                Steps = last.Steps,
                TotalTokens = last.TotalTokens,
            });
        }
        catch (Exception ex)
        {
            // A surface that cannot read the log is a surface with no run to offer, which it
            // already knows how to say. It is not a reason to fail construction.
            Status = $"Could not read the transcript for an earlier run: {ex.Message}";
        }
    }

    private async Task InitialiseAsync()
    {
        if (!_reviewer.Enabled)
        {
            _tooltip = "The retrospective is switched off. Set GlassCoder:Retrospective:Enabled to true.";
            OnPropertyChanged(nameof(Tooltip));
            return;
        }

        try
        {
            ReviewerAvailability availability = await _reviewer.ProbeAsync().ConfigureAwait(true);
            _available = availability.IsAvailable;
            _tooltip = availability.IsAvailable
                ? $"Have Claude Code ({availability.Version}) review this run three ways. It reads only; " +
                  "it changes nothing. Expect a few minutes and a few dollars."
                : availability.Reason ?? "The reviewer is not available.";
        }
        catch (Exception ex)
        {
            _available = false;
            _tooltip = $"The reviewer could not be probed: {ex.Message}";
        }

        OnPropertyChanged(nameof(Tooltip));
        OnPropertyChanged(nameof(CanRun));
    }

    private async Task RunAsync()
    {
        if (_run is not { } run || IsRunning)
        {
            return;
        }

        IsRunning = true;
        Clear();
        Status = "Reviewing the code this run produced… (1 of 3)";

        _cancellation = new CancellationTokenSource();
        try
        {
            Progress<RetrospectiveActivity> progress = new(OnActivity);
            Retrospective result = await _reviewer.ReviewAsync(
                run with { Instructions = Instructions },
                progress,
                _cancellation.Token).ConfigureAwait(true);

            Apply(result, announce: true);
        }
        catch (OperationCanceledException)
        {
            Status = "Cancelled. Any stage that finished is still here.";
        }
        catch (Exception ex)
        {
            // A failed retrospective must not take the shell down with it.
            Status = $"The retrospective failed: {ex.Message}";
        }
        finally
        {
            _cancellation?.Dispose();
            _cancellation = null;
            IsRunning = false;
        }
    }

    /// <summary>
    /// Renders one line of the CLI's work.
    /// <para>
    /// <see cref="Progress{T}"/> captures the creating thread's context, which is the dispatcher's,
    /// so this arrives on the UI thread even though the process reader raised it on its own. That
    /// is the whole reason it is a <c>Progress</c> and not a bare callback: the equivalent bug
    /// cost task 65 four tests that passed against a question never asked.
    /// </para>
    /// </summary>
    private void OnActivity(RetrospectiveActivity activity)
    {
        string prefix = activity.Kind switch
        {
            ClaudeCliEventKind.ToolCall => "· ",
            ClaudeCliEventKind.Note => "! ",
            ClaudeCliEventKind.Started => "▸ ",
            _ => string.Empty,
        };

        Activity.Add(prefix + activity.Text.ReplaceLineEndings(" "));
        while (Activity.Count > MaximumActivityLines)
        {
            Activity.RemoveAt(0);
        }

        Status = activity.Stage switch
        {
            RetrospectiveStageKind.Code => "Reviewing the code this run produced… (1 of 3)",
            RetrospectiveStageKind.Process => "Reviewing how the run got there… (2 of 3)",
            _ => "Working out what GlassCoder should learn… (3 of 3)",
        };
    }

    private void Apply(Retrospective result, bool announce)
    {
        _result = result;
        Stages.Clear();
        Recommendations.Clear();

        foreach (RetrospectiveStage stage in result.Stages)
        {
            // The harness stage opens; the two it was built from stay folded. It is the one with
            // something to decide, and three open reports is a wall of prose.
            Stages.Add(new RetrospectiveStageViewModel(stage, stage.Kind == RetrospectiveStageKind.Harness));
        }

        foreach (ReviewAction action in result.Recommendations)
        {
            ReviewActionViewModel item = new(action);
            item.PropertyChanged += OnRecommendationChanged;
            Recommendations.Add(item);
        }

        OnPropertyChanged(nameof(HasResult));
        OnPropertyChanged(nameof(ResultHeadline));
        OnPropertyChanged(nameof(RunLabelForButton));
        OnPropertyChanged(nameof(CanWriteWorkOrder));

        if (result.Failure is { } failure)
        {
            Status = failure;
        }
        else
        {
            Status = string.Create(
                CultureInfo.InvariantCulture,
                $"{result.Stages.Count(s => s.Reviewed)} of 3 stage(s) reviewed · " +
                $"{Recommendations.Count} recommendation(s) · ${result.TotalCostUsd:F2}");
        }

        // The window is the "it is done": the operator has been elsewhere for minutes, and the
        // one thing here that wants a decision is the checklist.
        if (announce && Recommendations.Count > 0)
        {
            _dialog?.Show(this);
        }
    }

    private void OnRecommendationChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ReviewActionViewModel.IsAccepted))
        {
            // The command itself needs no nudge: RelayCommand rides CommandManager, which
            // re-queries on its own. This is for the property the window binds directly.
            OnPropertyChanged(nameof(CanWriteWorkOrder));
        }
    }

    /// <summary>
    /// Writes every recommendation out, with the ticked ones marked. The rejected ones stay in
    /// the file because they are the context that explains the accepted ones - and because an
    /// agent reading it should know what was considered and turned down.
    /// </summary>
    private void WriteWorkOrder()
    {
        if (_result is not { } result || _run is not { } run)
        {
            return;
        }

        try
        {
            _workOrderPath = _writer.Write(new ReviewActionPlan(
                $"run {run.RunId}",
                result.TakenAt,
                result.Stages.FirstOrDefault()?.Model ?? string.Empty,
                result.TotalCostUsd,
                Preamble(result, run),
                [.. Recommendations.Select(r => new ReviewActionItem(r.Action, r.IsAccepted))])
            {
                Kind = ReviewActionFile.RetrospectiveKind,
                Target = "harness",
                RunId = run.RunId,
                Heading = $"GlassCoder retrospective - run {run.RunId}",
                Closing = Closing,
            });

            int accepted = Recommendations.Count(r => r.IsAccepted);
            Status = string.Create(
                CultureInfo.InvariantCulture,
                $"{accepted} recommendation(s) accepted · written to {_workOrderPath}");
            OnPropertyChanged(nameof(WorkOrderPath));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Status = $"Could not write the work order: {ex.Message}";
        }
    }

    /// <summary>
    /// What a cold agent needs before the list means anything: which run, what it was asked for,
    /// and where the reasoning behind each item is.
    /// </summary>
    private static string Preamble(Retrospective result, RetrospectiveRequest run)
    {
        System.Text.StringBuilder text = new();
        text.AppendLine(CultureInfo.InvariantCulture,
            $"These recommendations come from a retrospective on GlassCoder run `{run.RunId}`, " +
            $"which stopped as {run.StopReason ?? "unknown"} after {run.Steps} steps.");
        text.AppendLine();
        text.AppendLine("The run was given this goal:");
        text.AppendLine();
        text.AppendLine("```");
        text.AppendLine((run.Goal ?? "(not recorded)").Trim());
        text.AppendLine("```");
        text.AppendLine();

        if (result.Directory is { } directory)
        {
            text.AppendLine("The three reports these came out of:");
            text.AppendLine();
            foreach (RetrospectiveStage stage in result.Stages)
            {
                text.AppendLine(CultureInfo.InvariantCulture,
                    $"- {stage.Title}: `{stage.Path ?? Path.Combine(directory, "(not written)")}`");
            }

            text.AppendLine();
        }

        RetrospectiveStage? harness = result.Stages.FirstOrDefault(s => s.Kind == RetrospectiveStageKind.Harness);
        if (harness is { Reviewed: true })
        {
            text.AppendLine("## Why these, in the reviewer's words");
            text.AppendLine();
            text.AppendLine(harness.Report.TrimEnd());
        }

        return text.ToString();
    }

    private const string Closing = """
        ## How to use this

        Implement the ticked items above, in this repository, in priority order. Each was written
        to be accepted on its own, so an unticked item is not a dependency of a ticked one.

        Read the three reports linked above before starting: they are the evidence, and the
        `detail` line of each item is a summary of it rather than the whole argument.

        Add what you implement to `HISTORY.md`, and tick nothing here - this file is the record of
        what was asked for, not of what was done.
        """;

    private void ShowOutput()
    {
        if (_workOrderPath is not null)
        {
            _shell?.OpenFolder(Path.GetDirectoryName(_workOrderPath) ?? _workOrderPath);
        }
    }

    private void Clear()
    {
        foreach (ReviewActionViewModel item in Recommendations)
        {
            item.PropertyChanged -= OnRecommendationChanged;
        }

        _result = null;
        _workOrderPath = null;
        Stages.Clear();
        Recommendations.Clear();
        Activity.Clear();

        OnPropertyChanged(nameof(HasResult));
        OnPropertyChanged(nameof(ResultHeadline));
        OnPropertyChanged(nameof(CanWriteWorkOrder));
        OnPropertyChanged(nameof(WorkOrderPath));
    }

    /// <summary>Stops watching the transcript and cancels anything in flight.</summary>
    public void Dispose()
    {
        if (_transcript is not null)
        {
            _transcript.RunRecorded -= OnRunRecorded;
        }

        _cancellation?.Cancel();
        _cancellation?.Dispose();
        _cancellation = null;
    }
}
