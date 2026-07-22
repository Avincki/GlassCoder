using System.ComponentModel.DataAnnotations;

namespace GlassCoder.Models.Configuration;

/// <summary>
/// One served role behind the seam (CLAUDE.md §6). Everything here is configuration: swapping
/// a local server for a hosted API, or a 8B worker for a 200B drafter, must never be a code
/// change - that is exactly what the ablation harness depends on.
/// </summary>
public sealed class ModelRoleOptions
{
    /// <summary>
    /// OpenAI-compatible base endpoint, for example <c>http://localhost:8001/v1</c>.
    /// Never hardcoded anywhere in the harness.
    /// </summary>
    [Required]
    public string Endpoint { get; set; } = string.Empty;

    /// <summary>
    /// The <em>served-model alias</em> to address (for example <c>worker</c>), never a
    /// checkpoint path. Serving topology lives below the seam (CLAUDE.md §19).
    /// </summary>
    [Required]
    public string ModelAlias { get; set; } = string.Empty;

    /// <summary>
    /// API key for hosted arms. Prefer <see cref="ApiKeyEnvironmentVariable"/> so the secret
    /// never lands in a config file. Local servers usually ignore this entirely.
    /// </summary>
    public string? ApiKey { get; set; }

    /// <summary>Environment variable to read the API key from. Takes precedence over <see cref="ApiKey"/>.</summary>
    public string? ApiKeyEnvironmentVariable { get; set; }

    /// <summary>Per-request network timeout. Local generation can be slow; default is generous.</summary>
    [Range(1, 3600)]
    public int TimeoutSeconds { get; set; } = 600;

    /// <summary>Cap on tokens generated per model call. Null leaves it to the server.</summary>
    public int? MaxOutputTokens { get; set; }

    /// <summary>Sampling temperature. Tool-calling arms usually want this low.</summary>
    public float? Temperature { get; set; }

    /// <summary>Nucleus sampling cutoff.</summary>
    public float? TopP { get; set; }

    /// <summary>Seed, where the server supports it - ablations want reproducible arms.</summary>
    public long? Seed { get; set; }

    /// <summary>Cost per million input tokens, used for the cost-per-solved-task metric (§11).</summary>
    public decimal InputCostPerMillionTokens { get; set; }

    /// <summary>Cost per million output tokens, used for the cost-per-solved-task metric (§11).</summary>
    public decimal OutputCostPerMillionTokens { get; set; }

    /// <summary>Request-side constrained decoding for this role (CLAUDE.md §6).</summary>
    public ConstrainedDecodingOptions ConstrainedDecoding { get; set; } = new();

    /// <summary>
    /// Extra request properties merged into every call for this role. Values that parse as
    /// JSON are sent as JSON; everything else is sent as a string.
    /// </summary>
    public IDictionary<string, string> AdditionalRequestProperties { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>Resolves the effective API key, preferring the environment variable.</summary>
    public string? ResolveApiKey()
    {
        if (!string.IsNullOrWhiteSpace(ApiKeyEnvironmentVariable))
        {
            string? fromEnvironment = Environment.GetEnvironmentVariable(ApiKeyEnvironmentVariable);
            if (!string.IsNullOrWhiteSpace(fromEnvironment))
            {
                return fromEnvironment;
            }
        }

        return string.IsNullOrWhiteSpace(ApiKey) ? null : ApiKey;
    }
}
