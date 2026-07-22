using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GlassCoder.Core.Diagnostics;

/// <summary>
/// Default <see cref="IStepLogger"/> (workplan task 5).
/// <para>
/// Emits two events per step: the full <see cref="StepRecord"/> for the JSONL transcript, and a
/// one-line summary for the human view. Redaction is applied here rather than at the call site,
/// so the content switch cannot be forgotten by a caller.
/// </para>
/// </summary>
public sealed class StepLogger : IStepLogger
{
    private readonly ILogger<StepLogger> _logger;
    private readonly LoggingOptions _options;

    /// <summary>Creates the step logger.</summary>
    public StepLogger(ILogger<StepLogger> logger, IOptions<LoggingOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _logger = logger;
        _options = options.Value;
    }

    /// <inheritdoc />
    public void LogStep(StepRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        StepRecord sanitised = Sanitise(record);

        // The property name must stay in sync with SerilogBootstrap.StepPropertyName: that is
        // what routes this event to the JSONL transcript and away from the console.
        _logger.LogInformation("glasscoder.step {@Step}", sanitised);

        _logger.LogInformation(
            "Step {StepIndex} · {Outcome} · {ToolSummary} · {TotalTokens} tokens · {StepLatencyMs:F0} ms",
            record.StepIndex,
            record.Outcome,
            DescribeTools(record),
            record.TotalTokens ?? 0,
            record.StepLatencyMs);
    }

    private static string DescribeTools(StepRecord record) =>
        record.ToolCalls.Count == 0
            ? "no tool call"
            : string.Join(", ", record.ToolCalls.Select(c => $"{c.Name}:{c.Status}"));

    private StepRecord Sanitise(StepRecord record)
    {
        bool content = _options.LogSourceContent;
        int max = _options.MaxLoggedTextLength;

        return record with
        {
            Prompt = [.. record.Prompt.Select(m => m with { Text = SecretRedactor.Sanitise(m.Text, content, max) })],
            ResponseText = SecretRedactor.Sanitise(record.ResponseText, content, max),
            ToolCalls =
            [
                .. record.ToolCalls.Select(c => c with
                {
                    Result = SecretRedactor.Sanitise(c.Result, content, max),
                    Arguments = content ? c.Arguments : null,
                }),
            ],
        };
    }
}
