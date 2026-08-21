using System.Globalization;
using System.Text;
using System.Text.Json;
using GlassCoder.Core.Agent;
using GlassCoder.Core.Metrics;
using GlassCoder.Core.Verification;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GlassCoder.Core.Planning;

/// <summary>How one task of a plan ended.</summary>
public enum WorkplanTaskStatus
{
    /// <summary>The oracle passed. The checkbox was ticked.</summary>
    Passed,

    /// <summary>The oracle ran and did not pass.</summary>
    Failed,

    /// <summary>A budget or limit stopped the run before it could finish.</summary>
    LimitStopped,

    /// <summary>
    /// The task has no oracle, so nothing but a person can say whether it is done.
    /// <para>
    /// Not a failure and not a pass. The run happened and may well have produced the right work;
    /// what is missing is any way for this harness to know, and the checkbox is a record of
    /// oracle outcomes rather than of the model's opinion.
    /// </para>
    /// </summary>
    NeedsHumanDecision,

    /// <summary>
    /// The oracle named tests and none of them exist.
    /// <para>
    /// Its own status because it is the failure mode that makes an oracle worse than no oracle: a
    /// filter that matches nothing runs clean, and a task ticked on the strength of zero tests is
    /// worse off than one that was never claimed to be checkable.
    /// </para>
    /// </summary>
    OracleMatchedNothing,
}

/// <summary>What one task of a plan did.</summary>
/// <param name="Slug">The task's stable identifier, and the key its metrics are joined on.</param>
/// <param name="Title">The task's heading.</param>
/// <param name="Status">How it ended.</param>
/// <param name="Attempt">Which attempt at this slug this run was.</param>
/// <param name="Detail">One line a person can act on.</param>
public sealed record WorkplanTaskOutcome(
    string Slug,
    string Title,
    WorkplanTaskStatus Status,
    int Attempt,
    string Detail)
{
    /// <summary>The run itself, when one happened.</summary>
    public AgentRunResult? Run { get; init; }

    /// <summary>What the ladder said, when the task had an oracle to climb for.</summary>
    public VerificationReport? Verification { get; init; }

    /// <summary>Whether the checkbox was ticked as a result.</summary>
    public bool Ticked => Status == WorkplanTaskStatus.Passed;
}

/// <summary>What a whole invocation did.</summary>
/// <param name="Outcomes">One per task attempted, in order.</param>
/// <param name="Remaining">Tasks still unticked after this invocation.</param>
public sealed record WorkplanRunReport(IReadOnlyList<WorkplanTaskOutcome> Outcomes, int Remaining)
{
    /// <summary>Whether every task in the plan is now ticked.</summary>
    public bool Complete => Remaining == 0;

    /// <summary>The task that stopped the invocation, when one did.</summary>
    public WorkplanTaskOutcome? Stopper =>
        Outcomes.FirstOrDefault(outcome => outcome.Status != WorkplanTaskStatus.Passed);
}

/// <summary>Which plan to run, and how.</summary>
/// <param name="PlanPath">The plan file. Read at the start and written back after each tick.</param>
public sealed record WorkplanRunRequest(string PlanPath)
{
    /// <summary>Served role for each task's run. Null takes the configured default.</summary>
    public string? Role { get; init; }

    /// <summary>Budgets for each task's run. Null takes the configured defaults.</summary>
    public AgentOptions? Limits { get; init; }
}

/// <summary>
/// Executes a workplan, one task at a time, ticking a checkbox only when the task's own oracle
/// passed (workplan tasks 79 and 80).
/// <para>
/// The checkbox is the point. An agent that reports success is reporting an opinion; a named test
/// that passes is reporting a fact, and the difference between those two is the reason this
/// harness exists at all. So the box is ticked by <see cref="IVerificationLadder"/> and never by
/// the model, a task with no oracle is left for a person, and an oracle that matched no tests is
/// treated as louder than a failure rather than quieter.
/// </para>
/// <para>
/// Nothing else in the harness depends on this type. <c>run</c>, <c>suite</c> and <c>ablate</c>
/// reach the same loop without passing through here, so a plan is a way to drive GlassCoder and
/// never a thing it requires.
/// </para>
/// </summary>
public sealed class WorkplanRunner
{
    private readonly IAgentLoop _loop;
    private readonly IVerificationLadder _ladder;
    private readonly IMetricsRecorder _metrics;
    private readonly MetricsOptions _metricsOptions;
    private readonly ILogger<WorkplanRunner> _logger;
    private readonly TimeProvider _time;

    /// <summary>Creates the runner.</summary>
    /// <param name="loop">The agent loop each task is run through.</param>
    /// <param name="ladder">The oracle. What ticks the checkbox.</param>
    /// <param name="metrics">Where the per-task record is written.</param>
    /// <param name="metricsOptions">Where existing metrics live, for prior attempt numbers.</param>
    /// <param name="logger">Optional log.</param>
    /// <param name="timeProvider">Optional clock.</param>
    public WorkplanRunner(
        IAgentLoop loop,
        IVerificationLadder ladder,
        IMetricsRecorder metrics,
        IOptions<MetricsOptions> metricsOptions,
        ILogger<WorkplanRunner>? logger = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(metricsOptions);

        _loop = loop;
        _ladder = ladder;
        _metrics = metrics;
        _metricsOptions = metricsOptions.Value;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<WorkplanRunner>.Instance;
        _time = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Runs unticked tasks in order, stopping at the first that does not pass.
    /// <para>
    /// Stopping is not caution, it is arithmetic: the tasks are dependency-ordered, so everything
    /// after a failed prerequisite measures the prerequisite rather than itself. Re-invoking
    /// resumes at the first unticked task, because the file on disk is the state.
    /// </para>
    /// </summary>
    /// <param name="request">Which plan, and how to run it.</param>
    /// <param name="progress">Where to report each task as it finishes. Null runs silently.</param>
    /// <param name="cancellationToken">Cancels the task in flight; finished ticks stay ticked.</param>
    public async Task<WorkplanRunReport> RunAsync(
        WorkplanRunRequest request,
        IProgress<WorkplanTaskOutcome>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        string markdown = await File.ReadAllTextAsync(request.PlanPath, cancellationToken).ConfigureAwait(false);
        Workplan plan = Workplan.Parse(markdown);

        List<WorkplanTaskOutcome> outcomes = [];

        foreach (WorkplanTask task in plan.Tasks)
        {
            if (task.IsComplete)
            {
                continue;
            }

            cancellationToken.ThrowIfCancellationRequested();

            WorkplanTaskOutcome outcome = await RunTaskAsync(request, task, cancellationToken).ConfigureAwait(false);
            outcomes.Add(outcome);
            progress?.Report(outcome);

            if (outcome.Status != WorkplanTaskStatus.Passed)
            {
                break;
            }

            // Re-read rather than hold the text across a run: the agent has just been editing this
            // repository, and the plan is a file in it like any other.
            markdown = await File.ReadAllTextAsync(request.PlanPath, cancellationToken).ConfigureAwait(false);
            string ticked = Workplan.Tick(markdown, task.Slug.Length > 0 ? task.Slug : task.EffectiveSlug);

            if (!string.Equals(ticked, markdown, StringComparison.Ordinal))
            {
                await File.WriteAllTextAsync(request.PlanPath, ticked, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                _logger.LogWarning(
                    "Task {Slug} passed but its checkbox could not be found in {Plan}", task.EffectiveSlug, request.PlanPath);
            }
        }

        string finalText = await File.ReadAllTextAsync(request.PlanPath, cancellationToken).ConfigureAwait(false);
        int remaining = Workplan.Parse(finalText).Tasks.Count(t => !t.IsComplete);

        return new WorkplanRunReport(outcomes, remaining);
    }

    /// <summary>Runs one task and decides, on the oracle's evidence alone, whether it is done.</summary>
    private async Task<WorkplanTaskOutcome> RunTaskAsync(
        WorkplanRunRequest request,
        WorkplanTask task,
        CancellationToken cancellationToken)
    {
        string slug = task.EffectiveSlug;
        int attempt = PriorAttempts(slug) + 1;

        _logger.LogInformation("Workplan task {Slug} starting, attempt {Attempt}", slug, attempt);

        AgentRunResult run = await _loop.RunAsync(
            new AgentRunRequest
            {
                // The slug and nothing else. GlassContext joins run outcomes onto plan tasks by
                // this key alone, because position changes whenever a plan is reordered - a runner
                // that passed "task-3" would attach this run's metrics to whatever is third next
                // week.
                TaskId = slug,
                Goal = ComposeGoal(task),
                Attempt = attempt,
                Role = request.Role,
                Limits = request.Limits,

                // The loop's own row would be a second record of one run, and any consumer that
                // sums steps or tokens per task would count both. This runner writes the richer
                // row - the one that carries the oracle's verdict - so it writes the only one.
                RecordMetrics = false,
            },
            cancellationToken).ConfigureAwait(false);

        if (run.StopReason == AgentStopReason.Cancelled)
        {
            cancellationToken.ThrowIfCancellationRequested();
        }

        if (IsLimit(run.StopReason))
        {
            Record(slug, run, oraclePassed: null);
            return new WorkplanTaskOutcome(
                slug,
                task.Title,
                WorkplanTaskStatus.LimitStopped,
                attempt,
                $"{run.StopReason} after {run.Steps} steps. Raise the budget or split the task.")
            {
                Run = run,
            };
        }

        if (task.TestFilter is not { } filter)
        {
            // Ran, possibly well, and unticked either way. Reported rather than skipped, because
            // "nobody has decided this yet" is a state a plan is allowed to be in and silence is
            // not a way to say it.
            Record(slug, run, oraclePassed: null);
            return new WorkplanTaskOutcome(
                slug,
                task.Title,
                WorkplanTaskStatus.NeedsHumanDecision,
                attempt,
                string.IsNullOrWhiteSpace(task.Oracle)
                    ? "The task has no **Oracle:** line, so nothing here can tick it. Review the work and tick it yourself."
                    : $"The oracle '{task.Oracle}' carries no --filter expression, so no rung could be scoped to it.")
            {
                Run = run,
            };
        }

        VerificationReport report = await _ladder.VerifyAsync(
            new VerificationRequest(TestFilter: filter, Goal: task.Title, ChangeDescription: task.Description),
            cancellationToken).ConfigureAwait(false);

        RungResult? tests = report.Results.FirstOrDefault(result => result.Rung == VerificationRung.UnitTests);

        if (tests is null || tests.Skipped)
        {
            Record(slug, run, oraclePassed: false);
            return new WorkplanTaskOutcome(
                slug,
                task.Title,
                WorkplanTaskStatus.Failed,
                attempt,
                $"The oracle never ran: {report.Summary}")
            {
                Run = run,
                Verification = report,
            };
        }

        // The guard that makes an oracle worth having. A filter naming tests that do not exist
        // passes every other rung and verifies nothing, so a task ticked on it is worse off than
        // one that admitted it had no oracle at all.
        if (tests.Unverified)
        {
            Record(slug, run, oraclePassed: false);
            return new WorkplanTaskOutcome(
                slug,
                task.Title,
                WorkplanTaskStatus.OracleMatchedNothing,
                attempt,
                $"The oracle filter '{filter}' matched no tests. Fix the filter or the tests - " +
                "a task cannot be ticked by an oracle that checked nothing.")
            {
                Run = run,
                Verification = report,
            };
        }

        // Gated on this rung specifically, not on the whole climb: a task whose named tests fail
        // is not done whatever the rungs below it said, and one whose named tests pass is not
        // held back by an unrelated red elsewhere in the tree.
        if (!tests.Passed)
        {
            Record(slug, run, oraclePassed: false);
            return new WorkplanTaskOutcome(
                slug, task.Title, WorkplanTaskStatus.Failed, attempt, tests.Summary)
            {
                Run = run,
                Verification = report,
            };
        }

        Record(slug, run, oraclePassed: true);
        return new WorkplanTaskOutcome(
            slug, task.Title, WorkplanTaskStatus.Passed, attempt, tests.Summary)
        {
            Run = run,
            Verification = report,
        };
    }

    /// <summary>
    /// The goal one task becomes: what to do, where it is expected to land, and how it will be
    /// judged. The oracle is included deliberately - an agent told which tests decide the task can
    /// aim at them, and one that is not is guessing at the target it will be measured against.
    /// </summary>
    internal static string ComposeGoal(WorkplanTask task)
    {
        StringBuilder goal = new();
        goal.Append(task.Title.Trim());

        if (!string.IsNullOrWhiteSpace(task.Description))
        {
            goal.AppendLine().AppendLine().Append(task.Description.Trim());
        }

        if (task.TargetFiles.Count > 0)
        {
            goal.AppendLine().AppendLine()
                .Append("Files this is expected to touch: ")
                .Append(string.Join(", ", task.TargetFiles))
                .Append('.');
        }

        if (!string.IsNullOrWhiteSpace(task.Oracle))
        {
            goal.AppendLine().AppendLine()
                .Append("This task is done when `").Append(task.Oracle.Trim())
                .Append("` passes. That command is the only thing that decides it.");
        }

        return goal.ToString();
    }

    private static bool IsLimit(AgentStopReason reason) => reason
        is AgentStopReason.StepLimit
        or AgentStopReason.TokenLimit
        or AgentStopReason.TimeLimit
        or AgentStopReason.CostLimit;

    private void Record(string slug, AgentRunResult run, bool? oraclePassed)
    {
        if (run.Metrics is not { } measured)
        {
            return;
        }

        _metrics.Record(measured with
        {
            TaskId = slug,
            Source = "workplan",
            OraclePassed = oraclePassed,
            RecordedAt = _time.GetUtcNow(),
        });
    }

    /// <summary>
    /// How many times this slug has been attempted before, read from the metrics file.
    /// <para>
    /// From the file rather than from memory, because the runner stops at the first failure and
    /// the retry is a fresh invocation. Attempt numbers that restarted at 1 each time would tell
    /// a reader that every task was solved first try, which is the opposite of the truth they are
    /// there to record.
    /// </para>
    /// </summary>
    private int PriorAttempts(string slug)
    {
        string path;
        try
        {
            path = _metricsOptions.ResolveFilePath();
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return 0;
        }

        if (!File.Exists(path))
        {
            return 0;
        }

        int highest = 0;

        try
        {
            foreach (string line in File.ReadLines(path))
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                try
                {
                    using JsonDocument document = JsonDocument.Parse(line);
                    JsonElement root = document.RootElement;

                    if (root.ValueKind != JsonValueKind.Object ||
                        !root.TryGetProperty("taskId", out JsonElement taskId) ||
                        taskId.ValueKind != JsonValueKind.String ||
                        !string.Equals(taskId.GetString(), slug, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    int attempt = root.TryGetProperty("attempt", out JsonElement value) &&
                                  value.ValueKind == JsonValueKind.Number &&
                                  value.TryGetInt32(out int parsed)
                        ? parsed
                        : 1;

                    highest = Math.Max(highest, attempt);
                }
                catch (JsonException)
                {
                    // A half-written last line is normal for a file a live harness appends to.
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Could not read prior attempts from {Path}", path);
            return highest;
        }

        return highest;
    }

    /// <summary>One line per task, for a console that is watching a plan go by.</summary>
    public static string Describe(WorkplanTaskOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);

        string mark = outcome.Status switch
        {
            WorkplanTaskStatus.Passed => "PASS",
            WorkplanTaskStatus.Failed => "FAIL",
            WorkplanTaskStatus.LimitStopped => "STOP",
            WorkplanTaskStatus.OracleMatchedNothing => "VOID",
            _ => "ASK ",
        };

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{mark}  {outcome.Slug,-40} attempt {outcome.Attempt}  {outcome.Detail}");
    }
}
