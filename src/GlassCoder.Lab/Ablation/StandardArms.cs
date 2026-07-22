using GlassCoder.Core.Context;
using GlassCoder.Core.Verification;
using GlassCoder.Models.Configuration;
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
    /// <summary>Everything on. The number every other arm is read against.</summary>
    public static AblationArm Baseline { get; } = new(
        "baseline",
        "The full harness with every lever engaged.",
        new Dictionary<string, string?>(StringComparer.Ordinal));

    /// <summary>Harness lever: does constrained decoding actually buy tool-call validity?</summary>
    public static AblationArm NoConstrainedDecoding { get; } = new(
        "no-constrained-decoding",
        "Constrained decoding off. Watch tool-call validity rate.",
        new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [$"{ModelsOptions.SectionName}:Roles:worker:ConstrainedDecoding:Enabled"] = "false",
        });

    /// <summary>Harness lever: is the pre-write compile check worth its latency?</summary>
    public static AblationArm NoPreWriteVerification { get; } = new(
        "no-prewrite-verification",
        "Edits are written without the in-memory compile check. Watch compile-error rate per edit.",
        new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [$"{VerificationOptions.SectionName}:VerifyEditsBeforeWrite"] = "false",
        });

    /// <summary>Context lever: does the always-loaded root earn its tokens?</summary>
    public static AblationArm NoContext { get; } = new(
        "no-context",
        "No always-loaded root context. Watch pass@1 and tokens-to-solve.",
        new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [$"{ContextOptions.SectionName}:RootContextFiles:0"] = string.Empty,
        });

    /// <summary>Harness lever: does the summariser change outcomes, or only comfort?</summary>
    public static AblationArm UnsummarisedDiagnostics { get; } = new(
        "unsummarised-diagnostics",
        "Diagnostic cap raised to 500, so the model sees the cascade. Watch tokens and recovery rate.",
        new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [$"{VerificationOptions.SectionName}:MaxSummarisedDiagnostics"] = "500",
        });

    /// <summary>Verification lever: what does the Phase 2 critique pass do to recovery rate?</summary>
    public static AblationArm WithCritique { get; } = new(
        "with-critique",
        "Multi-critic refutation enabled on the critic role. Watch recovery rate.",
        new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [$"{CritiqueOptions.SectionName}:Enabled"] = "true",
        });

    /// <summary>The default comparison: baseline against each single-lever variant.</summary>
    public static IReadOnlyList<AblationArm> Default { get; } =
    [
        Baseline,
        NoConstrainedDecoding,
        NoPreWriteVerification,
        NoContext,
        UnsummarisedDiagnostics,
    ];
}
