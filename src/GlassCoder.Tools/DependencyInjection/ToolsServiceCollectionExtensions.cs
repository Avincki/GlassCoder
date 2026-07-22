using GlassCoder.Tools.FileSystem;
using GlassCoder.Tools.Guardrails;
using GlassCoder.Tools.Processes;
using GlassCoder.Tools.Registry;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace GlassCoder.Tools.DependencyInjection;

/// <summary>Registers the tool subsystem (workplan tasks 7-9).</summary>
public static class ToolsServiceCollectionExtensions
{
    /// <summary>
    /// Binds tool and workspace options, registers the guardrail, the process seam, the Phase 0
    /// read-only tools, and the registry that generates their schemas.
    /// </summary>
    public static IServiceCollection AddGlassCoderTools(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<ToolsOptions>()
            .Bind(configuration.GetSection(ToolsOptions.SectionName))
            .ValidateOnStart();

        services.AddOptions<WorkspaceOptions>()
            .Bind(configuration.GetSection(WorkspaceOptions.SectionName))
            .ValidateOnStart();

        services.TryAddSingleton<IPathGuard, PathGuard>();
        services.TryAddSingleton<IProcessRunner, ProcessRunner>();

        // Phase 0 tool set: read, grep, glob. Editing and building arrive in Phase 1.
        services.AddSingleton<IToolSet, ReadFileTool>();
        services.AddSingleton<IToolSet, GrepTool>();
        services.AddSingleton<IToolSet, GlobTool>();

        services.TryAddSingleton<IToolRegistry>(provider => new ToolRegistry(
            provider.GetRequiredService<IEnumerable<IToolSet>>(),
            provider.GetService<ILogger<ToolRegistry>>()));

        return services;
    }
}
