using GlassCoder.Core.Agent;
using GlassCoder.Core.Context;
using GlassCoder.Core.Diagnostics;
using GlassCoder.Core.Metrics;
using GlassCoder.Core.Orchestration;
using GlassCoder.Core.Provenance;
using GlassCoder.Core.Verification;
using GlassCoder.Models.Configuration;
using GlassCoder.Tools;
using GlassCoder.Tools.Changes;
using GlassCoder.Tools.Execution;
using GlassCoder.Tools.Git;
using GlassCoder.Tools.Guardrails;
using GlassCoder.Tools.Retrieval;
using GlassCoder.Tools.Verification;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace GlassCoder.Core.Configuration;

/// <summary>
/// Every configurable section of the harness, in one object (CLAUDE.md §13).
/// <para>
/// This is deliberately an aggregate of the <em>real</em> options classes rather than a parallel
/// set of editable copies. A settings dialog built on a second, hand-maintained model of the
/// configuration drifts from the one the harness binds the moment somebody adds a property, and
/// then the UI quietly stops being able to set it. Here a new option shows up in the file and
/// round-trips correctly the day it is added; only its editor has to be written.
/// </para>
/// </summary>
public sealed class GlassCoderSettings
{
    /// <summary>Root configuration section every setting hangs off.</summary>
    public const string RootSectionName = "GlassCoder";

    /// <summary>Served roles and the seam (<c>GlassCoder:Models</c>).</summary>
    public ModelsOptions Models { get; init; } = new();

    /// <summary>What the agent may read and write (<c>GlassCoder:Workspace</c>).</summary>
    public WorkspaceOptions Workspace { get; init; } = new();

    /// <summary>Per-call tool limits (<c>GlassCoder:Tools</c>).</summary>
    public ToolsOptions Tools { get; init; } = new();

    /// <summary>Loop budgets (<c>GlassCoder:Agent</c>).</summary>
    public AgentOptions Agent { get; init; } = new();

    /// <summary>Context-window policy (<c>GlassCoder:Context</c>).</summary>
    public ContextOptions Context { get; init; } = new();

    /// <summary>Compiler-feedback settings (<c>GlassCoder:Verification</c>).</summary>
    public VerificationOptions Verification { get; init; } = new();

    /// <summary>Ladder settings (<c>GlassCoder:VerificationLadder</c>).</summary>
    public VerificationLadderOptions VerificationLadder { get; init; } = new();

    /// <summary>Where commands may run (<c>GlassCoder:Sandbox</c>).</summary>
    public SandboxOptions Sandbox { get; init; } = new();

    /// <summary>Human-in-the-loop gating (<c>GlassCoder:Approval</c>).</summary>
    public ApprovalOptions Approval { get; init; } = new();

    /// <summary>Version control (<c>GlassCoder:Git</c>).</summary>
    public GitOptions Git { get; init; } = new();

    /// <summary>Retrieval over MCP (<c>GlassCoder:Retrieval</c>).</summary>
    public RetrievalOptions Retrieval { get; init; } = new();

    /// <summary>Critic panel (<c>GlassCoder:Critique</c>).</summary>
    public CritiqueOptions Critique { get; init; } = new();

    /// <summary>On-demand review of one file (<c>GlassCoder:FileReview</c>).</summary>
    public FileReviewOptions FileReview { get; init; } = new();

    /// <summary>The three-stage look back at a finished run (<c>GlassCoder:Retrospective</c>).</summary>
    public RetrospectiveOptions Retrospective { get; init; } = new();

    /// <summary>Sub-agents (<c>GlassCoder:Orchestration</c>).</summary>
    public OrchestrationOptions Orchestration { get; init; } = new();

    /// <summary>Run stamping and freshness (<c>GlassCoder:Provenance</c>).</summary>
    public ProvenanceOptions Provenance { get; init; } = new();

    /// <summary>Metrics sink (<c>GlassCoder:Metrics</c>).</summary>
    public MetricsOptions Metrics { get; init; } = new();

    /// <summary>Log sinks and redaction (<c>GlassCoder:Logging</c>).</summary>
    public LoggingOptions Logging { get; init; } = new();

    /// <summary>Tracing (<c>GlassCoder:Telemetry</c>).</summary>
    public TelemetryOptions Telemetry { get; init; } = new();

    /// <summary>
    /// Reads the <em>effective</em> configuration - every layer, including environment variables
    /// and any <c>--config</c> arm - so the dialog shows what the harness is actually running on
    /// rather than what one file happens to say.
    /// </summary>
    public static GlassCoderSettings ReadFrom(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        GlassCoderSettings settings = new()
        {
            Models = Section<ModelsOptions>(configuration, ModelsOptions.SectionName),
            Workspace = Section<WorkspaceOptions>(configuration, WorkspaceOptions.SectionName),
            Tools = Section<ToolsOptions>(configuration, ToolsOptions.SectionName),
            Agent = Section<AgentOptions>(configuration, AgentOptions.SectionName),
            Context = Section<ContextOptions>(configuration, ContextOptions.SectionName),
            Verification = Section<VerificationOptions>(configuration, VerificationOptions.SectionName),
            VerificationLadder = Section<VerificationLadderOptions>(configuration, VerificationLadderOptions.SectionName),
            Sandbox = Section<SandboxOptions>(configuration, SandboxOptions.SectionName),
            Approval = Section<ApprovalOptions>(configuration, ApprovalOptions.SectionName),
            Git = Section<GitOptions>(configuration, GitOptions.SectionName),
            Retrieval = Section<RetrievalOptions>(configuration, RetrievalOptions.SectionName),
            Critique = Section<CritiqueOptions>(configuration, CritiqueOptions.SectionName),

            // Both of these were missing, and a section this method does not read is a section
            // Save() then writes back as defaults - the file loses it. GlassCoder:Retrospective
            // held HarnessRepoPath on this operator's machine until a save from the dialog on
            // 2026-08-09 dropped the whole section, and the work-order button has been greyed out
            // with "set HarnessRepoPath" ever since. Every_section_on_the_settings_model_is_read_
            // back_from_configuration now fails for the next one, rather than a person noticing
            // months later that a feature quietly stopped working.
            FileReview = Section<FileReviewOptions>(configuration, FileReviewOptions.SectionName),
            Retrospective = Section<RetrospectiveOptions>(configuration, RetrospectiveOptions.SectionName),
            Orchestration = Section<OrchestrationOptions>(configuration, OrchestrationOptions.SectionName),
            Provenance = Section<ProvenanceOptions>(configuration, ProvenanceOptions.SectionName),
            Metrics = Section<MetricsOptions>(configuration, MetricsOptions.SectionName),
            Logging = Section<LoggingOptions>(configuration, LoggingOptions.SectionName),
            Telemetry = Section<TelemetryOptions>(configuration, TelemetryOptions.SectionName),
        };

        settings.DeduplicateLists();
        return settings;
    }

    /// <summary>
    /// Everything wrong with these settings, in the same words the startup validators would use.
    /// An empty list means the harness would start on them.
    /// </summary>
    public IReadOnlyList<string> Validate()
    {
        List<string> failures = [];

        ValidateOptionsResult models = new ModelsOptionsValidator().Validate(Options.DefaultName, Models);
        if (models.Failed && models.Failures is not null)
        {
            failures.AddRange(models.Failures);
        }

        if (Agent.MaxSteps < 1)
        {
            failures.Add($"{AgentOptions.SectionName}:MaxSteps must be at least 1.");
        }

        if (!Models.Roles.ContainsKey(Agent.Role))
        {
            failures.Add($"{AgentOptions.SectionName}:Role '{Agent.Role}' is not a configured role.");
        }

        if (Critique.Enabled && !Models.Roles.ContainsKey(Critique.Role))
        {
            failures.Add($"{CritiqueOptions.SectionName}:Role '{Critique.Role}' is not a configured role.");
        }

        // The same validator the startup path runs, so the dialog refuses what the harness would
        // refuse rather than saving a configuration that fails on the next launch.
        ValidateOptionsResult retrieval = new RetrievalOptionsValidator().Validate(Options.DefaultName, Retrieval);
        if (retrieval.Failed && retrieval.Failures is not null)
        {
            failures.AddRange(retrieval.Failures);
        }

        if (Context.CompactionThreshold is <= 0 or > 1)
        {
            failures.Add($"{ContextOptions.SectionName}:CompactionThreshold must be greater than 0 and at most 1.");
        }

        if (Context.CharactersPerToken <= 0)
        {
            failures.Add($"{ContextOptions.SectionName}:CharactersPerToken must be greater than 0.");
        }

        if (Sandbox.Mode == SandboxMode.Local && !Sandbox.AllowUnsandboxedExecution)
        {
            failures.Add(
                $"{SandboxOptions.SectionName}:Mode is Local but AllowUnsandboxedExecution is off, so no command " +
                "could run. A build is arbitrary code execution - opt in deliberately or stay on Docker.");
        }

        if (!string.IsNullOrWhiteSpace(Telemetry.OtlpEndpoint) &&
            !Uri.TryCreate(Telemetry.OtlpEndpoint, UriKind.Absolute, out _))
        {
            failures.Add($"{TelemetryOptions.SectionName}:OtlpEndpoint '{Telemetry.OtlpEndpoint}' is not an absolute URI.");
        }

        failures.AddRange(ValidateGit());

        return failures;
    }

    /// <summary>
    /// Git settings are only checked when the tools are switched on: a name that would be
    /// unusable is not worth blocking a save over while nothing can call git anyway.
    /// </summary>
    private IEnumerable<string> ValidateGit()
    {
        if (!Git.Enabled)
        {
            yield break;
        }

        if (string.IsNullOrWhiteSpace(Git.GitExecutable))
        {
            yield return $"{GitOptions.SectionName}:GitExecutable is required when the git tools are enabled.";
        }

        // The tools refuse a remote or branch that git would read as an option, so a name that
        // can never work should be caught here rather than as a puzzling tool failure later.
        if (!IsUsableName(Git.Remote))
        {
            yield return $"{GitOptions.SectionName}:Remote '{Git.Remote}' must be a name without spaces or a leading '-'.";
        }

        foreach (string branch in Git.PushableBranches.Concat(Git.ProtectedBranches))
        {
            if (!IsUsableName(branch))
            {
                yield return $"{GitOptions.SectionName}: branch '{branch}' must be a name without spaces or a leading '-'.";
            }
        }

        if (Git.PullRequestBaseBranch is { } baseBranch && !IsUsableName(baseBranch))
        {
            yield return
                $"{GitOptions.SectionName}:PullRequestBaseBranch '{baseBranch}' must be a name without spaces or a leading '-'.";
        }

        if (Git.CommandTimeoutSeconds < 1)
        {
            yield return $"{GitOptions.SectionName}:CommandTimeoutSeconds must be at least 1.";
        }

        // Every branch is refused when the allow-list and the deny-list agree on nothing.
        if (Git.PushableBranches.Count > 0 &&
            Git.PushableBranches.All(b => Git.ProtectedBranches.Contains(b, StringComparer.Ordinal)))
        {
            yield return
                $"{GitOptions.SectionName}: every pushable branch is also protected, so nothing could ever be pushed.";
        }
    }

    private static bool IsUsableName(string? name) =>
        !string.IsNullOrWhiteSpace(name) && name[0] != '-' && !name.Any(char.IsWhiteSpace);

    private static T Section<T>(IConfiguration configuration, string sectionName)
        where T : class, new() =>
        configuration.GetSection(sectionName).Get<T>() ?? new T();

    /// <summary>
    /// Collapses repeated entries in every list-valued setting.
    /// <para>
    /// The configuration binder <em>appends</em> to a list that already has defaults rather than
    /// replacing it. Without this, saving the effective configuration back to disk would write
    /// out the defaults, which the binder would then append to the defaults again on the next
    /// load - a list that doubles on every visit to the settings dialog.
    /// </para>
    /// </summary>
    private void DeduplicateLists()
    {
        Deduplicate(Models.KnownEndpoints);
        Deduplicate(Workspace.ReadablePaths);
        Deduplicate(Workspace.WritablePaths);
        Deduplicate(Workspace.DeniedGlobs);
        Deduplicate(Context.RootContextFiles);
        Deduplicate(Verification.ExtraReferenceDirectories);
        Deduplicate(Sandbox.Environment);
        Deduplicate(Provenance.TriggerExclusions);
        Deduplicate(Provenance.SourceExtensions);
        Deduplicate(Logging.RedactedPropertyNames);
        Deduplicate(Telemetry.AdditionalSources);
        Deduplicate(Git.PushableBranches);
        Deduplicate(Git.ProtectedBranches);
    }

    private static void Deduplicate(IList<string> values)
    {
        // Forward, keeping the first occurrence: the defaults come first and their order is
        // meaningful (the denied globs read as a policy, not as a set).
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        int index = 0;
        while (index < values.Count)
        {
            if (seen.Add(values[index]))
            {
                index++;
            }
            else
            {
                values.RemoveAt(index);
            }
        }
    }
}
