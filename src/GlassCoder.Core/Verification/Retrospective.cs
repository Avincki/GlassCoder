using System.Globalization;
using System.Text;
using System.Text.Json;
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

    /// <summary>The read-only working set, used when nothing configures a list.</summary>
    /// <remarks>
    /// Held separately because the property below has to default to <em>empty</em>. The
    /// configuration binder appends to whatever a collection property already holds - it does
    /// this for <c>IList&lt;string&gt;</c> and for <c>string[]</c> alike, and a setter does not
    /// change it. A property that defaulted to these three could therefore only ever be added
    /// to: configuring ["Read"] bound to ["Read","Grep","Glob","Read"], so "restrict the
    /// reviewer to Read" silently left Grep and Glob switched on, and every save round-tripped
    /// a longer list than the one before it. Defaulting to empty leaves nothing to append to,
    /// and the fallback happens here instead.
    /// </remarks>
    public static IReadOnlyList<string> DefaultAllowedTools { get; } = ["Read", "Grep", "Glob"];

    /// <summary>
    /// The tools each stage may use. Read-only by construction, exactly as the file review's
    /// are: this is what makes running a coding agent on the host defensible.
    /// <para>
    /// Empty means <see cref="DefaultAllowedTools"/> - read <see cref="EffectiveAllowedTools"/>
    /// rather than this to find out what a stage is actually handed.
    /// </para>
    /// </summary>
    public IList<string> AllowedTools { get; set; } = [];

    /// <summary>What a stage is actually given: the configured list, or the default when none is.</summary>
    public IReadOnlyList<string> EffectiveAllowedTools =>
        AllowedTools.Count > 0 ? [.. AllowedTools] : DefaultAllowedTools;

    /// <summary>
    /// Spend ceiling for one stage, so three stages cost at most three of these.
    /// <para>
    /// Raised from 2.00 once there was a measurement instead of a guess. The first whole
    /// retrospective (run <c>d5edbc59</c>, 2026-08-08) cost $0.68 and $0.48 for stages 1 and 2 and
    /// about $5 for stage 3, which reads <c>WORKPLAN.md</c>, <c>HISTORY.md</c> and whatever source
    /// its recommendations touch. At 2.00 the expensive stage could only ever be cut off.
    /// </para>
    /// </summary>
    public decimal MaxBudgetUsd { get; set; } = 8.00m;

    /// <summary>
    /// How long one stage may take. Longer than a file review's, because a stage reads a whole
    /// run's worth of material rather than one file.
    /// </summary>
    public int TimeoutSeconds { get; set; } = 900;

    /// <summary>
    /// Whether to run the CLI without hooks, plugins or skills. Off by default, and for a
    /// non-obvious reason - see <see cref="FileReviewOptions.Bare"/>: <c>--bare</c> also skips
    /// the configuration the subscription login lives in, so a bare session cannot authenticate.
    /// </summary>
    public bool Bare { get; set; }

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

/// <summary>
/// Which model answered for a role, as the run itself recorded it.
/// <para>
/// The role is what the harness addressed; the model id is what the server said answered. They
/// are separate because they disagree exactly when it matters - two roles can share one alias,
/// and one alias can be served by a different checkpoint next week. A retrospective that named
/// only the role would read the same whatever was behind it.
/// </para>
/// </summary>
/// <param name="Role">The served role, as the harness addresses it.</param>
/// <param name="ModelId">What the server reported, or null when it reported nothing.</param>
public sealed record ModelInUse(string Role, string? ModelId)
{
    /// <summary>The pair as one phrase, saying so when the server named nothing.</summary>
    public override string ToString() =>
        string.IsNullOrWhiteSpace(ModelId) ? $"{Role} (the server reported no model id)" : $"{Role}: {ModelId}";
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

    /// <summary>
    /// The models that answered during this run, in the order they first did.
    /// <para>
    /// Empty means nothing recorded any, which is not the same as one model having run: a folder
    /// written before this was carried says nothing here, and reads as unknown rather than as
    /// none. Filled from the steps by the reviewer, and read back out of the stage front matter
    /// when a retrospective is reopened - <c>capability ≈ model × harness × context</c> is the
    /// frame every conclusion in these reports is read through, and a report that cannot say
    /// which model produced the run has quietly dropped one of the three terms.
    /// </para>
    /// </summary>
    public IReadOnlyList<ModelInUse> Models { get; init; } = [];
}

/// <summary>
/// A retrospective read back out of its own folder: what it concluded, and the run it judged.
/// <para>
/// Two halves because the folder is both. <see cref="Retrospective"/> is what the stages said;
/// <see cref="RetrospectiveRequest"/> is what the header says about the run they said it about,
/// and the folder can only answer that because the stage front matter carries it. Handing back
/// one without the other is what made a reopened retrospective three reports over a bare
/// hexadecimal id.
/// </para>
/// </summary>
/// <param name="Run">The run the stages judged, as much of it as the front matter recorded.</param>
/// <param name="Result">The stages, their proposals, and their totals.</param>
public sealed record SavedRetrospective(RetrospectiveRequest Run, Retrospective Result);

/// <summary>One thing a stage did, as it did it.</summary>
/// <param name="Stage">Which stage is speaking.</param>
/// <param name="Kind">What kind of thing it was.</param>
/// <param name="Text">The line to show, already scrubbed.</param>
public sealed record RetrospectiveActivity(
    RetrospectiveStageKind Stage,
    ClaudeCliEventKind Kind,
    string Text)
{
    /// <summary>
    /// The stage that has just finished, on the one activity that announces it. Null on every
    /// other line, which is all of them.
    /// <para>
    /// Here rather than on a second <see cref="IProgress{T}"/> because the ordering is the point:
    /// a stage's report has to arrive after its own narration and before the next stage's first
    /// line, and one channel is what makes that a guarantee rather than a race. It is also what
    /// lets a surface show a finished report while the next stage is still running - three
    /// sessions take minutes, and nothing was readable until all three were over.
    /// </para>
    /// </summary>
    public RetrospectiveStage? Completed { get; init; }
}

/// <summary>
/// Renders a session's steps as the digest the process stage reads (workplan task 67).
/// <para>
/// Extracted rather than handed over raw, for the reason task 15 gives about compiler output: a
/// forty-step run's JSONL is tens of thousands of tokens of prompt echo and tool results, and
/// almost none of it is what "how did this run go" needs. What survives is the shape - what each
/// step called, whether it worked, what verification said, and where the loop went round again.
/// </para>
/// <para>
/// A <em>session</em>, not a run, and that distinction is the point. An operator rarely gets there
/// in one go: they run, read, adjust the goal, run again. The digest selected the retrospective's
/// own run id out of the session and dropped everything before it, so a review of three runs' work
/// was written from the last one - and the earlier runs, which are where the decisions that shaped
/// the last one were taken, were invisible to the one reviewer whose whole job is how the work went.
/// </para>
/// <para>
/// It is also what keeps the CLI out of <c>%LocalAppData%</c>: the digest is written into the
/// workspace beside the reports, so the subprocess needs no root it would not otherwise have.
/// </para>
/// </summary>
public static class RetrospectiveTranscript
{
    /// <summary>The run id a step carries when it happened outside any run.</summary>
    private const string OutsideARun = "no-run";

    /// <summary>
    /// Which models answered, in the order they first did.
    /// <para>
    /// Read off the steps rather than off configuration, because configuration says what a role
    /// is pointed at <em>now</em> and the steps say what actually answered <em>then</em>. Those
    /// differ for every retrospective taken after the endpoint was repointed, which is precisely
    /// the retrospective somebody takes when comparing two models.
    /// </para>
    /// </summary>
    /// <param name="steps">The steps to read.</param>
    /// <param name="runId">Restricts to one run. Null reads the whole session.</param>
    public static IReadOnlyList<ModelInUse> ModelsInUse(IReadOnlyList<StepRecord> steps, string? runId = null)
    {
        ArgumentNullException.ThrowIfNull(steps);

        List<ModelInUse> found = [];
        HashSet<(string Role, string? ModelId)> seen = [];

        foreach (StepRecord step in steps)
        {
            if (runId is not null && !string.Equals(step.RunId, runId, StringComparison.Ordinal))
            {
                continue;
            }

            // Keyed on the pair: the same checkpoint serving worker and critic is two facts worth
            // reporting, and one role that changed model mid-session is the fact this exists for.
            if (seen.Add((step.Role, step.ModelId)))
            {
                found.Add(new ModelInUse(step.Role, step.ModelId));
            }
        }

        return found;
    }

    /// <summary>Renders the digest for a whole session, run by run.</summary>
    /// <param name="steps">Every step recorded this session, across every run in it.</param>
    /// <param name="request">The run the retrospective was taken on, and what is known about it.</param>
    /// <param name="maxCharacters">
    /// Cap across the whole digest, not per run. Middles are dropped first, and every drop is
    /// declared.
    /// </param>
    /// <param name="runs">
    /// The session's finished runs, for the header of each run the <paramref name="request"/> does
    /// not describe. Without them an earlier run still renders, headed by what its steps say.
    /// </param>
    public static string Render(
        IReadOnlyList<StepRecord> steps,
        RetrospectiveRequest request,
        int maxCharacters = 40000,
        IReadOnlyList<RunRecord>? runs = null)
    {
        ArgumentNullException.ThrowIfNull(steps);
        ArgumentNullException.ThrowIfNull(request);

        List<Section> sections = Split(steps, request, runs);

        StringBuilder text = new();
        AppendSessionHeader(text, sections, request, ModelsInUse(steps));

        // The cap is a budget over the session rather than per run: a nine-step run beside a
        // forty-step one must not be cut in half to make room for a share it never needed. Each
        // run's prologue is written whatever happens - a run reduced to its header still says it
        // existed - and what remains is split between the step blocks in proportion to their size,
        // with whatever a small run leaves unspent flowing on to the next.
        int budget = Math.Max(0, maxCharacters - text.Length - sections.Sum(s => s.Prologue.Length));
        int outstanding = sections.Sum(s => s.Weight);

        foreach (Section section in sections)
        {
            text.Append(section.Prologue);

            int share = outstanding <= 0 ? budget : (int)((long)budget * section.Weight / outstanding);
            int before = text.Length;
            AppendCapped(text, section.Blocks, share);

            budget = Math.Max(0, budget - (text.Length - before));
            outstanding -= section.Weight;
        }

        return text.ToString();
    }

    /// <summary>One run of the session, rendered but not yet fitted to the budget.</summary>
    /// <param name="RunId">The run this section is of.</param>
    /// <param name="Number">Its place in the session, 1-based. Zero for the steps outside any run.</param>
    /// <param name="Subject">Whether this is the run the retrospective was taken on.</param>
    /// <param name="Prologue">The header, the goal and the plan - everything before the steps.</param>
    /// <param name="Blocks">One rendered block per step, in order.</param>
    private sealed record Section(string RunId, int Number, bool Subject, string Prologue, List<string> Blocks)
    {
        /// <summary>What this run's steps would cost in full, which is its claim on the budget.</summary>
        public int Weight { get; } = Blocks.Sum(b => b.Length);
    }

    /// <summary>
    /// Splits the session into its runs, in the order they happened, and renders each.
    /// <para>
    /// Grouped by first appearance rather than by run record, because the two disagree in exactly
    /// the cases that matter: a run that crashed or was cancelled never wrote a record, and it is
    /// still part of what happened.
    /// </para>
    /// </summary>
    private static List<Section> Split(
        IReadOnlyList<StepRecord> steps,
        RetrospectiveRequest request,
        IReadOnlyList<RunRecord>? runs)
    {
        List<string> order = [];
        Dictionary<string, List<StepRecord>> grouped = new(StringComparer.Ordinal);

        foreach (StepRecord step in steps)
        {
            if (!grouped.TryGetValue(step.RunId, out List<StepRecord>? mine))
            {
                mine = [];
                grouped[step.RunId] = mine;
                order.Add(step.RunId);
            }

            mine.Add(step);
        }

        // The run the retrospective was taken on gets a section whether or not this session holds
        // its steps. A cold start offers the last run out of yesterday's log, and "no steps were
        // recorded" is the answer to that rather than a digest with nothing in it.
        if (!grouped.ContainsKey(request.RunId))
        {
            grouped[request.RunId] = [];
            order.Add(request.RunId);
        }

        Dictionary<string, RunRecord> records = new(StringComparer.Ordinal);
        foreach (RunRecord run in runs ?? [])
        {
            records[run.RunId] = run;
        }

        int total = order.Count(id => !string.Equals(id, OutsideARun, StringComparison.Ordinal));
        int numbered = 0;
        List<Section> sections = [];

        foreach (string runId in order)
        {
            List<StepRecord> mine = [.. grouped[runId].OrderBy(s => s.StepIndex)];
            bool subject = string.Equals(runId, request.RunId, StringComparison.Ordinal);
            bool outside = string.Equals(runId, OutsideARun, StringComparison.Ordinal);

            StringBuilder head = new();
            if (outside)
            {
                head.AppendLine("## Work outside any run");
                head.AppendLine();
                head.AppendLine(
                    "Steps the operator took directly - a commit, a rating, a file review - which " +
                    "belong to the session rather than to one of its runs.");
                head.AppendLine();
            }
            else
            {
                numbered++;
                AppendRunHeader(
                    head,
                    Describe(runId, mine, records.GetValueOrDefault(runId), subject ? request : null),
                    numbered,
                    total,
                    subject);
            }

            if (mine.Count == 0)
            {
                head.AppendLine("_No steps were recorded for this run in this session._");
                head.AppendLine();
                sections.Add(new Section(runId, outside ? 0 : numbered, subject, head.ToString(), []));
                continue;
            }

            if (!outside)
            {
                // Only a run plans. "No plan was recorded in this run" over a commit and a rating
                // is an absence reported about something that was never asked to have one.
                AppendPlan(head, mine);
            }

            head.AppendLine("### Steps");
            head.AppendLine();

            // The plan carries from step to step so an update can be rendered as what it changed. A
            // reader of one step wants the plan as it then stood; a reader of the run wants to see
            // which step moved it. It does not carry across runs: each run plans for itself.
            bool nameModels = ModelsInUse(mine)
                .Select(m => m.ModelId)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count() > 1;

            List<string> rendered = [];
            List<PlanItem>? plan = null;
            foreach (StepRecord step in mine)
            {
                rendered.Add(RenderStep(step, ref plan, nameModels));
            }

            sections.Add(new Section(runId, outside ? 0 : numbered, subject, head.ToString(), rendered));
        }

        return sections;
    }

    /// <summary>
    /// What the session was, before any of it is read: how many runs, how many steps, and which run
    /// the retrospective was taken on - because that run names the reports and the work order, and a
    /// reader has to be able to find it among the others.
    /// </summary>
    private static void AppendSessionHeader(
        StringBuilder text,
        IReadOnlyList<Section> sections,
        RetrospectiveRequest request,
        IReadOnlyList<ModelInUse> models)
    {
        int runs = sections.Count(s => s.Number > 0);
        Section? subject = sections.FirstOrDefault(s => s.Subject);

        text.AppendLine("# This session, run by run");
        text.AppendLine();
        text.AppendLine(CultureInfo.InvariantCulture, $"- Runs in this session: {runs}");
        text.AppendLine(CultureInfo.InvariantCulture, $"- Steps across all of them: {sections.Sum(s => s.Blocks.Count)}");
        string place = subject is { Number: > 0 } ? $", which is run {subject.Number} of {runs}." : ".";
        text.AppendLine(CultureInfo.InvariantCulture,
            $"- The retrospective was taken on run `{request.RunId}`{place}");

        // Only where there is more than one run to summarise. A single-run session would have the
        // same names here and again in the run header three lines down, which is the duplication
        // this renderer refuses everywhere else.
        if (models.Count > 0 && runs > 1)
        {
            text.AppendLine(CultureInfo.InvariantCulture,
                $"- Models that answered, across the session: {string.Join(", ", models)}");
        }

        text.AppendLine();

        // Said once, at the top, where it changes how everything below is read. This harness
        // frames capability as model x harness x context, so a session that changed model
        // partway is not one run of evidence about the harness - it is two.
        if (models.Select(m => m.ModelId).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1)
        {
            text.AppendLine(
                "More than one model answered in this session. Where a difference between runs " +
                "could be the model rather than the harness or the context, say so rather than " +
                "attributing it to the code.");
            text.AppendLine();
        }

        if (runs > 1)
        {
            text.AppendLine(
                "Every run of the session is below, oldest first, and they are one piece of work " +
                "rather than several: a later run picks up what an earlier one left, and its goal " +
                "is often a repair of what the earlier one did. Judge the whole session. Where a " +
                "claim is about one run, say which.");
            text.AppendLine();
        }
    }

    /// <summary>What a digest can say about one run before reading its steps.</summary>
    /// <param name="RunId">The run's identifier.</param>
    /// <param name="TaskId">The task it was attempting.</param>
    /// <param name="Stopped">Why the loop stopped, as far as anything here knows.</param>
    /// <param name="Steps">Completed iterations.</param>
    /// <param name="Tokens">Total tokens across the run.</param>
    /// <param name="Goal">The goal it was given, when something recorded one.</param>
    /// <param name="Models">Which models answered in it, in the order they first did.</param>
    private sealed record RunHeader(
        string RunId,
        string TaskId,
        string Stopped,
        int Steps,
        long Tokens,
        string? Goal,
        IReadOnlyList<ModelInUse> Models);

    /// <summary>
    /// What is known about one run, from whichever record carries it: the request for the run the
    /// retrospective was taken on, the run record for the rest, and the steps themselves for a run
    /// that ended without writing one.
    /// </summary>
    private static RunHeader Describe(
        string runId, IReadOnlyList<StepRecord> steps, RunRecord? record, RetrospectiveRequest? request)
    {
        IReadOnlyList<ModelInUse> models = ModelsInUse(steps);

        if (request is not null)
        {
            return new RunHeader(
                runId,
                request.TaskId,
                request.StopReason ?? "unknown",
                request.Steps,
                request.TotalTokens,
                request.Goal,

                // The steps win over the request even for the subject run: the request describes
                // the run, but only the steps witnessed which model answered.
                models.Count > 0 ? models : request.Models);
        }

        if (record is not null)
        {
            return new RunHeader(
                runId, record.TaskId, record.StopReason, record.Steps, record.TotalTokens, record.Goal, models);
        }

        return new RunHeader(
            runId,
            steps.Count > 0 ? steps[0].TaskId : "unknown",

            // No run record for it, which is a fact rather than a gap: the loop writes one at the
            // end, so a run without one either is still going or died without saying how.
            "no ending recorded - the run either is still going or ended without writing one",
            steps.Count,
            steps.Sum(s => s.TotalTokens ?? 0),
            null,
            models);
    }

    private static void AppendRunHeader(StringBuilder text, RunHeader run, int number, int total, bool subject)
    {
        text.AppendLine(CultureInfo.InvariantCulture, $"## Run {number} of {total} - {Short(run.RunId)}");
        text.AppendLine();
        text.AppendLine(CultureInfo.InvariantCulture, $"- Run id: `{run.RunId}`");
        text.AppendLine(CultureInfo.InvariantCulture, $"- Task: `{run.TaskId}`");
        text.AppendLine(CultureInfo.InvariantCulture, $"- Stopped: {run.Stopped}");
        text.AppendLine(CultureInfo.InvariantCulture, $"- Steps: {run.Steps}");
        text.AppendLine(CultureInfo.InvariantCulture, $"- Tokens: {run.Tokens:N0}");

        if (run.Models.Count > 0)
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"- Models: {string.Join(", ", run.Models)}");
        }

        if (subject)
        {
            text.AppendLine("- This is the run the retrospective was taken on.");
        }

        text.AppendLine();

        if (!string.IsNullOrWhiteSpace(run.Goal))
        {
            text.AppendLine("### The goal it was given");
            text.AppendLine();
            text.AppendLine("```");
            text.AppendLine(run.Goal.Trim());
            text.AppendLine("```");
            text.AppendLine();
        }
    }

    /// <summary>
    /// The plan the run made, as it last stood, with when it was written and how often it moved.
    /// <para>
    /// Every digest before this one said <c>Plan updated: 3/5 complete</c> five times and never
    /// once what the five were. The plan is the run's own account of what it thought the task
    /// decomposed into - the one thing in the transcript written by the agent about the whole job
    /// rather than about the step in front of it - and three retrospectives in a row reasoned about
    /// planning behaviour from a ratio. Rendered from the last <c>update_todos</c> observation,
    /// which is the harness's own record of what it accepted.
    /// </para>
    /// <para>
    /// The first and last step numbers are here because they are the question the reviewers kept
    /// asking: a plan authored at step 0, before any tool has reported anything, and never touched
    /// again is a different object from one that absorbed what the run learned.
    /// </para>
    /// </summary>
    private static void AppendPlan(StringBuilder text, IReadOnlyList<StepRecord> steps)
    {
        List<(int Step, ToolCallRecord Call)> updates =
        [
            .. steps.SelectMany(s => s.ToolCalls.Select(c => (s.StepIndex, Call: c)))
                .Where(c => string.Equals(c.Call.Name, TodoToolName, StringComparison.Ordinal)),
        ];

        if (updates.Count == 0)
        {
            // An absence worth one line: a run that never wrote a plan is a fact about the run, and
            // silence here reads as a digest that dropped it.
            text.AppendLine("### The plan it made");
            text.AppendLine();
            text.AppendLine("_No plan was recorded in this run._");
            text.AppendLine();
            return;
        }

        if (ReadPlan(updates[^1].Call) is not { Count: > 0 } items)
        {
            return;
        }

        text.AppendLine("### The plan it made");
        text.AppendLine();
        text.AppendLine(CultureInfo.InvariantCulture,
            $"Written at step {updates[0].Step}, last updated at step {updates[^1].Step} " +
            $"({updates.Count} update{(updates.Count == 1 ? string.Empty : "s")}), " +
            $"{items.Count(i => i.Done)} of {items.Count} complete.");
        text.AppendLine();

        foreach (PlanItem item in items)
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"- [{item.Status}] {OneLine(item.Title, 200)}");
        }

        text.AppendLine();
    }

    /// <summary>
    /// The items on one <c>update_todos</c> call, from the observation it returned, falling back to
    /// the arguments the model sent when the payload is not there to read.
    /// </summary>
    private static List<PlanItem>? ReadPlan(ToolCallRecord call)
    {
        List<PlanItem>? items = null;

        if (call.Result is { Length: > 0 } result)
        {
            try
            {
                using JsonDocument document = JsonDocument.Parse(result);
                if (document.RootElement.TryGetProperty("data", out JsonElement data) &&
                    data.TryGetProperty("items", out JsonElement array) &&
                    array.ValueKind == JsonValueKind.Array)
                {
                    // An empty plan is a plan. Only an unreadable payload stays null, because the
                    // two are different facts and the digest says which.
                    items = [];
                    Read(array, items);
                }
            }
            catch (JsonException)
            {
                // A payload that will not parse is not worth a broken digest.
            }
        }

        if (items is null or { Count: 0 } && call.Arguments?.GetValueOrDefault("items") is { } argument)
        {
            // Models send this either as an array or as a string holding one, and both have been
            // seen in this repository's own logs.
            try
            {
                using JsonDocument document = JsonDocument.Parse(
                    argument as string ?? argument.ToString() ?? "[]");
                if (document.RootElement.ValueKind == JsonValueKind.Array)
                {
                    items ??= [];
                    Read(document.RootElement, items);
                }
            }
            catch (JsonException)
            {
            }
        }

        return items;

        static void Read(JsonElement array, List<PlanItem> into)
        {
            if (array.ValueKind != JsonValueKind.Array)
            {
                return;
            }

            foreach (JsonElement element in array.EnumerateArray())
            {
                string title = element.TryGetProperty("title", out JsonElement t) ? t.GetString() ?? string.Empty : string.Empty;
                string status = element.TryGetProperty("status", out JsonElement s) ? s.GetString() ?? string.Empty : string.Empty;
                if (title.Length > 0)
                {
                    into.Add(new PlanItem(title, Describe(status), status.Equals("Completed", StringComparison.OrdinalIgnoreCase)));
                }
            }
        }

        // The three states in words a reader does not have to map back to an enum.
        static string Describe(string status) => status.ToUpperInvariant() switch
        {
            "COMPLETED" => "done",
            "INPROGRESS" => "in progress",
            _ => "to do",
        };
    }

    /// <summary>
    /// The plan as one update left it, and what that update moved.
    /// <para>
    /// Every item every time, because the question a reader brings to a plan step is "what did it
    /// think the job was at that moment" and a delta cannot answer it. What is not repeated is a
    /// plan that did not move: a re-announcement is a fact about the run - this repository has
    /// spent whole steps on them - and it is one line, not another copy of the list.
    /// </para>
    /// </summary>
    private static void AppendPlanUpdate(StringBuilder text, List<PlanItem>? items, ref List<PlanItem>? previous)
    {
        if (items is null)
        {
            // An update_todos call the digest cannot read the plan out of. Better said than
            // silently skipped, or the reader concludes the run planned nothing.
            text.AppendLine("  - plan: recorded, but its items could not be read back");
            return;
        }

        if (items.Count == 0)
        {
            // Emptying the plan is a decision, and one worth seeing: it is what a run does when it
            // abandons its decomposition rather than finishing it.
            text.AppendLine("  - plan: emptied");
            previous = items;
            return;
        }

        if (previous is not null && Signature(previous) == Signature(items))
        {
            text.AppendLine(CultureInfo.InvariantCulture,
                $"  - plan: unchanged, still {items.Count(i => i.Done)} of {items.Count} complete");
            previous = items;
            return;
        }

        // What moved, named before the list, so a long plan does not have to be diffed by eye.
        string moved = previous is null
            ? "first written"
            : Describe(previous, items);

        text.AppendLine(CultureInfo.InvariantCulture,
            $"  - plan ({moved}), {items.Count(i => i.Done)} of {items.Count} complete:");

        foreach (PlanItem item in items)
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"    - [{item.Status}] {OneLine(item.Title, 200)}");
        }

        previous = items;

        static string Signature(List<PlanItem> items) =>
            string.Join("|", items.Select(i => $"{i.Status}:{i.Title}"));

        // Titles are the identity here: the tool takes an id, but a model that renames an item
        // keeps the id and one that re-plans reuses it, so what a reader recognises is the words.
        static string Describe(List<PlanItem> before, List<PlanItem> after)
        {
            List<string> changes = [];

            int added = after.Count(a => !before.Any(b => string.Equals(b.Title, a.Title, StringComparison.Ordinal)));
            int dropped = before.Count(b => !after.Any(a => string.Equals(a.Title, b.Title, StringComparison.Ordinal)));
            int moved = after.Count(a => before.Any(b =>
                string.Equals(b.Title, a.Title, StringComparison.Ordinal) &&
                !string.Equals(b.Status, a.Status, StringComparison.Ordinal)));

            if (moved > 0)
            {
                changes.Add($"{moved} item{(moved == 1 ? string.Empty : "s")} moved");
            }

            if (added > 0)
            {
                changes.Add($"{added} added");
            }

            if (dropped > 0)
            {
                changes.Add($"{dropped} dropped");
            }

            return changes.Count == 0 ? "reordered" : string.Join(", ", changes);
        }
    }

    private sealed record PlanItem(string Title, string Status, bool Done);

    /// <summary>The tool whose calls carry the plan.</summary>
    private const string TodoToolName = "update_todos";

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
            $"_[{tail - head} steps omitted here to fit the digest. This run had {blocks.Count} in total.]_");
        text.AppendLine();

        for (int at = tail; at < blocks.Count; at++)
        {
            text.Append(blocks[at]);
        }
    }

    /// <summary>
    /// Whether the failure detail is the sentence already on the line above, so that printing it
    /// again would be duplication rather than information. A prefixed error code still counts as
    /// the same sentence - the code is on the record either way.
    /// </summary>
    private static bool SaysTheSame(string error, string detail) =>
        detail.Length > 0 &&
        (string.Equals(error, detail, StringComparison.Ordinal) ||
         error.EndsWith($": {detail}", StringComparison.Ordinal) ||
         detail.Contains(error, StringComparison.Ordinal));

    /// <summary>
    /// One step, and - when the step touched the plan - the plan as it then stood.
    /// </summary>
    /// <param name="step">The step to render.</param>
    /// <param name="plan">
    /// The plan as the previous update left it, updated here. Carried so an update that moved
    /// nothing can say so instead of reprinting a list the reader has already read: this
    /// repository has spent runs on re-announcements that looked like progress.
    /// </param>
    /// <param name="nameModel">
    /// Whether to put the model on the heading. True only where the run used more than one, so
    /// the one thing telling two steps apart is never left off, and never repeated when it is
    /// the same name every time.
    /// </param>
    private static string RenderStep(StepRecord step, ref List<PlanItem>? plan, bool nameModel = false)
    {
        StringBuilder text = new();

        // The model is on the heading only where the run had more than one to tell apart. The
        // run header names it otherwise, and a name repeated down forty steps is the kind of
        // re-announcement this renderer exists to keep out.
        string model = nameModel && !string.IsNullOrWhiteSpace(step.ModelId)
            ? $" · {step.ModelId}"
            : string.Empty;

        text.AppendLine(
            CultureInfo.InvariantCulture,
            $"#### Step {step.StepIndex} · {step.Role}{model} · {step.Outcome}");

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

            // The failure detail, unless the line above is already the same sentence - which it is
            // for every observation Observation.Fail produces, where the error is the summary with
            // a code in front of it. That is the only shadowing allowed here, and it is why
            // RetrospectiveDigestTests populates the fields with different text: a renderer that
            // drops a field saying something else fails the build.
            if (!string.IsNullOrWhiteSpace(call.Error) && !SaysTheSame(call.Error, detail))
            {
                text.AppendLine(CultureInfo.InvariantCulture, $"  - error: {OneLine(call.Error, 300)}");
            }

            // The hint on its own line, and with its own budget. Folded into the line above it
            // would be the first thing the 300-character cap ate, which is how the most actionable
            // sentence the harness writes came to be missing from the digest its reviewers read.
            if (!string.IsNullOrWhiteSpace(call.Hint))
            {
                text.AppendLine(CultureInfo.InvariantCulture, $"  - hint: {OneLine(call.Hint, 300)}");
            }

            // And what the model was actually handed, when the call did not do what it set out to.
            // Run dbaa0580's process reviewer decided a build step "returned strictly less
            // information" than the one before it and reasoned from that; the model had the
            // MSB1011 diagnostics in the payload the whole time, and this line is where they were
            // missing. Capped like the rest, and only on the calls where the payload decides
            // something - a successful build's serialized result is noise.
            if (!call.OutcomeOk && !string.IsNullOrWhiteSpace(call.Result))
            {
                text.AppendLine(CultureInfo.InvariantCulture, $"  - result: {OneLine(call.Result, 300)}");
            }

            // And the plan itself, every time it is written. "Plan updated: 3/5 complete" is a
            // ratio; which three, and what the other two were, is the thing a reader of this
            // digest has never been shown.
            if (string.Equals(call.Name, TodoToolName, StringComparison.Ordinal))
            {
                AppendPlanUpdate(text, ReadPlan(call), ref plan);
            }
        }

        if (step.Verification is { } verification)
        {
            // Through the shared renderer, because this line is why both reviewers of run
            // ae72c5ad reported the harness as passing a test gate with no test in existence.
            // The rung was honest - it recorded that it verified nothing - and this retelling,
            // which is what stage 2 and the human read, said only "passed".
            text.AppendLine(CultureInfo.InvariantCulture,
                $"- verification: {VerificationVerdict.Describe(verification.Passed, verification.Unverified, verification.Noticed)} at " +
                $"{verification.FailedRung ?? verification.HighestRungReached} - {OneLine(verification.Summary, 300)}");

            if (verification.Critique is { } critique)
            {
                // The ratio is labelled because it counts the losing side. Rendered bare it read
                // "accepted 1/3", and the process reviewer of run 46231701 took the word and the
                // number for a contradiction and said so in a report that was otherwise right -
                // the instrument the harness learns through, misleading its own reader.
                text.AppendLine(CultureInfo.InvariantCulture,
                    $"- critique: {(critique.Refuted ? "REFUTED" : "accepted")} " +
                    $"({critique.RefutingVotes} of {critique.RespondingVotes} refuted)");

                // And each vote says which way it went. Printing a dissenter's lens and reasoning
                // without its verdict leaves a reader to guess which of three paragraphs was the
                // one that objected.
                foreach (ReviewVoteRecord vote in critique.Votes)
                {
                    string stance = !vote.Available ? "no answer" : vote.Refuted ? "refuted" : "accepted";
                    text.AppendLine(CultureInfo.InvariantCulture,
                        $"  - [{vote.Lens ?? "critic"}: {stance}] {OneLine(vote.Reason, 240)}");
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
        // Local, and through the provider rather than DateTimeOffset.ToLocalTime(), so the clock
        // and the zone are both the injected one and a test does not depend on where it runs.
        string path = Path.Combine(
            directory,
            ReviewActionFile.SuggestRetrospectiveFileName(_time.GetLocalNow()));

        File.WriteAllText(path, ReviewActionFile.Render(plan), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        _logger.LogInformation("Wrote a retrospective work order with {Count} item(s) to {Path}", plan.Items.Count, path);
        return path;
    }
}
