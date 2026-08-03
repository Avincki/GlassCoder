using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Windows.Data;
using System.Windows.Threading;
using GlassCoder.Core.Diagnostics;
using GlassCoder.Wpf.Mvvm;

namespace GlassCoder.Wpf.ViewModels;

/// <summary>One step, shaped for the transcript list.</summary>
public sealed class StepRowViewModel
{
    private readonly DateTimeOffset _runStartedAt;

    /// <summary>Creates the row from a step record.</summary>
    /// <param name="record">The step to show.</param>
    /// <param name="runStartedAt">
    /// When the run this step belongs to began - the origin the elapsed column counts from.
    /// </param>
    public StepRowViewModel(StepRecord record, DateTimeOffset runStartedAt)
    {
        Record = record;
        _runStartedAt = runStartedAt;
        Tools = record.ToolCalls.Count == 0
            ? "-"
            : string.Join(", ", record.ToolCalls.Select(c => c.Name));

        Severity = record.Error is not null ? "error"
            : record.ToolCalls.Any(c => !c.Parsed) ? "warning"
            : "info";

        Summary = record.ToolCalls.Count == 0
            ? record.ResponseText ?? record.Outcome
            : string.Join(" · ", record.ToolCalls.Select(c => $"{c.Name}:{c.Status}"));
    }

    /// <summary>The underlying record.</summary>
    public StepRecord Record { get; }

    /// <summary>Step index.</summary>
    public int Index => Record.StepIndex;

    /// <summary>Tools called in this step.</summary>
    public string Tools { get; }

    /// <summary>info, warning or error - what the severity filter matches on.</summary>
    public string Severity { get; }

    /// <summary>One line describing the step.</summary>
    public string Summary { get; }

    /// <summary>Tokens for this step.</summary>
    public long Tokens => Record.TotalTokens ?? 0;

    /// <summary>Model latency for this step.</summary>
    public string Latency => Record.ModelLatencyMs.ToString("F0", CultureInfo.InvariantCulture) + " ms";

    /// <summary>
    /// How far into the run this step started. Latency answers "how long did this step take";
    /// this answers "when did it happen", which is what makes a slow patch in the middle of an
    /// otherwise brisk run visible at a glance.
    /// </summary>
    public string Elapsed => FormatElapsed(Record.StartedAt - _runStartedAt);

    /// <summary>
    /// <c>m:ss</c> up to an hour, <c>h:mm:ss</c> past it. Clamped at zero: a clock that moved
    /// between steps should read 0:00 rather than a negative offset.
    /// </summary>
    private static string FormatElapsed(TimeSpan elapsed)
    {
        if (elapsed < TimeSpan.Zero)
        {
            elapsed = TimeSpan.Zero;
        }

        return elapsed.TotalHours >= 1
            ? elapsed.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture)
            : elapsed.ToString(@"m\:ss", CultureInfo.InvariantCulture);
    }

    /// <summary>Outcome.</summary>
    public string Outcome => Record.Outcome;

    /// <summary>The full prompt and response, for the detail pane.</summary>
    public string Detail
    {
        get
        {
            System.Text.StringBuilder text = new();
            foreach (TranscriptMessage message in Record.Prompt)
            {
                text.AppendLine(CultureInfo.InvariantCulture, $"[{message.Role}] {message.Text}");
            }

            if (Record.ResponseText is not null)
            {
                text.AppendLine();
                text.AppendLine(CultureInfo.InvariantCulture, $"[assistant] {Record.ResponseText}");
            }

            foreach (ToolCallRecord call in Record.ToolCalls)
            {
                text.AppendLine();
                text.AppendLine(CultureInfo.InvariantCulture, $"[tool {call.Name} → {call.Status}] {call.Result}");
            }

            return text.ToString();
        }
    }
}

/// <summary>
/// The live transcript (CLAUDE.md §9, workplan task 26): scrolling, filterable by step, tool and
/// severity, fed by the in-process bus as the run happens.
/// </summary>
public sealed class TranscriptViewModel : ViewModelBase
{
    private readonly ITranscriptBus _bus;
    private readonly Dispatcher _dispatcher;
    private readonly Dictionary<string, DateTimeOffset> _runStarts = [];
    private string _toolFilter = "All";
    private string _severityFilter = "All";
    private string _search = string.Empty;
    private int _minimumStep;
    private StepRowViewModel? _selected;

    /// <summary>Creates the view model and subscribes to the bus.</summary>
    public TranscriptViewModel(ITranscriptBus bus, Dispatcher? dispatcher = null)
    {
        _bus = bus;
        _dispatcher = dispatcher ?? Dispatcher.CurrentDispatcher;

        foreach (StepRecord record in bus.Steps)
        {
            Steps.Add(CreateRow(record));
        }

        View = CollectionViewSource.GetDefaultView(Steps);
        View.Filter = Matches;

        _bus.StepRecorded += OnStepRecorded;
        ClearCommand = new RelayCommand(() =>
        {
            _bus.Clear();
            Steps.Clear();
            _runStarts.Clear();
        });
    }

    /// <summary>Every step, newest last.</summary>
    public ObservableCollection<StepRowViewModel> Steps { get; } = [];

    /// <summary>The filtered view bound to the list.</summary>
    public ICollectionView View { get; }

    /// <summary>Tool names to filter by, plus "All".</summary>
    public IReadOnlyList<string> ToolFilters { get; } =
    [
        "All", "update_todos", "read_file", "grep", "glob", "create_file", "edit_file", "build", "run_tests", "bash",
        "git_status", "git_commit", "git_sync", "git_push", "create_pull_request",
    ];

    /// <summary>Severities to filter by.</summary>
    public IReadOnlyList<string> SeverityFilters { get; } = ["All", "info", "warning", "error"];

    /// <summary>Selected tool filter.</summary>
    public string ToolFilter
    {
        get => _toolFilter;
        set { if (SetProperty(ref _toolFilter, value)) { View.Refresh(); } }
    }

    /// <summary>Selected severity filter.</summary>
    public string SeverityFilter
    {
        get => _severityFilter;
        set { if (SetProperty(ref _severityFilter, value)) { View.Refresh(); } }
    }

    /// <summary>Free-text search across the step summary and detail.</summary>
    public string Search
    {
        get => _search;
        set { if (SetProperty(ref _search, value)) { View.Refresh(); } }
    }

    /// <summary>Lowest step index to show.</summary>
    public int MinimumStep
    {
        get => _minimumStep;
        set { if (SetProperty(ref _minimumStep, value)) { View.Refresh(); } }
    }

    /// <summary>The selected step, shown in the detail pane.</summary>
    public StepRowViewModel? Selected
    {
        get => _selected;
        set => SetProperty(ref _selected, value);
    }

    /// <summary>Clears the transcript.</summary>
    public RelayCommand ClearCommand { get; }

    private bool Matches(object item)
    {
        if (item is not StepRowViewModel row)
        {
            return false;
        }

        if (row.Index < MinimumStep)
        {
            return false;
        }

        if (ToolFilter != "All" && !row.Record.ToolCalls.Any(c => c.Name == ToolFilter))
        {
            return false;
        }

        if (SeverityFilter != "All" && row.Severity != SeverityFilter)
        {
            return false;
        }

        return string.IsNullOrWhiteSpace(Search) ||
               row.Summary.Contains(Search, System.StringComparison.OrdinalIgnoreCase) ||
               row.Detail.Contains(Search, System.StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Builds a row, anchoring its elapsed time to the first step seen for that run.
    /// <para>
    /// Keyed by run rather than taken from row zero because the bus is session-scoped: two runs
    /// without a Clear in between share one list, and the second one's clock has to restart. The
    /// anchor is the earliest step still held - if the bus has already evicted the opening steps
    /// of a long run, elapsed is measured from the oldest one that survived.
    /// </para>
    /// </summary>
    private StepRowViewModel CreateRow(StepRecord record)
    {
        if (!_runStarts.TryGetValue(record.RunId, out DateTimeOffset runStartedAt))
        {
            runStartedAt = record.StartedAt;
            _runStarts[record.RunId] = runStartedAt;
        }

        return new StepRowViewModel(record, runStartedAt);
    }

    private void OnStepRecorded(object? sender, StepRecord record)
    {
        // The loop runs on a background thread; the collection is bound to the UI. Building the
        // row inside the callback keeps _runStarts single-threaded, and dispatcher operations of
        // equal priority run in the order they were posted, so the first step of a run still
        // arrives first and still wins the anchor.
        _dispatcher.BeginInvoke(() => Steps.Add(CreateRow(record)));
    }
}
