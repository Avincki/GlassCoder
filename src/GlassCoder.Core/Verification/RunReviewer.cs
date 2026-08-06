using System.Diagnostics;
using System.Globalization;
using System.Text;
using GlassCoder.Core.Agent;
using GlassCoder.Core.Diagnostics;
using GlassCoder.Tools.Changes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GlassCoder.Core.Verification;

/// <summary>What a post-run review concluded.</summary>
public sealed record RunReview
{
    /// <summary>Whether a critic actually judged the run.</summary>
    public required bool Reviewed { get; init; }

    /// <summary>What to show a human, whether or not a critic ran.</summary>
    public required string Summary { get; init; }

    /// <summary>The panel's verdict, when one was reached.</summary>
    public CritiqueResult? Critique { get; init; }

    /// <summary>The critic role that judged.</summary>
    public string Role { get; init; } = string.Empty;

    /// <summary>Wall-clock for the review.</summary>
    public double DurationMs { get; init; }

    /// <summary>Whether the panel refuted the run's work.</summary>
    public bool Refuted => Critique?.Refuted ?? false;

    /// <summary>Whether too little of the panel voted to conclude anything.</summary>
    public bool Inconclusive => Critique?.Inconclusive ?? false;

    /// <summary>What the second opinion cost, at the critic role's own prices.</summary>
    public decimal EstimatedCostUsd => Critique?.EstimatedCostUsd ?? 0m;

    /// <summary>
    /// Whether there is something worth retrying on. Deliberately only an offer: acting on it is
    /// a human pressing a button, never the reviewer starting a run.
    /// </summary>
    public bool SuggestsRetry => Reviewed && Refuted;

    /// <summary>A review that did not happen, and why.</summary>
    public static RunReview NotReviewed(string reason) => new() { Reviewed = false, Summary = reason };
}

/// <summary>Post-run review settings.</summary>
public sealed class RunReviewOptions
{
    /// <summary>Configuration section these options bind from.</summary>
    public const string SectionName = "GlassCoder:RunReview";

    /// <summary>
    /// Whether a finished run is offered to a critic. On by default, but critique itself is off
    /// by default, so nothing reaches a model until <see cref="CritiqueOptions.Enabled"/> is set:
    /// one switch to turn the feature on, not two.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Whether only runs the model ended itself are reviewed.
    /// <para>
    /// A run that stopped on a step, token, time or cost limit already says why it stopped, and
    /// a critic re-deriving that is spend for nothing. The interesting case is the run that
    /// believes it is finished.
    /// </para>
    /// </summary>
    public bool OnlyCompletedRuns { get; set; } = true;

    /// <summary>Cap on the diff text handed to the critic, so one large edit cannot fill its window.</summary>
    public int MaxChangeCharacters { get; set; } = 20000;
}

/// <summary>
/// Reviews a run after it has finished (companion to rung 6, which reviews a change during one).
/// <para>
/// The two are the same oracle asked at different moments. Rung 6 judges an edit mid-run and
/// tells the agent; this judges the finished result and tells <em>you</em>, which is why it runs
/// on a completed run and produces prose rather than a gate.
/// </para>
/// <para>
/// It never starts a run. An automatically retried run would be a second attempt chosen by a
/// model, and pass@1 measured over attempts a critic decided to grant is not pass@1 any more
/// (CLAUDE.md §11). The retry is composed here and pressed by a human.
/// </para>
/// </summary>
public interface IRunReviewer
{
    /// <summary>Whether reviewing is switched on and the default critic can be addressed.</summary>
    bool Enabled { get; }

    /// <summary>Whether a run submitted with this critic role could be reviewed.</summary>
    bool CanReview(string? criticRole);

    /// <summary>Asks the critic panel what it makes of a finished run.</summary>
    Task<RunReview> ReviewAsync(AgentRunResult result, CancellationToken cancellationToken = default);
}

/// <summary>Default <see cref="IRunReviewer"/>, assembling the run's own diffs as the change under review.</summary>
public sealed class RunReviewer : IRunReviewer
{
    private readonly ICriticPanel _critics;
    private readonly IChangeLog _changes;
    private readonly RunReviewOptions _options;
    private readonly IStepLogger? _transcript;
    private readonly TimeProvider _time;
    private readonly ILogger<RunReviewer> _logger;

    /// <summary>Creates the reviewer.</summary>
    public RunReviewer(
        ICriticPanel critics,
        IChangeLog changes,
        IOptions<RunReviewOptions> options,
        ILogger<RunReviewer>? logger = null,
        IStepLogger? transcript = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        _critics = critics;
        _changes = changes;
        _options = options.Value;
        _transcript = transcript;
        _time = timeProvider ?? TimeProvider.System;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<RunReviewer>.Instance;
    }

    /// <inheritdoc />
    public bool Enabled => _options.Enabled && _critics.Enabled;

    /// <inheritdoc />
    public bool CanReview(string? criticRole) => _options.Enabled && _critics.CanCritique(criticRole);

    /// <summary>
    /// Builds the goal for a retry, carrying the reviewer's findings into the next attempt.
    /// <para>
    /// A rejection the agent cannot read is a rejection it will earn again, so the findings go in
    /// verbatim. The retry is a new run against the same task, which is what keeps it countable
    /// as a second attempt rather than a continuation of the first.
    /// </para>
    /// </summary>
    public static string ComposeRetryGoal(string originalGoal, RunReview review)
    {
        ArgumentNullException.ThrowIfNull(review);

        if (review.Critique is null || !review.Refuted)
        {
            return originalGoal;
        }

        IEnumerable<string> findings = review.Critique.Votes
            .Where(v => v.Available && v.Refuted)
            .Select(v => $"- {v.Reason}");

        return new StringBuilder(originalGoal)
            .AppendLine()
            .AppendLine()
            .AppendLine("A previous attempt at this goal was reviewed and refuted. Address these findings")
            .AppendLine("directly rather than repeating the same approach:")
            .AppendLine()
            .AppendLine(string.Join(Environment.NewLine, findings))
            .ToString()
            .TrimEnd();
    }

    /// <inheritdoc />
    public async Task<RunReview> ReviewAsync(AgentRunResult result, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(result);

        if (!_options.Enabled)
        {
            return RunReview.NotReviewed("Post-run review is switched off.");
        }

        if (_options.OnlyCompletedRuns && !result.RanToCompletion)
        {
            // The stop reason already says what went wrong; a critic would only paraphrase it.
            return RunReview.NotReviewed(
                $"Not reviewed: the run stopped on {result.StopReason}, which already explains itself.");
        }

        if (!_critics.CanCritique(result.CriticRole))
        {
            string role = _critics.ResolveRole(result.CriticRole);
            return RunReview.NotReviewed(
                $"Not reviewed: the critic role '{role}' is not enabled, not configured, or is missing its API key.");
        }

        IReadOnlyList<CodeChange> changes = ChangesFor(result.RunId);
        if (changes.Count == 0)
        {
            return RunReview.NotReviewed("Not reviewed: the run changed no files, so there is nothing to refute.");
        }

        long start = Stopwatch.GetTimestamp();
        CritiqueResult critique = await _critics.CritiqueAsync(
            result.Goal ?? "(no goal recorded)",
            DescribeChanges(changes),
            DescribeEvidence(result, changes),
            result.CriticRole,
            cancellationToken).ConfigureAwait(false);

        double elapsed = Stopwatch.GetElapsedTime(start).TotalMilliseconds;

        _logger.LogInformation(
            "Run {RunId} reviewed on role {Role}: {Outcome} in {Duration:F0} ms",
            result.RunId,
            critique.Role,
            critique.Inconclusive ? "inconclusive" : critique.Refuted ? "REFUTED" : "accepted",
            elapsed);

        // The critique goes into the transcript verbatim (workplan task 37). Until now it lived
        // in the view model until dismissed - and an opinion that shaped a retry but left no
        // trace was the one thing on that surface that could not be reconstructed.
        _transcript?.LogReview(new ReviewRecord
        {
            RunId = result.RunId,
            TaskId = result.TaskId,
            Attempt = result.Attempt,
            CriticRole = critique.Role,
            Refuted = critique.Refuted,
            Inconclusive = critique.Inconclusive,
            Summary = critique.Summary,
            Votes = [.. critique.Votes.Select(v => new ReviewVoteRecord(v.Refuted, v.Confidence, v.Reason, v.Available, v.Lens))],
            RespondingVotes = critique.RespondingVotes,
            UnavailableVotes = critique.UnavailableVotes,
            InputTokens = critique.InputTokens,
            OutputTokens = critique.OutputTokens,
            EstimatedCostUsd = critique.EstimatedCostUsd,
            DurationMs = elapsed,
            RecordedAt = _time.GetUtcNow(),
        });

        return new RunReview
        {
            Reviewed = !critique.Inconclusive,
            Summary = critique.Summary,
            Critique = critique,
            Role = critique.Role,
            DurationMs = elapsed,
        };
    }

    private IReadOnlyList<CodeChange> ChangesFor(string runId) =>
        [.. _changes.All().Where(c =>
            string.Equals(c.RunId, runId, StringComparison.Ordinal) &&
            c.Status is ChangeStatus.Applied or ChangeStatus.Proposed)];

    /// <summary>
    /// Renders the run's work as one net diff per file - "it edited Pager.cs" is not reviewable
    /// (CLAUDE.md §10), and neither is the journey. Replaying every intermediate edit showed the
    /// panel a wrong expected value being written before it was fixed, and the panel refuted the
    /// finished run for having once been wrong (run ff74b2d4, all three critics at full
    /// confidence). The claim under judgment is the final shape of the work; the journey stays
    /// in the transcript for humans.
    /// </summary>
    private string DescribeChanges(IReadOnlyList<CodeChange> changes)
    {
        StringBuilder text = new();
        foreach (IGrouping<string, CodeChange> file in changes.GroupBy(c => c.Path, StringComparer.Ordinal))
        {
            CodeChange first = file.First();
            CodeChange last = file.Last();

            // Edited and then put back: net nothing. Saying so beats showing an empty diff.
            if (string.Equals(first.BeforeText, last.AfterText, StringComparison.Ordinal))
            {
                text.AppendLine(CultureInfo.InvariantCulture, $"--- {file.Key}: no net change.");
                text.AppendLine();
                continue;
            }

            text.Append(CultureInfo.InvariantCulture, $"--- {file.Key} ({last.Status})");
            text.AppendLine();
            foreach (DiffLine line in TextDiff.Compute(first.BeforeText, last.AfterText))
            {
                text.AppendLine(line.ToString());
            }

            text.AppendLine();

            if (text.Length >= _options.MaxChangeCharacters)
            {
                text.AppendLine(CultureInfo.InvariantCulture, $"[truncated at {_options.MaxChangeCharacters} characters]");
                break;
            }
        }

        return text.ToString();
    }

    /// <summary>
    /// What the run offers as proof: the last verification climb, which describes the tree as
    /// the run left it.
    /// <para>
    /// This used to be every change's summary in the order they happened, unlabelled - so "ran
    /// 0 tests" from before the tests existed and the suite that later passed read as coequal
    /// facts about one state, and the panel refuted the contradiction at full confidence (run
    /// ff74b2d4). A run that breaks something and fixes it is the loop working; the evidence
    /// for the finished claim is the state it finished in.
    /// </para>
    /// </summary>
    private static string DescribeEvidence(AgentRunResult result, IReadOnlyList<CodeChange> changes)
    {
        StringBuilder text = new();
        text.AppendLine(
            "The changes are the run's net result; intermediate attempts it corrected itself are " +
            "not shown and are not grounds for refutation. Judge the state the run finished in.");
        text.AppendLine();
        text.AppendLine(CultureInfo.InvariantCulture,
            $"The run stopped as {result.StopReason} after {result.Steps} steps, " +
            $"{result.ToolCallsTotal} tool calls ({result.ToolCallValidityRate:P0} valid).");

        // The last climb is the one that describes the finished tree; every earlier summary
        // describes a state the run has since replaced.
        CodeChange? verified = changes.LastOrDefault(c => !string.IsNullOrWhiteSpace(c.VerificationSummary));
        if (verified is not null)
        {
            text.AppendLine();
            text.AppendLine("Final verification of the finished tree:");
            text.AppendLine(verified.VerificationSummary);
        }

        if (!string.IsNullOrWhiteSpace(result.FinalText))
        {
            text.AppendLine();
            text.AppendLine("The agent's closing statement:");
            text.AppendLine(result.FinalText);
        }

        return text.ToString();
    }
}
