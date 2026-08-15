using GlassCoder.Tools.Build;
using GlassCoder.Tools.Changes;
using GlassCoder.Tools.Planning;
using GlassCoder.Tools.Execution;
using GlassCoder.Tools.FileSystem;
using GlassCoder.Tools.Git;
using GlassCoder.Tools.Guardrails;
using GlassCoder.Tools.Processes;
using GlassCoder.Tools.Registry;
using GlassCoder.Tools.Retrieval;
using GlassCoder.Tools.Verification;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

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

        services.AddOptions<GitOptions>()
            .Bind(configuration.GetSection(GitOptions.SectionName));

        // Bound and validated whether or not retrieval is on: a misspelled endpoint or a tool
        // entry with no description should fail at startup, not on the first call of the first
        // arm that switches it on (workplan task 55).
        services.AddOptions<RetrievalOptions>()
            .Bind(configuration.GetSection(RetrievalOptions.SectionName))
            .ValidateOnStart();
        services.TryAddSingleton<IValidateOptions<RetrievalOptions>, RetrievalOptionsValidator>();

        services.TryAddSingleton<IPathGuard, PathGuard>();
        services.TryAddSingleton<IProcessRunner, ProcessRunner>();
        services.TryAddSingleton<ITodoList, TodoList>();
        services.TryAddSingleton<IChangeLog, ChangeLog>();

        // Consumed by the context assembler to open each run with a bounded picture of the
        // tree, so a five-file workspace does not cost six discovery steps.
        services.TryAddSingleton<WorkspaceMapBuilder>();

        // Fails closed: when approval is required and no interactive gate is registered, writes
        // are refused rather than silently allowed (workplan task 28).
        services.TryAddSingleton<IApprovalGate, AutoApprovalGate>();

        // Compiler feedback: rungs 1-2 in process, and the summariser that stands between any
        // diagnostic and the model (CLAUDE.md §8.2).
        // Registered concretely as well, so find_symbol can sweep the workspace through the same
        // syntax-tree cache the pre-write compile fills rather than opening a second one.
        services.TryAddSingleton<RoslynCodeAnalyzer>();
        services.TryAddSingleton<ICodeAnalyzer>(sp => sp.GetRequiredService<RoslynCodeAnalyzer>());
        // The summariser optionally reports what it saw to the retrieval signals, which is how
        // "a compile error names something the workspace does not declare" becomes the one thing
        // that admits a lookup (workplan task 59). Resolved lazily and only when retrieval is
        // registered, so a run without it constructs nothing and pays nothing.
        services.TryAddSingleton(provider =>
        {
            DiagnosticRetrievalSignals? signals = provider.GetService<DiagnosticRetrievalSignals>();

            return new DiagnosticSummarizer(
                provider.GetRequiredService<IOptions<VerificationOptions>>(),
                signals is null
                    ? null
                    : (diagnostics, complete) => signals.Observe(
                        diagnostics,
                        name => provider.GetRequiredService<FindSymbolTool>().Declares(name),
                        complete));
        });

        // One tracker for create_file and edit_file together: run 5c071f37 alternated between
        // the two against the same file, and a per-tool count would never reach its limit.
        services.TryAddSingleton<VerificationRefusalTracker>();

        // What each file looked like when this run last read it, so an unchanged re-read says so
        // rather than looking like progress (workplan task 70). Singleton and keyed by run, the
        // same shape as the tracker above.
        services.TryAddSingleton<FileReadMemo>();

        // Execution: a build is arbitrary code execution, so it goes through the sandbox seam.
        // The Dropbox marker rides that seam - a workspace inside Dropbox gets its build
        // output excluded from sync around every command, including folders born mid-run.
        services.TryAddSingleton<DockerCommandExecutor>();
        services.TryAddSingleton<LocalCommandExecutor>();
        services.TryAddSingleton<DropboxIgnoreMarker>();
        services.TryAddSingleton<ICommandExecutor, SandboxedCommandExecutor>();

        AddPhase0Tools(services);
        AddPhase1Tools(services);

        // bash arrives last and only behind the sandbox (CLAUDE.md §7, workplan task 34).
        // Keyed off the property name so the switch cannot drift from the option it reads.
        if (configuration.GetValue($"{SandboxOptions.SectionName}:{nameof(SandboxOptions.EnableBashTool)}", false))
        {
            AddBashTool(services);
        }

        // Version control is opt-in like bash, but runs on the host: the sandbox has neither
        // the credentials nor the network that the later push step is for (workplan task 40).
        if (configuration.GetValue($"{GitOptions.SectionName}:{nameof(GitOptions.Enabled)}", false))
        {
            AddGitTools(services);
        }

        // Retrieval, master switch first (workplan task 55). The per-server switches decide
        // which tools are registered, in task 57; this decides whether the machinery exists at
        // all, so an off run constructs no policy and holds no signals.
        if (configuration.GetValue($"{RetrievalOptions.SectionName}:{nameof(RetrievalOptions.Enabled)}", false))
        {
            AddRetrieval(services);
        }

        services.TryAddSingleton<IToolRegistry>(provider => new ToolRegistry(
            provider.GetRequiredService<IEnumerable<IToolSet>>(),
            provider.GetRequiredService<IEnumerable<IToolFunctionSource>>(),
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
        services.TryAddSingleton<ListProjectsTool>();
        services.TryAddSingleton<ListChangesTool>();
        services.TryAddSingleton<FindSymbolTool>();

        services.AddSingleton<IToolSet>(sp => sp.GetRequiredService<TodoTool>());
        services.AddSingleton<IToolSet>(sp => sp.GetRequiredService<ListChangesTool>());
        services.AddSingleton<IToolSet>(sp => sp.GetRequiredService<ReadFileTool>());
        services.AddSingleton<IToolSet>(sp => sp.GetRequiredService<GrepTool>());
        services.AddSingleton<IToolSet>(sp => sp.GetRequiredService<FindSymbolTool>());
        services.AddSingleton<IToolSet>(sp => sp.GetRequiredService<GlobTool>());
        services.AddSingleton<IToolSet>(sp => sp.GetRequiredService<ListProjectsTool>());
        return services;
    }

    /// <summary>
    /// The tools that close the loop: create and edit, then the two oracles that check them -
    /// <c>build</c> before <c>run_tests</c>, in that order.
    /// </summary>
    public static IServiceCollection AddPhase1Tools(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Shared by the build tool, which fills it, and the project tool, which empties it when
        // the SDK rewrites a project file behind the change log's back.
        services.TryAddSingleton<BuildCache>();

        services.TryAddSingleton<CreateFileTool>();
        services.TryAddSingleton<EditFileTool>();
        services.TryAddSingleton<FileOperationTool>();
        services.TryAddSingleton<BuildTool>();
        services.TryAddSingleton<RunTestsTool>();
        services.TryAddSingleton<DotnetProjectTool>();

        // What running the application showed, carried to the panel that asks for it
        // (workplan task 71). Keyed by run, like the refusal tracker and the read memo.
        services.TryAddSingleton<RuntimeEvidence>();

        // What the run asked for, was refused, and never came back to. Keyed by run, like the
        // refusal tracker and the runtime evidence beside it.
        services.TryAddSingleton<AbandonedIntents>();
        services.TryAddSingleton<IWindowPresence, WindowPresence>();
        services.TryAddSingleton<LaunchAppTool>();

        services.AddSingleton<IToolSet>(sp => sp.GetRequiredService<CreateFileTool>());
        services.AddSingleton<IToolSet>(sp => sp.GetRequiredService<EditFileTool>());
        services.AddSingleton<IToolSet>(sp => sp.GetRequiredService<FileOperationTool>());
        services.AddSingleton<IToolSet>(sp => sp.GetRequiredService<DotnetProjectTool>());
        services.AddSingleton<IToolSet>(sp => sp.GetRequiredService<BuildTool>());
        services.AddSingleton<IToolSet>(sp => sp.GetRequiredService<RunTestsTool>());
        services.AddSingleton<IToolSet>(sp => sp.GetRequiredService<LaunchAppTool>());
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

    /// <summary>
    /// The git tools, step 1: <c>git_status</c> and <c>git_commit</c> - the local, reversible
    /// half of version control (workplan task 40). Push waits for the approval seam (task 41).
    /// </summary>
    public static IServiceCollection AddGitTools(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<GitTool>();
        services.AddSingleton<IToolSet>(sp => sp.GetRequiredService<GitTool>());
        return services;
    }

    /// <summary>
    /// The retrieval gate (workplan task 55): the policy every MCP-facing call passes through,
    /// and the signal seam that decides when one is indicated.
    /// <para>
    /// No tool and no client yet - those arrive in task 57 behind the per-server switches. This
    /// is the admission machinery, and it deliberately opens no socket.
    /// </para>
    /// </summary>
    public static IServiceCollection AddRetrieval(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // What decides whether a call is indicated at all (workplan task 59). Registered
        // concretely as well, because the verification path feeds it and the policy only reads
        // it - two different needs of one object.
        services.TryAddSingleton<DiagnosticRetrievalSignals>();
        services.TryAddSingleton<IRetrievalSignals>(sp => sp.GetRequiredService<DiagnosticRetrievalSignals>());
        services.TryAddSingleton<IRetrievalPolicy, RetrievalPolicy>();

        services.TryAddSingleton<McpRetrievalUpstream>();
        services.TryAddSingleton<IRetrievalUpstream>(sp => sp.GetRequiredService<McpRetrievalUpstream>());

        // Core resolves this against the app data root, where logs and metrics live. A Tools-only
        // host has no such root, so it degrades to a working-directory path rather than throwing
        // - which is what the comment on the Core side promises, and it was not true until this
        // fallback existed.
        services.TryAddSingleton<IRetrievalCache>(sp =>
        {
            string configured = sp.GetRequiredService<IOptions<RetrievalOptions>>().Value.CacheDirectory;
            return new RetrievalCache(string.IsNullOrWhiteSpace(configured)
                ? RetrievalOptions.DefaultCacheDirectory
                : configured);
        });

        services.TryAddSingleton(sp => new CachingRetrievalUpstream(
            sp.GetRequiredService<IRetrievalUpstream>(),
            sp.GetRequiredService<IRetrievalCache>()));

        services.TryAddSingleton(sp => new RetrievalCatalog(
            sp.GetRequiredService<IRetrievalCache>(),
            sp.GetRequiredService<McpRetrievalUpstream>(),
            sp.GetService<ILogger<RetrievalCatalog>>()));

        // The per-server switches, resolved into the tools this session advertises. A server
        // whose switch is off contributes nothing here, which is what makes it absent from the
        // schema rather than present and refusing - the difference between a lever an arm can
        // move and a lever that measures nothing.
        services.AddSingleton<IToolFunctionSource>(sp => BuildRetrievalTools(sp));
        return services;
    }

    private static RetrievalToolSource BuildRetrievalTools(IServiceProvider provider)
    {
        RetrievalOptions options = provider.GetRequiredService<IOptions<RetrievalOptions>>().Value;
        RetrievalCatalog catalog = provider.GetRequiredService<RetrievalCatalog>();
        IRetrievalPolicy policy = provider.GetRequiredService<IRetrievalPolicy>();
        CachingRetrievalUpstream upstream = provider.GetRequiredService<CachingRetrievalUpstream>();
        ILogger? logger = provider.GetService<ILogger<RetrievalToolSource>>();

        List<Microsoft.Extensions.AI.AIFunction> functions = [];

        // Untrusted text reaching an agent that can create and edit files is the risk public
        // code search brings and Learn does not. The approval gate is the backstop, so a run
        // that switches GitHub on without it is told once, at startup, rather than never
        // (workplan task 62).
        if (options.GitHub.Enabled &&
            !provider.GetRequiredService<IOptions<ApprovalOptions>>().Value.RequireApprovalForWrites)
        {
            logger?.LogWarning(
                "GitHub retrieval is enabled and Approval:RequireApprovalForWrites is false. Public " +
                "repository text is attacker-controllable and reaches an agent that can write files; " +
                "the verification ladder still gates every change, but a human gate is the backstop.");
        }

        foreach (RetrievalServer server in options.EnabledServers())
        {
            IReadOnlyList<RetrievalToolDescriptor> advertised = catalog.Describe(server, options.Mode);

            foreach (RetrievalToolOptions configured in options.For(server).Tools)
            {
                RetrievalToolDescriptor? descriptor = advertised.FirstOrDefault(
                    d => string.Equals(d.ServerTool, configured.ServerTool, StringComparison.Ordinal));

                if (descriptor is null)
                {
                    // Configured for a tool the server does not offer. Not fatal - the rest of
                    // the allow-list is still usable - but loud, because a silently missing tool
                    // is an arm quietly measuring something other than what it says.
                    logger?.LogWarning(
                        "The {Server} MCP server does not advertise '{Tool}', so '{Name}' is not registered",
                        server, configured.ServerTool, configured.Name);
                    continue;
                }

                functions.Add(new RetrievalFunction(server, configured, descriptor, options, policy, upstream));
            }
        }

        return new RetrievalToolSource(functions);
    }
}
