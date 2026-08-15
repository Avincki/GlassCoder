namespace GlassCoder.Core.Verification;

/// <summary>
/// The words a verification outcome is told in, chosen in one place.
/// <para>
/// The verdict vocabulary has been precise where it is computed and lossy at every re-telling.
/// <see cref="RungResult.Unverified"/> and <see cref="RungResult.Noticed"/> exist, are persisted on
/// <c>StepVerificationRecord</c> so a run can be grepped for them, and were then dropped by three
/// separate renderers in turn - each one re-deriving the verdict from the pass flag alone, each
/// fixed on its own after a retrospective caught it. Run <c>ae72c5ad</c> is the third: the
/// retrospective transcript said a test rung passed in a workspace with no tests in it, and both
/// reviewers of that run reported the harness as passing a test gate it had never run.
/// </para>
/// <para>
/// A shared function rather than three careful renderers, because the failure mode is a *new*
/// renderer, written by someone who never read the other two. <c>VerificationVerdictTests</c> fails
/// the build when a surface maps the pass flag straight onto the word for a pass.
/// </para>
/// </summary>
public static class VerificationVerdict
{
    /// <summary>
    /// What a climb concluded, in the words every surface uses for it.
    /// </summary>
    /// <param name="passed">Whether every gating rung that ran passed.</param>
    /// <param name="unverified">Whether a rung ran and verified nothing - a test run that found none.</param>
    /// <param name="noticed">Whether a rung that passed had something to say about what it verified.</param>
    public static string Describe(bool passed, bool unverified, bool noticed = false) =>
        !passed ? "FAILED"

        // Leads with the fact, not with the word for a pass. "passed (0 tests)" put the
        // reassurance first and the qualification in brackets, and this file's own argument -
        // that a reader stops at the first line when it is reassuring - applies to the first
        // *word* as well. Run 29356042's process reviewer built its central section on four
        // consecutive "verification: passed (0 tests)" lines while the model-facing text one call
        // away said "reached UnitTests, which verified nothing". One state, one sentence.
        : unverified ? "verified nothing (0 tests)"
        : noticed ? "passed (with a notice)"
        : "passed";
}
