using System.ComponentModel;
using GlassCoder.Tools.Registry;
using Microsoft.Extensions.Logging;

namespace GlassCoder.Tools.Planning;

/// <summary>Result payload of <c>update_todos</c>.</summary>
/// <param name="Items">The plan as it now stands.</param>
/// <param name="Pending">How many items are still to do.</param>
/// <param name="Completed">How many are finished.</param>
public sealed record TodoResult(
    [property: Description("The plan as it now stands.")] IReadOnlyList<TodoItem> Items,
    [property: Description("Number of items not yet finished.")] int Pending,
    [property: Description("Number of items finished.")] int Completed);

/// <summary>
/// <c>update_todos</c> - the agent's own plan (workplan task 24).
/// <para>
/// Whole-list replacement rather than per-item mutation: it takes one call to restate the plan,
/// it cannot drift out of sync with what the agent believes, and the transcript then shows the
/// plan as it stood at every step rather than a stream of deltas to reconstruct.
/// </para>
/// </summary>
public sealed class TodoTool : IToolSet
{
    private const string ToolName = "update_todos";

    private readonly ITodoList _todos;
    private readonly ILogger<TodoTool> _logger;

    /// <summary>Creates the tool.</summary>
    public TodoTool(ITodoList todos, ILogger<TodoTool>? logger = null)
    {
        _todos = todos;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<TodoTool>.Instance;
    }

    /// <summary>Replaces the plan.</summary>
    [GlassCoderTool(ToolName, Order = 5)]
    [Description("Record or update your plan for this task. Send the complete list every time - it replaces "
        + "the previous plan. Break a multi-step task down before starting it, mark exactly one item "
        + "InProgress while you work on it, and mark items Completed as you finish them.")]
    public ToolObservation<TodoResult> UpdateTodos(
        [Description("The complete plan: every item, each with an id, a title and a status.")]
        IReadOnlyList<TodoItem> items)
    {
        if (items is null)
        {
            return Observation.Fail<TodoResult>(ToolName, ToolErrorCodes.InvalidArgument, "items is required.");
        }

        List<TodoItem> cleaned = [];
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);

        foreach (TodoItem item in items)
        {
            if (item is null || string.IsNullOrWhiteSpace(item.Id) || string.IsNullOrWhiteSpace(item.Title))
            {
                return Observation.Fail<TodoResult>(
                    ToolName,
                    ToolErrorCodes.InvalidArgument,
                    "Every item needs a non-empty id and title.");
            }

            if (!seen.Add(item.Id))
            {
                return Observation.Fail<TodoResult>(
                    ToolName,
                    ToolErrorCodes.InvalidArgument,
                    $"Item id '{item.Id}' appears more than once.");
            }

            cleaned.Add(item);
        }

        int inProgress = cleaned.Count(i => i.Status == TodoStatus.InProgress);
        if (inProgress > 1)
        {
            return Observation.Fail<TodoResult>(
                ToolName,
                ToolErrorCodes.InvalidArgument,
                $"{inProgress} items are InProgress. Work on one thing at a time.",
                "Mark the others Pending.");
        }

        _todos.Replace(cleaned);

        int completed = cleaned.Count(i => i.Status == TodoStatus.Completed);
        TodoResult result = new(cleaned, cleaned.Count - completed, completed);

        _logger.LogInformation("Plan updated: {Completed}/{Total} complete", completed, cleaned.Count);
        return Observation.Ok(ToolName, result, $"Plan updated: {completed}/{cleaned.Count} complete.");
    }
}
