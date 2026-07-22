using Microsoft.Extensions.Configuration;

namespace GlassCoder.Core.Configuration;

/// <summary>
/// Builds the configuration both front ends share (CLAUDE.md §13, workplan task 3).
/// <para>
/// Every endpoint URL, model alias, budget and limit lives here rather than in code. An
/// ablation arm is then a configuration file, which is what makes arms comparable at all.
/// </para>
/// </summary>
public static class GlassCoderConfiguration
{
    /// <summary>Default configuration file name.</summary>
    public const string DefaultFileName = "appsettings.json";

    /// <summary>
    /// Builds configuration from, in increasing precedence: the default settings file, an
    /// optional environment-specific file, an explicit configuration file, environment
    /// variables (<c>GlassCoder__Agent__MaxSteps=50</c>), and command-line arguments.
    /// </summary>
    /// <param name="configPath">Explicit configuration file. Must exist when supplied.</param>
    /// <param name="args">Command-line arguments, when the caller has any.</param>
    /// <param name="environment">Environment name for <c>appsettings.{environment}.json</c>.</param>
    /// <param name="basePath">Directory the default files are read from. Defaults to the app directory.</param>
    public static IConfigurationRoot Build(
        string? configPath = null,
        string[]? args = null,
        string? environment = null,
        string? basePath = null)
    {
        string root = basePath ?? AppContext.BaseDirectory;

        ConfigurationBuilder builder = new();
        builder.SetBasePath(root);
        builder.AddJsonFile(DefaultFileName, optional: true, reloadOnChange: false);

        if (!string.IsNullOrWhiteSpace(environment))
        {
            builder.AddJsonFile($"appsettings.{environment}.json", optional: true, reloadOnChange: false);
        }

        if (!string.IsNullOrWhiteSpace(configPath))
        {
            builder.AddJsonFile(Path.GetFullPath(configPath), optional: false, reloadOnChange: false);
        }

        builder.AddEnvironmentVariables();

        if (args is { Length: > 0 })
        {
            builder.AddCommandLine(args);
        }

        return builder.Build();
    }
}
