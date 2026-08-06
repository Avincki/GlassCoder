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

    /// <inheritdoc />
    public void LogRun(RunRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        bool content = _options.LogSourceContent;
        int max = _options.MaxLoggedTextLength;

        RunRecord sanitised = record with
        {
            Goal = SecretRedactor.Sanitise(record.Goal, content, max),
            SystemPrompt = SecretRedactor.Sanitise(record.SystemPrompt, content, max),
            FinalText = SecretRedactor.Sanitise(record.FinalText, content, max),
        };

        // Same routing trick as the step record: the property name is what sends this to the
        // JSONL transcript and keeps it out of the console.
        _logger.LogInformation("glasscoder.run {@Run}", sanitised);
    }

    /// <inheritdoc />
    public void LogReview(ReviewRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        bool content = _options.LogSourceContent;
        int max = _options.MaxLoggedTextLength;

        // A critic quotes the diff it judged, so its words fall under the same redaction switch
        // as everything else that carries source content.
        ReviewRecord sanitised = record with
        {
            Summary = SecretRedactor.Sanitise(record.Summary, content, max) ?? string.Empty,
            Votes =
            [
                .. record.Votes.Select(v => v with
                {
                    Reason = SecretRedactor.Sanitise(v.Reason, content, max) ?? string.Empty,
                }),
            ],
        };

        // Same routing trick again: the property name sends this to the JSONL transcript.
        _logger.LogInformation("glasscoder.review {@Review}", sanitised);

        _logger.LogInformation(
            "Review of run {RunId} by {CriticRole}: {Outcome} · {Responding}/{Votes} voted · ${Cost:F4}",
            record.RunId,
            record.CriticRole,
            record.Inconclusive ? "inconclusive" : record.Refuted ? "REFUTED" : "accepted",
            record.RespondingVotes,
            record.Votes.Count,
            record.EstimatedCostUsd);
    }

    /// <summary>
    /// What the step's tool calls did, for the console line.
    /// <para>
    /// The status alone was ambiguous, and the ambiguity hid a real failure: a build that
    /// reported MSB1003 and compiled nothing logged as <c>build:Succeeded</c>, because the
    /// <em>call</em> had succeeded - a failed build is a handled outcome, not a tool fault. The
    /// summary is what disambiguates it, so it is appended whenever the tool wrote one.
    /// </para>
    /// </summary>
    private static string DescribeTools(StepRecord record) =>
        record.ToolCalls.Count == 0
            ? "no tool call"
            : string.Join(", ", record.ToolCalls.Select(Describe));

    private static string Describe(ToolCallRecord call)
    {
        string outcome = $"{call.Name}:{call.Status}";
        return string.IsNullOrWhiteSpace(call.Summary)
            ? outcome
            : $"{outcome} — {Shorten(call.Summary)}";
    }

    /// <summary>One line of console is one line. The full text is in the JSONL beside it.</summary>
    private static string Shorten(string summary)
    {
        string line = summary.ReplaceLineEndings(" ").Trim();
        return line.Length <= 80 ? line : string.Concat(line.AsSpan(0, 80), "…");
    }

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
            Verification = Sanitise(record.Verification, content, max),
        };
    }

    /// <summary>
    /// A verification summary quotes compiler output over the agent's code, and a critique vote
    /// quotes the diff it judged - the same redaction switch as everything else that carries
    /// source content, exactly as <see cref="LogReview"/> already applies to its votes.
    /// </summary>
    private static StepVerificationRecord? Sanitise(StepVerificationRecord? verification, bool content, int max)
    {
        if (verification is null)
        {
            return null;
        }

        return verification with
        {
            Summary = SecretRedactor.Sanitise(verification.Summary, content, max) ?? string.Empty,
            Critique = verification.Critique is { } critique
                ? critique with
                {
                    Votes =
                    [
                        .. critique.Votes.Select(v => v with
                        {
                            Reason = SecretRedactor.Sanitise(v.Reason, content, max) ?? string.Empty,
                        }),
                    ],
                }
                : null,
        };
    }
}
