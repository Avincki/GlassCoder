using System.Collections.Concurrent;
using System.Globalization;
using GlassCoder.Tools.Changes;

namespace GlassCoder.Tools.Registry;

/// <summary>
/// Notices that rode on a successful result, said the same thing about the same subject over and
/// over, and changed nothing.
/// <para>
/// Every consequence mechanism in this harness is keyed to failure: the suite notice into
/// <c>RunProgressSentry</c>, <see cref="AbandonedIntents"/> on refused calls, the identical-failure
/// counter, the test-outcome counter re-doored on 2026-08-17. A notice attached to an <c>[ok]</c>
/// result had no counter at all, and run <c>31983adb</c> is what that costs: six true, load-bearing
/// notices fired across twenty-one steps - the ladder-restating plan item five times, the clipping
/// warning, the change-log pointer, the plan-complete nudge, the at-rest launch caveat - and not one
/// of them changed behaviour or reached the run record. The harness's facts were all correct and its
/// consequences were all optional.
/// </para>
/// <para>
/// The generalisation this closes is the one HISTORY wrote down on 2026-08-15 - <em>a mechanism
/// keyed to the shape of the run that revealed it goes blind the moment the same fact arrives by a
/// different door.</em> Here the door is simply <strong>success</strong>.
/// </para>
/// <para>
/// A notice, never a gate. This repository has paid twice for gates that would not concede
/// (<c>5c071f37</c>, <c>a408b61b</c>), and a repeated advisory is weaker evidence than a red tree:
/// it says the run may be ignoring something, which the model is entitled to have decided. One line
/// to the completion panel and one on the run record, exactly as the refusal ledger beside it.
/// </para>
/// </summary>
public sealed class AdvisoryNotices
{
    /// <summary>
    /// Consecutive emissions about one unchanged subject before the notice counts as unanswered.
    /// Three, for the reason the identical-failure counter uses three: the first is information,
    /// the second is a coincidence, the third is a pattern.
    /// </summary>
    private const int UnansweredAfter = 3;

    private readonly ConcurrentDictionary<string, Entry> _notices = new(StringComparer.Ordinal);

    /// <summary>
    /// Records what one notice-bearing call had to say.
    /// <para>
    /// <paramref name="subject"/> is what the notice is about - a plan item's title, a file path -
    /// and never the sentence itself: prose that synthesis writes must not be prose that detection
    /// keys on, which is the rule the failure counter follows for the same reason. A null or empty
    /// subject means <em>this call had nothing to say</em>, and clears the entry: a notice is
    /// answered when the source stops raising it, the same contract the suite notice uses.
    /// </para>
    /// </summary>
    /// <param name="source">Which organ spoke - the tool name, narrowed by clause where it has more than one.</param>
    /// <param name="subject">What it spoke about, or null when it had nothing to say this time.</param>
    public void Observe(string source, string? subject)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);

        string key = $"{RunContext.Current.RunId}|{source}";
        if (string.IsNullOrWhiteSpace(subject))
        {
            _notices.TryRemove(key, out _);
            return;
        }

        _notices.AddOrUpdate(
            key,
            new Entry(source, subject, 1),
            (_, seen) => string.Equals(seen.Subject, subject, StringComparison.Ordinal)
                ? seen with { Count = seen.Count + 1 }

                // A different subject is a different notice, not a continuation of this one.
                : new Entry(source, subject, 1));
    }

    /// <summary>
    /// One line naming every notice this run raised repeatedly and never answered, or null when
    /// there is nothing to say.
    /// </summary>
    public string? Summary()
    {
        string prefix = $"{RunContext.Current.RunId}|";
        List<Entry> unanswered =
        [
            .. _notices
                .Where(pair => pair.Key.StartsWith(prefix, StringComparison.Ordinal))
                .Select(pair => pair.Value)
                .Where(entry => entry.Count >= UnansweredAfter)
                .OrderByDescending(entry => entry.Count)
                .ThenBy(entry => entry.Source, StringComparer.Ordinal),
        ];

        if (unanswered.Count == 0)
        {
            return null;
        }

        string what = string.Join(
            ", ",
            unanswered.Select(entry => string.Create(
                CultureInfo.InvariantCulture,
                $"{entry.Source} on '{entry.Subject}' ({entry.Count} times)")));

        return $"Raised on every call and never answered in this run: {what}.";
    }

    private sealed record Entry(string Source, string Subject, int Count);
}
