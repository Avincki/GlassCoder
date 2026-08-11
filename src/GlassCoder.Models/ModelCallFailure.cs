using System.ClientModel;
using System.Globalization;
using System.Net.Sockets;
using System.Text.Json;
using GlassCoder.Models.Configuration;

namespace GlassCoder.Models;

/// <summary>
/// Which way a model call went wrong. Each kind has a different fix, which is exactly why one
/// stop reason cannot stand in for all of them.
/// </summary>
public enum ModelCallFailureKind
{
    /// <summary>Nothing could be classified; the exception speaks for itself, such as it is.</summary>
    Unknown,

    /// <summary>Nothing answered on the endpoint's port: refused, unroutable, or a host that is down.</summary>
    Unreachable,

    /// <summary>The endpoint's host name does not resolve.</summary>
    NameNotResolved,

    /// <summary>The connection was accepted and then died before the answer arrived.</summary>
    ConnectionDropped,

    /// <summary>The server took the call and produced nothing before the client gave up.</summary>
    TimedOut,

    /// <summary>The server answered, and rejected the credentials.</summary>
    Unauthorized,

    /// <summary>The server answered 404: the wrong path, or an alias it does not serve.</summary>
    NotFound,

    /// <summary>The server answered 429.</summary>
    RateLimited,

    /// <summary>The server failed on its own side (5xx).</summary>
    ServerError,

    /// <summary>The server understood the request and refused it (a 4xx of its own).</summary>
    RequestRejected,
}

/// <summary>
/// What a failed model call actually was, in words somebody can act on.
/// <para>
/// The loop has one stop reason for every way a call can fail, and <c>ModelError</c> on its own
/// names none of them. A closed port, a host name that stopped resolving, a generation that ran
/// past the role's timeout, a rejected key, an alias the server does not serve and a 400 from the
/// server's own validator all arrive at the same <c>catch</c>, and each has a different fix. Run
/// 71b16c1d said "ModelError" seven times about a worker process that had died: the endpoint was
/// in the configuration and the refusal was in the exception, and neither reached the reader.
/// </para>
/// <para>
/// The same reasoning as <see cref="IModelConnectionProbe"/> - distinct failure modes reported
/// distinctly - applied after a call rather than before one. The probe stays the fuller answer,
/// so the message says so where it is the next thing worth doing.
/// </para>
/// </summary>
/// <param name="Kind">Which failure mode, for callers that branch rather than print.</param>
/// <param name="Message">One explicit line: what failed, where, why, and what to do about it.</param>
public sealed record ModelCallFailure(ModelCallFailureKind Kind, string Message)
{
    /// <summary>Cap on quoted server text, so a stack of HTML never lands in the status bar.</summary>
    private const int MaxQuoted = 200;

    /// <summary>Describes a failed model call.</summary>
    /// <param name="role">The role that was being addressed.</param>
    /// <param name="settings">That role's settings, for the endpoint, alias and timeout. Null when unknown.</param>
    /// <param name="exception">Whatever came out of the call.</param>
    /// <param name="elapsed">How long the call took before it failed. Told apart an instant refusal
    /// from a timeout that held the run for ten minutes, so it is worth carrying.</param>
    public static ModelCallFailure Describe(
        string role,
        ModelRoleOptions? settings,
        Exception exception,
        TimeSpan? elapsed = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(role);
        ArgumentNullException.ThrowIfNull(exception);

        string endpoint = string.IsNullOrWhiteSpace(settings?.Endpoint) ? "(no endpoint configured)" : settings.Endpoint;
        string alias = string.IsNullOrWhiteSpace(settings?.ModelAlias) ? role : settings.ModelAlias;

        (ModelCallFailureKind kind, string cause, string action) = Classify(exception, settings, alias);

        string when = elapsed is { } span
            ? string.Create(CultureInfo.InvariantCulture, $" after {span.TotalSeconds:F1} s")
            : string.Empty;

        return new ModelCallFailure(
            kind,
            $"The \"{role}\" model call to {endpoint} (alias '{alias}') failed{when}: {cause} {action} [{Detail(exception)}]");
    }

    /// <inheritdoc />
    public override string ToString() => Message;

    /// <summary>
    /// Reads the exception chain from the outside in and stops at the first link that names a
    /// failure mode. The transport wraps its own layers - a socket error arrives inside an
    /// <see cref="HttpRequestException"/> inside a <see cref="ClientResultException"/> - and it is
    /// the innermost link that says what actually happened.
    /// </summary>
    private static (ModelCallFailureKind Kind, string Cause, string Action) Classify(
        Exception exception,
        ModelRoleOptions? settings,
        string alias)
    {
        if (Chain(exception).OfType<SocketException>().FirstOrDefault() is { } socket)
        {
            return FromSocket(socket);
        }

        // The loop catches a cancellation the caller asked for before this is ever reached, so a
        // cancellation here is the client's own clock running out.
        if (Chain(exception).OfType<OperationCanceledException>().Any())
        {
            int timeout = settings?.TimeoutSeconds ?? 0;

            return (
                ModelCallFailureKind.TimedOut,
                timeout > 0
                    ? string.Create(
                        CultureInfo.InvariantCulture,
                        $"the server accepted the call and produced no answer within the role's {timeout}s timeout.")
                    : "the server accepted the call and produced no answer before the client gave up.",
                "A server still loading its weights, a prompt longer than the served context, and a generation " +
                "genuinely slower than the timeout all look identical from here; the model server's own log tells them apart.");
        }

        if (StatusOf(exception) is { } status and > 0)
        {
            return FromStatus(status, Quoted(exception), alias);
        }

        if (Chain(exception).OfType<HttpRequestException>().FirstOrDefault() is { } http)
        {
            return FromHttpError(http);
        }

        return (
            ModelCallFailureKind.Unknown,
            "the client reported a failure that names no socket, no timeout and no HTTP status.",
            "The detail below is everything it said. The connection check in Settings ▸ Model exercises the " +
            "endpoint, the key and the alias one at a time, which is the quickest way to narrow this.");
    }

    private static (ModelCallFailureKind Kind, string Cause, string Action) FromSocket(SocketException socket) =>
        socket.SocketErrorCode switch
        {
            SocketError.ConnectionRefused => (
                ModelCallFailureKind.Unreachable,
                "the host answered but nothing is listening on that port, so the connection was refused.",
                "The network is fine and the model server is not: start the server for this role, or point the role at one that is running."),

            SocketError.HostNotFound or SocketError.NoData or SocketError.TryAgain => (
                ModelCallFailureKind.NameNotResolved,
                "the host name did not resolve.",
                "Check the endpoint for a typo, and that this machine is still on the network that serves that name - a VPN or mesh interface that dropped takes its names with it."),

            SocketError.TimedOut => (
                ModelCallFailureKind.Unreachable,
                "the connection attempt got no answer at all before it timed out.",
                "Nothing accepted the call and nothing refused it, which is a host that is off, asleep, or behind a firewall that drops rather than rejects."),

            SocketError.HostUnreachable or SocketError.NetworkUnreachable or SocketError.NetworkDown => (
                ModelCallFailureKind.Unreachable,
                "there is no route from here to that host.",
                "This is the network rather than the model server - check the interface or tunnel that carries this endpoint."),

            SocketError.ConnectionReset or SocketError.ConnectionAborted => (
                ModelCallFailureKind.ConnectionDropped,
                "the connection was accepted and then dropped before the answer arrived.",
                "Something closed the socket mid-generation; a local server killed for running out of memory ends its calls exactly this way, and its log will say so."),

            _ => (
                ModelCallFailureKind.Unreachable,
                $"the socket failed with {socket.SocketErrorCode}.",
                "Nothing was read from the endpoint, so this is the connection rather than the model."),
        };

    private static (ModelCallFailureKind Kind, string Cause, string Action) FromStatus(
        int status,
        string quoted,
        string alias) => status switch
        {
            401 or 403 => (
                ModelCallFailureKind.Unauthorized,
                $"the server answered {status} and rejected the credentials.{quoted}",
                "Give the role a key - ApiKeyEnvironmentVariable keeps it out of the settings file - or address a role that does not need one."),

            404 => (
                ModelCallFailureKind.NotFound,
                $"the server answered 404 for this request.{quoted}",
                $"Either the endpoint path is wrong (the OpenAI transport expects it to end in /v1) or '{alias}' is not an alias this server serves."),

            408 or 504 => (
                ModelCallFailureKind.TimedOut,
                $"the server answered {status}: it gave up waiting before the answer was ready.{quoted}",
                "The generation outlived a timeout between here and the model - the server's own, or a proxy in front of it."),

            429 => (
                ModelCallFailureKind.RateLimited,
                $"the server answered 429: too many requests.{quoted}",
                "Wait and retry, or reduce how many calls this endpoint is being asked to carry at once."),

            >= 500 => (
                ModelCallFailureKind.ServerError,
                $"the server failed on its own side with {status}.{quoted}",
                "Nothing on this side fixes that: the model server's log says what happened, and a worker killed mid-generation is the usual answer."),

            >= 400 => (
                ModelCallFailureKind.RequestRejected,
                $"the server understood the request and refused it with {status}.{quoted}",
                "The server's own reason is the quoted text: a prompt past the served context length and an unsupported guided-decoding property both arrive as a 400."),

            _ => (
                ModelCallFailureKind.Unknown,
                $"the server answered {status}, which the client could not use.{quoted}",
                "The detail below is everything it said."),
        };

    private static (ModelCallFailureKind Kind, string Cause, string Action) FromHttpError(HttpRequestException http) =>
        http.HttpRequestError switch
        {
            HttpRequestError.NameResolutionError => (
                ModelCallFailureKind.NameNotResolved,
                "the host name did not resolve.",
                "Check the endpoint for a typo, and that this machine is still on the network that serves that name."),

            HttpRequestError.ConnectionError => (
                ModelCallFailureKind.Unreachable,
                "the connection to the endpoint could not be established.",
                "Nothing was read from the server, so this is the endpoint or the network rather than the model."),

            HttpRequestError.SecureConnectionError => (
                ModelCallFailureKind.Unreachable,
                "the TLS handshake failed.",
                "The port answered but not with the certificate the client would accept - an https endpoint pointed at a plain-http server does this."),

            HttpRequestError.ResponseEnded => (
                ModelCallFailureKind.ConnectionDropped,
                "the server ended the response before it was complete.",
                "The call was accepted and abandoned mid-answer; the model server's log says why."),

            _ => (
                ModelCallFailureKind.Unknown,
                "the HTTP call failed without an answer to read.",
                "The detail below is everything the client reported."),
        };

    /// <summary>
    /// The HTTP status the server answered with, where a link in the chain knows it. Zero and
    /// null both mean "no response", which is a different failure and is classified elsewhere.
    /// </summary>
    private static int? StatusOf(Exception exception)
    {
        foreach (Exception link in Chain(exception))
        {
            switch (link)
            {
                case ClientResultException { Status: > 0 } client:
                    return client.Status;

                case HttpRequestException { StatusCode: { } code }:
                    return (int)code;

                default:
                    continue;
            }
        }

        return null;
    }

    /// <summary>
    /// The server's own words about why it refused, ready to append to a sentence. This is where
    /// the actionable half of a 4xx lives - vLLM says which limit a prompt passed - so it is worth
    /// digging out of the response body rather than reporting the status alone.
    /// </summary>
    private static string Quoted(Exception exception)
    {
        string body = ResponseBody(exception);

        return body.Length == 0 ? string.Empty : $" It said: \"{Clip(body, MaxQuoted)}\".";
    }

    private static string ResponseBody(Exception exception)
    {
        foreach (ClientResultException link in Chain(exception).OfType<ClientResultException>())
        {
            string body;
            try
            {
                body = link.GetRawResponse()?.Content?.ToString() ?? string.Empty;
            }
            catch (InvalidOperationException)
            {
                // An unbuffered response cannot be read twice. The status still says plenty.
                continue;
            }

            if (!string.IsNullOrWhiteSpace(body))
            {
                return ErrorMessageIn(body) ?? body.ReplaceLineEndings(" ").Trim();
            }
        }

        return string.Empty;
    }

    /// <summary>
    /// Pulls the human-readable half out of an error body. Both shapes in the wild put it in the
    /// same place: <c>{"message": ...}</c> from vLLM, <c>{"error": {"message": ...}}</c> from the
    /// OpenAI API. Anything else is returned whole by the caller rather than guessed at.
    /// </summary>
    private static string? ErrorMessageIn(string body)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            if (document.RootElement.TryGetProperty("error", out JsonElement error) &&
                error.ValueKind == JsonValueKind.Object &&
                error.TryGetProperty("message", out JsonElement nested) &&
                nested.ValueKind == JsonValueKind.String)
            {
                return nested.GetString();
            }

            return document.RootElement.TryGetProperty("message", out JsonElement flat) &&
                flat.ValueKind == JsonValueKind.String
                    ? flat.GetString()
                    : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The innermost exception, which is the one that knows what went wrong.</summary>
    private static string Detail(Exception exception)
    {
        Exception root = Chain(exception).Last();
        string name = root is SocketException socket
            ? $"{root.GetType().Name}/{socket.SocketErrorCode}"
            : root.GetType().Name;

        return $"{name}: {Clip(root.Message.ReplaceLineEndings(" ").Trim(), MaxQuoted)}";
    }

    private static IEnumerable<Exception> Chain(Exception exception)
    {
        for (Exception? link = exception; link is not null; link = link.InnerException)
        {
            yield return link;
        }
    }

    private static string Clip(string value, int max) =>
        value.Length <= max ? value : string.Concat(value.AsSpan(0, max), "…");
}
