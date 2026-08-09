using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using GlassCoder.Core.Diagnostics;
using GlassCoder.Tools.Changes;
using GlassCoder.Tools.Guardrails;
using GlassCoder.Tools.Processes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GlassCoder.Core.Verification;

/// <summary>
/// The look back at a finished run (workplan task 67).
/// <para>
/// The expensive, on-demand sibling of <see cref="IRunReviewer"/>. That one is the cheap local
/// second opinion that advises a retry; this reads the code, the process and the harness, and
/// advises GlassCoder's own backlog. Neither replaces the other and neither runs the other.
/// </para>
/// </summary>
public interface IRetrospectiveReviewer
{
    /// <summary>Whether the feature is switched on. Configuration only - no subprocess.</summary>
    bool Enabled { get; }

    /// <summary>Whether the CLI can be reached, probed once and remembered.</summary>
    Task<ReviewerAvailability> ProbeAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Runs the three stages in order. Failures come back inside the
    /// <see cref="Retrospective"/>, never as exceptions.
    /// </summary>
    /// <param name="request">Which run to look back at.</param>
    /// <param name="progress">Where to send the work as it happens. Null runs it silently.</param>
    /// <param name="cancellationToken">Cancels the stage in flight; finished stages are kept.</param>
    Task<Retrospective> ReviewAsync(
        RetrospectiveRequest request,
        IProgress<RetrospectiveActivity>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>Reads a retrospective already on disk for this run, if there is one.</summary>
    /// <param name="runId">The run to look for.</param>
    Retrospective? Load(string runId);
}

/// <summary>
/// <see cref="IRetrospectiveReviewer"/> over headless Claude Code, three staged sessions deep.
/// <para>
/// Three sessions rather than one, because the three questions have three subjects in three
/// places: the produced code is in the workspace, the process is in the transcript, and the
/// harness is a different repository altogether. One session pointed at any of them could not
/// honestly answer about the other two.
/// </para>
/// <para>
/// They are staged rather than parallel for the reason the second question is worth asking at
/// all: "how did the run go, given what it produced" needs what it produced to have been judged
/// first. Stage 2 is handed stage 1's report, and stage 3 is handed both.
/// </para>
/// </summary>
public sealed class ClaudeCodeRetrospectiveReviewer : IRetrospectiveReviewer
{
    /// <summary>The shape stages 1 and 2 answer in: prose, and nothing to tick.</summary>
    private const string ReportSchema =
        """{"type":"object","additionalProperties":false,"required":["report"],"properties":{"report":{"type":"string","description":"The review, as Markdown."}}}""";


    /// <summary>
    /// The shape stage 3 answers in. The item shape is task 43's, deliberately: a recommendation
    /// and a review action are the same thing seen from two distances, and one shape means one
    /// renderer, one parser and one row template.
    /// </summary>
    private const string RecommendationSchema =
        """{"type":"object","additionalProperties":false,"required":["report","recommendations"],"properties":{"report":{"type":"string","description":"What the harness should learn, as Markdown."},"recommendations":{"type":"array","description":"Improvements to GlassCoder and its tools, most important first.","items":{"type":"object","additionalProperties":false,"required":["id","title","detail","priority"],"properties":{"id":{"type":"string","description":"Short kebab-case slug."},"title":{"type":"string","description":"What to do, in a few words."},"detail":{"type":"string","description":"Why, where, and how it would be verified."},"priority":{"enum":["High","Medium","Low","Optional"]}}}}}}""";


    private static readonly JsonSerializerOptions PayloadOptions = new(JsonSerializerDefaults.Web);

    private readonly ClaudeCliSession _cli;
    private readonly IPathGuard _guard;
    private readonly IChangeLog _changes;
    private readonly ITranscriptBus? _transcript;
    private readonly IStepLogger? _steps;
    private readonly RetrospectiveOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<ClaudeCodeRetrospectiveReviewer> _logger;

    /// <summary>Creates the reviewer.</summary>
    public ClaudeCodeRetrospectiveReviewer(
        IProcessRunner processes,
        IPathGuard guard,
        IChangeLog changes,
        IOptions<RetrospectiveOptions> options,
        ILogger<ClaudeCodeRetrospectiveReviewer>? logger = null,
        ITranscriptBus? transcript = null,
        IStepLogger? steps = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        _guard = guard;
        _changes = changes;
        _transcript = transcript;
        _steps = steps;
        _options = options.Value;
        _time = timeProvider ?? TimeProvider.System;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<ClaudeCodeRetrospectiveReviewer>.Instance;
        _cli = new ClaudeCliSession(
            processes,
            new ClaudeCliProfile(
                _options.CliPath,
                _options.Model,
                _options.PermissionMode,
                [.. _options.AllowedTools],
                _options.Bare,
                _options.ApiKeyEnvironmentVariable),
            _logger);
    }

    /// <inheritdoc />
    public bool Enabled => _options.Enabled;

    /// <inheritdoc />
    public Task<ReviewerAvailability> ProbeAsync(CancellationToken cancellationToken = default) =>
        _options.Enabled
            ? _cli.ProbeAsync(cancellationToken)
            : Task.FromResult(ReviewerAvailability.Unavailable("The retrospective is switched off in settings."));

    /// <inheritdoc />
    public async Task<Retrospective> ReviewAsync(
        RetrospectiveRequest request,
        IProgress<RetrospectiveActivity>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        DateTimeOffset takenAt = _time.GetUtcNow();

        if (!_options.Enabled)
        {
            return Retrospective.NotTaken(request.RunId, "The retrospective is switched off in settings.", takenAt);
        }

        ReviewerAvailability availability = await ProbeAsync(cancellationToken).ConfigureAwait(false);
        if (!availability.IsAvailable)
        {
            return Retrospective.NotTaken(
                request.RunId, availability.Reason ?? "The reviewer is not available.", takenAt);
        }

        string directory = Directory(request.RunId);
        List<RetrospectiveStage> stages = [];
        IReadOnlyList<ReviewAction> recommendations = [];
        string? unexpected = null;

        // Written before the first stage runs, because it is stage 2's material and it is
        // useful to a person on its own even if every stage after this fails.
        string transcript = WriteTranscript(directory, request);

        try
        {
            RetrospectiveStage code = await RunStageAsync(
                RetrospectiveStageKind.Code,
                CodeDirective(request),
                CodeSystemPrompt,
                ReportSchema,
                _guard.RepoRoot,
                [],
                progress,
                cancellationToken).ConfigureAwait(false);
            stages.Add(Persist(directory, code));

            RetrospectiveStage process = await RunStageAsync(
                RetrospectiveStageKind.Process,
                ProcessDirective(request, code, transcript),
                ProcessSystemPrompt,
                ReportSchema,
                _guard.RepoRoot,
                [],
                progress,
                cancellationToken).ConfigureAwait(false);
            stages.Add(Persist(directory, process));

            // The only stage that reads outside the workspace, and only if it was told where
            // GlassCoder's own source is. Without that it still runs, on the two reports alone,
            // and its report says so rather than pretending it read the code.
            string[] roots = string.IsNullOrWhiteSpace(_options.HarnessRepoPath)
                ? []
                : [_options.HarnessRepoPath];

            RetrospectiveStage harness = await RunStageAsync(
                RetrospectiveStageKind.Harness,
                HarnessDirective(request, code, process, roots.Length > 0),
                HarnessSystemPrompt,
                RecommendationSchema,
                _guard.RepoRoot,
                roots,
                progress,
                cancellationToken).ConfigureAwait(false);

            (harness, recommendations) = ReadRecommendations(harness);

            // Persisted as the ranked list rather than the raw one, so what a restart reads back
            // is what this session showed - a proposal dropped here for having no title must not
            // reappear tomorrow.
            stages.Add(Persist(directory, harness with { Recommendations = recommendations }));
        }
        catch (OperationCanceledException)
        {
            // Everything finished before the cancellation is kept. A retrospective stopped after
            // two stages is two stages of answer, not nothing.
            _logger.LogInformation("The retrospective was cancelled after {Count} stage(s)", stages.Count);
        }
        catch (Exception ex)
        {
            // The interface promises failures come back inside the result. This is reached from a
            // button on a surface, and an exception crossing that boundary takes the application
            // with it (CLAUDE.md §7). Whatever finished is still returned.
            _logger.LogError(ex, "The retrospective failed after {Count} stage(s)", stages.Count);
            unexpected = $"The retrospective stopped unexpectedly: {ClaudeCliSession.Scrub(ex.Message)}";
        }

        Retrospective result = new()
        {
            RunId = request.RunId,
            Goal = request.Goal,
            TakenAt = takenAt,
            Stages = stages,
            Recommendations = recommendations,
            Directory = directory,
            Failure = unexpected ?? (stages.Count == 0 ? "No stage completed." : null),
        };

        Record(request, result);
        return result;
    }

    /// <inheritdoc />
    public Retrospective? Load(string runId)
    {
        ArgumentNullException.ThrowIfNull(runId);

        string directory = Directory(runId);
        if (!System.IO.Directory.Exists(directory))
        {
            return null;
        }

        List<RetrospectiveStage> stages = [];
        IReadOnlyList<ReviewAction> recommendations = [];

        foreach (RetrospectiveStageKind kind in (RetrospectiveStageKind[])[
            RetrospectiveStageKind.Code, RetrospectiveStageKind.Process, RetrospectiveStageKind.Harness])
        {
            string path = Path.Combine(directory, FileName(kind));
            if (!File.Exists(path))
            {
                continue;
            }

            try
            {
                string text = File.ReadAllText(path);
                stages.Add(new RetrospectiveStage
                {
                    Kind = kind,
                    Reviewed = true,
                    Report = StripFrontMatter(text),
                    Model = _options.Model,
                    Path = path,
                });

                if (kind == RetrospectiveStageKind.Harness)
                {
                    recommendations = ReadRecommendationsFile(directory);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A report that cannot be read is a report that is not there, as far as
                // rehydration is concerned. Nothing here is worth failing a window over.
                _logger.LogWarning(ex, "Could not read {Path}", path);
            }
        }

        if (stages.Count == 0)
        {
            return null;
        }

        return new Retrospective
        {
            RunId = runId,
            TakenAt = File.GetLastWriteTimeUtc(Path.Combine(directory, FileName(stages[0].Kind))),
            Stages = stages,
            Recommendations = recommendations,
            Directory = directory,
        };
    }

    private async Task<RetrospectiveStage> RunStageAsync(
        RetrospectiveStageKind kind,
        string directive,
        string systemPrompt,
        string schema,
        string workingDirectory,
        IReadOnlyList<string> addDirectories,
        IProgress<RetrospectiveActivity>? progress,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        ClaudeCliResult answer = await _cli.RunAsync(
            new ClaudeCliRequest(directive)
            {
                WorkingDirectory = workingDirectory,
                AddDirectories = addDirectories,
                SystemPrompt = systemPrompt,
                ResponseSchema = schema,
                MaxBudgetUsd = _options.MaxBudgetUsd,
                Timeout = TimeSpan.FromSeconds(_options.TimeoutSeconds),

                // Only wired when somebody is watching. A retrospective run headlessly asks for
                // the buffered call, which is one less thing that can go wrong for no gain.
                OnEvent = progress is null
                    ? null
                    : e => progress.Report(new RetrospectiveActivity(kind, e.Kind, e.Text)),
            },
            cancellationToken).ConfigureAwait(false);

        if (!answer.Succeeded)
        {
            return RetrospectiveStage.NotReviewed(kind, answer.Failure ?? "The stage failed.") with
            {
                DurationMs = answer.DurationMs,
                Model = _options.Model,

                // Carried even though the stage produced nothing: a session cut off after four
                // minutes of paid work was recorded as costing $0 with no session to go and read,
                // which is how the first retrospective's failure became so hard to diagnose.
                SessionId = answer.SessionId,
                CostUsd = answer.CostUsd,
            };
        }

        ReportPayload? payload =
            Deserialise(answer.StructuredOutput) ?? Deserialise(ClaudeCliSession.ExtractJson(answer.Result));
        string report = payload?.Report ?? answer.Result?.Trim() ?? string.Empty;

        return new RetrospectiveStage
        {
            Kind = kind,
            Reviewed = report.Length > 0,
            Report = report.Length > 0 ? report : "The reviewer returned nothing.",
            Model = _options.Model,
            SessionId = answer.SessionId,
            DurationMs = answer.DurationMs,
            CostUsd = answer.CostUsd,

            // A stage that hit its spend ceiling after answering keeps its report and carries the
            // caveat, which is the whole of workplan task 68: the surface renders `Failure` beside
            // the report rather than instead of it.
            Failure = report.Length > 0 ? answer.Caveat : "The reviewer returned nothing.",
            Recommendations = payload?.Recommendations ?? [],
        };
    }

    /// <summary>
    /// Pulls the harness stage's proposals out and ranks them. A stage that answered in prose
    /// keeps its report and loses only the tickable list, which is the same concession task 43
    /// makes for the same version-dependent reason.
    /// </summary>
    private (RetrospectiveStage Stage, IReadOnlyList<ReviewAction> Recommendations) ReadRecommendations(
        RetrospectiveStage stage)
    {
        if (!stage.Reviewed)
        {
            return (stage, []);
        }

        List<ReviewAction> ranked =
        [
            .. stage.Recommendations
                .Where(a => a is not null && !string.IsNullOrWhiteSpace(a.Title))
                .OrderBy(a => a.Priority)
                .Take(Math.Max(1, _options.MaxRecommendations))
        ];

        if (ranked.Count == 0)
        {
            const string nothingToTick =
                "The reviewer wrote a report but proposed nothing to tick. Its findings " +
                "are below; there is no work order to write from them.";

            // A stage that was cut off at its ceiling proposed nothing *because* it was cut off,
            // and that is the more useful half of the sentence. Both are kept rather than one
            // overwriting the other.
            return (
                stage with
                {
                    Failure = stage.Failure is { Length: > 0 } caveat
                        ? $"{caveat} {nothingToTick}"
                        : nothingToTick,
                },
                []);
        }

        return (stage, ranked);
    }

    private static string StripFrontMatter(string text)
    {
        string[] lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        if (lines.Length == 0 || lines[0].Trim() != "---")
        {
            return text;
        }

        int at = 1;
        while (at < lines.Length && lines[at].Trim() != "---")
        {
            at++;
        }

        return at >= lines.Length ? text : string.Join(Environment.NewLine, lines[(at + 1)..]).Trim();
    }

    private string Directory(string runId) =>
        Path.Combine(
            Path.GetFullPath(_guard.RepoRoot),
            _options.OutputDirectory.Replace('/', Path.DirectorySeparatorChar),
            Sanitise(runId));

    private static string Sanitise(string runId)
    {
        string name = runId.Trim();
        foreach (char invalid in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(invalid, '-');
        }

        return name.Length == 0 ? "run" : name;
    }

    private static string FileName(RetrospectiveStageKind kind) => kind switch
    {
        RetrospectiveStageKind.Code => "1-code.md",
        RetrospectiveStageKind.Process => "2-process.md",
        _ => "3-harness.md",
    };

    /// <summary>
    /// Writes a finished stage beside its siblings, so a crash in stage three does not cost the
    /// two that already answered and the surface can rehydrate after a restart.
    /// </summary>
    private RetrospectiveStage Persist(string directory, RetrospectiveStage stage)
    {
        if (!stage.Reviewed)
        {
            return stage;
        }

        try
        {
            System.IO.Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, FileName(stage.Kind));

            StringBuilder text = new();
            text.AppendLine("---");
            text.AppendLine("glasscoder: retrospective");
            text.AppendLine(CultureInfo.InvariantCulture, $"stage: {stage.Kind}");
            text.AppendLine(CultureInfo.InvariantCulture, $"model: {stage.Model}");
            text.AppendLine(CultureInfo.InvariantCulture,
                $"costUsd: {stage.CostUsd.ToString("0.0000", CultureInfo.InvariantCulture)}");
            text.AppendLine(CultureInfo.InvariantCulture,
                $"takenAt: {_time.GetUtcNow().UtcDateTime:yyyy-MM-ddTHH:mm:ssZ}");
            text.AppendLine("---");
            text.AppendLine();
            text.AppendLine(CultureInfo.InvariantCulture, $"# {stage.Title}");
            text.AppendLine();
            text.AppendLine(stage.Report.TrimEnd());

            File.WriteAllText(path, text.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            if (stage.Kind == RetrospectiveStageKind.Harness && stage.Recommendations.Count > 0)
            {
                // The proposals as data as well as prose, so a rehydrated window can offer the
                // same tickboxes rather than a report with nothing to do about it.
                File.WriteAllText(
                    Path.Combine(directory, "recommendations.json"),
                    JsonSerializer.Serialize(stage.Recommendations, PayloadOptions));
            }

            return stage with { Path = path };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not write the {Stage} report", stage.Kind);
            return stage;
        }
    }

    private IReadOnlyList<ReviewAction> ReadRecommendationsFile(string directory)
    {
        string path = Path.Combine(directory, "recommendations.json");
        if (!File.Exists(path))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<ReviewAction>>(File.ReadAllText(path), PayloadOptions) ?? [];
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not read the recorded recommendations");
            return [];
        }
    }

    private string WriteTranscript(string directory, RetrospectiveRequest request)
    {
        string digest = RetrospectiveTranscript.Render(
            _transcript?.Steps ?? [], request, _options.MaxTranscriptCharacters);

        try
        {
            System.IO.Directory.CreateDirectory(directory);
            File.WriteAllText(
                Path.Combine(directory, "transcript.md"),
                digest,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The digest goes into the directive either way; the file is a convenience for a
            // person reading afterwards, not the mechanism.
            _logger.LogWarning(ex, "Could not write the transcript digest");
        }

        return digest;
    }

    /// <summary>
    /// The run's own footprint, as the code stage is told about it: which files it touched, and
    /// the diffs, capped. Reviewing the whole workspace instead would spend the budget on
    /// scaffold nobody wrote.
    /// </summary>
    private string DescribeChanges(string runId)
    {
        List<CodeChange> mine =
        [
            .. _changes.All()
                .Where(c => string.Equals(c.RunId, runId, StringComparison.Ordinal))
                .Where(c => c.Status is ChangeStatus.Applied or ChangeStatus.Proposed)
        ];

        if (mine.Count == 0)
        {
            return "_This run recorded no file changes._";
        }

        StringBuilder text = new();
        foreach (string path in mine.Select(c => c.Path).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal))
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"- `{path}`");
        }

        text.AppendLine();
        text.AppendLine("The diffs, most recent last:");
        text.AppendLine();

        int budget = _options.MaxChangeCharacters;
        int shown = 0;
        foreach (CodeChange change in mine)
        {
            string diff = string.Join(
                Environment.NewLine,
                change.Diff().Select(line => $"{Marker(line)}{line.Text}"));

            if (diff.Length > budget)
            {
                break;
            }

            budget -= diff.Length;
            shown++;
            text.AppendLine(CultureInfo.InvariantCulture, $"#### {change.Path} ({change.Tool}, {change.Status})");
            text.AppendLine("```diff");
            text.AppendLine(diff);
            text.AppendLine("```");
            text.AppendLine();
        }

        if (shown < mine.Count)
        {
            // Said, not silently dropped: a reviewer that thinks it saw everything will judge
            // the run's completeness on a fraction of it.
            text.AppendLine(CultureInfo.InvariantCulture,
                $"_{mine.Count - shown} further change(s) are not shown here; open the files to read them._");
        }

        return text.ToString();
    }

    private static string Marker(DiffLine line) => line.Kind switch
    {
        DiffKind.Added => "+",
        DiffKind.Removed => "-",
        _ => " ",
    };

    private const string CodeSystemPrompt =
        "You are reviewing code an autonomous coding agent just wrote, for the engineer who owns " +
        "the agent. You have read-only tools. Read the files before judging them, and read enough " +
        "around them - callers, tests, project files - to judge them in context. Prefer one " +
        "specific finding with a line number over three general observations. Say plainly where " +
        "the code is sound; inventing work here is worse than finding nothing.";

    /// <summary>
    /// Stage 2 diagnoses and does not prescribe (workplan task 75).
    /// <para>
    /// It has the transcript and the code review; it does not have <c>WORKPLAN.md</c> or
    /// <c>HISTORY.md</c>, and no <c>--add-dir</c> on the harness. Asked for recommendations
    /// anyway, the 2026-08-08 stage 2 produced five, of which three were already shipped - the
    /// 0-test label, the <c>edit_file</c> diagnostics from task 45, and task 65's runtime launch -
    /// and its diagnosis of the <c>edit_file</c> cause contradicted what HISTORY records for the
    /// identical incident. Stage 3 caught all three, because it had the files.
    /// </para>
    /// <para>
    /// So recommending happens once, in the stage that can check whether the thing is already
    /// built. What stage 2 is uniquely able to see - where steps went without progress, and
    /// whether the verification it got was the verification it needed - is what it is now asked
    /// for and nothing else.
    /// </para>
    /// </summary>
    private const string ProcessSystemPrompt =
        "You are reviewing how an autonomous coding agent worked, not only what it produced. Your " +
        "reader is the engineer who builds the agent, and what they need is where effort went " +
        "that did not have to. You have read-only tools and the run's own transcript digest. " +
        "Ground every claim in a step number. Do not repeat the code review you have been given - " +
        "use it, by connecting what the run did to what the code turned out to be. " +
        "Diagnose; do not prescribe. You cannot see the harness's source, its workplan or its " +
        "history, so you cannot tell a missing capability from one that shipped last week - a " +
        "later stage has those files and does the recommending. Describe what happened and what " +
        "the agent was missing at that moment, and stop there.";

    private const string HarnessSystemPrompt =
        "You are advising on GlassCoder itself - the harness that ran this agent - not on the code " +
        "it produced. Your reader maintains that harness. You have read-only tools. Recommend " +
        "changes to the harness, its tools, its prompts, its verification or its interface, each " +
        "one small enough to accept on its own. Read WORKPLAN.md and HISTORY.md before proposing " +
        "anything, so you neither re-propose what is done nor re-propose what is already planned.";

    private string CodeDirective(RetrospectiveRequest request)
    {
        string extra = string.IsNullOrWhiteSpace(request.Instructions)
            ? string.Empty
            : $"\n\nThe engineer asked specifically about this:\n{request.Instructions}\n";

        return $"""
            Review the code produced by run `{request.RunId}` in this workspace.

            The goal it was given:

            ```
            {request.Goal ?? "(not recorded)"}
            ```

            The files it changed, and the diffs:

            {DescribeChanges(request.RunId)}
            {extra}
            Open those files and read them as they now stand, plus enough around them to judge
            them: their callers, the types they use, the project files that build them, and the
            tests that claim to cover them.

            `report` is the review, as Markdown, along the usual code-quality lines: correctness,
            error handling, misuse of the APIs it calls, threading, naming and structure, and
            anything the code claims but does not do. Cite locations as `path:line`.

            Two questions matter more here than in an ordinary review, because an agent wrote this.
            Do the tests actually exercise the product, or do they assert over literals and never
            call it? And is the result complete against the goal above, or does it merely compile
            and pass?

            Do not change anything. Report; do not fix.
            """;
    }

    private static string ProcessDirective(RetrospectiveRequest request, RetrospectiveStage code, string transcript)
    {
        string codeReport = code.Reviewed
            ? code.Report
            : $"(The code review did not complete: {code.Failure})";

        return $"""
            Review how run `{request.RunId}` went - the process, not the product.

            It stopped as `{request.StopReason ?? "unknown"}` after {request.Steps} steps and
            {request.TotalTokens:N0} tokens.

            ## The code review of what it produced

            {codeReport}

            ## The run, step by step

            {transcript}

            ## What to answer

            `report` is your review of the run, as Markdown. Ground every claim in a step number.
            Cover, at least:

            - Where steps were spent without progress - repeated edits, thrash, retried failures,
              refusal loops - and what the agent was missing at that moment.
            - Whether the verification it got was the verification it needed: did a green result
              mean what the agent took it to mean, and did a failure tell it enough to recover?
            - Where the defects in the code review above first became possible, and whether
              anything in the run could have caught them.
            - What the run did well, plainly. A process review that only finds fault is not
              usable as evidence.

            Do not recommend changes to GlassCoder, and do not write a list of improvements for
            its owner. You cannot see its source, its workplan or its history from here, so you
            cannot tell a capability that is missing from one that shipped last week - and a
            later stage, which has those files, does the recommending from this report. Your value
            is the diagnosis: what happened, in which step, and what the agent could not see at
            the time.

            Do not change anything, and do not restate the code review - use it.
            """;
    }

    private static string HarnessDirective(
        RetrospectiveRequest request,
        RetrospectiveStage code,
        RetrospectiveStage process,
        bool hasSource) =>
        $"""
            Two reviews of run `{request.RunId}` follow: one of the code it produced, one of how it
            worked. Say what GlassCoder - the harness that ran it - should learn from them.

            ## The code review

            {(code.Reviewed ? code.Report : $"(did not complete: {code.Failure})")}

            ## The process review

            {(process.Reviewed ? process.Report : $"(did not complete: {process.Failure})")}

            ## What to answer

            {(hasSource
                ? "GlassCoder's own source tree is available to you as an additional directory. " +
                  "Read `WORKPLAN.md` and `HISTORY.md` first - they record what is built and what " +
                  "is already planned - and read the code you are proposing to change before you " +
                  "propose changing it."
                : "GlassCoder's source tree was NOT made available to you, so you are working from " +
                  "the two reviews alone. Say so in your report, and keep your recommendations to " +
                  "what the evidence above actually supports.")}

            `report` is your reasoning, as Markdown: what these two reviews reveal about the
            harness rather than about this one run, and which of it is a pattern rather than an
            accident.

            `recommendations` are concrete improvements to GlassCoder, its tools, its prompts, its
            verification ladder or its interface. Each one must be small enough to accept on its
            own and be implemented independently of the others, because a person will tick a
            subset of them and an agent will implement exactly what was ticked. In `detail`, say
            where in the harness the change goes and how anyone would know it worked. Use priority
            High for defects in the harness, Medium for real risks, Low for maintainability,
            Optional for taste. Order them High first.

            Do not propose changes to the workspace code the run produced - that is the first
            review's business, and somebody else's decision.
            """;

    /// <summary>
    /// Puts each stage in the transcript as a human-initiated step, the way the file review and
    /// the operator's rating put theirs there. A paid model call that left no trace would be the
    /// one thing on this surface that cannot be reconstructed afterwards (CLAUDE.md §9).
    /// </summary>
    private void Record(RetrospectiveRequest request, Retrospective result)
    {
        if (_steps is null)
        {
            return;
        }

        foreach (RetrospectiveStage stage in result.Stages)
        {
            try
            {
                _steps.LogStep(new StepRecord
                {
                    RunId = request.RunId,
                    TaskId = request.TaskId,

                    // One past whatever the run reached, which is what every other human step
                    // does. The caller cannot know that number; the bus saw every step.
                    StepIndex = _transcript?.NextStepIndex(request.RunId) ?? 0,
                    Role = "human",
                    ModelId = stage.Model,
                    StartedAt = _time.GetUtcNow(),
                    Prompt = [new TranscriptMessage("user", $"Retrospective: {stage.Title}")],
                    ResponseText = SecretRedactor.Truncate(stage.Report, 4000),
                    ToolCalls =
                    [
                        new ToolCallRecord(
                            Guid.NewGuid().ToString("n")[..12],
                            ToolName(stage.Kind),
                            new Dictionary<string, object?>(StringComparer.Ordinal)
                            {
                                ["runId"] = request.RunId,
                                ["stage"] = stage.Kind.ToString(),
                                ["sessionId"] = stage.SessionId,
                                ["costUsd"] = stage.CostUsd,
                            },
                            stage.Reviewed ? "Succeeded" : "Failed",
                            Parsed: true,
                            stage.DurationMs,
                            null,
                            stage.Failure,
                            stage.Reviewed
                                ? $"{stage.Title} - reviewed in {stage.DurationMs / 1000:F0}s."
                                : $"{stage.Title} - not reviewed."),
                    ],
                    ModelLatencyMs = stage.DurationMs,
                    StepLatencyMs = stage.DurationMs,
                    Outcome = stage.Reviewed ? "reviewed" : "not_reviewed",
                    Error = stage.Failure,
                });
            }
            catch (Exception ex)
            {
                // Failing to write the transcript must not lose the review the operator paid for.
                _logger.LogWarning(ex, "Could not record the {Stage} stage in the transcript", stage.Kind);
            }
        }
    }

    private static string ToolName(RetrospectiveStageKind kind) => kind switch
    {
        RetrospectiveStageKind.Code => "retrospective_code",
        RetrospectiveStageKind.Process => "retrospective_process",
        _ => "retrospective_harness",
    };

    private static ReportPayload? Deserialise(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            ReportPayload? payload = JsonSerializer.Deserialize<ReportPayload>(json, PayloadOptions);
            return payload?.Report is null && payload?.Recommendations is null or [] ? null : payload;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The schema-validated answer. Stages 1 and 2 fill only the report.</summary>
    private sealed record ReportPayload
    {
        [JsonPropertyName("report")]
        public string? Report { get; init; }

        [JsonPropertyName("recommendations")]
        public IReadOnlyList<ReviewAction> Recommendations { get; init; } = [];
    }
}
