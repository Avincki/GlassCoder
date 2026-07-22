using GlassCoder.Core.Agent;

namespace GlassCoder.Lab.Checkpoints;

/// <summary>
/// The cases each phase checkpoint runs (workplan tasks 13 and 19).
/// <para>
/// These are not the task suite (task 21). They are the smallest thing that proves the
/// <em>harness</em> works at each phase: Phase 0 asks whether the agent can navigate a
/// repository and come back with an answer, Phase 1 asks whether it can change a repository and
/// prove the change was good. Both grade with an oracle, never with a human reading output.
/// </para>
/// </summary>
public static class PhaseCases
{
    /// <summary>
    /// Phase 0: one worker model, read/grep/glob, no editing. What is being watched is
    /// tool-call validity - can this model call tools at all (CLAUDE.md §17)?
    /// </summary>
    public static IReadOnlyList<CheckpointCase> Phase0(string projectHint = "GlassCoder.Core") =>
    [
        new CheckpointCase(
            "phase0-locate",
            $"Find where the controller loop is implemented in this repository. Use glob and grep before " +
            $"reading whole files. When you know, answer with the file path and nothing else.",
            result => result.RanToCompletion &&
                      result.ToolCallsTotal > 0 &&
                      result.ToolCallValidityRate >= 1d),

        new CheckpointCase(
            "phase0-comprehend",
            $"Read the main source file of {projectHint} and summarise in two sentences what it does. " +
            "Locate it with grep or glob first.",
            result => result.RanToCompletion &&
                      result.ToolCallsTotal > 0 &&
                      !string.IsNullOrWhiteSpace(result.FinalText)),
    ];

    /// <summary>
    /// Phase 1: edit, build and run_tests, with the diagnostic summariser active from day one.
    /// What is being watched is compile-error rate per edit and edits-to-green.
    /// </summary>
    public static IReadOnlyList<CheckpointCase> Phase1(string targetFile, string testFilter) =>
    [
        new CheckpointCase(
            "phase1-edit-verify",
            $"The file {targetFile} has a failing test. Read it, fix the defect with edit_file, then run " +
            $"build and run_tests (filter: {testFilter}) to prove the fix works. Do not stop until the " +
            "tests pass or you are certain you cannot fix it.",
            result => result.RanToCompletion && result.ToolCallValidityRate >= 0.9d),
    ];
}
