using System.Collections.Concurrent;

namespace GlassCoder.Tools.Changes;

/// <summary>Where a change has got to (CLAUDE.md §10).</summary>
public enum ChangeStatus
{
    /// <summary>Computed but not yet written.</summary>
    Proposed,

    /// <summary>Written to the working tree.</summary>
    Applied,

    /// <summary>Refused - by a verification rung, or by a human.</summary>
    Rejected,

    /// <summary>Written and then undone.</summary>
    Reverted,
}

/// <summary>One change the agent made or wanted to make.</summary>
public sealed record CodeChange
{
    /// <summary>Identifier for this change.</summary>
    public required string Id { get; init; }

    /// <summary>Run the change belongs to.</summary>
    public required string RunId { get; init; }

    /// <summary>Task the change belongs to, so the per-task change log can be assembled.</summary>
    public required string TaskId { get; init; }

    /// <summary>Repo-relative file.</summary>
    public required string Path { get; init; }

    /// <summary>Tool that produced it.</summary>
    public required string Tool { get; init; }

    /// <summary>File content before.</summary>
    public required string BeforeText { get; init; }

    /// <summary>File content after.</summary>
    public required string AfterText { get; init; }

    /// <summary>When it was proposed.</summary>
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>Where it has got to.</summary>
    public ChangeStatus Status { get; init; } = ChangeStatus.Proposed;

    /// <summary>Why it was rejected or reverted, when it was.</summary>
    public string? Note { get; init; }

    /// <summary>
    /// The compile or test result produced by this change, tying an outcome to the edit that
    /// caused it (CLAUDE.md §10).
    /// </summary>
    public string? VerificationSummary { get; init; }

    /// <summary>The diff, computed on demand.</summary>
    public IReadOnlyList<DiffLine> Diff() => TextDiff.Compute(BeforeText, AfterText);

    /// <summary>1-based line range the change touches.</summary>
    public (int Start, int End)? Range() => TextDiff.ChangedRange(Diff());
}

/// <summary>
/// The per-task change log (CLAUDE.md §10, workplan task 27).
/// <para>
/// Every proposed change is recorded <em>before</em> it is applied, so a change that was refused
/// is as visible as one that landed. A UI that only ever sees successful writes cannot show a
/// user what the agent tried to do.
/// </para>
/// </summary>
public interface IChangeLog
{
    /// <summary>Records a proposed change and returns it.</summary>
    CodeChange Propose(string path, string tool, string beforeText, string afterText);

    /// <summary>Moves a change to a new status.</summary>
    CodeChange? Update(string id, ChangeStatus status, string? note = null, string? verificationSummary = null);

    /// <summary>All changes, newest last.</summary>
    IReadOnlyList<CodeChange> All();

    /// <summary>Changes belonging to one task.</summary>
    IReadOnlyList<CodeChange> ForTask(string taskId);

    /// <summary>Raised whenever a change is added or its status moves.</summary>
    event EventHandler<CodeChange>? Changed;
}

/// <summary>In-memory <see cref="IChangeLog"/>, ordered by proposal.</summary>
public sealed class ChangeLog : IChangeLog
{
    private readonly ConcurrentDictionary<string, CodeChange> _changes = new(StringComparer.Ordinal);
    private readonly List<string> _order = [];
    private readonly Lock _gate = new();
    private readonly TimeProvider _time;

    /// <summary>Creates the log.</summary>
    public ChangeLog(TimeProvider? timeProvider = null) => _time = timeProvider ?? TimeProvider.System;

    /// <inheritdoc />
    public event EventHandler<CodeChange>? Changed;

    /// <inheritdoc />
    public CodeChange Propose(string path, string tool, string beforeText, string afterText)
    {
        RunContext context = RunContext.Current;
        CodeChange change = new()
        {
            Id = Guid.NewGuid().ToString("n")[..12],
            RunId = context.RunId,
            TaskId = context.TaskId,
            Path = path,
            Tool = tool,
            BeforeText = beforeText,
            AfterText = afterText,
            CreatedAt = _time.GetUtcNow(),
            Status = ChangeStatus.Proposed,
        };

        lock (_gate)
        {
            _changes[change.Id] = change;
            _order.Add(change.Id);
        }

        Changed?.Invoke(this, change);
        return change;
    }

    /// <inheritdoc />
    public CodeChange? Update(string id, ChangeStatus status, string? note = null, string? verificationSummary = null)
    {
        if (!_changes.TryGetValue(id, out CodeChange? existing))
        {
            return null;
        }

        CodeChange updated = existing with
        {
            Status = status,
            Note = note ?? existing.Note,
            VerificationSummary = verificationSummary ?? existing.VerificationSummary,
        };

        _changes[id] = updated;
        Changed?.Invoke(this, updated);
        return updated;
    }

    /// <inheritdoc />
    public IReadOnlyList<CodeChange> All()
    {
        lock (_gate)
        {
            return [.. _order.Select(id => _changes[id])];
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<CodeChange> ForTask(string taskId) =>
        [.. All().Where(c => string.Equals(c.TaskId, taskId, StringComparison.Ordinal))];
}

/// <summary>
/// Ambient identity of the run currently executing on this async flow.
/// <para>
/// Tools should not need a run id threaded through every signature to say which run their work
/// belongs to, and a tool contract that carries harness bookkeeping is a worse contract.
/// </para>
/// </summary>
public sealed record RunContext(string RunId, string TaskId)
{
    private static readonly AsyncLocal<RunContext?> Ambient = new();

    /// <summary>The current run, or a placeholder outside one.</summary>
    public static RunContext Current => Ambient.Value ?? new RunContext("no-run", "no-task");

    /// <summary>Sets the current run for this async flow and everything it starts.</summary>
    public static void Set(RunContext context) => Ambient.Value = context;

    /// <summary>Clears the current run.</summary>
    public static void Clear() => Ambient.Value = null;
}
