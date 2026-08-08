using GlassCoder.TestSupport;
using GlassCoder.Tools.Retrieval;

namespace GlassCoder.Tools.Tests;

/// <summary>
/// Record, replay, and the one property the Lab depends on (workplan task 56): two runs of the
/// same arm, days apart, ask the same question and get byte-identical answers - and a run that
/// has no recording says so loudly instead of quietly reaching the network.
/// </summary>
public sealed class RetrievalCacheTests : IDisposable
{
    private readonly TempWorkspace _workspace = new();

    private string Root => Path.Combine(_workspace.Root, "corpus");

    public void Dispose() => _workspace.Dispose();

    [Fact]
    public void A_replay_miss_fails_loudly_and_never_calls_out()
    {
        CountingUpstream upstream = new("should not be reached");
        CachingRetrievalUpstream caching = new(upstream, new RetrievalCache(Root));

        RetrievalResult result = Call(caching, RetrievalMode.Replay);

        result.Ok.ShouldBeFalse();
        result.Code.ShouldBe(ToolErrorCodes.RetrievalCacheMiss);
        result.Message.ShouldContain("Record");
        upstream.Calls.ShouldBe(0, "Replay is the mode that promises the network is not touched");
    }

    [Fact]
    public void Record_calls_out_once_and_replay_serves_it_for_ever()
    {
        CountingUpstream upstream = new("the answer");

        Call(new CachingRetrievalUpstream(upstream, new RetrievalCache(Root)), RetrievalMode.Record)
            .Payload.ShouldBe("the answer");
        upstream.Calls.ShouldBe(1);

        // A second process, a second corpus reader, the same disk - which is what an arm run
        // days later actually is.
        CountingUpstream later = new("a different answer entirely");
        RetrievalResult replayed = Call(
            new CachingRetrievalUpstream(later, new RetrievalCache(Root)), RetrievalMode.Replay);

        replayed.Ok.ShouldBeTrue();
        replayed.Payload.ShouldBe("the answer", "the corpus answers, not the world");
        later.Calls.ShouldBe(0);
    }

    /// <summary>
    /// The property the ablation rests on, stated as a test: same arm, same corpus, identical
    /// observations, however many times it runs.
    /// </summary>
    [Fact]
    public void Two_replay_runs_over_one_corpus_return_identical_bytes()
    {
        Call(new CachingRetrievalUpstream(new CountingUpstream("stable"), new RetrievalCache(Root)),
            RetrievalMode.Record);

        string?[] answers =
        [
            Call(new CachingRetrievalUpstream(new ThrowingUpstream(), new RetrievalCache(Root)), RetrievalMode.Replay).Payload,
            Call(new CachingRetrievalUpstream(new ThrowingUpstream(), new RetrievalCache(Root)), RetrievalMode.Replay).Payload,
        ];

        answers[0].ShouldBe(answers[1]);
        answers[0].ShouldBe("stable");
    }

    [Fact]
    public void Live_calls_out_every_time_and_records_nothing()
    {
        CountingUpstream upstream = new("fresh");
        CachingRetrievalUpstream caching = new(upstream, new RetrievalCache(Root));

        Call(caching, RetrievalMode.Live);
        upstream.Calls.ShouldBe(1);

        // Nothing was written, so a later Replay still misses - Live is for interactive work and
        // must not silently seed a corpus an arm would then read as deliberate.
        Call(new CachingRetrievalUpstream(new ThrowingUpstream(), new RetrievalCache(Root)), RetrievalMode.Replay)
            .Code.ShouldBe(ToolErrorCodes.RetrievalCacheMiss);
    }

    /// <summary>
    /// A cached timeout would replay for ever as a fact about the world rather than what it was:
    /// one bad minute on somebody's network.
    /// </summary>
    [Fact]
    public void A_failed_call_is_never_recorded()
    {
        FailingUpstream upstream = new();

        Call(new CachingRetrievalUpstream(upstream, new RetrievalCache(Root)), RetrievalMode.Record)
            .Code.ShouldBe(ToolErrorCodes.UpstreamUnavailable);

        Call(new CachingRetrievalUpstream(new ThrowingUpstream(), new RetrievalCache(Root)), RetrievalMode.Replay)
            .Code.ShouldBe(ToolErrorCodes.RetrievalCacheMiss);
    }

    /// <summary>
    /// Without normalisation a model that asks the same question in two spellings misses twice
    /// on one recording, and a Replay arm fails for a reason unrelated to the experiment.
    /// </summary>
    [Fact]
    public void Trivially_different_spellings_of_one_question_are_one_key()
    {
        RetrievalCacheKey plain = RetrievalCacheKey.From(
            RetrievalServer.Learn, "microsoft_docs_search",
            new Dictionary<string, object?> { ["query"] = "IAsyncEnumerable" });

        RetrievalCacheKey messy = RetrievalCacheKey.From(
            RetrievalServer.Learn, "microsoft_docs_search",
            new Dictionary<string, object?> { ["query"] = "  iasyncenumerable\t" });

        messy.Digest().ShouldBe(plain.Digest());
    }

    [Fact]
    public void Different_questions_are_different_keys()
    {
        string one = RetrievalCacheKey.From(RetrievalServer.Learn, "microsoft_docs_search",
            new Dictionary<string, object?> { ["query"] = "Array.Sort" }).Digest();

        string other = RetrievalCacheKey.From(RetrievalServer.Learn, "microsoft_docs_search",
            new Dictionary<string, object?> { ["query"] = "Array.SortedCopy" }).Digest();

        string otherServer = RetrievalCacheKey.From(RetrievalServer.GitHub, "microsoft_docs_search",
            new Dictionary<string, object?> { ["query"] = "Array.Sort" }).Digest();

        one.ShouldNotBe(other);
        one.ShouldNotBe(otherServer);
    }

    /// <summary>
    /// The key holds the server's own tool name, so renaming a tool locally - which task 55
    /// makes a configuration edit - does not invalidate a corpus recorded before the rename.
    /// </summary>
    [Fact]
    public void Renaming_a_tool_locally_does_not_invalidate_the_corpus()
    {
        RetrievalCache cache = new(Root);
        RetrievalCacheKey key = RetrievalCacheKey.From(
            RetrievalServer.Learn, "microsoft_docs_search",
            new Dictionary<string, object?> { ["query"] = "records" });

        cache.Put(key, "recorded under the server's name");

        cache.Get(key)!.Payload.ShouldBe("recorded under the server's name");
        cache.Get(key)!.ServerTool.ShouldBe("microsoft_docs_search");
    }

    [Fact]
    public void A_corrupt_entry_reads_as_a_miss_rather_than_a_crash()
    {
        RetrievalCache cache = new(Root);
        RetrievalCacheKey key = RetrievalCacheKey.From(
            RetrievalServer.Learn, "microsoft_docs_search",
            new Dictionary<string, object?> { ["query"] = "x" });

        cache.Put(key, "good");
        string file = Directory.EnumerateFiles(Root, "*.json", SearchOption.AllDirectories).Single();
        File.WriteAllText(file, "{ not json");

        cache.Get(key).ShouldBeNull();
    }

    /// <summary>
    /// The settings dialog writes the recorded tool list by hand, and the catalogue reads it at
    /// registration. Two spellings of that one key would be a corpus that reads back empty and a
    /// server whose tools never appear - which is exactly the failure an operator reported after
    /// switching retrieval on, so it is pinned rather than assumed.
    /// </summary>
    [Fact]
    public void What_the_dialog_records_is_what_registration_reads()
    {
        RetrievalCache cache = new(Root);
        RetrievalToolDescriptor[] advertised =
        [
            new("microsoft_docs_search", Schema("query")),
            new("microsoft_docs_fetch", Schema("url")),
        ];

        cache.Put(
            RetrievalCacheKey.From(RetrievalServer.Learn, RetrievalCatalog.ToolListKey, null),
            RetrievalCatalog.Serialize(advertised));

        // Replay, and no upstream at all: if this resolves, it resolved from disk.
        IReadOnlyList<RetrievalToolDescriptor> read =
            new RetrievalCatalog(cache, upstream: null).Describe(RetrievalServer.Learn, RetrievalMode.Replay);

        read.Select(d => d.ServerTool).ShouldBe(["microsoft_docs_search", "microsoft_docs_fetch"]);
        read[0].Schema.GetProperty("properties").TryGetProperty("query", out _).ShouldBeTrue();
    }

    private static System.Text.Json.JsonElement Schema(string parameter) => System.Text.Json.JsonDocument
        .Parse("{\"type\":\"object\",\"properties\":{\"" + parameter + "\":{\"type\":\"string\"}}}")
        .RootElement.Clone();

    private static RetrievalResult Call(CachingRetrievalUpstream caching, RetrievalMode mode) =>
        caching.CallAsync(
            mode,
            RetrievalServer.Learn,
            "microsoft_docs_search",
            new Dictionary<string, object?> { ["query"] = "IAsyncEnumerable" }).GetAwaiter().GetResult();

    private sealed class CountingUpstream(string payload) : IRetrievalUpstream
    {
        public int Calls { get; private set; }

        public Task<RetrievalResult> CallAsync(
            RetrievalServer server, string serverTool,
            IReadOnlyDictionary<string, object?> arguments, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(RetrievalResult.Answered(payload));
        }
    }

    private sealed class FailingUpstream : IRetrievalUpstream
    {
        public Task<RetrievalResult> CallAsync(
            RetrievalServer server, string serverTool,
            IReadOnlyDictionary<string, object?> arguments, CancellationToken cancellationToken = default) =>
            Task.FromResult(RetrievalResult.Failed(ToolErrorCodes.UpstreamUnavailable, "the server is down"));
    }

    /// <summary>Reaching this is the failure the test is checking for.</summary>
    private sealed class ThrowingUpstream : IRetrievalUpstream
    {
        public Task<RetrievalResult> CallAsync(
            RetrievalServer server, string serverTool,
            IReadOnlyDictionary<string, object?> arguments, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Replay reached the network.");
    }
}
