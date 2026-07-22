using GlassCoder.Core.Agent;
using GlassCoder.Core.Context;
using GlassCoder.Core.Diagnostics;
using GlassCoder.Core.Metrics;
using GlassCoder.Core.Verification;
using GlassCoder.Models.Configuration;
using GlassCoder.Models.DependencyInjection;
using GlassCoder.Tools.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;
using Serilog.Core;

namespace GlassCoder.Core.DependencyInjection;

/// <summary>
/// The shared service bootstrap (CLAUDE.md §4, workplan task 3). The WPF app and the console
/// host register exactly the same services through this one entry point - anything else and
/// the two front ends would slowly diverge into different agents.
/// </summary>
public static class GlassCoderServiceCollectionExtensions
{
    /// <summary>Registers models, tools, the controller loop, logging and telemetry.</summary>
    public static IServiceCollection AddGlassCoder(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddGlassCoderModels(configuration);
        services.AddGlassCoderTools(configuration);

        services.AddOptions<AgentOptions>()
            .Bind(configuration.GetSection(AgentOptions.SectionName))
            .ValidateOnStart();

        services.AddOptions<ContextOptions>()
            .Bind(configuration.GetSection(ContextOptions.SectionName));

        services.AddOptions<LoggingOptions>()
            .Bind(configuration.GetSection(LoggingOptions.SectionName));

        services.AddOptions<TelemetryOptions>()
            .Bind(configuration.GetSection(TelemetryOptions.SectionName));

        services.AddOptions<MetricsOptions>()
            .Bind(configuration.GetSection(MetricsOptions.SectionName));

        services.AddOptions<VerificationLadderOptions>()
            .Bind(configuration.GetSection(VerificationLadderOptions.SectionName));

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IStepLogger, StepLogger>();
        services.TryAddSingleton<ITokenEstimator, HeuristicTokenEstimator>();
        services.TryAddSingleton<IConversationCompactor, DigestCompactor>();
        services.TryAddSingleton<IContextAssembler, ContextAssembler>();
        services.TryAddSingleton<IMetricsRecorder, JsonlMetricsRecorder>();
        services.TryAddSingleton<IVerificationLadder, VerificationLadder>();
        services.TryAddTransient<IAgentLoop, AgentLoop>();

        services.AddGlassCoderTelemetry(configuration);

        return services;
    }

    /// <summary>
    /// Replaces the logging providers with the Serilog pipeline from
    /// <see cref="SerilogBootstrap"/>: JSONL for machines, text for people (CLAUDE.md §9).
    /// </summary>
    public static IServiceCollection AddGlassCoderLogging(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        LoggingOptions options = configuration.GetSection(LoggingOptions.SectionName).Get<LoggingOptions>()
            ?? new LoggingOptions();
        Logger logger = SerilogBootstrap.CreateLogger(options);

        services.AddLogging(builder =>
        {
            builder.ClearProviders();
            builder.AddSerilog(logger, dispose: true);
        });

        return services;
    }

    /// <summary>
    /// Subscribes a tracer provider to the harness source and the model-client source, so every
    /// model call is traced without a call site having to remember to do it (CLAUDE.md §9).
    /// </summary>
    public static IServiceCollection AddGlassCoderTelemetry(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        TelemetryOptions telemetry = configuration.GetSection(TelemetryOptions.SectionName).Get<TelemetryOptions>()
            ?? new TelemetryOptions();
        if (!telemetry.Enabled)
        {
            return services;
        }

        ModelsOptions models = configuration.GetSection(ModelsOptions.SectionName).Get<ModelsOptions>()
            ?? new ModelsOptions();

        services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService(telemetry.ServiceName))
            .WithTracing(tracing =>
            {
                tracing.AddSource(GlassCoderActivity.SourceName);
                tracing.AddSource(models.TelemetrySourceName);
                foreach (string source in telemetry.AdditionalSources)
                {
                    tracing.AddSource(source);
                }

                if (telemetry.ConsoleExporter)
                {
                    tracing.AddConsoleExporter();
                }

                if (!string.IsNullOrWhiteSpace(telemetry.OtlpEndpoint))
                {
                    tracing.AddOtlpExporter(exporter => exporter.Endpoint = new Uri(telemetry.OtlpEndpoint));
                }
            });

        return services;
    }
}
