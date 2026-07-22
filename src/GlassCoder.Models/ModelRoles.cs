namespace GlassCoder.Models;

/// <summary>
/// The served-model aliases the harness addresses (CLAUDE.md §2, §6). A role is a name the
/// model server serves - never a checkpoint path - so that swapping local for hosted, or a
/// small model for a large one, is a configuration change and never a code change.
/// </summary>
/// <remarks>
/// These are the well-known roles the harness itself reasons about. Configuration may define
/// any number of additional roles; nothing in the harness requires a role to be one of these.
/// </remarks>
public static class ModelRoles
{
    /// <summary>The default role that drives the controller loop.</summary>
    public const string Worker = "worker";

    /// <summary>A (usually larger) role used for drafting or hard sub-problems.</summary>
    public const string Drafter = "drafter";

    /// <summary>
    /// The reviewing role used by the verification critique pass. Ideally a different model
    /// family from <see cref="Worker"/> so its failure modes are uncorrelated.
    /// </summary>
    public const string Critic = "critic";
}
