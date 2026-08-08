using System.Globalization;
using System.Text;
using GlassCoder.Core.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GlassCoder.Core.Verification;

/// <summary>Which of the three questions a stage answers.</summary>
public enum RetrospectiveStageKind
{
    /// <summary>The code the run produced, judged on the usual code-quality grounds.</summary>
    Code,

    /// <summary>The run itself - how it got there, and what it wasted - read against the code review.</summary>
    Process,

    /// <summary>What GlassCoder and its tools should learn from the other two.</summary>
    Harness,
}

/// <summary>Settings for the run retrospective (workplan task 67).</summary>
public sealed class RetrospectiveOptions
{
    /// <summary>Configuration section these options bind from.</summary>
    public const string SectionName = "GlassCoder:Retrospective";

    /// <summary>Whether the surface offers the retrospective at all.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>The Claude Code executable. Empty means "whatever is on PATH".</summary>
    public string CliPath { get; set; } = "claude";

    /// <summary>Model alias passed through to the CLI.</summary>
    public string Model { get; set; } = "claude-opus-5";

    /// <summary>Permission mode for each stage. <c>plan</c> is the non-writing one.</summary>
    public string PermissionMode { get; set; } = "plan";

    /// <summary>
    /// The tools each stage may use. Read-only by construction, exactly as the file review's
    /// are: this is what makes running a coding agent on the host defensible.
    /// </summary>
    /// <remarks>
    /// Settable rather than get-only so a configured list <em>replaces</em> these rather than
    /// appending to them - with a get-only collection the binder can only add, and "restrict it
    /// to Read" would silently leave Grep and Glob switched on.
    /// </remarks>
    public IList<string> AllowedTools { get; set; } = new List<string> { "Read", "Grep", "Glob" };

    /// <summary>Spend ceiling for one stage, so three stages cost at most three of these.</summary>
    public decimal MaxBudgetUsd { get; set; } = 2.00m;

    /// <summary>
    /// How long one stage may take. Longer than a file review's, because a stage reads a whole
    /// run's worth of material rather than one file.
    /// </summary>
    public int TimeoutSeconds { get; set; } = 900;

    /// <summary>Whether to run the CLI without hooks, plugins or skills.</summary>
    public bool Bare { get; set; } = true;

    /// <summary>Most recommendations to keep from the harness stage.</summary>
    public int MaxRecommendations { get; set; } = 12;

    /// <summary>
    /// Cap on the diff text handed to the code stage, so one large edit cannot fill its window.
    /// The same reasoning as <see cref="RunReviewOptions.MaxChangeCharacters"/>.
    /// </summary>
    public int MaxChangeCharacters { get; set; } = 24000;

    /// <summary>Cap on the transcript digest handed to the process stage.</summary>
    public int MaxTranscriptCharacters { get; set; } = 40000;

    /// <summary>
    /// Where stage reports are kept, relative to the workspace root. Inside the workspace on
    /// purpose: they show up in the tree, open in the file viewer, and are readable by the
    /// agent's own <c>read_file</c>.
    /// </summary>
    public string OutputDirectory { get; set; } = ".glasscoder/retrospectives";

    /// <summary>
    /// The GlassCoder source tree, for the stage that recommends changes to it.
    /// <para>
    /// Configured rather than derived, because a running application knows its build directory
    /// and not its source. Empty is the honest default and greys out the work order with this
    /// setting named - a recommendation written into the workspace would be invisible to the
    /// agent meant to implement it, and the workspace's own Clean button would delete it.
    /// </para>
    /// </summary>
    public string HarnessRepoPath { get; set; } = string.Empty;

    /// <summary>Where work orders land under <see cref="HarnessRepoPath"/>.</summary>
    public string WorkOrderDirectory { get; set; } = "docs/retrospectives";

    /// <summary>Environment variable holding the API key to hand the CLI, if any.</summary>
    public string? ApiKeyEnvironmentVariable { get; set; }
}

/// <summary>What one stage concluded, or why it did not.</summary>
public sealed record RetrospectiveStage
{
    /// <summary>Which question this stage answered.</summary>
    public required RetrospectiveStageKind Kind { get; init; }

    /// <summary>Whether a reviewer actually judged it.</summary>
    public required bool Reviewed { get; init; }

    /// <summary>The report, as Markdown. Carries the failure text when there was one.</summary>
    public required string Report { get; init; }

    /// <summary>The model that answered.</summary>
    public string Model { get; init; } = string.Empty;

    /// <summary>The CLI's own session id, so the stage can be found in its transcript later.</summary>
    public string? SessionId { get; init; }

    /// <summary>Wall-clock for the stage.</summary>
    public double DurationMs { get; init; }

    /// <summary>What the stage cost, as the CLI reported it.</summary>
    public decimal CostUsd { get; init; }

    /// <summary>Why the stage is not usable, when it is not.</summary>
    public string? Failure { get; init; }

    /// <summary>Where the report was written, when it was written.</summary>
    public string? Path { get; init; }

    /// <summary>
    /// What this stage proposed, before ranking and capping. Only the harness stage fills it;
    /// the other two answer in prose because there is nothing on them to tick.
    /// </summary>
    public IReadOnlyList<ReviewAction> Recommendations { get; init; } = [];

    /// <summary>The stage's name, as a person would say it.</summary>
    public string Title => Kind switch
    {
        RetrospectiveStageKind.Code => "The code this run produced",
        RetrospectiveStageKind.Process => "How the run got there",
        _ => "What GlassCoder should learn",
    };

    /// <summary>A stage that did not happen, and why.</summary>
    public static RetrospectiveStage NotReviewed(RetrospectiveStageKind kind, string reason) =>
        new() { Kind = kind, Reviewed = false, Report = reason, Failure = reason };
}

/// <summary>What a whole retrospective concluded.</summary>
public sealed record Retrospective
{
    /// <summary>The run that was judged.</summary>
    public required string RunId { get; init; }

    /// <summary>The goal that run was given.</summary>
    public string? Goal { get; init; }

    /// <summary>When it was taken.</summary>
    public required DateTimeOffset TakenAt { get; init; }

    /// <summary>The stages, in the order they ran.</summary>
    public required IReadOnlyList<RetrospectiveStage> Stages { get; init; }

    /// <summary>The harness stage's proposals, already ranked and capped.</summary>
    public IReadOnlyList<ReviewAction> Recommendations { get; init; } = [];

    /// <summary>Where the stage reports were written.</summary>
    public string? Directory { get; init; }

    /// <summary>Why the retrospective as a whole did not happen, when it did not.</summary>
    public string? Failure { get; init; }

    /// <summary>What the whole thing cost.</summary>
    public decimal TotalCostUsd => Stages.Sum(s => s.CostUsd);

    /// <summary>Wall-clock across every stage.</summary>
    public double TotalDurationMs => Stages.Sum(s => s.DurationMs);

    /// <summary>Whether every stage produced a report.</summary>
    public bool Complete => Stages.Count == 3 && Stages.All(s => s.Reviewed);

    /// <summary>A retrospective that never started, and why.</summary>
    public static Retrospective NotTaken(string runId, string reason, DateTimeOffset takenAt) =>
        new() { RunId = runId, TakenAt = takenAt, Stages = [], Failure = reason };
}

/// <summary>Which run to look back at, and what is known about it.</summary>
/// <param name="RunId">The run whose steps and changes are the material.</param>
public sealed record RetrospectiveRequest(string RunId)
{
    /// <summary>The task identifier, for the record.</summary>
    public string TaskId { get; init; } = "desktop";

    /// <summary>The goal the run was given, which is what its output is judged against.</summary>
    public string? Goal { get; init; }

    /// <summary>Why the loop stopped.</summary>
    public string? StopReason { get; init; }

    /// <summary>Completed loop iterations.</summary>
    public int Steps { get; init; }

    /// <summary>Total tokens across the run.</summary>
    public long TotalTokens { get; init; }

    /// <summary>Extra direction from the operator, when they typed some.</summary>
    public string? Instructions { get; init; }
}

/// <summary>One thing a stage did, as it did it.</summary>
/// <param name="Stage">Which stage is speaking.</param>
/// <param name="Kind">What kind of thing it was.</param>
/// <param name="Text">The line to show, already scrubbed.</param>
public sealed record RetrospectiveActivity(
    RetrospectiveStageKind Stage,
    ClaudeCliEventKind Kind,
    string Text);

/// <summary>
/// Renders a run's steps as the digest the process stage reads (workplan task 67).
/// <para>
/// Extracted rather than handed over raw, for the reason task 15 gives about compiler output: a
/// forty-step run's JSONL is tens of thousands of tokens of prompt echo and tool results, and
/// almost none of it is what "how did this run go" needs. What survives is the shape - what each
/// step called, whether it worked, what verification said, and where the loop went round again.
/// </para>
/// <para>
/// It is also what keeps the CLI out of <c>%LocalAppData%</c>: the digest is written into the
/// workspace beside the reports, so the subprocess needs no root it would not otherwise have.
/// </para>
/// </summary>
public static class RetrospectiveTranscript
{
    /// <summary>Renders the digest for one run.</summary>
    /// <param name="steps">Every step recorded, of which this run's are selected.</param>
    /// <param name="request">What is known about the run itself.</param>
    /// <param name="maxCharacters">Cap. The middle is dropped first, and the drop is declared.</param>
    public static string Render(
        IReadOnlyList<StepRecord> steps,
        RetrospectiveRequest request,
        int maxCharacters = 40000)
    {
        ArgumentNullException.ThrowIfNull(steps);
        ArgumentNullException.ThrowIfNull(request);

        List<StepRecord> mine =
        [
            .. steps
                .Where(s => string.Equals(s.RunId, request.RunId, StringComparison.Ordinal))
                .OrderBy(s => s.StepIndex)
        ];

        StringBuilder text = new();
        text.AppendLine(CultureInfo.InvariantCulture, $"# Run {Short(request.RunId)} - what happened, step by step");
        text.AppendLine();
        text.AppendLine(CultureInfo.InvariantCulture, $"- Run id: `{request.RunId}`");
        text.AppendLine(CultureInfo.InvariantCulture, $"- Task: `{request.TaskId}`");
        text.AppendLine(CultureInfo.InvariantCulture, $"- Stopped: {request.StopReason ?? "unknown"}");
        text.AppendLine(CultureInfo.InvariantCulture, $"- Steps: {request.Steps}");
        text.AppendLine(CultureInfo.InvariantCulture, $"- Tokens: {request.TotalTokens:N0}");
        text.AppendLine();

        if (!string.IsNullOrWhiteSpace(request.Goal))
        {
            text.AppendLine("## The goal it was given");
            text.AppendLine();
            text.AppendLine("```");
            text.AppendLine(request.Goal.Trim());
            text.AppendLine("```");
            text.AppendLine();
        }

        if (mine.Count == 0)
        {
            text.AppendLine("_No steps were recorded for this run in this session._");
            return text.ToString();
        }

        text.AppendLine("## Steps");
        text.AppendLine();

        List<string> rendered = [.. mine.Select(RenderStep)];
        AppendCapped(text, rendered, maxCharacters - text.Length);
        return text.ToString();
    }

    /// <summary>
    /// Writes as many step blocks as the budget allows, dropping the middle rather than the tail.
    /// <para>
    /// The end of a run is where it claims to be finished and where the critics answer, and the
    /// start is where it decided what to do. The thrash in between is what a long run has most of
    /// and what a reader needs least of - and the drop is stated, because a silent truncation
    /// reads as a run that was shorter than it was.
    /// </para>
    /// </summary>
    private static void AppendCapped(StringBuilder text, List<string> blocks, int budget)
    {
        int total = blocks.Sum(b => b.Length);
        if (total <= budget || blocks.Count <= 4)
        {
            foreach (string block in blocks)
            {
                text.Append(block);
            }

            return;
        }

        int head = 0;
        int used = 0;
        while (head < blocks.Count && used + blocks[head].Length < budget / 2)
        {
            used += blocks[head].Length;
            head++;
        }

        int tail = blocks.Count;
        while (tail > head && used + blocks[tail - 1].Length < budget)
        {
            used += blocks[tail - 1].Length;
            tail--;
        }

        for (int at = 0; at < head; at++)
        {
            text.Append(blocks[at]);
        }

        text.AppendLine(CultureInfo.InvariantCulture,
            $"_[{tail - head} steps omitted here to fit the digest. The run had {blocks.Count} in total.]_");
        text.AppendLine();

        for (int at = tail; at < blocks.Count; at++)
        {
            text.Append(blocks[at]);
        }
    }

    private static string RenderStep(StepRecord step)
    {
        StringBuilder text = new();
        text.AppendLine(CultureInfo.InvariantCulture, $"### Step {step.StepIndex} · {step.Role} · {step.Outcome}");

        if (!string.IsNullOrWhiteSpace(step.ResponseText))
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"> {OneLine(step.ResponseText, 400)}");
        }

        foreach (ToolCallRecord call in step.ToolCalls)
        {
            string mark = call.Parsed && call.Status is "Succeeded" ? "ok" : call.Status;
            string detail = call.Summary ?? call.Error ?? string.Empty;
            text.AppendLine(CultureInfo.InvariantCulture,
                $"- `{call.Name}` [{mark}] {OneLine(detail, 300)}");
        }

        if (step.Verification is { } verification)
        {
            text.AppendLine(CultureInfo.InvariantCulture,
                $"- verification: {(verification.Passed ? "passed" : "FAILED")} at " +
                $"{verification.FailedRung ?? verification.HighestRungReached} - {OneLine(verification.Summary, 300)}");

            if (verification.Critique is { } critique)
            {
                text.AppendLine(CultureInfo.InvariantCulture,
                    $"- critique: {(critique.Refuted ? "REFUTED" : "accepted")} " +
                    $"{critique.RefutingVotes}/{critique.RespondingVotes}");

                foreach (ReviewVoteRecord vote in critique.Votes)
                {
                    text.AppendLine(CultureInfo.InvariantCulture,
                        $"  - [{vote.Lens ?? "critic"}] {OneLine(vote.Reason, 240)}");
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(step.Error))
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"- error: {OneLine(step.Error, 300)}");
        }

        text.AppendLine();
        return text.ToString();
    }

    private static string Short(string runId) => runId.Length <= 8 ? runId : runId[..8];

    private static string OneLine(string? value, int max)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        string flat = value.ReplaceLineEndings(" ").Trim();
        while (flat.Contains("  ", StringComparison.Ordinal))
        {
            flat = flat.Replace("  ", " ", StringComparison.Ordinal);
        }

        return flat.Length <= max ? flat : flat[..max] + "…";
    }
}

/// <summary>
/// Writes an accepted retrospective out as a work order in the GlassCoder repository
/// (workplan task 67).
/// <para>
/// The one thing on this surface that writes outside the workspace, and the reason is the whole
/// point of the third stage: a recommendation about the harness has to land where the harness
/// lives, or the agent asked to implement it cannot see it.
/// </para>
/// </summary>
public interface IRetrospectiveWriter
{
    /// <summary>Whether a work order can be written at all.</summary>
    bool CanWrite { get; }

    /// <summary>Why not, in the operator's terms, when it cannot.</summary>
    string? UnavailableReason { get; }

    /// <summary>Writes the plan and returns the full path.</summary>
    string Write(ReviewActionPlan plan);
}

/// <summary>Default <see cref="IRetrospectiveWriter"/>, writing under the configured harness repository.</summary>
public sealed class RetrospectiveWriter : IRetrospectiveWriter
{
    private readonly RetrospectiveOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<RetrospectiveWriter> _logger;

    /// <summary>Creates the writer.</summary>
    public RetrospectiveWriter(
        IOptions<RetrospectiveOptions> options,
        ILogger<RetrospectiveWriter>? logger = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        _options = options.Value;
        _time = timeProvider ?? TimeProvider.System;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<RetrospectiveWriter>.Instance;
    }

    /// <inheritdoc />
    public bool CanWrite => UnavailableReason is null;

    /// <inheritdoc />
    public string? UnavailableReason
    {
        get
        {
            if (string.IsNullOrWhiteSpace(_options.HarnessRepoPath))
            {
                return "Set GlassCoder:Retrospective:HarnessRepoPath to the GlassCoder source tree. " +
                       "A work order about the harness has to land where the harness lives, and this " +
                       "application knows where it was built from, not where its source is.";
            }

            return Directory.Exists(_options.HarnessRepoPath)
                ? null
                : $"GlassCoder:Retrospective:HarnessRepoPath points at '{_options.HarnessRepoPath}', " +
                  "which is not a directory on this machine.";
        }
    }

    /// <inheritdoc />
    public string Write(ReviewActionPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        if (UnavailableReason is { } reason)
        {
            throw new InvalidOperationException(reason);
        }

        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(_options.HarnessRepoPath));
        string directory = Path.GetFullPath(Path.Combine(root, _options.WorkOrderDirectory));

        // The same containment ReviewActionWriter applies to the workspace, applied to the
        // harness repository: a configured relative path that climbs out of it is a mistake, not
        // an instruction.
        if (!directory.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(directory, root, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"The work order directory '{_options.WorkOrderDirectory}' resolves outside the harness repository.");
        }

        Directory.CreateDirectory(directory);
        string path = Path.Combine(
            directory,
            ReviewActionFile.SuggestRetrospectiveFileName(plan.RunId ?? plan.File, _time.GetUtcNow()));

        File.WriteAllText(path, ReviewActionFile.Render(plan), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        _logger.LogInformation("Wrote a retrospective work order with {Count} item(s) to {Path}", plan.Items.Count, path);
        return path;
    }
}
