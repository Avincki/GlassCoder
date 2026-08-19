using System.ClientModel;
using System.Diagnostics;
using System.Globalization;
using Anthropic;
using Anthropic.Exceptions;
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

    private readonly IServedModelDirectory _directory;
    private readonly bool _ownsDirectory;
    private bool _disposed;

    /// <summary>Creates a probe that owns its own directory. What a test or a console tool wants.</summary>
    public ModelConnectionProbe()
        : this(new ServedModelDirectory(), ownsDirectory: true)
    {
    }

    /// <summary>Creates a probe over the container's directory, so both share one parser and one socket pool.</summary>
    public ModelConnectionProbe(IServedModelDirectory directory)
        : this(directory, ownsDirectory: false)
    {
    }

    private ModelConnectionProbe(IServedModelDirectory directory, bool ownsDirectory)
    {
        _directory = directory;
        _ownsDirectory = ownsDirectory;
    }

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

        long listing = Stopwatch.GetTimestamp();
        ServedModelList list = await _directory.ListAsync(settings, timeout, cancellationToken).ConfigureAwait(false);
        served.AddRange(list.Models.Select(model => model.Alias));
        steps.Add(DescribeList(list, apiKey, timeout, listing));

        if (steps[^1].Outcome != ConnectionCheckOutcome.Failed && served.Count > 0)
        {
            steps.Add(CheckAlias(settings, list));
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

        // Only what this instance created. Disposing the container's singleton because a
        // transient probe went out of scope would take the next caller's sockets with it.
        if (_ownsDirectory && _directory is IDisposable disposable)
        {
            disposable.Dispose();
        }
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

    /// <summary>Turns what the directory found into the step the dialog shows.</summary>
    private static ConnectionCheckStep DescribeList(ServedModelList list, string? apiKey, TimeSpan timeout, long started) =>
        list.Outcome switch
        {
            ServedModelListOutcome.Unauthorized => Step(
                "Server",
                ConnectionCheckOutcome.Failed,
                $"The server answered {list.StatusCode}: it rejected the API key. " +
                (string.IsNullOrEmpty(apiKey) ? "No key is configured for this role." : "Check the key for this role."),
                started),

            // Plenty of local servers implement chat completions and nothing else. That is not a
            // failure - the completion step below is the one that decides.
            ServedModelListOutcome.Refused => Step(
                "Server",
                ConnectionCheckOutcome.Warning,
                $"The server answered {list.StatusCode} for {list.Url}, so its served aliases could not be listed.",
                started),

            ServedModelListOutcome.Unreachable => Step(
                "Server",
                ConnectionCheckOutcome.Failed,
                $"Could not reach {list.Url}: {list.Error} " +
                "Check that the model server is running and serving this endpoint.",
                started),

            _ => Step(
                "Server",
                ConnectionCheckOutcome.Ok,
                list.Models.Count > 0
                    ? $"Reachable, serving {list.Models.Count} alias(es): " +
                      $"{string.Join(", ", list.Models.Select(model => model.Alias))}."
                    : "Reachable, but it listed no served aliases.",
                started),
        };

    /// <summary>
    /// Whether the alias this role addresses is one the server actually serves - and, when the
    /// server volunteered it, what is behind that alias. The checkpoint is reported and nothing
    /// more: it tells the operator which weights answered, and the harness still addresses only
    /// the alias (CLAUDE.md §19).
    /// </summary>
    private static ConnectionCheckStep CheckAlias(ModelRoleOptions settings, ServedModelList list)
    {
        if (list.Find(settings.ModelAlias) is not { } served)
        {
            return new ConnectionCheckStep(
                "Alias",
                ConnectionCheckOutcome.Warning,
                $"'{settings.ModelAlias}' is not in the served list " +
                $"({string.Join(", ", list.Models.Select(model => model.Alias))}). " +
                "Address a served alias - serving topology lives below the seam.",
                0);
        }

        string detail = served.Identity is { } identity
            ? $"'{settings.ModelAlias}' is served by {identity}."
            : $"'{settings.ModelAlias}' is served; the server did not report a checkpoint.";

        if (served.MaxContextTokens is { } context)
        {
            detail += string.Create(CultureInfo.InvariantCulture, $" Context {context:N0} tokens.");
        }

        return new ConnectionCheckStep("Alias", ConnectionCheckOutcome.Ok, detail, 0);
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

        // Deliberately the bare transport: no constrained decoding, no telemetry stage. This
        // step answers "can this endpoint, key and alias produce a completion", and a server
        // that rejects a guided-decoding property is a different problem with a different fix.
        using IChatClient client = BuildBareClient(settings, apiKey, timeout);

        try
        {
            ChatResponse response = await client.GetResponseAsync(
                [new ChatMessage(ChatRole.User, Probe)],
                new ChatOptions
                {
                    ModelId = settings.ModelAlias,
                    MaxOutputTokens = 16,
                    // Current Anthropic models reject sampling parameters with a 400, so the
                    // probe would fail on exactly the thing it is not checking.
                    Temperature = settings.Transport == ModelTransport.Anthropic ? null : settings.Temperature,
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
        catch (AnthropicApiException ex)
        {
            return Step(
                "Completion",
                ConnectionCheckOutcome.Failed,
                $"The server refused the completion: {Clip(ex.Message, 200)}",
                started);
        }
        catch (HttpRequestException ex)
        {
            return Step("Completion", ConnectionCheckOutcome.Failed, $"The call failed: {ex.Message}", started);
        }
    }

    /// <summary>The unadorned client for the completion step, in whichever shape the role speaks.</summary>
    private static IChatClient BuildBareClient(ModelRoleOptions settings, string? apiKey, TimeSpan timeout)
    {
        if (settings.Transport == ModelTransport.Anthropic)
        {
            AnthropicClient anthropic = new()
            {
                ApiKey = apiKey ?? "local-no-auth",
                BaseUrl = settings.Endpoint,
                Timeout = timeout,
            };

            return anthropic.AsIChatClient(settings.ModelAlias, defaultMaxOutputTokens: 16);
        }

        OpenAIClientOptions clientOptions = new()
        {
            Endpoint = new Uri(settings.Endpoint),
            NetworkTimeout = timeout,
            UserAgentApplicationId = "GlassCoder",
        };

        return new OpenAIClient(new ApiKeyCredential(apiKey ?? "local-no-auth"), clientOptions)
            .GetChatClient(settings.ModelAlias)
            .AsIChatClient();
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
