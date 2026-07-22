using Microsoft.Extensions.AI;

namespace GlassCoder.TestSupport;

/// <summary>
/// A scripted <see cref="IChatClient"/> (CLAUDE.md §15: no unit test may need a live server).
/// <para>
/// Responses are returned in order; once the script runs out, the last response repeats, which
/// is what makes a "model that never stops calling tools" easy to write for limit tests.
/// </para>
/// </summary>
public sealed class FakeChatClient : IChatClient
{
    private readonly List<ChatResponse> _responses;
    private int _index;

    /// <summary>Creates a client that replays the given responses.</summary>
    public FakeChatClient(params ChatResponse[] responses) => _responses = [.. responses];

    /// <summary>Every request the loop made, in order.</summary>
    public List<(IReadOnlyList<ChatMessage> Messages, ChatOptions? Options)> Requests { get; } = [];

    /// <summary>Number of times the client was called.</summary>
    public int CallCount { get; private set; }

    /// <summary>Invoked before each response is returned. Use it to advance a fake clock.</summary>
    public Action<int>? OnRequest { get; set; }

    /// <summary>Set to throw from the next call, for the model-error path.</summary>
    public Exception? ThrowOnNextCall { get; set; }

    /// <summary>Builds a response containing a single tool call.</summary>
    public static ChatResponse ToolCall(string toolName, object? arguments = null, string callId = "call-1")
    {
        Dictionary<string, object?> map = arguments as Dictionary<string, object?> ?? [];
        return new ChatResponse(new ChatMessage(ChatRole.Assistant, [new FunctionCallContent(callId, toolName, map)]))
        {
            FinishReason = ChatFinishReason.ToolCalls,
            Usage = new UsageDetails { InputTokenCount = 100, OutputTokenCount = 20, TotalTokenCount = 120 },
        };
    }

    /// <summary>Builds a plain text response, which is how the loop learns the model is done.</summary>
    public static ChatResponse Text(string text, long totalTokens = 60) =>
        new(new ChatMessage(ChatRole.Assistant, text))
        {
            FinishReason = ChatFinishReason.Stop,
            Usage = new UsageDetails { InputTokenCount = totalTokens / 2, OutputTokenCount = totalTokens / 2, TotalTokenCount = totalTokens },
        };

    /// <inheritdoc />
    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Requests.Add(([.. messages], options));
        OnRequest?.Invoke(CallCount);
        CallCount++;

        if (ThrowOnNextCall is { } failure)
        {
            ThrowOnNextCall = null;
            throw failure;
        }

        if (_responses.Count == 0)
        {
            return Task.FromResult(Text("done"));
        }

        ChatResponse response = _responses[Math.Min(_index, _responses.Count - 1)];
        _index++;
        return Task.FromResult(response);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ChatResponse response = await GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
        foreach (ChatResponseUpdate update in response.ToChatResponseUpdates())
        {
            yield return update;
        }
    }

    /// <inheritdoc />
    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceKey is null && serviceType?.IsInstanceOfType(this) == true ? this : null;

    /// <inheritdoc />
    public void Dispose()
    {
    }
}
