using System.Collections.Concurrent;
using System.Globalization;
using GlassCoder.Tools.Changes;

namespace GlassCoder.Tools.Registry;

/// <summary>
/// What the run tried to do, was refused, and never came back to.
/// <para>
/// Step 2 of run <c>dd11ef7c</c> called <c>dotnet_project new_solution</c> at the repository root
/// and was refused with a complete, correct, actionable hint - a filename, a tool name and an
/// ordering. The model read it, wrote nothing down, and in twenty more steps never tried again.
/// The repository shipped with no solution at the root, <c>dotnet test</c> from the root answers
/// MSB1003, and the run's own green was gathered through a path the product does not have.
/// </para>
/// <para>
/// Nothing in the harness held that intent. Step 19's identically-shaped refusal was repaired
/// instantly, because its fix belonged to the very next call; step 2's did not, and a message
/// reaches exactly one step. <c>HISTORY.md</c> named this on 2026-08-09 - <em>every message this
/// harness has built worked; none of them had anywhere to be recorded as outstanding</em> - and
/// closed it for suite notices only. This is the same closure for refusals, which are the instance
/// where it is cheapest: the harness knows what was wanted, knows it did not happen, and can see
/// whether it ever happened afterwards.
/// </para>
/// <para>
/// A notice, never a gate (the contract task 66 set). It is one line to the completion panel and
/// one line on the run record; nothing here can stop a run.
/// </para>
/// </summary>
public sealed class AbandonedIntents
{
    private readonly ConcurrentDictionary<string, Entry> _outstanding = new(StringComparer.Ordinal);

    /// <summary>
    /// Records what one tool call did.
    /// <para>
    /// A refusal opens an entry; a later success on the same key closes it, which is what keeps
    /// step 19's refuse-then-repair out of the report entirely. The same refusal twice is still
    /// one outstanding intent - the report counts things not done, not times they failed.
    /// </para>
    /// </summary>
    /// <param name="tool">Tool name as the model called it.</param>
    /// <param name="operation">
    /// The operation argument where the tool has one, so <c>dotnet_project new_solution</c> and
    /// <c>dotnet_project add_reference</c> are two intents rather than one. Null where it has none.
    /// </param>
    /// <param name="succeeded">Whether the call did what it set out to do.</param>
    /// <param name="step">The step it happened on, for a report a reader can go and look at.</param>
    public void Observe(string tool, string? operation, bool succeeded, int step)
    {
        ArgumentNullException.ThrowIfNull(tool);

        string key = Key(tool, operation);
        if (succeeded)
        {
            _outstanding.TryRemove(key, out _);
            return;
        }

        _outstanding.TryAdd(key, new Entry(Describe(tool, operation), step));
    }

    /// <summary>
    /// One line naming everything asked for and never achieved, or null when there is nothing to
    /// say. Ordered by the step it was first refused on, so the reader meets them as the run did.
    /// </summary>
    public string? Summary()
    {
        string prefix = $"{RunContext.Current.RunId}|";
        List<Entry> outstanding =
        [
            .. _outstanding
                .Where(pair => pair.Key.StartsWith(prefix, StringComparison.Ordinal))
                .Select(pair => pair.Value)
                .OrderBy(entry => entry.Step),
        ];

        if (outstanding.Count == 0)
        {
            return null;
        }

        string what = string.Join(
            ", ",
            outstanding.Select(entry => string.Create(CultureInfo.InvariantCulture, $"{entry.What} (step {entry.Step})")));

        return $"Refused and never retried in this run: {what}.";
    }

    private static string Describe(string tool, string? operation) =>
        string.IsNullOrWhiteSpace(operation) ? tool : $"{tool} {operation}";

    private static string Key(string tool, string? operation) =>
        $"{RunContext.Current.RunId}|{Describe(tool, operation)}";

    private sealed record Entry(string What, int Step);
}
