using System.ComponentModel;
using System.Text.Json.Serialization;

namespace GlassCoder.Tools.Planning;

/// <summary>State of one planned step.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<TodoStatus>))]
public enum TodoStatus
{
    /// <summary>Not started.</summary>
    Pending,

    /// <summary>Being worked on now. Exactly one item should be here at a time.</summary>
    InProgress,

    /// <summary>Finished.</summary>
    Completed,
}

/// <summary>One item on the agent's plan.</summary>
/// <param name="Id">Stable identifier the agent chooses.</param>
/// <param name="Title">What the step is.</param>
/// <param name="Status">Where it has got to.</param>
public sealed record TodoItem(
    [property: Description("Stable short identifier for this item, for example 'find-bug'.")] string Id,
    [property: Description("What this step is, in a few words.")] string Title,
    [property: Description("One of: Pending, InProgress, Completed.")] TodoStatus Status = TodoStatus.Pending);

/// <summary>
/// The agent-maintained plan (CLAUDE.md §3.2 subsystem 4, workplan task 24).
/// <para>
/// Decomposition lives in the agent's own words rather than in harness logic, because the
/// harness does not know what the task decomposes into. What the harness contributes is that
/// the plan is <em>durable and visible</em>: it survives context compaction, it appears in the
/// transcript, and a run that quietly abandoned half its plan can be seen doing so afterwards.
/// </para>
/// </summary>
public interface ITodoList
{
    /// <summary>The current plan, in the order the agent last supplied.</summary>
    IReadOnlyList<TodoItem> Items { get; }

    /// <summary>Replaces the whole plan.</summary>
    void Replace(IEnumerable<TodoItem> items);

    /// <summary>Clears the plan, for the start of a run.</summary>
    void Clear();

    /// <summary>Raised whenever the plan changes, so the UI can follow along.</summary>
    event EventHandler<IReadOnlyList<TodoItem>>? Changed;
}

/// <summary>In-memory <see cref="ITodoList"/>. One plan per process, replaced per run.</summary>
public sealed class TodoList : ITodoList
{
    private readonly Lock _gate = new();
    private List<TodoItem> _items = [];

    /// <inheritdoc />
    public IReadOnlyList<TodoItem> Items
    {
        get
        {
            lock (_gate)
            {
                return [.. _items];
            }
        }
    }

    /// <inheritdoc />
    public event EventHandler<IReadOnlyList<TodoItem>>? Changed;

    /// <inheritdoc />
    public void Replace(IEnumerable<TodoItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);

        IReadOnlyList<TodoItem> snapshot;
        lock (_gate)
        {
            _items = [.. items];
            snapshot = [.. _items];
        }

        Changed?.Invoke(this, snapshot);
    }

    /// <inheritdoc />
    public void Clear() => Replace([]);
}
