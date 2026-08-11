using System.Text.Json;
using GlassCoder.Core.Agent;
using GlassCoder.Core.Verification;
using GlassCoder.Tools.Registry;

namespace GlassCoder.Core.Metrics;

/// <summary>
/// Accumulates the Section 11 indicators as a run happens (workplan task 20).
/// <para>
/// It reads the observations the tools already return rather than asking the tools to report
/// metrics themselves. That keeps measurement out of the tool contract - a tool's job is to do
/// the thing and describe what happened - and it means the numbers describe what the model
/// actually saw, which is the thing being measured.
/// </para>
/// </summary>
public sealed class RunMetricsCollector
{
    private int _editsSinceBreak;
    private bool _broken;

    /// <summary>Edits applied.</summary>
    public int Edits { get; private set; }

    /// <summary>Edits followed by a failing build.</summary>
    public int EditsWithCompileErrors { get; private set; }

    /// <summary>Builds run.</summary>
    public int Builds { get; private set; }

    /// <summary>Builds that failed.</summary>
    public int BuildFailures { get; private set; }

    /// <summary>Test runs.</summary>
    public int TestRuns { get; private set; }

    /// <summary>Test runs that were red.</summary>
    public int TestFailures { get; private set; }

    /// <summary>Edits taken to restore a compiling state after the most recent break.</summary>
    public int EditsToGreen { get; private set; }

    /// <summary>Times the agent was in a failing state.</summary>
    public int RecoveryOpportunities { get; private set; }

    /// <summary>Times it got back out of one.</summary>
    public int Recoveries { get; private set; }

    /// <summary>Diagnostics the compiler reported.</summary>
    public int DiagnosticsReported { get; private set; }

    /// <summary>Diagnostics the summariser showed.</summary>
    public int DiagnosticsShown { get; private set; }

    /// <summary>Folds one executed tool call into the running totals.</summary>
    public void Observe(ToolInvocation invocation)
    {
        ArgumentNullException.ThrowIfNull(invocation);

        if (!invocation.IsValid)
        {
            return;
        }

        JsonElement? data = Payload(invocation.Result);

        switch (invocation.ToolName)
        {
            case "edit_file":
            case "create_file":
                if (invocation.Status == ToolCallStatus.Succeeded)
                {
                    Edits++;
                    _editsSinceBreak++;
                }

                break;

            case "build":
                ObserveBuild(data);
                break;

            case "run_tests":
                ObserveTests(data);
                break;

            default:
                break;
        }
    }

    /// <summary>Records what a diagnostic summary hid, which is what the cascade ratio measures.</summary>
    public void ObserveDiagnostics(int reported, int shown)
    {
        DiagnosticsReported += reported;
        DiagnosticsShown += shown;
    }

    /// <summary>
    /// Folds a verification-ladder climb into the same counters the build and test tools feed
    /// (workplan task 36). A run whose oracles the harness drove must measure like one whose
    /// oracles the model called itself, or recovery rate would depend on who pressed the button.
    /// </summary>
    public void ObserveVerification(VerificationReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        foreach (RungResult rung in report.Results)
        {
            if (rung.Skipped)
            {
                continue;
            }

            switch (rung.Rung)
            {
                case VerificationRung.Syntax when !rung.Passed:
                    // A syntax error in the working tree is a broken state even though no
                    // build ran to prove it - the compile rung it blocked would only agree.
                    if (Edits > 0)
                    {
                        EditsWithCompileErrors++;
                    }

                    if (!_broken)
                    {
                        _broken = true;
                        RecoveryOpportunities++;
                        _editsSinceBreak = 0;
                    }

                    break;

                case VerificationRung.Compile:
                    ObserveBuildOutcome(rung.Passed);
                    break;

                case VerificationRung.UnitTests:
                case VerificationRung.FullSuite:
                    ObserveTestOutcome(rung.Passed);
                    break;

                default:
                    break;
            }
        }
    }

    /// <summary>
    /// What retrieval spent this run (workplan task 61), or null when no policy was in play.
    /// <para>
    /// Set by the loop from <c>IRetrievalPolicy.Stats</c> before the record is built. The policy
    /// has counted these since task 55 and nothing read them - its own summary said "read by the
    /// metrics recorder" while no recorder did, which is the kind of comment that ages into a lie.
    /// </para>
    /// </summary>
    public GlassCoder.Tools.Retrieval.RetrievalStats? Retrieval { get; set; }

    /// <summary>Builds the record for a finished run.</summary>
    public RunMetrics Build(AgentRunResult result, string source, bool? oraclePassed, DateTimeOffset recordedAt)
    {
        ArgumentNullException.ThrowIfNull(result);

        return new RunMetrics
        {
            RunId = result.RunId,
            TaskId = result.TaskId,
            Role = source,
            Attempt = result.Attempt,
            CriticRole = result.CriticRole,
            Source = source,
            RecordedAt = recordedAt,
            StopReason = result.StopReason.ToString(),
            OraclePassed = oraclePassed,
            Steps = result.Steps,
            InputTokens = result.InputTokens,
            OutputTokens = result.OutputTokens,
            TotalTokens = result.TotalTokens,
            WallClockMs = result.Elapsed.TotalMilliseconds,
            CostUsd = result.EstimatedCostUsd,
            ToolCallsTotal = result.ToolCallsTotal,
            ToolCallsValid = result.ToolCallsValid,
            Edits = Edits,
            EditsWithCompileErrors = EditsWithCompileErrors,
            Builds = Builds,
            BuildFailures = BuildFailures,
            TestRuns = TestRuns,
            TestFailures = TestFailures,
            EditsToGreen = EditsToGreen,
            RecoveryOpportunities = RecoveryOpportunities,
            Recoveries = Recoveries,
            DiagnosticsReported = DiagnosticsReported,
            DiagnosticsShown = DiagnosticsShown,
            RetrievalCallsAllowed = Retrieval?.Allowed ?? 0,
            RetrievalCallsBlocked = Retrieval?.Blocked ?? new Dictionary<string, int>(StringComparer.Ordinal),
            RetrievalCharsReturned = Retrieval?.CharsReturned ?? 0,
        };
    }

    private void ObserveBuild(JsonElement? data)
    {
        int errors = Count(data, "totalErrors");
        ObserveDiagnostics(errors, Math.Min(errors, CountedEntries(data)));
        ObserveBuildOutcome(Flag(data, "succeeded") ?? false);
    }

    private void ObserveBuildOutcome(bool succeeded)
    {
        Builds++;

        if (succeeded)
        {
            if (_broken)
            {
                // The agent broke the build and got itself back out. That round trip is what
                // edits-to-green and recovery rate are counting.
                Recoveries++;
                EditsToGreen = _editsSinceBreak;
                _broken = false;
            }

            _editsSinceBreak = 0;
            return;
        }

        BuildFailures++;
        if (Edits > 0)
        {
            EditsWithCompileErrors++;
        }

        if (!_broken)
        {
            _broken = true;
            RecoveryOpportunities++;
            _editsSinceBreak = 0;
        }
    }

    private void ObserveTests(JsonElement? data) => ObserveTestOutcome(Flag(data, "ok") ?? false);

    private void ObserveTestOutcome(bool ok)
    {
        TestRuns++;

        if (ok)
        {
            if (_broken)
            {
                Recoveries++;
                _broken = false;
            }

            return;
        }

        TestFailures++;
        if (!_broken)
        {
            _broken = true;
            RecoveryOpportunities++;
        }
    }

    /// <summary>
    /// How many diagnostics the summary actually listed, read back out of its rendered text.
    /// </summary>
    private static int CountedEntries(JsonElement? data)
    {
        if (data is not { ValueKind: JsonValueKind.Object } payload ||
            !payload.TryGetProperty("diagnostics", out JsonElement text) ||
            text.ValueKind != JsonValueKind.String)
        {
            return 0;
        }

        string? rendered = text.GetString();
        if (string.IsNullOrEmpty(rendered))
        {
            return 0;
        }

        int entries = 0;
        foreach (string line in rendered.Split('\n'))
        {
            if (line.StartsWith("  ", StringComparison.Ordinal) && !line.Contains('…', StringComparison.Ordinal))
            {
                entries++;
            }
        }

        return entries;
    }

    private static JsonElement? Payload(object? result)
    {
        JsonElement element;
        switch (result)
        {
            case JsonElement json:
                element = json;
                break;

            default:
                if (result is null)
                {
                    return null;
                }

                try
                {
                    element = JsonSerializer.SerializeToElement(result, ToolFunctionFactory.SerializerOptions);
                }
                catch (NotSupportedException)
                {
                    return null;
                }

                break;
        }

        return element.ValueKind == JsonValueKind.Object && element.TryGetProperty("data", out JsonElement data)
            ? data
            : null;
    }

    // Both readers check the payload is an object first. TryGetProperty throws on anything else,
    // and metrics are a bystander here: a tool whose payload is not the shape this expected must
    // cost a missing number, never the run.
    private static bool? Flag(JsonElement? data, string property) =>
        data is { ValueKind: JsonValueKind.Object } payload &&
        payload.TryGetProperty(property, out JsonElement value) &&
        value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : null;

    private static int Count(JsonElement? data, string property) =>
        data is { ValueKind: JsonValueKind.Object } payload &&
        payload.TryGetProperty(property, out JsonElement value) &&
        value.ValueKind == JsonValueKind.Number
            ? value.GetInt32()
            : 0;
}
