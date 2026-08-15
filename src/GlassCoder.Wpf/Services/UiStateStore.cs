using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security;
using Microsoft.Win32;

namespace GlassCoder.Wpf.Services;

/// <summary>
/// Small pieces of desktop state that survive a restart - the goals of recent runs, so a repeated
/// test run is a press of Run rather than a paste, and an earlier prompt is a pick from a list
/// rather than a retype.
/// <para>
/// Deliberately <em>not</em> the user settings store. Everything saved there feeds
/// <c>IConfiguration</c>, and the provenance stamp hashes the effective configuration so that a
/// run's arm is identifiable (<c>ProvenanceStamp.ConfigHash</c>) - state that changes with every
/// run would relabel every arm and make no two runs comparable. UI state therefore lives where
/// configuration never looks.
/// </para>
/// </summary>
public interface IUiStateStore
{
    /// <summary>
    /// The goals recent runs were started with, most recent first, empty when none has been saved.
    /// </summary>
    IReadOnlyList<string> RecentGoals { get; }

    /// <summary>
    /// Records a goal as the most recent one. A goal that is already remembered moves to the
    /// front rather than appearing twice, and the oldest fall off the end past
    /// <see cref="PromptHistory.Capacity"/>.
    /// </summary>
    void RememberGoal(string goal);
}

/// <summary>
/// The shape of the remembered list, kept apart from any one store so the rule - newest first, no
/// duplicates, bounded - is one function rather than a habit each implementation repeats.
/// </summary>
public static class PromptHistory
{
    /// <summary>
    /// How many goals are kept. Twenty is a dropdown you can still read to the bottom of; a
    /// history longer than that is an archive, and an archive wants search rather than a list.
    /// </summary>
    public const int Capacity = 20;

    /// <summary>
    /// The remembered list with <paramref name="goal"/> at its front.
    /// <para>
    /// Ordinal equality, not a trimmed or case-insensitive one: two prompts that differ only in
    /// whitespace are two prompts to the model, so collapsing them here would hand the operator a
    /// list entry that is not what ran.
    /// </para>
    /// </summary>
    public static IReadOnlyList<string> With(IEnumerable<string> remembered, string goal)
    {
        ArgumentNullException.ThrowIfNull(remembered);

        IEnumerable<string> kept = remembered.Where(g => !string.IsNullOrWhiteSpace(g));
        if (string.IsNullOrWhiteSpace(goal))
        {
            // A run that never started has nothing worth remembering, and must not push a real
            // prompt off the end of the list either.
            return [.. kept.Take(Capacity)];
        }

        List<string> history = [goal];
        history.AddRange(kept.Where(g => !string.Equals(g, goal, StringComparison.Ordinal)));
        return history.Count <= Capacity ? history : history.GetRange(0, Capacity);
    }

    /// <summary>
    /// One line standing for a whole prompt, for a dropdown row.
    /// <para>
    /// A goal is a five-line box's worth of text and every newline in it would make the row
    /// taller, so the line breaks become spaces. Nothing is truncated here - the row trims to its
    /// own width in the view, where the width is actually known.
    /// </para>
    /// </summary>
    public static string Summarize(string goal)
    {
        ArgumentNullException.ThrowIfNull(goal);

        return string.Join(' ', goal.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }
}

/// <summary>
/// The Windows implementation, under <c>HKCU\Software\GlassCoder</c>.
/// <para>
/// Every failure path returns the absence of a convenience rather than an error: a machine
/// where the key cannot be read starts with an empty goal box and an empty history, exactly like
/// a first-ever start, and a save that fails loses nothing but the pre-fill.
/// </para>
/// </summary>
public sealed class RegistryUiStateStore : IUiStateStore
{
    private const string KeyPath = @"Software\GlassCoder";
    private const string RecentGoalsName = "RecentGoals";

    /// <summary>What the single-slot version of this store wrote, read once and then retired.</summary>
    private const string LastGoalName = "LastGoal";

    /// <inheritdoc />
    public IReadOnlyList<string> RecentGoals
    {
        get
        {
            try
            {
                using RegistryKey? key = Registry.CurrentUser.OpenSubKey(KeyPath);
                if (key?.GetValue(RecentGoalsName) is string[] stored)
                {
                    return [.. stored.Where(g => !string.IsNullOrWhiteSpace(g)).Take(PromptHistory.Capacity)];
                }

                // Upgrading from the build that remembered one goal: its prompt is the history.
                return key?.GetValue(LastGoalName) is string last && !string.IsNullOrWhiteSpace(last)
                    ? [last]
                    : [];
            }
            catch (Exception ex) when (ex is SecurityException or IOException or UnauthorizedAccessException)
            {
                return [];
            }
        }
    }

    /// <inheritdoc />
    public void RememberGoal(string goal)
    {
        if (string.IsNullOrWhiteSpace(goal))
        {
            return;
        }

        IReadOnlyList<string> history = PromptHistory.With(RecentGoals, goal);

        try
        {
            using RegistryKey key = Registry.CurrentUser.CreateSubKey(KeyPath);
            key.SetValue(RecentGoalsName, history.ToArray(), RegistryValueKind.MultiString);

            // The single-slot value is now stale the moment this list moves on, and it is only
            // ever read when the list is missing. Retire it rather than leave a second answer to
            // the same question lying next to the first.
            key.DeleteValue(LastGoalName, throwOnMissingValue: false);
        }
        catch (Exception ex) when (ex is SecurityException or IOException or UnauthorizedAccessException
                                       or ArgumentException)
        {
            // Losing the pre-fill is not worth interrupting a run over. ArgumentException is in
            // the list because REG_MULTI_SZ cannot hold an embedded NUL, and a pasted prompt is
            // not ours to vet.
        }
    }
}
