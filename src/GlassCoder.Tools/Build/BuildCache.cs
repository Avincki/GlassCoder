using GlassCoder.Tools.Changes;
using Microsoft.Extensions.Logging;

namespace GlassCoder.Tools.Build;

/// <summary>
/// Remembers the last successful build and test run of each target, and forgets everything the
/// moment the tree changes.
/// <para>
/// The waste this exists for is an agent building the same unchanged tree several steps running
/// - three consecutive builds with no edit between them, each costing ten to thirty seconds and
/// a step of a finite budget. The agent does that because it has no way to know the answer it
/// already has is still good. This gives it one.
/// </para>
/// <para>
/// Test runs joined on the same terms (workplan task 74). Run <c>d5edbc59</c> spent steps 19, 20,
/// 25 and 26 re-establishing greens that steps 17 and 24 had already reported inline - four of
/// twenty-eight steps re-confirming the one axis that was never in doubt, in a run where the axis
/// that <em>was</em> refuted got none.
/// </para>
/// <para>
/// Only successes are cached. A failed build is the observation the agent is acting on, and
/// replaying a stale failure could leave it fixing something it already fixed - whereas a stale
/// success is impossible, because anything that could change the answer invalidates the entry.
/// A test run that ran zero tests is not cached either: it is not a success, it is the absence of
/// one, and replaying it would put "nothing was verified" behind a cache hit.
/// </para>
/// </summary>
public sealed class BuildCache
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, BuildResult> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, TestRunResult> _tests = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ChangeStatus> _statuses = new(StringComparer.Ordinal);
    private readonly ILogger<BuildCache> _logger;

    /// <summary>Creates the cache and subscribes it to the change log.</summary>
    /// <param name="changes">
    /// The change log. Every proposal and every status <em>move</em> invalidates, which is
    /// deliberately pessimistic: a rejected write cannot change the build, but distinguishing that
    /// from one that can is not worth being wrong about.
    /// <para>
    /// A change re-announced at the status it already had is the exception, and it is the reason
    /// this cache had never once been read in production. <c>IChangeLog.Update</c> raises
    /// <c>Changed</c> for any write to a change, including a pure bookkeeping one - and
    /// <c>AgentLoop</c> ends every verified step by writing the ladder's summary back onto each
    /// applied change at its existing status. That arrives immediately after the ladder's own
    /// Compile and UnitTests rungs have filled this, so the cache was emptied a few milliseconds
    /// after being populated, every step, all day. Two runs on 2026-08-09 recorded zero hits
    /// between them.
    /// </para>
    /// <para>
    /// The filter is on the transition rather than on the status, so nothing about which statuses
    /// touch the tree has to be assumed - a re-assertion moved no bytes whatever it says, and
    /// everything else invalidates exactly as before.
    /// </para>
    /// </param>
    /// <param name="logger">Where hits are reported, so a suspiciously fast build is explainable.</param>
    public BuildCache(IChangeLog? changes = null, ILogger<BuildCache>? logger = null)
    {
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<BuildCache>.Instance;

        if (changes is not null)
        {
            changes.Changed += (_, change) => OnChanged(change);
        }
    }

    /// <summary>
    /// Invalidates unless this change is being re-announced at the status it already carried.
    /// </summary>
    private void OnChanged(CodeChange change)
    {
        if (change is null)
        {
            return;
        }

        lock (_gate)
        {
            if (_statuses.TryGetValue(change.Id, out ChangeStatus seen) && seen == change.Status)
            {
                return;
            }

            _statuses[change.Id] = change.Status;
        }

        Invalidate();
    }

    /// <summary>How many times the cache has been emptied. Useful in tests and traces.</summary>
    public int Generation { get; private set; }

    /// <summary>The cached result for a target, when the tree has not moved since.</summary>
    public bool TryGet(string target, bool allowRestore, out BuildResult? result)
    {
        lock (_gate)
        {
            return _entries.TryGetValue(Key(target, allowRestore), out result);
        }
    }

    /// <summary>Remembers a successful build. Failures are ignored on purpose.</summary>
    public void Set(string target, bool allowRestore, BuildResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        if (!result.Succeeded)
        {
            return;
        }

        lock (_gate)
        {
            _entries[Key(target, allowRestore)] = result;
        }
    }

    /// <summary>The cached test run for a target and filter, when the tree has not moved since.</summary>
    public bool TryGetTests(string target, string? filter, out TestRunResult? result)
    {
        lock (_gate)
        {
            return _tests.TryGetValue(Key(target, filter), out result);
        }
    }

    /// <summary>
    /// Remembers a green test run. A red one is the observation the agent is acting on, and a run
    /// that executed nothing is not a result worth replaying.
    /// </summary>
    public void SetTests(string target, string? filter, TestRunResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        if (!result.Ok || result.Total == 0)
        {
            return;
        }

        lock (_gate)
        {
            _tests[Key(target, filter)] = result;
        }
    }

    /// <summary>
    /// Forgets everything. Called from the change log, and by hand from any tool that changes
    /// the tree without going through it - editing a project file through <c>dotnet</c>, say.
    /// </summary>
    public void Invalidate()
    {
        lock (_gate)
        {
            if (_entries.Count == 0 && _tests.Count == 0)
            {
                return;
            }

            _entries.Clear();
            _tests.Clear();
            Generation++;
        }

        _logger.LogDebug("Build cache invalidated (generation {Generation})", Generation);
    }

    private static string Key(string target, bool allowRestore) => $"{target}|{allowRestore}";

    // A filter narrows what ran, so "all tests passed" for one filter says nothing about another.
    private static string Key(string target, string? filter) => $"{target}|{filter ?? string.Empty}";
}
