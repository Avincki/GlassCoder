namespace GlassCoder.Core.Diagnostics;

/// <summary>
/// Writes the transcript (CLAUDE.md §9). Kept behind an interface so the loop depends on the
/// schema, not on a logging library, and so tests can assert on what was recorded.
/// </summary>
public interface IStepLogger
{
    /// <summary>Records one loop iteration.</summary>
    void LogStep(StepRecord record);

    /// <summary>Records the run that those iterations belonged to.</summary>
    void LogRun(RunRecord record);
}
