using System.Text.Json;
using GlassCoder.Core.Agent;
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
        };
    }

    private void ObserveBuild(JsonElement? data)
    {
        Builds++;

        bool succeeded = Flag(data, "succeeded") ?? false;
        int errors = Count(data, "totalErrors");
        ObserveDiagnostics(errors, Math.Min(errors, CountedEntries(data)));

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

    private void ObserveTests(JsonElement? data)
    {
        TestRuns++;

        if (Flag(data, "ok") ?? false)
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
        if (data is null || !data.Value.TryGetProperty("diagnostics", out JsonElement text) ||
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

    private static bool? Flag(JsonElement? data, string property) =>
        data is not null && data.Value.TryGetProperty(property, out JsonElement value) &&
        value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : null;

    private static int Count(JsonElement? data, string property) =>
        data is not null && data.Value.TryGetProperty(property, out JsonElement value) &&
        value.ValueKind == JsonValueKind.Number
            ? value.GetInt32()
            : 0;
}
