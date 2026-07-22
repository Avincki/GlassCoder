using System.ClientModel;
using System.Collections.Concurrent;
using GlassCoder.Models.Configuration;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenAI;

namespace GlassCoder.Models;

/// <summary>
/// Builds one OpenAI-compatible <see cref="IChatClient"/> pipeline per served role
/// (CLAUDE.md §6, workplan task 4).
/// <para>
/// The pipeline is, from outermost to innermost: role defaults → constrained decoding →
/// OpenTelemetry → the transport client. Tracing sits innermost on purpose, so a span records
/// the request as it actually goes over the wire rather than as the caller wrote it.
/// </para>
/// <para>
/// Deliberately absent: <c>UseFunctionInvocation()</c>. The controller loop is the agent
/// (CLAUDE.md §3.1); a framework auto-invoker would take the loop away from us and with it
/// the ability to interrupt, budget and log every step.
/// </para>
/// </summary>
public sealed class ChatClientFactory : IChatClientFactory, IDisposable
{
    private readonly ModelsOptions _options;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ConcurrentDictionary<string, IChatClient> _clients =
        new(StringComparer.OrdinalIgnoreCase);

    private bool _disposed;

    /// <summary>Creates the factory from bound configuration.</summary>
    public ChatClientFactory(IOptions<ModelsOptions> options, ILoggerFactory? loggerFactory = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        _options = options.Value;
        _loggerFactory = loggerFactory ?? Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance;
        Roles = [.. _options.Roles.Keys];
        DefaultRole = string.IsNullOrWhiteSpace(_options.DefaultRole) ? ModelRoles.Worker : _options.DefaultRole;
    }

    /// <inheritdoc />
    public IReadOnlyList<string> Roles { get; }

    /// <inheritdoc />
    public string DefaultRole { get; }

    /// <inheritdoc />
    public bool ContainsRole(string role) => _options.Roles.ContainsKey(role);

    /// <inheritdoc />
    public IChatClient GetClient(string? role = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        string resolved = Resolve(role);
        return _clients.GetOrAdd(resolved, BuildClient);
    }

    /// <inheritdoc />
    public ModelRoleOptions GetRoleOptions(string? role = null) => _options.Roles[Resolve(role)];

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (IChatClient client in _clients.Values)
        {
            client.Dispose();
        }

        _clients.Clear();
    }

    private string Resolve(string? role)
    {
        string resolved = string.IsNullOrWhiteSpace(role) ? DefaultRole : role;
        if (!_options.Roles.ContainsKey(resolved))
        {
            throw new ArgumentException(
                $"Model role '{resolved}' is not configured. Configured roles: {string.Join(", ", Roles)}.",
                nameof(role));
        }

        return resolved;
    }

    private IChatClient BuildClient(string role)
    {
        ModelRoleOptions settings = _options.Roles[role];

        OpenAIClientOptions clientOptions = new()
        {
            Endpoint = new Uri(settings.Endpoint),
            NetworkTimeout = TimeSpan.FromSeconds(settings.TimeoutSeconds),
            UserAgentApplicationId = "GlassCoder",
        };

        // Local servers ignore the credential but the client requires a non-empty one.
        ApiKeyCredential credential = new(settings.ResolveApiKey() ?? "local-no-auth");

        IChatClient transport = new OpenAIClient(credential, clientOptions)
            .GetChatClient(settings.ModelAlias)
            .AsIChatClient();

        ILogger constrainedDecodingLogger = _loggerFactory.CreateLogger(
            $"{typeof(ConstrainedDecodingChatClient).FullName}.{role}");

        // First stage added is the outermost, so this reads in call order: fill in role
        // defaults, constrain the decoding, then trace whatever came out of that.
        return transport
            .AsBuilder()
            .ConfigureOptions(options => ApplyRoleDefaults(options, settings))
            .Use(inner => new ConstrainedDecodingChatClient(inner, settings, constrainedDecodingLogger))
            .UseOpenTelemetry(
                _loggerFactory,
                _options.TelemetrySourceName,
                client => client.EnableSensitiveData = _options.EnableSensitiveTelemetryData)
            .Build();
    }

    /// <summary>
    /// Fills in sampling settings the caller left unspecified. Caller-supplied values always
    /// win: an ablation arm that pins a temperature must not be silently overridden.
    /// </summary>
    private static void ApplyRoleDefaults(ChatOptions options, ModelRoleOptions settings)
    {
        options.ModelId ??= settings.ModelAlias;
        options.MaxOutputTokens ??= settings.MaxOutputTokens;
        options.Temperature ??= settings.Temperature;
        options.TopP ??= settings.TopP;
        options.Seed ??= settings.Seed;
    }
}
