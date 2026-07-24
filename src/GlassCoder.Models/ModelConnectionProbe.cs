using System.ClientModel;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using GlassCoder.Models.Configuration;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using OpenAI;

namespace GlassCoder.Models;

/// <summary>
/// The default <see cref="IModelConnectionProbe"/>: validate, list, then actually talk.
/// <para>
/// The last step is a real completion rather than a handshake, because everything cheaper can
/// pass while generation still fails - a served alias whose weights failed to load answers
/// <c>/models</c> perfectly well. A check that does not exercise the thing being checked is
/// worse than no check, since it is believed.
/// </para>
/// </summary>
public sealed class ModelConnectionProbe : IModelConnectionProbe, IDisposable
{
    /// <summary>Prompt sent by the completion step. Short on purpose: this is a check, not a run.</summary>
    private const string Probe = "Reply with the single word: pong.";

    /// <summary>
    /// Ceiling on how long a check may take, whatever the role's own timeout is. A role
    /// configured for 600-second generations must not leave somebody staring at a dialog for
    /// ten minutes to find out a port is closed.
    /// </summary>
    private const int MaxProbeSeconds = 30;

    private readonly HttpClient _http = new();
    private bool _disposed;

    /// <inheritdoc />
    public async Task<ConnectionCheckResult> CheckAsync(
        string role,
        ModelRoleOptions settings,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(role);
        ArgumentNullException.ThrowIfNull(settings);

        long started = Stopwatch.GetTimestamp();
        List<ConnectionCheckStep> steps = [];
        List<string> served = [];

        ConnectionCheckStep configured = ValidateSettings(role, settings);
        steps.Add(configured);

        if (configured.Outcome == ConnectionCheckOutcome.Failed)
        {
            return Report(role, steps, served, started);
        }

        TimeSpan timeout = TimeSpan.FromSeconds(Math.Min(Math.Max(settings.TimeoutSeconds, 1), MaxProbeSeconds));
        string? apiKey = settings.ResolveApiKey();

        steps.Add(await ListModelsAsync(settings, apiKey, served, timeout, cancellationToken).ConfigureAwait(false));

        if (steps[^1].Outcome != ConnectionCheckOutcome.Failed && served.Count > 0)
        {
            steps.Add(CheckAlias(settings, served));
        }

        if (steps[^1].Outcome != ConnectionCheckOutcome.Failed)
        {
            steps.Add(await CompleteAsync(settings, apiKey, timeout, cancellationToken).ConfigureAwait(false));
        }

        return Report(role, steps, served, started);
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
    /// The offline rung: the same rules the harness refuses to start on, applied before anything
    /// touches the network. A typo in an endpoint should not look like an unreachable server.
    /// </summary>
    private static ConnectionCheckStep ValidateSettings(string role, ModelRoleOptions settings)
    {
        ModelsOptions single = new() { DefaultRole = role };
        single.Roles[role] = settings;

        ValidateOptionsResult result = new ModelsOptionsValidator().Validate(Options.DefaultName, single);

        return result.Failed && result.Failures is not null
            ? new ConnectionCheckStep("Settings", ConnectionCheckOutcome.Failed, string.Join(" ", result.Failures), 0)
            : new ConnectionCheckStep(
                "Settings",
                ConnectionCheckOutcome.Ok,
                $"Endpoint {settings.Endpoint}, alias '{settings.ModelAlias}'" +
                (string.IsNullOrEmpty(settings.ResolveApiKey()) ? ", no API key." : ", API key supplied."),
                0);
    }

    private async Task<ConnectionCheckStep> ListModelsAsync(
        ModelRoleOptions settings,
        string? apiKey,
        List<string> served,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        long started = Stopwatch.GetTimestamp();
        Uri url = new(settings.Endpoint.TrimEnd('/') + "/models");

        using CancellationTokenSource limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(timeout);

        try
        {
            using HttpRequestMessage request = new(HttpMethod.Get, url);
            if (!string.IsNullOrEmpty(apiKey))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            }

            using HttpResponseMessage response = await _http
                .SendAsync(request, HttpCompletionOption.ResponseContentRead, limit.Token)
                .ConfigureAwait(false);

            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                return Step(
                    "Server",
                    ConnectionCheckOutcome.Failed,
                    $"The server answered {(int)response.StatusCode} {response.StatusCode}: it rejected the API key. " +
                    (string.IsNullOrEmpty(apiKey) ? "No key is configured for this role." : "Check the key for this role."),
                    started);
            }

            if (!response.IsSuccessStatusCode)
            {
                // Plenty of local servers implement chat completions and nothing else. That is
                // not a failure - the completion step below is the one that decides.
                return Step(
                    "Server",
                    ConnectionCheckOutcome.Warning,
                    $"The server answered {(int)response.StatusCode} {response.StatusCode} for /models, " +
                    "so its served aliases could not be listed.",
                    started);
            }

            served.AddRange(ParseModelIds(await response.Content.ReadAsStringAsync(limit.Token).ConfigureAwait(false)));

            return Step(
                "Server",
                ConnectionCheckOutcome.Ok,
                served.Count > 0
                    ? $"Reachable, serving {served.Count} alias(es): {string.Join(", ", served)}."
                    : "Reachable, but it listed no served aliases.",
                started);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Step(
                "Server",
                ConnectionCheckOutcome.Failed,
                $"No answer from {url} within {timeout.TotalSeconds:F0} seconds.",
                started);
        }
        catch (HttpRequestException ex)
        {
            return Step(
                "Server",
                ConnectionCheckOutcome.Failed,
                $"Could not reach {url}: {ex.Message} Check that the model server is running and serving this endpoint.",
                started);
        }
    }

    private static ConnectionCheckStep CheckAlias(ModelRoleOptions settings, List<string> served)
    {
        bool present = served.Contains(settings.ModelAlias, StringComparer.OrdinalIgnoreCase);

        return new ConnectionCheckStep(
            "Alias",
            present ? ConnectionCheckOutcome.Ok : ConnectionCheckOutcome.Warning,
            present
                ? $"'{settings.ModelAlias}' is served."
                : $"'{settings.ModelAlias}' is not in the served list ({string.Join(", ", served)}). " +
                  "Address a served alias - serving topology lives below the seam.",
            0);
    }

    private static async Task<ConnectionCheckStep> CompleteAsync(
        ModelRoleOptions settings,
        string? apiKey,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        long started = Stopwatch.GetTimestamp();

        using CancellationTokenSource limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(timeout);

        OpenAIClientOptions clientOptions = new()
        {
            Endpoint = new Uri(settings.Endpoint),
            NetworkTimeout = timeout,
            UserAgentApplicationId = "GlassCoder",
        };

        // Deliberately the bare transport: no constrained decoding, no telemetry stage. This
        // step answers "can this endpoint, key and alias produce a completion", and a server
        // that rejects a guided-decoding property is a different problem with a different fix.
        using IChatClient client = new OpenAIClient(new ApiKeyCredential(apiKey ?? "local-no-auth"), clientOptions)
            .GetChatClient(settings.ModelAlias)
            .AsIChatClient();

        try
        {
            ChatResponse response = await client.GetResponseAsync(
                [new ChatMessage(ChatRole.User, Probe)],
                new ChatOptions
                {
                    ModelId = settings.ModelAlias,
                    MaxOutputTokens = 16,
                    Temperature = settings.Temperature,
                },
                limit.Token).ConfigureAwait(false);

            string reply = response.Text.ReplaceLineEndings(" ").Trim();
            string tokens = response.Usage is { } usage
                ? string.Create(
                    CultureInfo.InvariantCulture,
                    $" ({usage.InputTokenCount} prompt + {usage.OutputTokenCount} completion tokens)")
                : string.Empty;

            return string.IsNullOrEmpty(reply)
                ? Step("Completion", ConnectionCheckOutcome.Warning, $"The model answered with no text{tokens}.", started)
                : Step(
                    "Completion",
                    ConnectionCheckOutcome.Ok,
                    $"The model answered \"{Clip(reply)}\"{tokens}.",
                    started);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Step(
                "Completion",
                ConnectionCheckOutcome.Failed,
                $"No completion within {timeout.TotalSeconds:F0} seconds. The server may still be loading the model.",
                started);
        }
        catch (ClientResultException ex)
        {
            return Step(
                "Completion",
                ConnectionCheckOutcome.Failed,
                $"The server refused the completion ({ex.Status}): {Clip(ex.Message, 200)}",
                started);
        }
        catch (HttpRequestException ex)
        {
            return Step("Completion", ConnectionCheckOutcome.Failed, $"The call failed: {ex.Message}", started);
        }
    }

    /// <summary>Reads the <c>data[].id</c> aliases out of an OpenAI-shaped model list.</summary>
    private static List<string> ParseModelIds(string json)
    {
        List<string> ids = [];

        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("data", out JsonElement data) ||
                data.ValueKind != JsonValueKind.Array)
            {
                return ids;
            }

            foreach (JsonElement entry in data.EnumerateArray())
            {
                if (entry.ValueKind == JsonValueKind.Object &&
                    entry.TryGetProperty("id", out JsonElement id) &&
                    id.ValueKind == JsonValueKind.String)
                {
                    ids.Add(id.GetString()!);
                }
            }
        }
        catch (JsonException)
        {
            // Something answered but it was not a model list. The completion step decides.
        }

        return ids;
    }

    private static ConnectionCheckResult Report(
        string role,
        List<ConnectionCheckStep> steps,
        List<string> served,
        long started)
    {
        ConnectionCheckOutcome worst = ConnectionCheckOutcome.Ok;
        foreach (ConnectionCheckStep step in steps)
        {
            if (step.Outcome > worst)
            {
                worst = step.Outcome;
            }
        }

        double elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        string headline = worst switch
        {
            ConnectionCheckOutcome.Ok => "Works",
            ConnectionCheckOutcome.Warning => "Works, with warnings",
            _ => "Does not work",
        };

        // The first step that went wrong is the one worth putting on the summary line; the rest
        // are usually its consequences.
        ConnectionCheckStep? culprit = steps.Find(step => step.Outcome == worst);
        string detail = worst == ConnectionCheckOutcome.Ok ? steps[^1].Detail : culprit?.Detail ?? string.Empty;

        return new ConnectionCheckResult(
            role,
            worst,
            string.Create(CultureInfo.InvariantCulture, $"{headline} · {elapsed:F0} ms · {detail}"),
            steps,
            served,
            elapsed);
    }

    private static ConnectionCheckStep Step(string name, ConnectionCheckOutcome outcome, string detail, long started) =>
        new(name, outcome, detail, Stopwatch.GetElapsedTime(started).TotalMilliseconds);

    private static string Clip(string value, int max = 80) =>
        value.Length <= max ? value : string.Concat(value.AsSpan(0, max), "…");
}
