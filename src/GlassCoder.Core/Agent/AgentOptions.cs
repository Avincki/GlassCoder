using GlassCoder.Models;

namespace GlassCoder.Core.Agent;

/// <summary>
/// Budgets and limits for the controller loop (CLAUDE.md §3.1, §18; workplan task 10).
/// <para>
/// Limits are part of the loop, not an afterthought. Every one of them has a graceful
/// give-up path: the run stops, says which limit tripped, and hands back everything it learned.
/// </para>
/// </summary>
public sealed class AgentOptions
{
    /// <summary>Configuration section these options bind from.</summary>
    public const string SectionName = "GlassCoder:Agent";

    /// <summary>Served role the loop drives.</summary>
    public string Role { get; set; } = ModelRoles.Worker;

    /// <summary>Maximum loop iterations.</summary>
    public int MaxSteps { get; set; } = 30;

    /// <summary>Maximum total tokens across the run, prompt plus completion.</summary>
    public long MaxTotalTokens { get; set; } = 250_000;

    /// <summary>Maximum wall-clock for the whole run.</summary>
    public int MaxWallClockSeconds { get; set; } = 900;

    /// <summary>Maximum estimated spend, from the per-role token prices. Null disables the check.</summary>
    public decimal? MaxCostUsd { get; set; }

    /// <summary>
    /// How many consecutive invalid tool calls to tolerate before giving up. A model that
    /// cannot produce a parseable call will not start doing so on the twentieth try.
    /// </summary>
    public int MaxConsecutiveInvalidToolCalls { get; set; } = 5;

    /// <summary>
    /// How many times the same tool may fail the same way, consecutively, before the run stops.
    /// <para>
    /// A different limit from <see cref="MaxConsecutiveInvalidToolCalls"/>, because these calls
    /// are not invalid: they parse, bind and execute, and report a failure the agent then fails
    /// to act on. Nothing counted them, so a run could spend seventeen consecutive steps on one
    /// unsatisfiable edit while tool-call validity read 100%. Zero switches the limit off.
    /// </para>
    /// </summary>
    public int MaxIdenticalToolFailures { get; set; } = 8;

    /// <summary>
    /// How many consecutive stalled steps end the run - a stalled step being one where every
    /// successful call repeats an earlier call verbatim and receives the identical answer,
    /// with no change applied.
    /// <para>
    /// The failure limit above has a blind spot: a run cycling read-only calls never fails
    /// anything. Run 21f25fea repeated list_changes, list_projects and glob for twenty-five
    /// steps of byte-identical answers, 100% valid, all the way to the step limit. The unit is
    /// deliberately the step, not the call: a habitual status check beside a novel read is not
    /// a stall, and neither is re-reading an unchanged file after compaction dropped it. The
    /// count resets whenever a change is applied or anything novel is learned. Zero switches
    /// the limit off.
    /// </para>
    /// </summary>
    public int MaxStalledSteps { get; set; } = 5;

    /// <summary>
    /// System prompt used when a run does not supply one. Context assembly (task 12) will take
    /// this over; until then it is the whole of the always-loaded context.
    /// </summary>
    public string SystemPrompt { get; set; } =
        "You are GlassCoder, a coding agent working in a local repository. " +
        "Work in small steps: call exactly one tool, read the observation, then decide the next step. " +
        "Every tool result is an observation object with an 'ok' flag - when ok is false, read the error and adapt. " +
        "Prefer grep and glob to locate code before reading whole files. " +
        "When the goal is met, reply with a short plain-text summary and no tool call.";
}
