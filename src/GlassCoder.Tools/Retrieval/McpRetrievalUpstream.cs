using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace GlassCoder.Tools.Retrieval;

/// <summary>
/// Where a fresh MCP tool list comes from (workplan tasks 57 and 76).
/// <para>
/// Its own interface, small on purpose: the catalogue depends on this rather than on the MCP
/// client so that "registration never waits for a server" can be asserted against a server that
/// never answers - which is the only kind the property is about.
/// </para>
/// </summary>
public interface IRetrievalToolLister
{
    /// <summary>The tools a server advertises, with their schemas.</summary>
    /// <param name="server">Which configured server to ask.</param>
    /// <param name="cancellationToken">Cancels the listing.</param>
    Task<IReadOnlyList<RetrievalToolDescriptor>> ListToolsAsync(
        RetrievalServer server, CancellationToken cancellationToken = default);
}

/// <summary>
/// The real upstream: one MCP session per enabled server, opened on demand and closed with the
/// process (workplan task 57).
/// <para>
/// Sessions are a lifetime problem rather than a connection problem. A server left holding a
/// session after a cancelled run is a leak the operator never sees, so this is disposable and
/// the container owns it.
/// </para>
/// <para>
/// Nothing here throws into the loop. A dead server, a timeout, a refused token and a protocol
/// error all come back as <see cref="RetrievalResult.Failed"/> with
/// <see cref="ToolErrorCodes.UpstreamUnavailable"/>, because a tool failure is information the
/// agent acts on and never a reason for the run to end (CLAUDE.md §7).
/// </para>
/// </summary>
public sealed class McpRetrievalUpstream : IRetrievalUpstream, IRetrievalToolLister, IAsyncDisposable, IDisposable
{
    /// <summary>
    /// How long a synchronous dispose waits for sessions to close. Bounded because shutdown must
    /// not hang on a server that has stopped answering - the point of closing is to leave nothing
    /// behind, and a leaked session is a smaller failure than a window that will not shut.
    /// </summary>
    private static readonly TimeSpan CloseTimeout = TimeSpan.FromSeconds(5);

    private readonly IOptionsMonitor<RetrievalOptions> _options;
    private readonly ILogger<McpRetrievalUpstream> _logger;
    private readonly Dictionary<RetrievalServer, McpClient> _sessions = [];
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _disposed;

    /// <summary>Creates the upstream.</summary>
    public McpRetrievalUpstream(
        IOptionsMonitor<RetrievalOptions> options, ILogger<McpRetrievalUpstream>? logger = null)
    {
        _options = options;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<McpRetrievalUpstream>.Instance;
    }

    /// <summary>
    /// The tools a server advertises. Used at registration to learn their schemas, which is the
    /// one thing configuration cannot supply: a schema is the contract the executor on the other
    /// end enforces, so inventing one locally would be inventing a contract.
    /// </summary>
    public async Task<IReadOnlyList<RetrievalToolDescriptor>> ListToolsAsync(
        RetrievalServer server, CancellationToken cancellationToken = default)
    {
        try
        {
            McpClient client = await SessionAsync(server, cancellationToken).ConfigureAwait(false);
            IList<McpClientTool> tools = await client.ListToolsAsync(cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            return [.. tools.Select(tool => new RetrievalToolDescriptor(tool.Name, tool.JsonSchema))];
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Could not list tools on the {Server} MCP server", server);
            return [];
        }
    }

    /// <inheritdoc />
    public async Task<RetrievalResult> CallAsync(
        RetrievalServer server,
        string serverTool,
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken = default)
    {
        try
        {
            McpClient client = await SessionAsync(server, cancellationToken).ConfigureAwait(false);

            CallToolResult result = await client
                .CallToolAsync(serverTool, arguments, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            if (result.IsError == true)
            {
                return RetrievalResult.Failed(
                    ToolErrorCodes.UpstreamUnavailable,
                    $"{server} refused the call: {Flatten(result)}");
            }

            return RetrievalResult.Answered(Flatten(result));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "The {Server} MCP server did not answer {Tool}", server, serverTool);

            // The session may be the thing that broke; drop it so the next call reconnects
            // rather than failing for ever against a socket that closed an hour ago.
            await ForgetAsync(server).ConfigureAwait(false);
            return RetrievalResult.Failed(ToolErrorCodes.UpstreamUnavailable, ex.Message);
        }
    }

    /// <summary>
    /// Closes any open sessions, synchronously.
    /// <para>
    /// Present because the container disposes what it built, and a service that is only
    /// <see cref="IAsyncDisposable"/> makes a synchronous <c>provider.Dispose()</c> throw - which
    /// both hosts do. In Replay, the mode the Lab runs, nothing was ever connected and this is a
    /// no-op; the blocking wait exists for Record and Live, and is bounded.
    /// </para>
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        if (!DisposeAsync().AsTask().Wait(CloseTimeout))
        {
            _logger.LogWarning("An MCP session did not close within {Timeout}", CloseTimeout);
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // Under the gate, like every other reader of _sessions. Without it this enumerated the
        // dictionary while a connecting call inserted into it - "collection was modified" out of
        // provider.DisposeAsync(), or, on the other interleaving, a freshly opened session left
        // behind for the server to time out, which is the leak this class exists to prevent.
        List<McpClient> closing;
        try
        {
            await _gate.WaitAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        try
        {
            closing = [.. _sessions.Values];
            _sessions.Clear();
        }
        finally
        {
            _gate.Release();
        }

        foreach (McpClient client in closing)
        {
            try
            {
                await client.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "An MCP session did not close cleanly");
            }
        }

        // Disposed on every path, including the Replay one where no session was ever opened -
        // the early return there used to leak the semaphore.
        _gate.Dispose();
    }

    private async Task<McpClient> SessionAsync(RetrievalServer server, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Again, now that the gate is held: a caller that passed the check above and then
            // waited behind a disposal would otherwise connect a session nothing will ever close.
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (_sessions.TryGetValue(server, out McpClient? existing))
            {
                return existing;
            }

            RetrievalServerOptions settings = _options.CurrentValue.For(server);
            HttpClientTransportOptions transport = new()
            {
                Endpoint = new Uri(settings.Endpoint),
                TransportMode = HttpTransportMode.AutoDetect,
                Name = server.ToString(),
                ConnectionTimeout = TimeSpan.FromSeconds(30),
                AdditionalHeaders = Headers(settings),
            };

            McpClient client = await McpClient
                .CreateAsync(new HttpClientTransport(transport), cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            _logger.LogInformation(
                "Connected to the {Server} MCP server: {Name} {Version}",
                server, client.ServerInfo?.Name, client.ServerInfo?.Version);

            _sessions[server] = client;
            return client;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// The token, and the read-only request. The token comes from the environment rather than
    /// from any settings file: <c>.glasscoder.json</c> is meant to be committed, and a secret in
    /// a committed file is a secret that has left the machine.
    /// </summary>
    private static Dictionary<string, string> Headers(RetrievalServerOptions settings)
    {
        Dictionary<string, string> headers = new(StringComparer.Ordinal);

        if (!string.IsNullOrWhiteSpace(settings.ApiKeyEnvironmentVariable))
        {
            string? token = Environment.GetEnvironmentVariable(settings.ApiKeyEnvironmentVariable);
            if (!string.IsNullOrWhiteSpace(token))
            {
                headers["Authorization"] = $"Bearer {token}";
            }
        }

        if (settings.ReadOnly)
        {
            // Asked of the server as well as enforced by the allow-list. Belt and braces on
            // purpose: the allow-list is ours to get wrong, and this one is not.
            headers["X-MCP-Readonly"] = "true";
        }

        return headers;
    }

    private async Task ForgetAsync(RetrievalServer server)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_sessions.Remove(server, out McpClient? client))
            {
                try
                {
                    await client.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "A broken MCP session did not close cleanly");
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// The content blocks flattened to the text the model will read. Non-text blocks are named
    /// rather than dropped: a silently empty answer reads as "the server knows nothing", which
    /// is a different and much more misleading claim than "the server sent an image".
    /// </summary>
    private static string Flatten(CallToolResult result)
    {
        if (result.Content is not { Count: > 0 })
        {
            return string.Empty;
        }

        return string.Join(
            Environment.NewLine,
            result.Content.Select(block => block switch
            {
                TextContentBlock text => text.Text,
                _ => $"[{block.Type} content omitted]",
            }));
    }
}

/// <summary>
/// Resolves the tools each enabled server advertises, without insisting on a network
/// (workplan tasks 56, 57).
/// <para>
/// The tool list is an exchange like any other, so it is recorded like any other. That is what
/// lets a Replay arm register its tools from the corpus and reach no network at all - not
/// during a call, and not at startup either. Without it, the mode that promises hermeticity
/// would still have opened a socket before the first step.
/// </para>
/// </summary>
public sealed class RetrievalCatalog
{
    /// <summary>
    /// The corpus key under which a server's advertised tools are recorded. Public because the
    /// settings dialog writes the same entry when an operator records the lists by hand, and two
    /// spellings of one key would be a corpus that reads back empty.
    /// </summary>
    public const string ToolListKey = "__tools__";

    /// <summary>
    /// How long registration will wait for a server to list its tools. Shorter than the
    /// transport's own connection timeout on purpose: this wait happens on the UI thread, and a
    /// tool list is not worth a frozen window.
    /// </summary>
    private static readonly TimeSpan ListTimeout = TimeSpan.FromSeconds(10);

    private static readonly JsonSerializerOptions Serializer = new() { WriteIndented = true };

    /// <summary>Renders descriptors for the corpus, in the shape <see cref="Describe"/> reads.</summary>
    public static string Serialize(IEnumerable<RetrievalToolDescriptor> descriptors)
    {
        ArgumentNullException.ThrowIfNull(descriptors);
        return JsonSerializer.Serialize(descriptors.Select(Persistable), Serializer);
    }

    private readonly IRetrievalCache _cache;
    private readonly IRetrievalToolLister? _upstream;
    private readonly ILogger _logger;

    /// <summary>Servers with a background list in flight, so one server is never asked twice at once.</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<RetrievalServer, bool> _refreshing = new();

    /// <summary>Creates the catalogue. A null upstream is Replay-only by construction.</summary>
    /// <param name="cache">The corpus, which is what registration actually reads.</param>
    /// <param name="upstream">
    /// Where a fresh tool list comes from. An interface rather than the concrete client so the
    /// no-waiting property of workplan task 76 can be asserted against a server that never
    /// answers, which is the only kind that matters here.
    /// </param>
    /// <param name="logger">Where an unreachable server is reported.</param>
    public RetrievalCatalog(IRetrievalCache cache, IRetrievalToolLister? upstream, ILogger? logger = null)
    {
        _cache = cache;
        _upstream = upstream;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
    }

    /// <summary>The tools <paramref name="server"/> advertises, from the corpus or from the wire.</summary>
    public IReadOnlyList<RetrievalToolDescriptor> Describe(RetrievalServer server, RetrievalMode mode)
    {
        RetrievalCacheKey key = RetrievalCacheKey.From(server, ToolListKey, null);

        if (mode == RetrievalMode.Replay || _upstream is null)
        {
            // Replay's only source, and it must never connect.
            if (_cache.Get(key) is { } recorded &&
                Deserialize(recorded.Payload) is { Count: > 0 } fromCorpus)
            {
                return fromCorpus;
            }

            _logger.LogWarning(
                "No recorded tool list for the {Server} MCP server, and Replay does not connect. " +
                "Its tools are not registered this run; record a corpus with Retrieval:Mode=Record.",
                server);
            return [];
        }

        // Nothing here connects (workplan task 76). This runs inside the DI factory for
        // IToolRegistry, which the desktop resolves on the UI thread while the shell is being
        // built - so a server that is slow, unreachable or behind a captive portal used to hold
        // the window closed for as long as the bound allowed, at startup, which is exactly when a
        // first-run operator is watching. Bounding an unbounded hang turned it into a shorter
        // hang; it was never a fix.
        //
        // The decision, of the three the task offered: **start without it and register late.**
        // This run gets whatever is in the corpus - possibly nothing, said out loud - and a
        // background refresh writes the corpus so the next run has it. Preferring the recording
        // forever was the trap the old comment warned about; preferring it *while refreshing* is
        // not, because the refresh is what breaks the loop.
        IReadOnlyList<RetrievalToolDescriptor> known =
            _cache.Get(key) is { } stored ? Deserialize(stored.Payload) : [];

        Refresh(server, key);

        if (known.Count == 0)
        {
            _logger.LogInformation(
                "No recorded tool list for the {Server} MCP server yet. Its tools are not registered " +
                "this run; the list is being fetched in the background and will be available next run.",
                server);
        }

        return known;
    }

    /// <summary>
    /// Asks the server for its tools without anyone waiting, and writes what comes back.
    /// <para>
    /// One in flight per server: the registry is built once per run, but a long-lived process that
    /// rebuilt it would otherwise stack connections against a server that is already not answering.
    /// Failures are logged and nothing else - the whole point is that this cannot make a caller
    /// wait, and it equally must not make one fail.
    /// </para>
    /// </summary>
    private void Refresh(RetrievalServer server, RetrievalCacheKey key)
    {
        if (_upstream is null || !_refreshing.TryAdd(server, true))
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                IReadOnlyList<RetrievalToolDescriptor> live =
                    await _upstream.ListToolsAsync(server).WaitAsync(ListTimeout).ConfigureAwait(false);

                if (live.Count > 0)
                {
                    // Written in Live as well as Record. The corpus is what the next run reads, so
                    // a Live arm that never wrote one would never learn a renamed tool either.
                    _cache.Put(key, Serialize(live));
                    _logger.LogInformation(
                        "Recorded {Count} tool(s) from the {Server} MCP server for the next run", live.Count, server);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "The {Server} MCP server did not list its tools within {Timeout}. Its tools stay " +
                    "unregistered; switch it off in settings, or use Retrieval:Mode=Replay against a " +
                    "recorded corpus.",
                    server, ListTimeout);
            }
            finally
            {
                _refreshing.TryRemove(server, out _);
            }
        });
    }

    private static PersistedDescriptor Persistable(RetrievalToolDescriptor descriptor) =>
        new(descriptor.ServerTool, descriptor.Schema.GetRawText());

    private static IReadOnlyList<RetrievalToolDescriptor> Deserialize(string payload)
    {
        try
        {
            List<PersistedDescriptor>? persisted =
                JsonSerializer.Deserialize<List<PersistedDescriptor>>(payload, Serializer);

            return persisted is null
                ? []
                : [.. persisted.Select(p => new RetrievalToolDescriptor(
                    p.ServerTool, JsonDocument.Parse(p.Schema).RootElement.Clone()))];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>A descriptor as it sits on disk - the schema as text, so it round-trips exactly.</summary>
    private sealed record PersistedDescriptor(string ServerTool, string Schema);
}
