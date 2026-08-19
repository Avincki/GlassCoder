using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using GlassCoder.Models.Configuration;

namespace GlassCoder.Models;

/// <summary>
/// The default <see cref="IServedModelDirectory"/>: one <c>GET</c>, parsed leniently.
/// <para>
/// Lenient on purpose. The fields worth having beyond <c>id</c> are extensions - <c>root</c> and
/// <c>max_model_len</c> come from vLLM, <c>display_name</c> from Anthropic - and a parser that
/// insisted on them would report every other server as broken. Anything absent is null, and the
/// caller says "the server did not say" rather than guessing.
/// </para>
/// </summary>
public sealed class ServedModelDirectory : IServedModelDirectory, IDisposable
{
    private readonly HttpClient _http = new();
    private bool _disposed;

    /// <inheritdoc />
    public async Task<ServedModelList> ListAsync(
        ModelRoleOptions settings,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(settings);

        // The two transports keep their model lists in different places: /models next to an
        // OpenAI endpoint that already ends in /v1, /v1/models under an Anthropic host root.
        Uri url = new(settings.Endpoint.TrimEnd('/') +
            (settings.Transport == ModelTransport.Anthropic ? "/v1/models" : "/models"));

        using CancellationTokenSource limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(timeout);

        try
        {
            using HttpRequestMessage request = new(HttpMethod.Get, url);
            Authenticate(request, settings);

            using HttpResponseMessage response = await _http
                .SendAsync(request, HttpCompletionOption.ResponseContentRead, limit.Token)
                .ConfigureAwait(false);

            int status = (int)response.StatusCode;

            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                return new ServedModelList(ServedModelListOutcome.Unauthorized, [], url, status);
            }

            if (!response.IsSuccessStatusCode)
            {
                return new ServedModelList(ServedModelListOutcome.Refused, [], url, status);
            }

            string body = await response.Content.ReadAsStringAsync(limit.Token).ConfigureAwait(false);

            return new ServedModelList(ServedModelListOutcome.Listed, Parse(body), url, status);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new ServedModelList(
                ServedModelListOutcome.Unreachable,
                [],
                url,
                Error: $"No answer within {timeout.TotalSeconds:F0} seconds.");
        }
        catch (HttpRequestException ex)
        {
            return new ServedModelList(ServedModelListOutcome.Unreachable, [], url, Error: ex.Message);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _http.Dispose();
    }

    /// <summary>
    /// Reads <c>data[]</c> out of a model list. One parser for both transports: the OpenAI and
    /// Anthropic lists agree on <c>data[].id</c> and differ only in which optional fields they
    /// add, so the difference is which properties come back null.
    /// </summary>
    internal static IReadOnlyList<ServedModel> Parse(string json)
    {
        List<ServedModel> models = [];

        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("data", out JsonElement data) ||
                data.ValueKind != JsonValueKind.Array)
            {
                return models;
            }

            foreach (JsonElement entry in data.EnumerateArray())
            {
                if (entry.ValueKind == JsonValueKind.Object && Text(entry, "id") is { } id)
                {
                    models.Add(new ServedModel(id, Text(entry, "root"), Number(entry, "max_model_len"), Text(entry, "display_name")));
                }
            }
        }
        catch (JsonException)
        {
            // Something answered but it was not a model list. An empty list is the honest report:
            // the caller says the server would not say, which is exactly what happened.
        }

        return models;
    }

    private static void Authenticate(HttpRequestMessage request, ModelRoleOptions settings)
    {
        string? apiKey = settings.ResolveApiKey();

        if (settings.Transport == ModelTransport.Anthropic)
        {
            // Anthropic-style auth is a header pair, not a bearer token.
            request.Headers.Add("anthropic-version", "2023-06-01");
            if (!string.IsNullOrEmpty(apiKey))
            {
                request.Headers.Add("x-api-key", apiKey);
            }
        }
        else if (!string.IsNullOrEmpty(apiKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        }
    }

    private static string? Text(JsonElement entry, string name) =>
        entry.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int? Number(JsonElement entry, string name) =>
        entry.TryGetProperty(name, out JsonElement value) &&
        value.ValueKind == JsonValueKind.Number &&
        value.TryGetInt32(out int parsed)
            ? parsed
            : null;
}
