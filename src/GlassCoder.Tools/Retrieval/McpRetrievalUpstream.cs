using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace GlassCoder.Tools.Retrieval;

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
public sealed class McpRetrievalUpstream : IRetrievalUpstream, IAsyncDisposable, IDisposable
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
        if (_disposed || _sessions.Count == 0)
        {
            _disposed = true;
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

        foreach (McpClient client in _sessions.Values)
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

        _sessions.Clear();
        _gate.Dispose();
    }

    private async Task<McpClient> SessionAsync(RetrievalServer server, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
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

    private static readonly JsonSerializerOptions Serializer = new() { WriteIndented = true };

    /// <summary>Renders descriptors for the corpus, in the shape <see cref="Describe"/> reads.</summary>
    public static string Serialize(IEnumerable<RetrievalToolDescriptor> descriptors)
    {
        ArgumentNullException.ThrowIfNull(descriptors);
        return JsonSerializer.Serialize(descriptors.Select(Persistable), Serializer);
    }

    private readonly IRetrievalCache _cache;
    private readonly McpRetrievalUpstream? _upstream;
    private readonly ILogger _logger;

    /// <summary>Creates the catalogue. A null upstream is Replay-only by construction.</summary>
    public RetrievalCatalog(IRetrievalCache cache, McpRetrievalUpstream? upstream, ILogger? logger = null)
    {
        _cache = cache;
        _upstream = upstream;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
    }

    /// <summary>The tools <paramref name="server"/> advertises, from the corpus or from the wire.</summary>
    public IReadOnlyList<RetrievalToolDescriptor> Describe(RetrievalServer server, RetrievalMode mode)
    {
        RetrievalCacheKey key = RetrievalCacheKey.From(server, ToolListKey, null);

        if (_cache.Get(key) is { } recorded &&
            Deserialize(recorded.Payload) is { Count: > 0 } fromCorpus)
        {
            return fromCorpus;
        }

        if (mode == RetrievalMode.Replay || _upstream is null)
        {
            _logger.LogWarning(
                "No recorded tool list for the {Server} MCP server, and Replay does not connect. " +
                "Its tools are not registered this run; record a corpus with Retrieval:Mode=Record.",
                server);
            return [];
        }

        // Record and Live are opt-in, interactive modes, so a bounded blocking connect at
        // registration is acceptable where it would not be in Replay.
        IReadOnlyList<RetrievalToolDescriptor> live = _upstream
            .ListToolsAsync(server)
            .GetAwaiter()
            .GetResult();

        if (live.Count > 0 && mode == RetrievalMode.Record)
        {
            _cache.Put(key, Serialize(live));
        }

        return live;
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
