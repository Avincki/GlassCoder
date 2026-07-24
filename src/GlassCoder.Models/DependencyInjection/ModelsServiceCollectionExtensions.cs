using GlassCoder.Models.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace GlassCoder.Models.DependencyInjection;

/// <summary>Registers the model-client seam (workplan tasks 3-4).</summary>
public static class ModelsServiceCollectionExtensions
{
    /// <summary>
    /// Binds <see cref="ModelsOptions"/> from configuration, validates it at startup, and
    /// registers the role-aware <see cref="IChatClientFactory"/>.
    /// </summary>
    public static IServiceCollection AddGlassCoderModels(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<ModelsOptions>()
            .Bind(configuration.GetSection(ModelsOptions.SectionName))
            .ValidateOnStart();

        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<ModelsOptions>, ModelsOptionsValidator>());
        services.TryAddSingleton<IChatClientFactory, ChatClientFactory>();
        services.TryAddSingleton<IModelConnectionProbe, ModelConnectionProbe>();

        return services;
    }
}
