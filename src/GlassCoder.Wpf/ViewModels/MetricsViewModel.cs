using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using GlassCoder.Core.Metrics;
using GlassCoder.Wpf.Mvvm;
using Microsoft.Extensions.Options;

namespace GlassCoder.Wpf.ViewModels;

/// <summary>One row of the ablation comparison: an arm or source, aggregated.</summary>
public sealed class MetricsRowViewModel
{
    /// <summary>Creates the row from the runs belonging to one group.</summary>
    public MetricsRowViewModel(string group, IReadOnlyList<RunMetrics> runs, double maxTokens)
    {
        Group = group;
        Runs = runs.Count;

        IReadOnlyList<RunMetrics> graded = [.. runs.Where(r => r.OraclePassed is not null)];
        PassAtOne = graded.Count == 0 ? null : (double)graded.Count(r => r.OraclePassed == true) / graded.Count;

        ToolCallValidity = runs.Average(r => r.ToolCallValidityRate);
        TokensToSolve = (long)runs.Average(r => r.TotalTokens);
        WallClockSeconds = runs.Average(r => r.WallClockMs) / 1000;
        CompileErrorRate = runs.Average(r => r.CompileErrorRatePerEdit);
        RecoveryRate = runs.Average(r => r.RecoveryRate);
        CascadeRatio = runs.Average(r => r.CascadeRatio);
        CostUsd = runs.Sum(r => r.CostUsd);
        FreshRuns = runs.Count(r => r.ContextFresh == true);

        // Bar width as a fraction of the widest row, so the chart needs no plotting library.
        TokenBar = maxTokens <= 0 ? 0 : TokensToSolve / maxTokens;
    }

    /// <summary>Arm or source name.</summary>
    public string Group { get; }

    /// <summary>Runs in this group.</summary>
    public int Runs { get; }

    /// <summary>pass@1, or null when nothing in the group was graded.</summary>
    public double? PassAtOne { get; }

    /// <summary>Mean tool-call validity.</summary>
    public double ToolCallValidity { get; }

    /// <summary>Mean tokens per run.</summary>
    public long TokensToSolve { get; }

    /// <summary>Mean wall-clock per run.</summary>
    public double WallClockSeconds { get; }

    /// <summary>Mean compile errors per edit.</summary>
    public double CompileErrorRate { get; }

    /// <summary>Mean recovery rate.</summary>
    public double RecoveryRate { get; }

    /// <summary>Mean cascade ratio.</summary>
    public double CascadeRatio { get; }

    /// <summary>Total estimated spend.</summary>
    public decimal CostUsd { get; }

    /// <summary>Runs whose context was fresh, for the Phase 6 comparison.</summary>
    public int FreshRuns { get; }

    /// <summary>Relative bar width, 0 to 1.</summary>
    public double TokenBar { get; }

    /// <summary>pass@1 formatted, or a dash when ungraded.</summary>
    public string PassAtOneText =>
        PassAtOne is null ? "-" : PassAtOne.Value.ToString("P0", CultureInfo.InvariantCulture);
}

/// <summary>
/// The metrics dashboard and ablation comparison (CLAUDE.md §11, workplan task 29).
/// <para>
/// Reads the JSONL the harness writes - the same file a notebook would read - and groups it by
/// arm. Grouping by arm rather than by run is the whole point: a single run's numbers are noise,
/// and the question an ablation answers is always "which arm, and by how much".
/// </para>
/// </summary>
public sealed class MetricsViewModel : ViewModelBase
{
    private static readonly JsonSerializerOptions ReadOptions = new(JsonSerializerDefaults.Web);

    private readonly MetricsOptions _options;
    private string _status = "No metrics loaded.";
    private string _groupBy = "Arm";

    /// <summary>Creates the view model.</summary>
    public MetricsViewModel(IOptions<MetricsOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);

        _options = options.Value;
        ReloadCommand = new RelayCommand(Reload);
        Reload();
    }

    /// <summary>The comparison rows.</summary>
    public ObservableCollection<MetricsRowViewModel> Rows { get; } = [];

    /// <summary>How rows are grouped.</summary>
    public IReadOnlyList<string> Groupings { get; } = ["Arm", "Task", "Source"];

    /// <summary>Selected grouping.</summary>
    public string GroupBy
    {
        get => _groupBy;
        set { if (SetProperty(ref _groupBy, value)) { Reload(); } }
    }

    /// <summary>What happened on the last load.</summary>
    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    /// <summary>Reloads the metrics file.</summary>
    public RelayCommand ReloadCommand { get; }

    /// <summary>Reads the JSONL and rebuilds the rows.</summary>
    public void Reload()
    {
        Rows.Clear();

        string path = Path.Combine(Path.GetFullPath(_options.Directory), _options.FileName);
        if (!File.Exists(path))
        {
            Status = $"No metrics yet at {path}.";
            return;
        }

        List<RunMetrics> runs = [];
        foreach (string line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            try
            {
                if (JsonSerializer.Deserialize<RunMetrics>(line, ReadOptions) is { } metrics)
                {
                    runs.Add(metrics);
                }
            }
            catch (JsonException)
            {
                // A partially written last line must not empty the dashboard.
            }
        }

        if (runs.Count == 0)
        {
            Status = "The metrics file is empty.";
            return;
        }

        Func<RunMetrics, string> key = GroupBy switch
        {
            "Task" => m => m.TaskId,
            "Source" => m => m.Source,
            _ => m => m.Arm ?? m.Source,
        };

        List<IGrouping<string, RunMetrics>> groups = [.. runs.GroupBy(key).OrderBy(g => g.Key, StringComparer.Ordinal)];
        double maxTokens = groups.Max(g => g.Average(r => r.TotalTokens));

        foreach (IGrouping<string, RunMetrics> group in groups)
        {
            Rows.Add(new MetricsRowViewModel(group.Key, [.. group], maxTokens));
        }

        Status = string.Create(CultureInfo.InvariantCulture,
            $"{runs.Count} runs across {groups.Count} groups, from {path}.");
    }
}
