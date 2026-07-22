using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using GlassCoder.Tools.Changes;
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
    private ChangeRowViewModel? _selected;
    private PendingApproval? _pending;

    /// <summary>Creates the view model and subscribes to the change log.</summary>
    public ChangesViewModel(IChangeLog changes, Dispatcher? dispatcher = null)
    {
        _changes = changes;
        _dispatcher = dispatcher ?? Dispatcher.CurrentDispatcher;

        foreach (CodeChange change in changes.All())
        {
            Changes.Add(new ChangeRowViewModel(change));
        }

        _changes.Changed += OnChanged;

        ApproveCommand = new RelayCommand(() => Decide(true), () => Pending is not null);
        RejectCommand = new RelayCommand(() => Decide(false), () => Pending is not null);
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

    /// <summary>Called by the approval gate when a change needs a decision.</summary>
    public Task<ApprovalDecision> RequestApprovalAsync(CodeChange change, CancellationToken cancellationToken)
    {
        PendingApproval pending = new(change);

        _dispatcher.BeginInvoke(() =>
        {
            Pending = pending;
            Selected = Changes.FirstOrDefault(c => c.Id == change.Id);
        });

        cancellationToken.Register(() => pending.Completion.TrySetResult(
            ApprovalDecision.Reject("The run was cancelled while waiting for approval.")));

        return pending.Completion.Task;
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
            : ApprovalDecision.Reject("A reviewer rejected this change."));

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

    /// <summary>A change waiting on a decision.</summary>
    public sealed class PendingApproval
    {
        /// <summary>Creates the pending approval.</summary>
        public PendingApproval(CodeChange change) => Change = change;

        /// <summary>The change.</summary>
        public CodeChange Change { get; }

        /// <summary>Repo-relative path.</summary>
        public string Path => Change.Path;

        /// <summary>The diff awaiting a decision.</summary>
        public IReadOnlyList<DiffLine> Diff => Change.Diff();

        /// <summary>Completed when the user decides.</summary>
        public TaskCompletionSource<ApprovalDecision> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
