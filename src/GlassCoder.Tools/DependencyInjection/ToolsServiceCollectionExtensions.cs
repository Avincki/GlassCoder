using GlassCoder.Tools.Build;
using GlassCoder.Tools.Changes;
using GlassCoder.Tools.Planning;
using GlassCoder.Tools.Execution;
using GlassCoder.Tools.FileSystem;
using GlassCoder.Tools.Guardrails;
using GlassCoder.Tools.Processes;
using GlassCoder.Tools.Registry;
using GlassCoder.Tools.Verification;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace GlassCoder.Tools.DependencyInjection;

/// <summary>Registers the tool subsystem (workplan tasks 7-9, 14-17).</summary>
public static class ToolsServiceCollectionExtensions
{
    /// <summary>
    /// Binds tool, workspace, verification and sandbox options, then registers the guardrail,
    /// the process and command seams, the compiler-feedback rungs, the tools themselves, and the
    /// registry that generates their schemas.
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

        services.AddOptions<VerificationOptions>()
            .Bind(configuration.GetSection(VerificationOptions.SectionName))
            .ValidateOnStart();

        services.AddOptions<SandboxOptions>()
            .Bind(configuration.GetSection(SandboxOptions.SectionName))
            .ValidateOnStart();

        services.AddOptions<ApprovalOptions>()
            .Bind(configuration.GetSection(ApprovalOptions.SectionName));

        services.TryAddSingleton<IPathGuard, PathGuard>();
        services.TryAddSingleton<IProcessRunner, ProcessRunner>();
        services.TryAddSingleton<ITodoList, TodoList>();
        services.TryAddSingleton<IChangeLog, ChangeLog>();

        // Fails closed: when approval is required and no interactive gate is registered, writes
        // are refused rather than silently allowed (workplan task 28).
        services.TryAddSingleton<IApprovalGate, AutoApprovalGate>();

        // Compiler feedback: rungs 1-2 in process, and the summariser that stands between any
        // diagnostic and the model (CLAUDE.md §8.2).
        services.TryAddSingleton<ICodeAnalyzer, RoslynCodeAnalyzer>();
        services.TryAddSingleton<DiagnosticSummarizer>();

        // Execution: a build is arbitrary code execution, so it goes through the sandbox seam.
        services.TryAddSingleton<DockerCommandExecutor>();
        services.TryAddSingleton<LocalCommandExecutor>();
        services.TryAddSingleton<ICommandExecutor, SandboxedCommandExecutor>();

        AddPhase0Tools(services);
        AddPhase1Tools(services);

        // bash arrives last and only behind the sandbox (CLAUDE.md §7, workplan task 34).
        if (configuration.GetValue(SandboxOptions.SectionName + ":EnableBashTool", false))
        {
            AddBashTool(services);
        }

        services.TryAddSingleton<IToolRegistry>(provider => new ToolRegistry(
            provider.GetRequiredService<IEnumerable<IToolSet>>(),
            provider.GetService<ILogger<ToolRegistry>>()));

        return services;
    }

    /// <summary>
    /// The read-only tool set. Phase 0 runs with these alone so tool-call validity can be
    /// measured before the agent is allowed to change anything (CLAUDE.md §17).
    /// </summary>
    public static IServiceCollection AddPhase0Tools(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Registered concretely as well as behind IToolSet, so the verification ladder can
        // drive build and run_tests directly without going through the model-facing registry.
        services.TryAddSingleton<ReadFileTool>();
        services.TryAddSingleton<GrepTool>();
        services.TryAddSingleton<GlobTool>();
        services.TryAddSingleton<TodoTool>();

        services.AddSingleton<IToolSet>(sp => sp.GetRequiredService<TodoTool>());
        services.AddSingleton<IToolSet>(sp => sp.GetRequiredService<ReadFileTool>());
        services.AddSingleton<IToolSet>(sp => sp.GetRequiredService<GrepTool>());
        services.AddSingleton<IToolSet>(sp => sp.GetRequiredService<GlobTool>());
        return services;
    }

    /// <summary>
    /// The tools that close the loop: edit, then the two oracles that check the edit -
    /// <c>build</c> before <c>run_tests</c>, in that order.
    /// </summary>
    public static IServiceCollection AddPhase1Tools(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<EditFileTool>();
        services.TryAddSingleton<BuildTool>();
        services.TryAddSingleton<RunTestsTool>();

        services.AddSingleton<IToolSet>(sp => sp.GetRequiredService<EditFileTool>());
        services.AddSingleton<IToolSet>(sp => sp.GetRequiredService<BuildTool>());
        services.AddSingleton<IToolSet>(sp => sp.GetRequiredService<RunTestsTool>());
        return services;
    }

    /// <summary>
    /// The <c>bash</c> tool. Opt-in, and only meaningful with a working sandbox: it is exactly
    /// as privileged as running a build (CLAUDE.md §8.4).
    /// </summary>
    public static IServiceCollection AddBashTool(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<BashTool>();
        services.AddSingleton<IToolSet>(sp => sp.GetRequiredService<BashTool>());
        return services;
    }
}
