using Microsoft.Extensions.Options;

namespace GlassCoder.Models.Configuration;

/// <summary>
/// Fails fast on model configuration that would silently misbehave at run time: a missing
/// endpoint, a non-absolute endpoint, or - the one that matters most - a checkpoint path
/// smuggled in where a served alias belongs (CLAUDE.md §6).
/// </summary>
public sealed class ModelsOptionsValidator : IValidateOptions<ModelsOptions>
{
    private static readonly char[] PathIndicators = ['/', '\\'];

    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, ModelsOptions options)
    {
        List<string> failures = [];

        if (options.Roles.Count == 0)
        {
            failures.Add($"{ModelsOptions.SectionName}:Roles must define at least one role.");
        }

        foreach ((string role, ModelRoleOptions settings) in options.Roles)
        {
            string prefix = $"{ModelsOptions.SectionName}:Roles:{role}";

            if (string.IsNullOrWhiteSpace(settings.Endpoint))
            {
                failures.Add($"{prefix}:Endpoint is required.");
            }
            else if (!Uri.TryCreate(settings.Endpoint, UriKind.Absolute, out Uri? endpoint) ||
                     (endpoint.Scheme != Uri.UriSchemeHttp && endpoint.Scheme != Uri.UriSchemeHttps))
            {
                failures.Add($"{prefix}:Endpoint '{settings.Endpoint}' must be an absolute http(s) URI.");
            }

            if (string.IsNullOrWhiteSpace(settings.ModelAlias))
            {
                failures.Add($"{prefix}:ModelAlias is required.");
            }
            else if (settings.ModelAlias.IndexOfAny(PathIndicators) >= 0 || Path.IsPathRooted(settings.ModelAlias))
            {
                failures.Add(
                    $"{prefix}:ModelAlias '{settings.ModelAlias}' looks like a checkpoint path. " +
                    "Address the served alias instead - serving topology lives below the seam.");
            }

            if (settings.TimeoutSeconds is < 1 or > 3600)
            {
                failures.Add($"{prefix}:TimeoutSeconds must be between 1 and 3600.");
            }
        }

        if (!string.IsNullOrWhiteSpace(options.DefaultRole) && !options.Roles.ContainsKey(options.DefaultRole))
        {
            failures.Add($"{ModelsOptions.SectionName}:DefaultRole '{options.DefaultRole}' is not a configured role.");
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }
}
