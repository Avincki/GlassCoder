using GlassCoder.Models.Configuration;

namespace GlassCoder.Models;

/// <summary>
/// One entry from a server's model list: the alias it answers to, and whatever it was willing to
/// say about what is behind that alias.
/// </summary>
/// <param name="Alias">
/// <c>data[].id</c> - the served-model alias, which is what the harness addresses. For a server
/// started without <c>--served-model-name</c> this is the checkpoint itself.
/// </param>
/// <param name="Checkpoint">
/// <c>data[].root</c> - what the alias was served from, for example
/// <c>RedHatAI/Qwen3-Coder-Next-NVFP4</c>. Null when the server did not say. This is not part of
/// the OpenAI shape: vLLM and SGLang volunteer it, llama.cpp's server generally does not, and
/// Ollama has no alias to hide so the name is already in <see cref="Alias"/>. Read for display
/// only - nothing in the harness may address it or branch on it (CLAUDE.md §19).
/// </param>
/// <param name="MaxContextTokens"><c>data[].max_model_len</c>, when the server reports it.</param>
/// <param name="DisplayName"><c>data[].display_name</c> - the Anthropic list's readable name.</param>
public sealed record ServedModel(
    string Alias,
    string? Checkpoint = null,
    int? MaxContextTokens = null,
    string? DisplayName = null)
{
    /// <summary>
    /// The most specific name the server gave, or null when all it gave was the alias back.
    /// <para>
    /// An alias equal to the checkpoint means the server was started without an alias, so
    /// repeating it would say the same thing twice.
    /// </para>
    /// </summary>
    public string? Identity
    {
        get
        {
            string? name = string.IsNullOrWhiteSpace(DisplayName) ? Checkpoint : DisplayName;

            return string.IsNullOrWhiteSpace(name) || string.Equals(name, Alias, StringComparison.OrdinalIgnoreCase)
                ? null
                : name;
        }
    }
}

/// <summary>How asking a server for its model list went.</summary>
public enum ServedModelListOutcome
{
    /// <summary>The server answered with a list. It may still have been an empty one.</summary>
    Listed,

    /// <summary>The server refused the credential.</summary>
    Unauthorized,

    /// <summary>The server answered, but not with a list. Plenty implement chat completions and nothing else.</summary>
    Refused,

    /// <summary>Nothing answered inside the timeout.</summary>
    Unreachable,
}

/// <summary>
/// What one <c>GET /models</c> produced. The outcome is carried rather than collapsed into an
/// empty list because "the port is closed", "the key was refused" and "this server has no model
/// list" have three different fixes, and a caller that cannot tell them apart writes one useless
/// message for all three.
/// </summary>
/// <param name="Outcome">Which of the four things happened.</param>
/// <param name="Models">What the server listed, empty unless <see cref="Outcome"/> is <c>Listed</c>.</param>
/// <param name="Url">The URL that was asked - the one thing a failure message has to name.</param>
/// <param name="StatusCode">The HTTP status, when there was a response at all.</param>
/// <param name="Error">The transport failure, when there was not.</param>
public sealed record ServedModelList(
    ServedModelListOutcome Outcome,
    IReadOnlyList<ServedModel> Models,
    Uri Url,
    int? StatusCode = null,
    string? Error = null)
{
    /// <summary>Finds one alias in the list, or null when the server does not serve it.</summary>
    public ServedModel? Find(string alias)
    {
        foreach (ServedModel model in Models)
        {
            if (string.Equals(model.Alias, alias, StringComparison.OrdinalIgnoreCase))
            {
                return model;
            }
        }

        return null;
    }
}

/// <summary>
/// Asks an endpoint what it is serving - one <c>GET</c>, nothing generated.
/// <para>
/// Split out from <see cref="IModelConnectionProbe"/> because the two questions have different
/// costs. The probe's last step is a real completion, which is the point of a "does this work?"
/// button and entirely wrong for anything that runs unasked: it writes a prompt into the server's
/// logs and metrics, and on a cold server it can take the best part of a minute. Reading the
/// model list is sub-second and side-effect free, so it is the only half that a window is allowed
/// to run at startup.
/// </para>
/// </summary>
public interface IServedModelDirectory
{
    /// <summary>Asks one endpoint for its model list. Never throws - the outcome carries the failure.</summary>
    /// <param name="settings">The role whose endpoint, transport and key to ask with.</param>
    /// <param name="timeout">Ceiling for this call, independent of the role's own generous one.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    Task<ServedModelList> ListAsync(
        ModelRoleOptions settings,
        TimeSpan timeout,
        CancellationToken cancellationToken = default);
}
