using System.Globalization;
using System.Text;

namespace GlassCoder.Core.Diagnostics;

/// <summary>
/// One run, reconstructed from the JSONL log (workplan task 11).
/// <para>
/// The transcript is the primary teaching artifact (CLAUDE.md §9), so this is deliberately a
/// plain object built from the log rather than from live state: if it can be produced here, the
/// claim that a run is reconstructable from its logs alone is true rather than aspirational.
/// </para>
/// </summary>
public sealed class RunTranscript
{
    /// <summary>Creates a transcript from its parts.</summary>
    public RunTranscript(string runId, RunRecord? run, IReadOnlyList<StepRecord> steps)
    {
        RunId = runId;
        Run = run;
        Steps = steps;
    }

    /// <summary>Run identifier.</summary>
    public string RunId { get; }

    /// <summary>The run-level record, when the run completed and wrote one.</summary>
    public RunRecord? Run { get; }

    /// <summary>Steps in index order.</summary>
    public IReadOnlyList<StepRecord> Steps { get; }

    /// <summary>Task identifier, from whichever record carries it.</summary>
    public string? TaskId => Run?.TaskId ?? (Steps.Count > 0 ? Steps[0].TaskId : null);

    /// <summary>Total tokens summed from the steps, independent of the run record.</summary>
    public long TotalTokens => Steps.Sum(s => s.TotalTokens ?? 0);

    /// <summary>Model wall-clock summed from the steps.</summary>
    public double TotalModelLatencyMs => Steps.Sum(s => s.ModelLatencyMs);

    /// <summary>Every tool call in the run, in order.</summary>
    public IEnumerable<ToolCallRecord> ToolCalls => Steps.SelectMany(s => s.ToolCalls);

    /// <summary>
    /// Whether the transcript is complete: a run record, at least one step, and no gap in the
    /// step indexes. A replay that cannot say this is not a transcript, it is a sample.
    /// </summary>
    public bool IsComplete =>
        Run is not null &&
        Steps.Count > 0 &&
        Steps.Select((s, i) => s.StepIndex == i).All(ok => ok);

    /// <summary>Renders the transcript as readable text.</summary>
    public string ToText()
    {
        StringBuilder text = new();
        CultureInfo culture = CultureInfo.InvariantCulture;

        text.AppendLine(culture, $"=== run {RunId} · task {TaskId} ===");
        if (Run is not null)
        {
            text.AppendLine(culture, $"role: {Run.Role}  started: {Run.StartedAt:u}");
            text.AppendLine(culture, $"goal: {Run.Goal}");
            text.AppendLine(culture, $"system: {Run.SystemPrompt}");
        }

        foreach (StepRecord step in Steps)
        {
            text.AppendLine();
            text.AppendLine(culture, $"--- step {step.StepIndex} ({step.Outcome}) ---");
            text.AppendLine(culture,
                $"tokens: in {step.InputTokens ?? 0} / out {step.OutputTokens ?? 0} · model {step.ModelLatencyMs:F0} ms · step {step.StepLatencyMs:F0} ms");

            if (!string.IsNullOrWhiteSpace(step.ResponseText))
            {
                text.AppendLine(culture, $"assistant: {step.ResponseText}");
            }

            foreach (ToolCallRecord call in step.ToolCalls)
            {
                text.AppendLine(culture, $"tool {call.Name} [{call.Status}] {call.DurationMs:F0} ms");
                if (call.Arguments is { Count: > 0 })
                {
                    text.AppendLine(culture, $"  args: {string.Join(", ", call.Arguments.Select(a => $"{a.Key}={a.Value}"))}");
                }

                if (call.Result is not null)
                {
                    text.AppendLine(culture, $"  result: {call.Result}");
                }
            }

            if (step.Error is not null)
            {
                text.AppendLine(culture, $"error: {step.Error}");
            }
        }

        if (Run is not null)
        {
            text.AppendLine();
            text.AppendLine(culture,
                $"=== {Run.StopReason} after {Run.Steps} steps · {Run.TotalTokens} tokens · {Run.ElapsedMs:F0} ms · tool-call validity {ValidityRate(Run):P0} ===");
        }

        return text.ToString();
    }

    private static double ValidityRate(RunRecord run) =>
        run.ToolCallsTotal == 0 ? 1d : (double)run.ToolCallsValid / run.ToolCallsTotal;
}
