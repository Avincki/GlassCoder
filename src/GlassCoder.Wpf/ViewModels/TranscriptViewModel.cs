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

        // Every entry names its actor. The prefix looks redundant while the worker is the only
        // one calling tools, but this column also carries the actors that never call any - the
        // critique panel, the verification ladder - and rows from different actors have to be
        // tellable apart at a glance (and filterable by name).
        List<string> entries = [.. record.ToolCalls.Select(c => $"{record.Role}.{c.Name}")];
        if (record.Verification is { } verification)
        {
            entries.Add(verification.Critique is { } critique
                ? $"critic.refute {critique.RefutingVotes}/{critique.RespondingVotes}"
                : $"harness.verify→{verification.FailedRung ?? verification.HighestRungReached}");
        }

        _tools = entries.Count == 0 ? "-" : string.Join(", ", entries);

        // A failed climb or a refuting panel colours the row, so the transcript can be scanned
        // for the steps where an oracle pushed back.
        Severity = record.Error is not null ? "error"
            : record.ToolCalls.Any(c => !c.Parsed) ? "warning"
            : record.Verification is { } pushback && (!pushback.Passed || pushback.Critique?.Refuted == true) ? "warning"
            : "info";

        Summary = record.ToolCalls.Count == 0
            ? record.ResponseText ?? record.Outcome
            : string.Join(" · ", record.ToolCalls.Select(c => $"{c.Name}:{c.Status}"));
    }

    /// <summary>
    /// The post-run review as a transcript row, so the transcript is the one complete record of
    /// a run - the banner above it is dismissable, and an opinion that vanished on dismissal was
    /// the only part of a run the transcript could not show.
    /// </summary>
    /// <param name="review">The recorded review.</param>
    /// <param name="index">Index to display, one past the run's last step.</param>
    /// <param name="runStartedAt">When the reviewed run began, for the elapsed column.</param>
    public static StepRowViewModel ForReview(ReviewRecord review, int index, DateTimeOffset runStartedAt)
    {
        // A display-only shape: this record is never logged - the durable ReviewRecord already
        // was - it only lets the review ride the same row, detail pane and filters as a step.
        StepRecord record = new()
        {
            RunId = review.RunId,
            TaskId = review.TaskId,
            StepIndex = index,
            Role = review.CriticRole,
            StartedAt = review.RecordedAt - TimeSpan.FromMilliseconds(review.DurationMs),
            Prompt = [],
            ResponseText = review.Summary,
            ToolCalls = [],
            InputTokens = review.InputTokens,
            OutputTokens = review.OutputTokens,
            TotalTokens = review.InputTokens + review.OutputTokens,
            ModelLatencyMs = review.DurationMs,
            StepLatencyMs = review.DurationMs,
            Outcome = review.Inconclusive ? "review: inconclusive" : review.Refuted ? "review: refuted" : "review: accepted",
            Verification = new StepVerificationRecord(
                !review.Refuted,
                "Critique",
                review.Refuted ? "Critique" : null,
                review.DurationMs,
                review.Summary,
                review.EstimatedCostUsd)
            {
                Critique = new StepCritiqueRecord(
                    review.CriticRole,
                    review.Refuted,
                    review.Inconclusive,
                    review.Votes.Count(v => v.Available && v.Refuted),
                    review.RespondingVotes,
                    review.UnavailableVotes,
                    review.Votes),
            },
        };

        return new StepRowViewModel(record, runStartedAt) { ToolsOverride = "critic.review" };
    }

    /// <summary>Replaces the synthesized tool entries, for rows that are not steps.</summary>
    private string? ToolsOverride { get; init; }

    private readonly string _tools;

    /// <summary>The underlying record.</summary>
    public StepRecord Record { get; }

    /// <summary>Step index.</summary>
    public int Index => Record.StepIndex;

    /// <summary>Who did what in this step, each entry prefixed with its actor.</summary>
    public string Tools => ToolsOverride ?? _tools;

    /// <summary>info, warning or error - what the severity filter matches on.</summary>
    public string Severity { get; }

    /// <summary>One line describing the step.</summary>
    public string Summary { get; }

    /// <summary>Tokens for this step.</summary>
    public long Tokens => Record.TotalTokens ?? 0;

    /// <summary>Model latency for this step.</summary>
    public string Latency => Record.ModelLatencyMs.ToString("F0", CultureInfo.InvariantCulture) + " ms";

    /// <summary>
    /// How far into the run this step finished. Latency answers "how long did this step take";
    /// this reads the run's clock as the step's outcome landed - the start offset plus the whole
    /// step, tool and verification included, because a row appears when its step completes and a
    /// clock that stopped at the step's start would lag the run by exactly the action it just
    /// watched.
    /// </summary>
    public string Elapsed => FormatElapsed(
        Record.StartedAt + TimeSpan.FromMilliseconds(Record.StepLatencyMs) - _runStartedAt);

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

            if (Record.Verification is { } verification)
            {
                text.AppendLine();
                text.AppendLine(CultureInfo.InvariantCulture,
                    $"[verification {(verification.Passed ? "passed" : "FAILED")} at " +
                    $"{verification.FailedRung ?? verification.HighestRungReached} · {verification.DurationMs:F0} ms]");
                text.AppendLine(verification.Summary);

                // One block per critic, dissent and unreachable included - the tally alone hid
                // the one reason a human would actually want to read.
                if (verification.Critique is { } critique)
                {
                    foreach (ReviewVoteRecord vote in critique.Votes)
                    {
                        string verdict = !vote.Available ? "unreachable"
                            : vote.Refuted ? $"REFUTED {vote.Confidence:F2}"
                            : $"accepted {vote.Confidence:F2}";

                        text.AppendLine();
                        text.AppendLine(CultureInfo.InvariantCulture,
                            $"[{critique.CriticRole} · {vote.Lens ?? "no lens"} · {verdict}] {vote.Reason}");
                    }

                    if (verification.CritiqueCostUsd > 0m)
                    {
                        text.AppendLine();
                        text.AppendLine(CultureInfo.InvariantCulture,
                            $"[critique cost ${verification.CritiqueCostUsd:F4}]");
                    }
                }
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

        // Replayed reviews slot in after their run's last step, not at the tail of the whole
        // list - a session holds several runs, and run one's review does not follow run three.
        foreach (ReviewRecord review in bus.Reviews)
        {
            InsertReviewRow(review);
        }

        View = CollectionViewSource.GetDefaultView(Steps);
        View.Filter = Matches;

        _bus.StepRecorded += OnStepRecorded;
        _bus.ReviewRecorded += OnReviewRecorded;
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

    /// <summary>Tool names to filter by, plus "All" - and the actors that never call a tool.</summary>
    public IReadOnlyList<string> ToolFilters { get; } =
    [
        "All", "update_todos", "read_file", "grep", "glob", "create_file", "edit_file", "build", "run_tests", "bash",
        "git_status", "git_commit", "git_sync", "git_push", "create_pull_request", "critic", "harness.verify",
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

        // Matched against the rendered column rather than the raw calls, so "read_file" still
        // matches "worker.read_file" and the synthesized actors - critic, harness - are
        // filterable like any tool.
        if (ToolFilter != "All" && !row.Tools.Contains(ToolFilter, System.StringComparison.OrdinalIgnoreCase))
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

    private void OnReviewRecorded(object? sender, ReviewRecord record) =>
        _dispatcher.BeginInvoke(() => InsertReviewRow(record));

    /// <summary>
    /// Adds the review after the last row of the run it judges, numbered one past that run's
    /// last step. Live it lands at the tail anyway; on replay the position is what keeps a
    /// multi-run session reading in order.
    /// </summary>
    private void InsertReviewRow(ReviewRecord review)
    {
        int position = Steps.Count;
        int lastIndex = -1;
        for (int i = 0; i < Steps.Count; i++)
        {
            if (string.Equals(Steps[i].Record.RunId, review.RunId, System.StringComparison.Ordinal))
            {
                position = i + 1;
                lastIndex = Steps[i].Index;
            }
        }

        if (!_runStarts.TryGetValue(review.RunId, out DateTimeOffset runStartedAt))
        {
            runStartedAt = review.RecordedAt;
        }

        Steps.Insert(position, StepRowViewModel.ForReview(review, lastIndex + 1, runStartedAt));
    }
}
