using GlassCoder.Tools.Changes;

namespace GlassCoder.Tools.Retrieval;

/// <summary>What one call to an MCP server returned, or why it did not.</summary>
/// <param name="Ok">Whether the upstream answered.</param>
/// <param name="Payload">The answer, when there is one.</param>
/// <param name="Code">A <see cref="ToolErrorCodes"/> value when there is not.</param>
/// <param name="Message">What went wrong, for the observation.</param>
public sealed record RetrievalResult(bool Ok, string? Payload, string? Code = null, string? Message = null)
{
    /// <summary>An answer.</summary>
    public static RetrievalResult Answered(string payload) => new(true, payload);

    /// <summary>A failure, as an observation rather than an exception.</summary>
    public static RetrievalResult Failed(string code, string message) => new(false, null, code, message);
}

/// <summary>
/// The seam a retrieval call crosses to reach a server. Faked in tests; implemented over the
/// MCP client in task 57.
/// </summary>
public interface IRetrievalUpstream
{
    /// <summary>Calls one tool on one server.</summary>
    Task<RetrievalResult> CallAsync(
        RetrievalServer server,
        string serverTool,
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Applies <see cref="RetrievalMode"/> over an upstream and a corpus (workplan task 56).
/// <para>
/// Every retrieval call in the harness goes through here, which is the point: there is no path
/// from a tool to a network that does not first ask what mode this run is in. In
/// <see cref="RetrievalMode.Replay"/> the upstream is never touched at all - a miss is a loud
/// failure, because a cache that quietly reaches the network gives non-reproducible runs
/// <em>and</em> the belief that they are reproducible, which is worse than having no cache.
/// </para>
/// </summary>
public sealed class CachingRetrievalUpstream
{
    /// <summary>How many runs' worth of within-run answers to keep. Bounded, like the policy's.</summary>
    private const int MaximumTrackedRuns = 64;

    private readonly IRetrievalUpstream _upstream;
    private readonly IRetrievalCache _cache;
    private readonly Lock _gate = new();

    /// <summary>
    /// Answers already given in this run, so asking twice costs once.
    /// <para>
    /// This is what the durable corpus used to be doing, and doing wrongly. Consulting the corpus
    /// in every mode meant <see cref="RetrievalMode.Live"/> - documented as "call out without
    /// recording" - silently served a page captured weeks earlier, with no way to refresh short
    /// of deleting files by hand, and <see cref="RetrievalMode.Record"/> could never replace a
    /// stale entry it had recorded itself. Deduplication is a within-run concern, so it lives in
    /// within-run memory and expires with the process.
    /// </para>
    /// </summary>
    private readonly Dictionary<(string RunId, RetrievalCacheKey Key), string> _thisRun = [];

    private readonly List<(string RunId, RetrievalCacheKey Key)> _order = [];

    /// <summary>Creates the mode-aware wrapper.</summary>
    public CachingRetrievalUpstream(IRetrievalUpstream upstream, IRetrievalCache cache)
    {
        _upstream = upstream;
        _cache = cache;
    }

    /// <summary>Serves one call under <paramref name="mode"/>.</summary>
    public async Task<RetrievalResult> CallAsync(
        RetrievalMode mode,
        RetrievalServer server,
        string serverTool,
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken = default)
    {
        RetrievalCacheKey key = RetrievalCacheKey.From(server, serverTool, arguments);

        if (Remembered(key) is { } answered)
        {
            return RetrievalResult.Answered(answered);
        }

        if (mode == RetrievalMode.Replay)
        {
            // The corpus is the only source Replay has, and a miss is a loud failure: a cache
            // that quietly reached the network would give non-reproducible runs and the belief
            // that they are reproducible, which is worse than having no cache.
            return _cache.Get(key) is { } recorded
                ? RetrievalResult.Answered(recorded.Payload)
                : RetrievalResult.Failed(
                    ToolErrorCodes.RetrievalCacheMiss,
                    $"No recorded answer for {serverTool} with these arguments, and Replay never calls out. " +
                    "Record the corpus with Retrieval:Mode=Record before running this arm.");
        }

        RetrievalResult result = await _upstream
            .CallAsync(server, serverTool, arguments, cancellationToken)
            .ConfigureAwait(false);

        if (result is { Ok: true, Payload: not null })
        {
            Remember(key, result.Payload);

            // Recorded rather than merely kept: Record is the documented way to refresh a corpus,
            // and it can only be that if it overwrites what is already there.
            //
            // Failures are never recorded. A cached timeout would replay for ever as a fact about
            // the world rather than what it was: one bad minute on somebody's network.
            if (mode == RetrievalMode.Record)
            {
                _cache.Put(key, result.Payload);
            }
        }

        return result;
    }

    private string? Remembered(RetrievalCacheKey key)
    {
        lock (_gate)
        {
            return _thisRun.GetValueOrDefault((RunContext.Current.RunId, key));
        }
    }

    private void Remember(RetrievalCacheKey key, string payload)
    {
        lock (_gate)
        {
            (string, RetrievalCacheKey) slot = (RunContext.Current.RunId, key);
            if (!_thisRun.ContainsKey(slot))
            {
                if (_order.Count >= MaximumTrackedRuns)
                {
                    _thisRun.Remove(_order[0]);
                    _order.RemoveAt(0);
                }

                _order.Add(slot);
            }

            _thisRun[slot] = payload;
        }
    }
}
