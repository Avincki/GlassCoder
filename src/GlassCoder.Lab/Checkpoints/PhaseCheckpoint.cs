using System.Globalization;
using System.Text;
using GlassCoder.Core.Agent;
using GlassCoder.Core.Diagnostics;
using GlassCoder.Core.Metrics;
using GlassCoder.Tools.Registry;
using Microsoft.Extensions.Logging;

namespace GlassCoder.Lab.Checkpoints;

/// <summary>One thing a phase must be able to do before the next phase is allowed to start.</summary>
/// <param name="Id">Short identifier, used in the report and the metrics record.</param>
/// <param name="Goal">The goal handed to the agent.</param>
/// <param name="Oracle">
/// Decides whether the run counts as solved. The oracle is the point: no human grading
/// (CLAUDE.md §15), and pass@1 means nothing without one.
/// </param>
public sealed record CheckpointCase(string Id, string Goal, Func<AgentRunResult, bool> Oracle);

/// <summary>The outcome of one checkpoint case.</summary>
/// <param name="Case">Which case.</param>
/// <param name="Result">What the run did.</param>
/// <param name="OraclePassed">Whether the oracle accepted it.</param>
/// <param name="ToolCallValidityRate">Recorded because it is what Phase 0 is watching.</param>
public sealed record CheckpointCaseReport(
    CheckpointCase Case,
    AgentRunResult Result,
    bool OraclePassed,
    double ToolCallValidityRate);

/// <summary>The outcome of a whole checkpoint.</summary>
/// <param name="Phase">Which phase was checked.</param>
/// <param name="Cases">Per-case outcomes.</param>
/// <param name="Passed">Whether every case passed its oracle.</param>
public sealed record CheckpointReport(string Phase, IReadOnlyList<CheckpointCaseReport> Cases, bool Passed)
{
    /// <summary>Mean tool-call validity across the cases - the Phase 0 watch metric.</summary>
    public double ToolCallValidityRate =>
        Cases.Count == 0 ? 1d : Cases.Average(c => c.ToolCallValidityRate);

    /// <summary>Renders the report for a human.</summary>
    public string ToText()
    {
        CultureInfo culture = CultureInfo.InvariantCulture;
        StringBuilder text = new();
        text.AppendLine(culture, $"=== {Phase} checkpoint: {(Passed ? "GREEN" : "RED")} ===");

        foreach (CheckpointCaseReport report in Cases)
        {
            text.AppendLine(culture,
                $"  {(report.OraclePassed ? "pass" : "FAIL")}  {report.Case.Id}: " +
                $"{report.Result.StopReason} after {report.Result.Steps} steps, " +
                $"{report.Result.TotalTokens} tokens, tool-call validity {report.ToolCallValidityRate:P0}");
        }

        text.AppendLine(culture, $"  mean tool-call validity: {ToolCallValidityRate:P0}");
        return text.ToString();
    }
}

/// <summary>
/// Runs a phase's cases and says whether the phase is instrumented and stable
/// (CLAUDE.md §17, workplan tasks 13 and 19).
/// <para>
/// The roadmap rule is: <b>do not advance until the current phase is instrumented and green on
/// a couple of tasks.</b> This is that rule as code. It runs the same harness the real work uses,
/// grades with oracles rather than opinion, and records a metrics row per case so the claim
/// "Phase 0 is stable" is a number someone can check rather than a feeling.
/// </para>
/// </summary>
public sealed class PhaseCheckpoint
{
    private readonly IAgentLoop _loop;
    private readonly IToolRegistry _tools;
    private readonly IMetricsRecorder _metrics;
    private readonly TimeProvider _time;
    private readonly ILogger<PhaseCheckpoint> _logger;

    /// <summary>Creates the checkpoint runner.</summary>
    public PhaseCheckpoint(
        IAgentLoop loop,
        IToolRegistry tools,
        IMetricsRecorder metrics,
        TimeProvider? timeProvider = null,
        ILogger<PhaseCheckpoint>? logger = null)
    {
        _loop = loop;
        _tools = tools;
        _metrics = metrics;
        _time = timeProvider ?? TimeProvider.System;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<PhaseCheckpoint>.Instance;
    }

    /// <summary>The tools the phase is running with, as advertised to the model.</summary>
    public IReadOnlyList<string> ToolNames => [.. _tools.Functions.Select(f => f.Name)];

    /// <summary>Runs every case and reports.</summary>
    public async Task<CheckpointReport> RunAsync(
        string phase,
        IReadOnlyList<CheckpointCase> cases,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(cases);

        _logger.LogInformation(
            "{Phase} checkpoint starting with {CaseCount} cases and tools: {Tools}",
            phase, cases.Count, string.Join(", ", ToolNames));

        List<CheckpointCaseReport> reports = [];

        foreach (CheckpointCase checkpointCase in cases)
        {
            AgentRunResult result = await _loop.RunAsync(
                new AgentRunRequest { TaskId = checkpointCase.Id, Goal = checkpointCase.Goal },
                cancellationToken).ConfigureAwait(false);

            bool passed = checkpointCase.Oracle(result);

            // Re-record what the run measured, now with the oracle verdict attached: the loop
            // cannot know whether the task was solved, so pass@1 only becomes real here.
            RunMetrics measured = result.Metrics ?? new RunMetricsCollector().Build(
                result, $"checkpoint:{phase}", passed, _time.GetUtcNow());

            _metrics.Record(measured with
            {
                Source = $"checkpoint:{phase}",
                OraclePassed = passed,
                RecordedAt = _time.GetUtcNow(),
            });

            reports.Add(new CheckpointCaseReport(checkpointCase, result, passed, result.ToolCallValidityRate));

            _logger.LogInformation(
                "{Phase} case {CaseId}: {Outcome} ({StopReason}, {Steps} steps, validity {Validity:P0})",
                phase, checkpointCase.Id, passed ? "passed" : "FAILED", result.StopReason, result.Steps,
                result.ToolCallValidityRate);
        }

        CheckpointReport report = new(phase, reports, reports.Count > 0 && reports.All(r => r.OraclePassed));
        _logger.LogInformation("{Phase} checkpoint {Outcome}", phase, report.Passed ? "GREEN" : "RED");
        return report;
    }

    /// <summary>Reads back the transcripts a checkpoint produced, to prove they are replayable.</summary>
    public static IReadOnlyList<RunTranscript> ReadTranscripts(string logDirectory) =>
        TranscriptReader.ReadDirectory(logDirectory);
}
