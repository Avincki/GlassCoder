using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace GlassCoder.TestSupport;

/// <summary>
/// A minimal Anthropic-style <c>/v1/messages</c> endpoint, over a real socket (workplan task 37).
/// <para>
/// The counterpart to <see cref="FakeOpenAiServer"/> for the second transport: pointing the real
/// Anthropic client at a real socket exercises serialisation, authentication headers and usage
/// accounting - the parts a faked <c>IChatClient</c> can never break.
/// </para>
/// </summary>
public sealed class FakeAnthropicServer : IDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Queue<string> _responses = new();
    private readonly Lock _gate = new();
    private readonly Task _loop;

    /// <summary>Starts the server on a free loopback port.</summary>
    public FakeAnthropicServer()
    {
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _loop = Task.Run(AcceptAsync);
    }

    /// <summary>Port the server is listening on.</summary>
    public int Port { get; }

    /// <summary>
    /// Base endpoint to configure a role with. The host root, not <c>/v1</c> - the Anthropic
    /// client appends the path itself.
    /// </summary>
    public string Endpoint => $"http://127.0.0.1:{Port}";

    /// <summary>Every request body the server received, in order.</summary>
    public List<string> Requests { get; } = [];

    /// <summary>Every <c>x-api-key</c> header the server received, in order. Null when absent.</summary>
    public List<string?> ApiKeys { get; } = [];

    /// <summary>Every <c>anthropic-version</c> header the server received, in order. Null when absent.</summary>
    public List<string?> Versions { get; } = [];

    /// <summary>Model ids <c>GET /v1/models</c> reports as served.</summary>
    public List<string> ServedModels { get; } = ["claude-opus-5"];

    /// <summary>Queues a plain assistant message.</summary>
    public FakeAnthropicServer EnqueueText(string text)
    {
        Enqueue(JsonSerializer.Serialize(new
        {
            id = "msg_1",
            type = "message",
            role = "assistant",
            model = "claude-opus-5",
            content = new[] { new { type = "text", text } },
            stop_reason = "end_turn",
            stop_sequence = (string?)null,
            usage = new { input_tokens = 11, output_tokens = 7 },
        }));

        return this;
    }

    /// <summary>
    /// Queues a refusal: the safety classifiers declining before any output. A successful HTTP
    /// 200 with no content - which is exactly why a caller must read <c>stop_reason</c> first.
    /// </summary>
    public FakeAnthropicServer EnqueueRefusal()
    {
        Enqueue(JsonSerializer.Serialize(new
        {
            id = "msg_2",
            type = "message",
            role = "assistant",
            model = "claude-opus-5",
            content = Array.Empty<object>(),
            stop_reason = "refusal",
            stop_sequence = (string?)null,
            stop_details = new { type = "refusal", category = "cyber", explanation = "declined" },
            usage = new { input_tokens = 0, output_tokens = 0 },
        }));

        return this;
    }

    /// <summary>The parsed body of a received request.</summary>
    public JsonElement Request(int index) => JsonDocument.Parse(Requests[index]).RootElement;

    /// <inheritdoc />
    public void Dispose()
    {
        _shutdown.Cancel();
        _listener.Stop();

        try
        {
            _loop.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
            // Shutting down a listener races with the accept loop; nothing here is worth failing on.
        }

        _shutdown.Dispose();
    }

    private void Enqueue(string json)
    {
        lock (_gate)
        {
            _responses.Enqueue(json.ReplaceLineEndings(string.Empty));
        }
    }

    private string Next()
    {
        lock (_gate)
        {
            return _responses.Count > 0
                ? _responses.Dequeue()
                : """{"id":"msg_x","type":"message","role":"assistant","model":"claude-opus-5","content":[{"type":"text","text":"done"}],"stop_reason":"end_turn","stop_sequence":null,"usage":{"input_tokens":1,"output_tokens":1}}""";
        }
    }

    private async Task AcceptAsync()
    {
        while (!_shutdown.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_shutdown.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
            {
                return;
            }

            _ = Task.Run(() => ServeAsync(client));
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                using NetworkStream stream = client.GetStream();
                ReceivedRequest received = await ReadRequestAsync(stream).ConfigureAwait(false);

                lock (_gate)
                {
                    Requests.Add(received.Body);
                    ApiKeys.Add(received.ApiKey);
                    Versions.Add(received.Version);
                }

                bool modelList = received.Path.EndsWith("/models", StringComparison.Ordinal);
                byte[] payload = Encoding.UTF8.GetBytes(modelList ? ModelList() : Next());
                string headers =
                    "HTTP/1.1 200 OK\r\n" +
                    "Content-Type: application/json\r\n" +
                    string.Create(CultureInfo.InvariantCulture, $"Content-Length: {payload.Length}\r\n") +
                    "Connection: close\r\n\r\n";

                await stream.WriteAsync(Encoding.ASCII.GetBytes(headers)).ConfigureAwait(false);
                await stream.WriteAsync(payload).ConfigureAwait(false);
                await stream.FlushAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
            {
                // A client that hung up mid-exchange is not a test failure.
            }
        }
    }

    private string ModelList()
    {
        List<object> data = [];
        lock (_gate)
        {
            foreach (string id in ServedModels)
            {
                data.Add(new { type = "model", id, display_name = id, created_at = "2026-01-01T00:00:00Z" });
            }
        }

        return JsonSerializer.Serialize(new { data, has_more = false, first_id = (string?)null, last_id = (string?)null });
    }

    private static async Task<ReceivedRequest> ReadRequestAsync(NetworkStream stream)
    {
        byte[] buffer = new byte[16 * 1024];
        StringBuilder received = new();
        int contentLength = -1;
        int headerEnd = -1;
        string path = string.Empty;
        string? apiKey = null;
        string? version = null;

        while (true)
        {
            int read = await stream.ReadAsync(buffer).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            received.Append(Encoding.UTF8.GetString(buffer, 0, read));
            string text = received.ToString();

            if (headerEnd < 0)
            {
                headerEnd = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                if (headerEnd >= 0)
                {
                    string[] lines = text[..headerEnd].Split("\r\n");
                    string[] requestLine = lines[0].Split(' ');
                    if (requestLine.Length >= 2)
                    {
                        path = requestLine[1];
                    }

                    foreach (string line in lines)
                    {
                        if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                        {
                            contentLength = int.Parse(line[15..].Trim(), CultureInfo.InvariantCulture);
                        }
                        else if (line.StartsWith("x-api-key:", StringComparison.OrdinalIgnoreCase))
                        {
                            apiKey = line[10..].Trim();
                        }
                        else if (line.StartsWith("anthropic-version:", StringComparison.OrdinalIgnoreCase))
                        {
                            version = line[18..].Trim();
                        }
                    }

                    // A GET carries no body, so there is nothing further to wait for.
                    if (contentLength <= 0)
                    {
                        return new ReceivedRequest(path, apiKey, version, string.Empty);
                    }
                }
            }

            if (headerEnd >= 0 && text.Length - (headerEnd + 4) >= contentLength)
            {
                return new ReceivedRequest(path, apiKey, version, text.Substring(headerEnd + 4, contentLength));
            }
        }

        return new ReceivedRequest(path, apiKey, version, string.Empty);
    }

    private sealed record ReceivedRequest(string Path, string? ApiKey, string? Version, string Body);
}
