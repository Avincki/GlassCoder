using GlassCoder.Models.Configuration;

namespace GlassCoder.Models;

/// <summary>How a single check went.</summary>
public enum ConnectionCheckOutcome
{
    /// <summary>The check passed.</summary>
    Ok,

    /// <summary>The check did not pass, but the seam still works. Worth reading, not blocking.</summary>
    Warning,

    /// <summary>The seam does not work with these settings.</summary>
    Failed,
}

/// <summary>One step of a connection check.</summary>
/// <param name="Name">Short name, for the UI.</param>
/// <param name="Outcome">How it went.</param>
/// <param name="Detail">What happened, in words a person can act on.</param>
/// <param name="ElapsedMs">Wall-clock for this step.</param>
public sealed record ConnectionCheckStep(string Name, ConnectionCheckOutcome Outcome, string Detail, double ElapsedMs);

/// <summary>What a connection check found.</summary>
/// <param name="Role">The role that was checked.</param>
/// <param name="Outcome">The worst outcome of any step.</param>
/// <param name="Summary">One line for the UI.</param>
/// <param name="Steps">Every step attempted, in order.</param>
/// <param name="ServedModels">Aliases the server said it serves, when it was willing to say.</param>
/// <param name="ElapsedMs">Wall-clock for the whole check.</param>
public sealed record ConnectionCheckResult(
    string Role,
    ConnectionCheckOutcome Outcome,
    string Summary,
    IReadOnlyList<ConnectionCheckStep> Steps,
    IReadOnlyList<string> ServedModels,
    double ElapsedMs)
{
    /// <summary>Whether the seam works well enough to run against.</summary>
    public bool Succeeded => Outcome != ConnectionCheckOutcome.Failed;
}

/// <summary>
/// Answers "do these settings actually work?" before a run does (CLAUDE.md §6, §19).
/// <para>
/// Worth a seam of its own because the failure modes are distinct and a single "it did not
/// work" hides all of them: nothing listening on the port, a server that is up but rejects the
/// key, a key that is accepted but an alias that is not served, and an alias that is served but
/// cannot complete. Each has a different fix, so each is reported separately.
/// </para>
/// <para>
/// The settings are passed in rather than read from bound options on purpose: the settings
/// dialog has to be able to test what is on screen, not what the harness started with.
/// </para>
/// </summary>
public interface IModelConnectionProbe
{
    /// <summary>Checks one role's settings end to end.</summary>
    /// <param name="role">Role name, for the report.</param>
    /// <param name="settings">The settings to test - typically unsaved ones from the dialog.</param>
    /// <param name="cancellationToken">Cancels the check.</param>
    Task<ConnectionCheckResult> CheckAsync(
        string role,
        ModelRoleOptions settings,
        CancellationToken cancellationToken = default);
}
