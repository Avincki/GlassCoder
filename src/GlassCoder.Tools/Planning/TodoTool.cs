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
    // "Break a multi-step task down before starting" paid for the clause below. Decomposition is
    // what a model does with a plan tool unprompted; planning the work the ladder already does is
    // what it does not stop doing - run e426f418 closed with "Build and run tests" while every
    // applied change from step 2 had been compiled and tested on its own observation.
    [Description("Record or update your plan. Send the complete list every time - it replaces the previous "
        + "plan. Keep exactly one item InProgress, and mark items Completed as you finish them. Do not plan "
        + "a build or test step - applied changes are verified for you. Plan what verification cannot see: "
        + "a launch, a probe, behaviour in a running window.")]
    public ToolObservation<TodoResult> UpdateTodos(
        [Description("The complete plan, every item.")]
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
        return Observation.Ok(
            ToolName,
            result,
            $"Plan updated: {completed}/{cleaned.Count} complete." +
            FinishedPlanNotice(completed, cleaned.Count) +
            LadderDuplicateNotice(cleaned));
    }

    /// <summary>
    /// What a finished plan is and is not.
    /// <para>
    /// On run <c>e426f418</c> the <c>5/5</c> line was the last thing the agent read before claiming
    /// the goal at the next step, and the plan's closing item had been "Build and run tests" - work
    /// the ladder had already done after every applied change since step 2. A plan is the agent's
    /// own decomposition; finishing it says the decomposition is finished, which is a different
    /// claim from the one the critics are about to judge.
    /// </para>
    /// <para>
    /// A sentence, not a refusal and not a gate: the tool takes the plan it was given, and the
    /// panel remains the judge of the goal.
    /// </para>
    /// </summary>
    private static string FinishedPlanNotice(int completed, int total) =>
        total > 0 && completed == total
            ? " The plan is complete; that is not evidence the goal is met. Cite what the automatic " +
              "verification showed, and finish on evidence it could not see."
            : string.Empty;

    /// <summary>
    /// Names a plan item that only restates the ladder, once per call.
    /// <para>
    /// The cheap half of the schema clause, for a model that writes the closer anyway - which is
    /// what happened at step 0 of run <c>e426f418</c>, under a system prompt that already told it
    /// not to make the call. Naming beats refusing: a refusal spends a step on a rewrite, which is
    /// the waste this exists to prevent.
    /// </para>
    /// </summary>
    private static string LadderDuplicateNotice(List<TodoItem> items)
    {
        List<string> duplicates =
        [
            .. items.Where(i => LadderTitles.Contains(i.Title.Trim())).Select(i => $"'{i.Title.Trim()}'"),
        ];

        return duplicates.Count == 0
            ? string.Empty
            : $" {string.Join(", ", duplicates)} restates the automatic verification, which runs after " +
              "every applied change - the plan does not need it.";
    }

    /// <summary>
    /// Titles that name the ladder's own job. Whole titles only, case-insensitively: an item called
    /// "build the settings dialog" is real work, and matching on the word would call it a duplicate.
    /// </summary>
    private static readonly HashSet<string> LadderTitles = new(StringComparer.OrdinalIgnoreCase)
    {
        "build",
        "build and test",
        "build and run tests",
        "build and verify",
        "final build",
        "final test",
        "run tests",
        "run_tests",
        "run the tests",
        "test",
        "tests",
        "verify",
        "verify all",
        "verify everything",
    };
}
