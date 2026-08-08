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
    private readonly IRetrievalUpstream _upstream;
    private readonly IRetrievalCache _cache;

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

        // A recording answers in every mode. Replay is not the only mode that benefits: a run
        // that asks the same question twice should not pay for it twice, and an arm that
        // re-asks should not drift because the answer moved between two steps of one run.
        if (_cache.Get(key) is { } recorded)
        {
            return RetrievalResult.Answered(recorded.Payload);
        }

        if (mode == RetrievalMode.Replay)
        {
            return RetrievalResult.Failed(
                ToolErrorCodes.RetrievalCacheMiss,
                $"No recorded answer for {serverTool} with these arguments, and Replay never calls out. " +
                "Record the corpus with Retrieval:Mode=Record before running this arm.");
        }

        RetrievalResult result = await _upstream
            .CallAsync(server, serverTool, arguments, cancellationToken)
            .ConfigureAwait(false);

        // Failures are never recorded. A cached timeout would replay for ever as a fact about
        // the world rather than what it was: one bad minute on somebody's network.
        if (result is { Ok: true, Payload: not null } && mode == RetrievalMode.Record)
        {
            _cache.Put(key, result.Payload);
        }

        return result;
    }
}
