using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using GlassCoder.Core.Diagnostics;
using GlassCoder.Tools;
using GlassCoder.Tools.Changes;
using GlassCoder.Tools.Git;
using GlassCoder.Wpf.Mvvm;

namespace GlassCoder.Wpf.ViewModels;

/// <summary>One change, shaped for the change list and the diff pane.</summary>
public sealed class ChangeRowViewModel : ViewModelBase
{
    private CodeChange _change;

    /// <summary>Creates the row.</summary>
    public ChangeRowViewModel(CodeChange change) => _change = change;

    /// <summary>The underlying change.</summary>
    public CodeChange Change
    {
        get => _change;
        set
        {
            _change = value;
            OnPropertyChanged(nameof(Status));
            OnPropertyChanged(nameof(Note));
            OnPropertyChanged(nameof(Verification));
            OnPropertyChanged(nameof(Diff));
            OnPropertyChanged(nameof(IsPending));
        }
    }

    /// <summary>Change identifier.</summary>
    public string Id => _change.Id;

    /// <summary>File the change touches.</summary>
    public string Path => _change.Path;

    /// <summary>Task the change belongs to.</summary>
    public string TaskId => _change.TaskId;

    /// <summary>Proposed, Applied, Rejected or Reverted.</summary>
    public string Status => _change.Status.ToString();

    /// <summary>Why it was refused, when it was.</summary>
    public string? Note => _change.Note;

    /// <summary>The compile or test result this change produced.</summary>
    public string? Verification => _change.VerificationSummary;

    /// <summary>The line range the change touches.</summary>
    public string Range
    {
        get
        {
            (int Start, int End)? range = _change.Range();
            return range is null
                ? "-"
                : string.Create(CultureInfo.InvariantCulture, $"{range.Value.Start}-{range.Value.End}");
        }
    }

    /// <summary>When it was proposed.</summary>
    public string When => _change.CreatedAt.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture);

    /// <summary>The diff, ready for the before/after pane.</summary>
    public IReadOnlyList<DiffLine> Diff => _change.Diff();

    /// <summary>Whether this change is still waiting on a decision.</summary>
    public bool IsPending => _change.Status == ChangeStatus.Proposed;
}

/// <summary>
/// The change-visibility surface (CLAUDE.md §10, workplan tasks 27-28).
/// <para>
/// Every change appears here as a before/after diff with its file, its line range and its
/// status, and the compile or test result that it produced is attached to it. Proposed changes
/// waiting on approval are shown first, because that is the one thing the user has to act on.
/// </para>
/// </summary>
public sealed class ChangesViewModel : ViewModelBase
{
    private readonly IChangeLog _changes;
    private readonly Dispatcher _dispatcher;
    private readonly GitTool? _git;
    private readonly IStepLogger? _steps;
    private readonly ITranscriptBus? _transcript;
    private ChangeRowViewModel? _selected;
    private PendingApproval? _pending;
    private string _commitMessage = string.Empty;
    private string _gitStatus = string.Empty;
    private bool _isGitBusy;
    private bool _isAgentRunning;

    /// <summary>Creates the view model and subscribes to the change log.</summary>
    /// <param name="changes">The per-task change log.</param>
    /// <param name="dispatcher">UI dispatcher the collections are marshalled onto.</param>
    /// <param name="git">
    /// The git tools, when they are enabled. Null hides the manual controls rather than
    /// offering buttons that cannot work.
    /// </param>
    /// <param name="steps">
    /// The transcript logger, so a button-initiated git action lands in the record exactly as
    /// the model's own call would (workplan task 42).
    /// </param>
    public ChangesViewModel(
        IChangeLog changes,
        Dispatcher? dispatcher = null,
        GitTool? git = null,
        IStepLogger? steps = null,
        ITranscriptBus? transcript = null)
    {
        _changes = changes;
        _dispatcher = dispatcher ?? Dispatcher.CurrentDispatcher;
        _git = git;
        _steps = steps;
        _transcript = transcript;

        foreach (CodeChange change in changes.All())
        {
            Changes.Add(new ChangeRowViewModel(change));
        }

        _changes.Changed += OnChanged;

        ApproveCommand = new RelayCommand(() => Decide(true), () => Pending is not null);
        RejectCommand = new RelayCommand(() => Decide(false), () => Pending is not null);

        CommitCommand = new RelayCommand(
            async () => await CommitAsync().ConfigureAwait(true),
            () => CanRunGit && !string.IsNullOrWhiteSpace(CommitMessage));

        PushCommand = new RelayCommand(
            async () => await PushAsync().ConfigureAwait(true),
            () => CanRunGit);
    }

    /// <summary>Every change, newest last.</summary>
    public ObservableCollection<ChangeRowViewModel> Changes { get; } = [];

    /// <summary>The selected change, shown as a diff.</summary>
    public ChangeRowViewModel? Selected
    {
        get => _selected;
        set => SetProperty(ref _selected, value);
    }

    /// <summary>The change currently waiting on a human, if any.</summary>
    public PendingApproval? Pending
    {
        get => _pending;
        private set
        {
            if (SetProperty(ref _pending, value))
            {
                OnPropertyChanged(nameof(HasPending));
            }
        }
    }

    /// <summary>Whether anything is waiting on approval.</summary>
    public bool HasPending => Pending is not null;

    /// <summary>Approves the pending change.</summary>
    public RelayCommand ApproveCommand { get; }

    /// <summary>Rejects the pending change.</summary>
    public RelayCommand RejectCommand { get; }

    /// <summary>Commits the working tree by hand (workplan task 42).</summary>
    public RelayCommand CommitCommand { get; }

    /// <summary>Pushes the current branch by hand. Still asks for approval.</summary>
    public RelayCommand PushCommand { get; }

    /// <summary>Whether the git controls are shown at all - that is, whether git is enabled.</summary>
    public bool GitAvailable => _git is not null;

    /// <summary>Message for a manual commit.</summary>
    public string CommitMessage
    {
        get => _commitMessage;
        set => SetProperty(ref _commitMessage, value);
    }

    /// <summary>What the last manual git action did.</summary>
    public string GitStatus
    {
        get => _gitStatus;
        private set => SetProperty(ref _gitStatus, value);
    }

    /// <summary>Whether a manual git action is in flight.</summary>
    public bool IsGitBusy
    {
        get => _isGitBusy;
        private set => SetProperty(ref _isGitBusy, value);
    }

    /// <summary>
    /// Whether the agent is mid-run, set by the shell. Committing halfway through a run would
    /// record a tree the agent has not finished changing, so the buttons stand down until it is.
    /// </summary>
    public bool IsAgentRunning
    {
        get => _isAgentRunning;
        set => SetProperty(ref _isAgentRunning, value);
    }

    private bool CanRunGit => _git is not null && !IsGitBusy && !IsAgentRunning;

    /// <summary>Called by the approval gate when a change needs a decision.</summary>
    public Task<ApprovalDecision> RequestApprovalAsync(CodeChange change, CancellationToken cancellationToken)
    {
        PendingApproval pending = new(change);

        _dispatcher.BeginInvoke(() =>
        {
            Pending = pending;
            Selected = Changes.FirstOrDefault(c => c.Id == change.Id);
        });

        return Await(pending, cancellationToken);
    }

    /// <summary>
    /// Called by the approval gate when an action - a push, later a PR - needs a decision. Same
    /// strip, same buttons, same timeout-is-refusal contract; only the shape on display differs.
    /// </summary>
    public Task<ApprovalDecision> RequestApprovalAsync(AgentAction action, CancellationToken cancellationToken)
    {
        PendingApproval pending = new(action);
        _dispatcher.BeginInvoke(() => Pending = pending);
        return Await(pending, cancellationToken);
    }

    private static Task<ApprovalDecision> Await(PendingApproval pending, CancellationToken cancellationToken)
    {
        cancellationToken.Register(() => pending.Completion.TrySetResult(
            ApprovalDecision.Reject("The run was cancelled while waiting for approval.")));

        return pending.Completion.Task;
    }

    private async Task CommitAsync()
    {
        string message = CommitMessage;
        bool ok = await RunGitActionAsync(
            "git_commit",
            new Dictionary<string, object?> { ["message"] = message, ["stageAll"] = true },
            token => _git!.CommitAsync(message, stageAll: true, token)).ConfigureAwait(true);

        if (ok)
        {
            CommitMessage = string.Empty;
        }
    }

    private Task PushAsync() =>
        RunGitActionAsync("git_push", new Dictionary<string, object?>(), token => _git!.PushAsync(token));

    /// <summary>
    /// Runs one manual git action: the same tool method the model calls, so the guardrails
    /// cannot diverge between the two paths - a manual push still asks the approval gate.
    /// </summary>
    private async Task<bool> RunGitActionAsync<TData>(
        string tool,
        IReadOnlyDictionary<string, object?> arguments,
        Func<CancellationToken, Task<ToolObservation<TData>>> action)
    {
        IsGitBusy = true;
        GitStatus = "Working…";
        long start = Stopwatch.GetTimestamp();

        try
        {
            ToolObservation<TData> observation = await action(CancellationToken.None).ConfigureAwait(true);
            GitStatus = observation.Summary ?? (observation.Ok ? "Done." : "Failed.");
            Record(tool, arguments, observation.Ok, observation.Summary, observation.Error?.Message,
                Stopwatch.GetElapsedTime(start));
            return observation.Ok;
        }
        catch (Exception ex)
        {
            // A tool that throws is a defect, but the button must not take the window with it.
            GitStatus = $"Failed: {ex.Message}";
            Record(tool, arguments, false, null, ex.Message, Stopwatch.GetElapsedTime(start));
            return false;
        }
        finally
        {
            IsGitBusy = false;
        }
    }

    /// <summary>
    /// Puts a button-initiated action into the transcript as its own step, so the record of what
    /// happened to this repository stays whole whether the model or a person pressed it. The role
    /// is <c>human</c> and there is no run id outside a run, which is what distinguishes these
    /// from the loop's own steps rather than letting them masquerade as model work.
    /// </summary>
    private void Record(
        string tool,
        IReadOnlyDictionary<string, object?> arguments,
        bool ok,
        string? summary,
        string? error,
        TimeSpan duration)
    {
        if (_steps is null)
        {
            return;
        }

        RunContext context = RunContext.Current;
        _steps.LogStep(new StepRecord
        {
            RunId = context.RunId,
            TaskId = context.TaskId,
            StepIndex = _transcript?.NextStepIndex(context.RunId) ?? 0,
            Role = "human",
            StartedAt = DateTimeOffset.UtcNow,
            Prompt = [],
            ToolCalls =
            [
                new ToolCallRecord(
                    Guid.NewGuid().ToString("n")[..12],
                    tool,
                    arguments,
                    ok ? "Succeeded" : "Failed",
                    Parsed: true,
                    duration.TotalMilliseconds,
                    summary,
                    error),
            ],
            ModelLatencyMs = 0,
            StepLatencyMs = duration.TotalMilliseconds,
            Outcome = ok ? "manual" : "manual-failed",
        });
    }

    private void Decide(bool approved)
    {
        PendingApproval? pending = Pending;
        if (pending is null)
        {
            return;
        }

        pending.Completion.TrySetResult(approved
            ? ApprovalDecision.Approve()
            : ApprovalDecision.Reject(pending.RejectionReason));

        Pending = null;
    }

    private void OnChanged(object? sender, CodeChange change) =>
        _dispatcher.BeginInvoke(() =>
        {
            ChangeRowViewModel? existing = Changes.FirstOrDefault(c => c.Id == change.Id);
            if (existing is null)
            {
                Changes.Add(new ChangeRowViewModel(change));
                return;
            }

            existing.Change = change;
            if (Selected?.Id == change.Id)
            {
                OnPropertyChanged(nameof(Selected));
            }
        });

    /// <summary>A change or an action waiting on a decision - exactly one of the two.</summary>
    public sealed class PendingApproval
    {
        /// <summary>A pending file change, shown as its diff.</summary>
        public PendingApproval(CodeChange change)
        {
            Change = change;
            Path = change.Path;
            Explanation = "This change has passed verification and is waiting to be written.";
            Diff = change.Diff();
            RejectionReason = "A reviewer rejected this change.";
        }

        /// <summary>A pending action, its detail lines shown where the diff would be.</summary>
        public PendingApproval(AgentAction action)
        {
            Action = action;
            Path = action.Title;
            Explanation = "This action leaves the machine and cannot be unwound by the change log.";
            Diff = [.. action.Detail.Select(line => new DiffLine(DiffKind.Context, line, null, null))];
            RejectionReason = "A reviewer declined the action.";
        }

        /// <summary>The change, when a change is pending.</summary>
        public CodeChange? Change { get; }

        /// <summary>The action, when an action is pending.</summary>
        public AgentAction? Action { get; }

        /// <summary>Strip headline: the file for a change, the title for an action.</summary>
        public string Path { get; }

        /// <summary>One line under the headline saying what a decision means here.</summary>
        public string Explanation { get; }

        /// <summary>What the strip renders: a real diff, or detail lines dressed as context.</summary>
        public IReadOnlyList<DiffLine> Diff { get; }

        /// <summary>The reason handed back when the reviewer says no.</summary>
        public string RejectionReason { get; }

        /// <summary>Completed when the user decides.</summary>
        public TaskCompletionSource<ApprovalDecision> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
