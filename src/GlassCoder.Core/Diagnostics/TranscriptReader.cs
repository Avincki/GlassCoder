using System.Text.Json;

namespace GlassCoder.Core.Diagnostics;

/// <summary>
/// Replays the JSONL log back into transcripts (workplan task 11).
/// <para>
/// This is the other half of the logging requirement, and the half that proves it: if the log
/// cannot be read back into a complete run, then the log was never a transcript. Everything
/// downstream - the live transcript view, the metrics harness, the ablation runner - reads
/// through here.
/// </para>
/// </summary>
public static class TranscriptReader
{
    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>Reads every run recorded in a log file.</summary>
    public static IReadOnlyList<RunTranscript> ReadFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Read(File.ReadLines(path));
    }

    /// <summary>
    /// Reads every run recorded across a directory of rolling log files, oldest file first.
    /// </summary>
    public static IReadOnlyList<RunTranscript> ReadDirectory(string directory, string searchPattern = "*.jsonl")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        IEnumerable<string> lines = Directory
            .EnumerateFiles(directory, searchPattern)
            .OrderBy(f => f, StringComparer.Ordinal)
            .SelectMany(File.ReadLines);

        return Read(lines);
    }

    /// <summary>Reads every run out of a sequence of JSONL lines.</summary>
    public static IReadOnlyList<RunTranscript> Read(IEnumerable<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);

        Dictionary<string, List<StepRecord>> steps = new(StringComparer.Ordinal);
        Dictionary<string, RunRecord> runs = new(StringComparer.Ordinal);
        List<string> order = [];

        foreach (string line in lines)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            JsonElement root;
            try
            {
                using JsonDocument document = JsonDocument.Parse(line);
                root = document.RootElement.Clone();
            }
            catch (JsonException)
            {
                // A log file is an append-only stream that a crash can truncate mid-line.
                // One unreadable line must not cost the whole transcript.
                continue;
            }

            if (TryRead(root, SerilogBootstrap.StepPropertyName, out StepRecord? step) && step is not null)
            {
                Track(order, steps, step.RunId).Add(step);
            }
            else if (TryRead(root, SerilogBootstrap.RunPropertyName, out RunRecord? run) && run is not null)
            {
                Track(order, steps, run.RunId);
                runs[run.RunId] = run;
            }
        }

        return
        [
            .. order.Select(runId => new RunTranscript(
                runId,
                runs.GetValueOrDefault(runId),
                [.. steps[runId].OrderBy(s => s.StepIndex)])),
        ];
    }

    private static List<StepRecord> Track(List<string> order, Dictionary<string, List<StepRecord>> steps, string runId)
    {
        if (!steps.TryGetValue(runId, out List<StepRecord>? list))
        {
            list = [];
            steps[runId] = list;
            order.Add(runId);
        }

        return list;
    }

    private static bool TryRead<T>(JsonElement root, string propertyName, out T? value)
    {
        value = default;

        if (!root.TryGetProperty(propertyName, out JsonElement payload) || payload.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        try
        {
            value = payload.Deserialize<T>(ReadOptions);
            return value is not null;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
