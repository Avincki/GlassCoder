using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace GlassCoder.TestSupport;

/// <summary>
/// A minimal OpenAI-compatible <c>/v1/chat/completions</c> endpoint, over a real socket.
/// <para>
/// This is what makes the integration tests worth running (workplan task 32). A faked
/// <c>IChatClient</c> proves the loop works; it proves nothing about the seam. Pointing the real
/// OpenAI client at a real socket exercises serialisation, the constrained-decoding properties
/// that get attached to the request, tool-call parsing and usage accounting - the parts that
/// break when a package version moves.
/// </para>
/// <para>
/// Written on <see cref="TcpListener"/> rather than <c>HttpListener</c> deliberately: HttpListener
/// needs URL ACL registration on Windows, which would make the test suite require elevation.
/// </para>
/// </summary>
public sealed class FakeOpenAiServer : IDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Queue<string> _responses = new();
    private readonly Lock _gate = new();
    private readonly Task _loop;

    /// <summary>Starts the server on a free loopback port.</summary>
    public FakeOpenAiServer()
    {
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _loop = Task.Run(AcceptAsync);
    }

    /// <summary>Port the server is listening on.</summary>
    public int Port { get; }

    /// <summary>Base endpoint to configure a role with.</summary>
    public string Endpoint => $"http://127.0.0.1:{Port}/v1";

    /// <summary>Every request body the server received, in order.</summary>
    public List<string> Requests { get; } = [];

    /// <summary>Queues a plain assistant message.</summary>
    public FakeOpenAiServer EnqueueText(string text)
    {
        Enqueue(JsonSerializer.Serialize(new
        {
            id = "chatcmpl-1",
            @object = "chat.completion",
            created = 1,
            model = "worker",
            choices = new[]
            {
                new { index = 0, message = new { role = "assistant", content = text }, finish_reason = "stop" },
            },
            usage = new { prompt_tokens = 11, completion_tokens = 7, total_tokens = 18 },
        }));

        return this;
    }

    /// <summary>Queues an assistant message that calls a tool.</summary>
    public FakeOpenAiServer EnqueueToolCall(string name, string argumentsJson, string callId = "call_1")
    {
        Enqueue(JsonSerializer.Serialize(new
        {
            id = "chatcmpl-2",
            @object = "chat.completion",
            created = 1,
            model = "worker",
            choices = new[]
            {
                new
                {
                    index = 0,
                    message = new
                    {
                        role = "assistant",
                        content = (string?)null,
                        tool_calls = new[]
                        {
                            new
                            {
                                id = callId,
                                type = "function",
                                function = new { name, arguments = argumentsJson },
                            },
                        },
                    },
                    finish_reason = "tool_calls",
                },
            },
            usage = new { prompt_tokens = 23, completion_tokens = 9, total_tokens = 32 },
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
                : """{"id":"x","object":"chat.completion","created":1,"model":"worker","choices":[{"index":0,"message":{"role":"assistant","content":"done"},"finish_reason":"stop"}],"usage":{"prompt_tokens":1,"completion_tokens":1,"total_tokens":2}}""";
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
                string body = await ReadRequestAsync(stream).ConfigureAwait(false);

                lock (_gate)
                {
                    Requests.Add(body);
                }

                byte[] payload = Encoding.UTF8.GetBytes(Next());
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

    private static async Task<string> ReadRequestAsync(NetworkStream stream)
    {
        byte[] buffer = new byte[16 * 1024];
        StringBuilder received = new();
        int contentLength = -1;
        int headerEnd = -1;

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
                    foreach (string line in text[..headerEnd].Split("\r\n"))
                    {
                        if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                        {
                            contentLength = int.Parse(line[15..].Trim(), CultureInfo.InvariantCulture);
                        }
                    }
                }
            }

            if (headerEnd >= 0 && text.Length - (headerEnd + 4) >= contentLength)
            {
                return contentLength <= 0 ? string.Empty : text.Substring(headerEnd + 4, contentLength);
            }
        }

        return string.Empty;
    }

}
