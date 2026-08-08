using GlassCoder.Core.Context;
using GlassCoder.Core.Orchestration;
using GlassCoder.Core.Verification;
using GlassCoder.Models.Configuration;
using GlassCoder.Tools.Execution;
using GlassCoder.Tools.Retrieval;
using GlassCoder.Tools.Verification;

namespace GlassCoder.Lab.Ablation;

/// <summary>
/// The arms worth running first (CLAUDE.md §17, workplan task 22).
/// <para>
/// Each one isolates a single lever from <c>capability ≈ model × harness × context</c>, because
/// an ablation that moves two things at once measures neither.
/// </para>
/// </summary>
public static class StandardArms
{
    /// <summary>
    /// Everything on. The number every other arm is read against.
    /// <para>
    /// It states the optional capabilities <em>off</em> rather than saying nothing about them,
    /// which is not the same as leaving them out. An arm is a configuration layer over whatever
    /// is already there, and what is already there includes the settings the desktop dialog
    /// saves - so an operator who switched retrieval on to try it would have made every arm
    /// silently a retrieval arm, and the comparison would measure nothing while looking exactly
    /// as it always does. A lever an arm does not name is a lever the arm does not control.
    /// </para>
    /// </summary>
    public static AblationArm Baseline { get; } = new(
        "baseline",
        "The full harness with every lever engaged, and every optional capability explicitly off.",
        NoOptionalCapabilities());

    /// <summary>Harness lever: does constrained decoding actually buy tool-call validity?</summary>
    public static AblationArm NoConstrainedDecoding { get; } = new(
        "no-constrained-decoding",
        "Constrained decoding off. Watch tool-call validity rate.",
        Lever($"{ModelsOptions.SectionName}:Roles:worker:ConstrainedDecoding:Enabled", "false"));

    /// <summary>Harness lever: is the pre-write compile check worth its latency?</summary>
    public static AblationArm NoPreWriteVerification { get; } = new(
        "no-prewrite-verification",
        "Edits are written without the in-memory compile check. Watch compile-error rate per edit.",
        Lever($"{VerificationOptions.SectionName}:VerifyEditsBeforeWrite", "false"));

    /// <summary>Context lever: does the always-loaded root earn its tokens?</summary>
    public static AblationArm NoContext { get; } = new(
        "no-context",
        "No always-loaded root context. Watch pass@1 and tokens-to-solve.",
        Lever($"{ContextOptions.SectionName}:RootContextFiles:0", string.Empty));

    /// <summary>Harness lever: does the summariser change outcomes, or only comfort?</summary>
    public static AblationArm UnsummarisedDiagnostics { get; } = new(
        "unsummarised-diagnostics",
        "Diagnostic cap raised to 500, so the model sees the cascade. Watch tokens and recovery rate.",
        Lever($"{VerificationOptions.SectionName}:MaxSummarisedDiagnostics", "500"));

    /// <summary>Verification lever: what does the Phase 2 critique pass do to recovery rate?</summary>
    public static AblationArm WithCritique { get; } = new(
        "with-critique",
        "Multi-critic refutation enabled on the critic role. Watch recovery rate.",
        Lever($"{CritiqueOptions.SectionName}:Enabled", "true"));

    /// <summary>Capability lever: do sub-agents earn their fan-out?</summary>
    public static AblationArm WithOrchestration { get; } = new(
        "with-orchestration",
        "Sub-agent orchestration enabled. Watch steps/tokens-to-solve and wall-clock.",
        Lever($"{OrchestrationOptions.SectionName}:Enabled", "true"));

    /// <summary>Capability lever: does a shell change outcomes the typed tools cannot reach?</summary>
    public static AblationArm WithBash { get; } = new(
        "with-bash",
        "The bash tool enabled inside the existing sandbox. Watch pass@1 and validity.",
        Lever($"{SandboxOptions.SectionName}:EnableBashTool", "true"));

    /// <summary>The combination: every dormant capability at once, read against each one alone.</summary>
    public static AblationArm AllCapabilities { get; } = new(
        "all-capabilities",
        "Critique, orchestration and bash together - the task 38 combination row.",
        Levers(
            ($"{CritiqueOptions.SectionName}:Enabled", "true"),
            ($"{OrchestrationOptions.SectionName}:Enabled", "true"),
            ($"{SandboxOptions.SectionName}:EnableBashTool", "true")));

    /// <summary>Knowledge lever: does authoritative documentation change what the worker writes?</summary>
    public static AblationArm WithLearn { get; } = new(
        "with-learn",
        "Microsoft Learn registered, replay mode. Watch pass@1 and compile errors per edit.",
        WithServers(learn: true, github: false));

    /// <summary>Knowledge lever: is public code search a hallucination detector worth its schema?</summary>
    public static AblationArm WithCodeSearch { get; } = new(
        "with-code-search",
        "GitHub symbol search registered, replay mode. Watch compile errors per edit.",
        WithServers(learn: false, github: true));

    /// <summary>Both, to see whether they interact or merely double-count.</summary>
    public static AblationArm WithRetrieval { get; } = new(
        "with-retrieval",
        "Learn and GitHub together, replay mode.",
        WithServers(learn: true, github: true));

    /// <summary>
    /// Every optional capability off, and every one of them named.
    /// <para>
    /// The dictionary the arms are built from, so a lever is never inherited from whatever the
    /// desktop settings happen to hold. Retrieval is pinned to Replay as well as off, because an
    /// arm that reached the network would stop being comparable with the one beside it.
    /// </para>
    /// </summary>
    private static Dictionary<string, string?> NoOptionalCapabilities() =>
        new(StringComparer.Ordinal)
        {
            [$"{CritiqueOptions.SectionName}:Enabled"] = "false",
            [$"{OrchestrationOptions.SectionName}:Enabled"] = "false",
            [$"{SandboxOptions.SectionName}:EnableBashTool"] = "false",
            [$"{RetrievalOptions.SectionName}:Enabled"] = "false",
            [$"{RetrievalOptions.SectionName}:Learn:Enabled"] = "false",
            [$"{RetrievalOptions.SectionName}:GitHub:Enabled"] = "false",
            [$"{RetrievalOptions.SectionName}:Mode"] = nameof(RetrievalMode.Replay),
        };

    /// <summary>The explicit baseline with one key moved - which is what "one lever" means.</summary>
    private static Dictionary<string, string?> Lever(string key, string? value) => Levers((key, value));

    /// <summary>The explicit baseline with several keys moved, for the combination arms.</summary>
    private static Dictionary<string, string?> Levers(params (string Key, string? Value)[] levers)
    {
        Dictionary<string, string?> overrides = NoOptionalCapabilities();
        foreach ((string key, string? value) in levers)
        {
            overrides[key] = value;
        }

        return overrides;
    }

    /// <summary>Baseline with one or both retrieval servers switched back on.</summary>
    private static Dictionary<string, string?> WithServers(bool learn, bool github)
    {
        Dictionary<string, string?> overrides = NoOptionalCapabilities();
        overrides[$"{RetrievalOptions.SectionName}:Enabled"] = "true";
        overrides[$"{RetrievalOptions.SectionName}:Learn:Enabled"] = learn ? "true" : "false";
        overrides[$"{RetrievalOptions.SectionName}:GitHub:Enabled"] = github ? "true" : "false";
        return overrides;
    }

    /// <summary>The default comparison: baseline against each single-lever variant.</summary>
    public static IReadOnlyList<AblationArm> Default { get; } =
    [
        Baseline,
        NoConstrainedDecoding,
        NoPreWriteVerification,
        NoContext,
        UnsummarisedDiagnostics,
    ];

    /// <summary>
    /// The task 38 grid: baseline, each dormant capability in isolation, then all of them
    /// together. Isolation says what a capability does; the combination says whether they
    /// still do it in each other's company.
    /// </summary>
    public static IReadOnlyList<AblationArm> Capabilities { get; } =
    [
        Baseline,
        WithCritique,
        WithOrchestration,
        WithBash,
        AllCapabilities,
    ];

    /// <summary>
    /// The retrieval grid: baseline, each server alone, then both. Runnable today and not yet
    /// worth reading - every suite fixture is self-contained, so a perfect retrieval tool scores
    /// exactly the same as none at all until task 60 writes one that asks a question the
    /// repository cannot answer.
    /// </summary>
    public static IReadOnlyList<AblationArm> Retrieval { get; } =
    [
        Baseline,
        WithLearn,
        WithCodeSearch,
        WithRetrieval,
    ];

    /// <summary>Every named arm, for selection by name.</summary>
    public static IReadOnlyList<AblationArm> All { get; } =
    [
        Baseline,
        WithLearn,
        WithCodeSearch,
        WithRetrieval,
        NoConstrainedDecoding,
        NoPreWriteVerification,
        NoContext,
        UnsummarisedDiagnostics,
        WithCritique,
        WithOrchestration,
        WithBash,
        AllCapabilities,
    ];

    /// <summary>
    /// Resolves an arm selection: a set name (<c>default</c> or <c>capabilities</c>) or
    /// comma-separated arm names, deduplicated in the order given. Null or blank selects
    /// <see cref="Default"/>.
    /// </summary>
    /// <returns>The arms, or null when a name matches nothing - <paramref name="unknown"/> says which.</returns>
    public static IReadOnlyList<AblationArm>? Resolve(string? selection, out string? unknown)
    {
        unknown = null;

        if (string.IsNullOrWhiteSpace(selection)
            || selection.Equals("default", StringComparison.OrdinalIgnoreCase))
        {
            return Default;
        }

        if (selection.Equals("capabilities", StringComparison.OrdinalIgnoreCase))
        {
            return Capabilities;
        }

        if (selection.Equals("retrieval", StringComparison.OrdinalIgnoreCase))
        {
            return Retrieval;
        }

        List<AblationArm> arms = [];
        foreach (string name in selection.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            AblationArm? arm = All.FirstOrDefault(a => a.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (arm is null)
            {
                unknown = name;
                return null;
            }

            if (!arms.Contains(arm))
            {
                arms.Add(arm);
            }
        }

        return arms.Count == 0 ? Default : arms;
    }
}
