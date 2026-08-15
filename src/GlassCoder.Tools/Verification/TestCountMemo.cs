using System.Collections.Concurrent;
using GlassCoder.Tools.Changes;

namespace GlassCoder.Tools.Verification;

/// <summary>
/// How many tests the last climb over the same target ran, per run.
/// <para>
/// A passing count is the one signal that cannot distinguish a test added from no test added. Run
/// <c>29356042</c> is the shape: step 16 said it would add a test for the UI behaviour, step 17
/// applied a refactor and no test, the rung answered <em>7 tests passed</em> - the same seven as
/// step 13 - and step 18 offered "UI integration" as a leg of the suite's adequacy. Two
/// three-critic panels accepted it, because they were grading the application, which worked, and
/// not the summary, which was false.
/// </para>
/// <para>
/// This is the fifth axis of a defect the repository has already closed on four others -
/// <c>passed (0 tests)</c>, <c>BuildCache</c>'s <c>reused</c>, the memoised launch's <c>reused</c>,
/// and <c>CritiqueHistory.EvidenceUnchanged</c>. The generalisation each of them is an instance of:
/// <strong>every green should say what moved since the last one.</strong>
/// </para>
/// <para>
/// Not <see cref="Build.BuildCache"/>, which cannot serve this: an applied change invalidates that
/// cache, and an applied change is precisely when the comparison matters. Keyed by run, like the
/// read memo and the refusal tracker beside it.
/// </para>
/// </summary>
public sealed class TestCountMemo
{
    private readonly ConcurrentDictionary<string, int> _counts = new(StringComparer.Ordinal);

    /// <summary>
    /// Records how many tests this target just ran, and reports whether that is the number it ran
    /// last time.
    /// <para>
    /// The count, never the conclusion. A rewritten test keeps the number and the harness cannot
    /// tell the difference, so what it says is what it knows - and the reader draws the inference
    /// the count supports.
    /// </para>
    /// </summary>
    /// <param name="target">The project, solution or directory the tests ran against.</param>
    /// <param name="filter">The filter they ran under, since a narrower run is a different count.</param>
    /// <param name="total">How many tests ran.</param>
    /// <returns>True when a previous climb over the same target ran the same number.</returns>
    public bool Observe(string? target, string? filter, int total)
    {
        string key = $"{RunContext.Current.RunId}|{target}|{filter}";
        bool unchanged = _counts.TryGetValue(key, out int previous) && previous == total;
        _counts[key] = total;
        return unchanged;
    }
}
