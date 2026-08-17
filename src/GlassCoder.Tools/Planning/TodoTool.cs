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
    private readonly AdvisoryNotices _notices;
    private readonly ILogger<TodoTool> _logger;

    /// <summary>Creates the tool.</summary>
    public TodoTool(ITodoList todos, ILogger<TodoTool>? logger = null, AdvisoryNotices? notices = null)
    {
        _todos = todos;
        _notices = notices ?? new AdvisoryNotices();
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

        // Struck before anything counts them, so the plan the run carries, the counts it reports
        // and the transcript's rendering all agree that the item is gone.
        List<string> struck =
        [
            .. cleaned.Where(i => LadderTitles.Contains(i.Title.Trim())).Select(i => i.Title.Trim()),
        ];
        cleaned.RemoveAll(i => LadderTitles.Contains(i.Title.Trim()));

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

        // On the register, so a notice nobody acts on is countable rather than decorative. Keyed on
        // the subject - the title struck, the plan being complete - never on the sentence. A call
        // with nothing to say clears the entry, which is what "answered" means here: the model
        // stopped re-adding the item, or stopped calling a finished plan finished.
        _notices.Observe($"{ToolName} (ladder item)", struck.Count > 0 ? string.Join(", ", struck) : null);
        _notices.Observe(
            $"{ToolName} (plan complete)",
            cleaned.Count > 0 && completed == cleaned.Count ? "the plan is complete, the goal is not proved" : null);

        _logger.LogInformation("Plan updated: {Completed}/{Total} complete", completed, cleaned.Count);
        return Observation.Ok(
            ToolName,
            result,
            $"Plan updated: {completed}/{cleaned.Count} complete." +
            FinishedPlanNotice(completed, cleaned.Count) +
            LadderDuplicateNotice(struck));
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
    /// Says which items were struck for restating the ladder, once per call.
    /// <para>
    /// This used to name the item and leave it in the plan, on the argument that a refusal spends a
    /// step on a rewrite. Run <c>31983adb</c> is the first live test of that trade and it lost: the
    /// same sentence came back verbatim at steps 0, 3, 6, 10 and 14, the item was never edited, and
    /// at steps 15 and 16 the agent discharged it literally by re-running verification that had
    /// passed automatically since step 2. Five bookkeeping steps plus two redundant verification
    /// steps - a third of a 21-step run - against the one rewrite step the naming was protecting.
    /// </para>
    /// <para>
    /// Striking is the third option neither half of that trade had: no refusal, so no step is spent
    /// on a rewrite, and the item cannot be discharged later because it is no longer there. The
    /// plan is still the agent's own - this removes exactly the items whose whole title is the
    /// ladder's job, and says so.
    /// </para>
    /// </summary>
    private static string LadderDuplicateNotice(List<string> struck) =>
        struck.Count == 0
            ? string.Empty
            : $" {string.Join(", ", struck.Select(title => $"'{title}'"))} restates the automatic " +
              "verification, which runs after every applied change, so it was struck from the plan " +
              "rather than carried - there is nothing in it for you to do.";

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
