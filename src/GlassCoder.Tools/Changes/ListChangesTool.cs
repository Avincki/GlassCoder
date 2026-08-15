using System.ComponentModel;
using GlassCoder.Tools.Registry;

namespace GlassCoder.Tools.Changes;

/// <summary>One file this run has changed.</summary>
/// <param name="Path">Repo-relative file.</param>
/// <param name="LinesAdded">Net lines added across this run's changes to it.</param>
/// <param name="LinesRemoved">Net lines removed.</param>
/// <param name="Status">Where its most recent change got to.</param>
/// <param name="Tools">Which tools touched it.</param>
public sealed record ChangedFile(
    [property: Description("Repo-relative file.")] string Path,
    [property: Description("Net lines added across this run.")] int LinesAdded,
    [property: Description("Net lines removed across this run.")] int LinesRemoved,
    [property: Description("Status of its most recent change: Proposed, Applied, Rejected or Reverted.")] string Status,
    [property: Description("Tools that changed it.")] IReadOnlyList<string> Tools);

/// <summary>Result payload of <c>list_changes</c>.</summary>
/// <param name="Files">Every file this run has changed, most recently touched first.</param>
/// <param name="Applied">How many changes were written.</param>
/// <param name="Rejected">How many were refused, by verification or by a human.</param>
public sealed record ListChangesResult(
    [property: Description("Files this run has changed.")] IReadOnlyList<ChangedFile> Files,
    [property: Description("Number of changes written to the working tree.")] int Applied,
    [property: Description("Number of changes refused by verification or by a human.")] int Rejected);

/// <summary>
/// <c>list_changes</c> - what this run has already done (workplan task 50).
/// <para>
/// The change log has known every file a run touched, what it looked like before, and whether the
/// write stuck. None of it was reachable by the agent, which re-read files it had edited four
/// steps earlier to work out what it had done to them.
/// </para>
/// <para>
/// The justification is the one the todo list already established (task 24): a plan is durable
/// and visible <em>because</em> it survives context compaction. The change log has exactly that
/// property. Once a run is long enough to compact, the transcript stops being a reliable record
/// of the run's own edits and this does not.
/// </para>
/// </summary>
public sealed class ListChangesTool : IToolSet
{
    private const string ToolName = "list_changes";

    private readonly IChangeLog _changes;

    /// <summary>Creates the tool.</summary>
    public ListChangesTool(IChangeLog changes) => _changes = changes;

    /// <summary>Lists the files this run has changed.</summary>
    [GlassCoderTool(ToolName, Order = 6)]
    // Same cut, same reason: the line counts and the written/refused mark arrive in the result.
    // What stays is the only part that changes when the model would call it.
    [Description("List the files this run has changed. Still correct after the conversation is compacted.")]
    public ToolObservation<ListChangesResult> ListChanges()
    {
        string runId = RunContext.Current.RunId;

        List<CodeChange> mine =
            [.. _changes.All().Where(c => string.Equals(c.RunId, runId, StringComparison.Ordinal))];

        if (mine.Count == 0)
        {
            return Observation.Ok(
                ToolName,
                new ListChangesResult([], 0, 0),
                "This run has not changed anything yet.");
        }

        // The rollup is the same one the workspace pane draws, so the tool and the UI cannot
        // disagree about what the run did.
        IReadOnlyDictionary<string, FileChangeStats> stats = FileChangeSummary.Summarise(mine);

        List<ChangedFile> files = [];
        foreach (IGrouping<string, CodeChange> group in mine
            .GroupBy(c => c.Path, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Last().Id, StringComparer.Ordinal))
        {
            FileChangeStats counts = stats.TryGetValue(group.Key, out FileChangeStats found)
                ? found
                : default;

            files.Add(new ChangedFile(
                group.Key,
                counts.LinesAdded,
                counts.LinesRemoved,
                group.Last().Status.ToString(),
                [.. group.Select(c => c.Tool).Distinct(StringComparer.Ordinal)]));
        }

        int applied = mine.Count(c => c.Status == ChangeStatus.Applied);
        int rejected = mine.Count(c => c.Status == ChangeStatus.Rejected);

        return Observation.Ok(
            ToolName,
            new ListChangesResult(files, applied, rejected),
            $"{files.Count} file(s) changed this run: {applied} change(s) written, {rejected} refused.");
    }
}
