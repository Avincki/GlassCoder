using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using GlassCoder.Core.Agent;
using GlassCoder.Tools.Registry;
using GlassCoder.Wpf.Mvvm;

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
    private object? _currentView;
    private string _selectedSurface = "Transcript";
    private string _goal = string.Empty;
    private string _status = "Ready.";
    private bool _isRunning;
    private CancellationTokenSource? _cancellation;

    /// <summary>Creates the shell.</summary>
    public MainWindowViewModel(
        IAgentLoop loop,
        IToolRegistry tools,
        TranscriptViewModel transcript,
        ChangesViewModel changes,
        MetricsViewModel metrics)
    {
        _loop = loop;
        _tools = tools;

        Transcript = transcript;
        Changes = changes;
        Metrics = metrics;
        _currentView = transcript;

        RunCommand = new RelayCommand(async () => await RunAsync().ConfigureAwait(true), () => !IsRunning);
        CancelCommand = new RelayCommand(() => _cancellation?.Cancel(), () => IsRunning);

        Status = string.Create(CultureInfo.InvariantCulture,
            $"Ready. {_tools.Functions.Count} tools: {string.Join(", ", ToolNames)}");
    }

    /// <summary>The live transcript surface.</summary>
    public TranscriptViewModel Transcript { get; }

    /// <summary>The change-visibility surface.</summary>
    public ChangesViewModel Changes { get; }

    /// <summary>The metrics and ablation surface.</summary>
    public MetricsViewModel Metrics { get; }

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
        private set => SetProperty(ref _isRunning, value);
    }

    /// <summary>Starts a run.</summary>
    public RelayCommand RunCommand { get; }

    /// <summary>Cancels the run in flight.</summary>
    public RelayCommand CancelCommand { get; }

    /// <summary>Cancels and releases the run in flight, if any.</summary>
    public void Dispose()
    {
        _cancellation?.Cancel();
        _cancellation?.Dispose();
        _cancellation = null;
    }

    private async Task RunAsync()
    {
        if (string.IsNullOrWhiteSpace(Goal) || IsRunning)
        {
            return;
        }

        IsRunning = true;
        Status = "Running…";
        SelectedSurface = "Transcript";

        _cancellation = new CancellationTokenSource();
        try
        {
            AgentRunResult result = await _loop.RunAsync(
                new AgentRunRequest { TaskId = "desktop", Goal = Goal },
                _cancellation.Token).ConfigureAwait(true);

            Status = string.Create(CultureInfo.InvariantCulture,
                $"{result.StopReason} after {result.Steps} steps · {result.TotalTokens} tokens · " +
                $"tool-call validity {result.ToolCallValidityRate:P0}");
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
}
