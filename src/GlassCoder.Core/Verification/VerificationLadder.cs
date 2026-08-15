using System.Diagnostics;
using GlassCoder.Tools;
using GlassCoder.Tools.Build;
using GlassCoder.Tools.Guardrails;
using GlassCoder.Tools.Verification;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GlassCoder.Core.Verification;

/// <summary>The rungs, cheapest oracle first (CLAUDE.md §8).</summary>
public enum VerificationRung
{
    /// <summary>Nothing ran.</summary>
    None = 0,

    /// <summary>Rung 1: syntax of the changed file. Runs after every edit.</summary>
    Syntax = 1,

    /// <summary>Rung 2: the affected project compiles. Runs before any test.</summary>
    Compile = 2,

    /// <summary>Rung 3: analyzers. Reported, never a gate.</summary>
    Analyzers = 3,

    /// <summary>Rung 4: unit tests, once it compiles.</summary>
    UnitTests = 4,

    /// <summary>Rung 5: the full suite, before a change is accepted.</summary>
    FullSuite = 5,

    /// <summary>
    /// Rung 6: multi-critic refutation (Phase 2). Runs last because it is the only rung whose
    /// oracle is another model rather than a compiler or a test - and only when the caller
    /// offers a change description to refute. The agent loop deliberately offers none per step:
    /// it asks the panel once, at the completion claim, where refutation is a fair question.
    /// </summary>
    Critique = 6,
}

/// <summary>What one rung did.</summary>
/// <param name="Rung">Which rung.</param>
/// <param name="Passed">Whether it passed. Analyzers always pass - they do not gate.</param>
/// <param name="Summary">What to tell the model.</param>
/// <param name="DurationMs">Wall-clock.</param>
/// <param name="Skipped">Whether the rung was not applicable and was stepped over.</param>
public sealed record RungResult(
    VerificationRung Rung,
    bool Passed,
    string Summary,
    double DurationMs,
    bool Skipped = false)
{
    /// <summary>
    /// The panel's verdict, when this rung was the critique rung and it actually ran. Carried
    /// because the critique's spend is priced at the critic role's rates, and a caller that
    /// only saw a summary string could not bill it.
    /// </summary>
    public CritiqueResult? Critique { get; init; }

    /// <summary>
    /// True when the rung ran, failed nothing, and verified nothing - a test run that
    /// discovered zero tests. Distinct from <see cref="Passed"/> so a climb over a testless
    /// workspace stops logging "UnitTests passed" (runs a408b61b and ca727be3 each printed it
    /// eleven times with no test files on disk), and distinct from <see cref="Skipped"/> so
    /// the "nothing was verified" line stays in the summary the model and the critics read.
    /// </summary>
    public bool Unverified { get; init; }

    /// <summary>
    /// True when the rung passed but had something to say about the quality of what it verified -
    /// today, task 66's suite notices.
    /// <para>
    /// A flag beside the text, because the text alone has nowhere to go.
    /// <see cref="VerificationLadder"/> concatenates <c>tests.Notices</c> into
    /// <see cref="Summary"/>, which reaches the model and the critics and stops there; run
    /// 4c7de12b's notice was precise, arrived twice, was read by the completion panel, and moved
    /// nothing, because no part of the machinery that decides whether a run may stop could see it.
    /// </para>
    /// </summary>
    public bool Noticed { get; init; }
}

/// <summary>The outcome of climbing the ladder.</summary>
/// <param name="Passed">Whether every gating rung that ran passed.</param>
/// <param name="HighestRungReached">The last rung that ran.</param>
/// <param name="FailedRung">The rung that stopped the climb, if one did.</param>
/// <param name="Results">Every rung that ran, in order.</param>
/// <param name="DurationMs">Wall-clock for the whole climb.</param>
public sealed record VerificationReport(
    bool Passed,
    VerificationRung HighestRungReached,
    VerificationRung? FailedRung,
    IReadOnlyList<RungResult> Results,
    double DurationMs)
{
    /// <summary>The message the agent receives: the first failure, or a clean bill.</summary>
    public string Summary =>
        FailedRung is null
            ? string.Join(Environment.NewLine, Results.Where(r => !r.Skipped).Select(r => r.Summary))
            : Results.First(r => r.Rung == FailedRung).Summary;

    /// <summary>The critique verdict, when rung 6 ran, so the loop can bill the critic's spend.</summary>
    public CritiqueResult? Critique => Results.FirstOrDefault(r => r.Critique is not null)?.Critique;

    /// <summary>True when a rung ran but verified nothing - the asterisk on a green climb.</summary>
    public bool Unverified => Results.Any(r => !r.Skipped && r.Unverified);

    /// <summary>True when a rung that passed still had something to say about what it verified.</summary>
    public bool Noticed => Results.Any(r => !r.Skipped && r.Noticed);
}

/// <summary>Everything the ladder needs to know about what it is verifying.</summary>
/// <param name="FilePath">The edited file, when a single file was changed.</param>
/// <param name="FileText">That file's new content, for the syntax rung.</param>
/// <param name="ProjectPath">Project or directory to compile and test.</param>
/// <param name="TestFilter">Filter for the unit-test rung, so it stays cheaper than the full suite.</param>
/// <param name="RunFullSuite">Whether to finish with the whole suite.</param>
/// <param name="Goal">What the change was meant to achieve, for the critique rung.</param>
/// <param name="ChangeDescription">The change itself, for the critique rung.</param>
/// <param name="CriticRole">
/// Which critic judges rung 6. Null takes the configured default. Chosen by the caller before
/// the run rather than by the process, so a run is one arm from start to finish.
/// </param>
public sealed record VerificationRequest(
    string? FilePath = null,
    string? FileText = null,
    string? ProjectPath = null,
    string? TestFilter = null,
    bool RunFullSuite = false,
    string? Goal = null,
    string? ChangeDescription = null,
    string? CriticRole = null)
{
    /// <summary>
    /// Repo-relative paths of the files this step changed, used to work out what to build when
    /// <see cref="ProjectPath"/> is left null. Building the one project that owns the change is
    /// both faster and more accurate than building the tree.
    /// </summary>
    public IReadOnlyList<string>? ChangedPaths { get; init; }
}

/// <summary>
/// Climbs the verification ladder, cheapest oracle first, and stops at the first failure
/// (CLAUDE.md §8, workplan task 18).
/// </summary>
public interface IVerificationLadder
{
    /// <summary>Runs the rungs in order until one fails or all have run.</summary>
    Task<VerificationReport> VerifyAsync(VerificationRequest request, CancellationToken cancellationToken = default);
}

/// <summary>
/// Default <see cref="IVerificationLadder"/>.
/// <para>
/// The ordering is the design. Syntax costs milliseconds, compilation costs seconds, tests cost
/// minutes - so each rung only runs if the one below it passed, and the expensive oracles are
/// never spent on code that a cheap one already knows is broken. Running tests on code that
/// does not compile is the specific waste this class exists to prevent.
/// </para>
/// <para>
/// Analyzers sit at rung 3 and never gate. Convention drift is worth telling the agent about;
/// it is not worth blocking a correct fix over (CLAUDE.md §8, rung 3).
/// </para>
/// </summary>
public sealed class VerificationLadder : IVerificationLadder
{
    private readonly ICodeAnalyzer _analyzer;
    private readonly DiagnosticSummarizer _summarizer;
    private readonly BuildTool _build;
    private readonly RunTestsTool _tests;
    private readonly ICriticPanel _critics;
    private readonly IPathGuard _guard;
    private readonly VerificationLadderOptions _options;
    private readonly ILogger<VerificationLadder> _logger;

    /// <summary>Creates the ladder.</summary>
    public VerificationLadder(
        ICodeAnalyzer analyzer,
        DiagnosticSummarizer summarizer,
        BuildTool build,
        RunTestsTool tests,
        ICriticPanel critics,
        IPathGuard guard,
        IOptions<VerificationLadderOptions> options,
        ILogger<VerificationLadder>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        _analyzer = analyzer;
        _summarizer = summarizer;
        _build = build;
        _tests = tests;
        _critics = critics;
        _guard = guard;
        _options = options.Value;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<VerificationLadder>.Instance;
    }

    /// <inheritdoc />
    public async Task<VerificationReport> VerifyAsync(
        VerificationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        long start = Stopwatch.GetTimestamp();
        List<RungResult> results = [];

        // Decided once, here, rather than defaulted to the workspace root. A tree whose projects
        // live under src/ with no root solution answers MSB1003 to "build ." in 300 ms, and a
        // rung reporting that as a failure tells the agent its edit broke something it did not.
        request = request with { ProjectPath = ResolveTarget(request) };

        foreach (VerificationRung rung in Rungs(request))
        {
            RungResult result = await RunAsync(rung, request, results, cancellationToken).ConfigureAwait(false);
            results.Add(result);

            if (result.Skipped)
            {
                continue;
            }

            _logger.LogInformation(
                "Verification rung {Rung}: {Outcome} in {Duration:F0} ms",
                rung,
                VerificationVerdict.Describe(result.Passed, result.Unverified, result.Noticed),
                result.DurationMs);

            if (!result.Passed)
            {
                // Fail fast. Everything above this rung would be measuring broken code.
                return new VerificationReport(
                    false,
                    rung,
                    rung,
                    results,
                    Stopwatch.GetElapsedTime(start).TotalMilliseconds);
            }
        }

        VerificationRung highest = results.Where(r => !r.Skipped)
            .Select(r => r.Rung)
            .DefaultIfEmpty(VerificationRung.None)
            .Max();

        return new VerificationReport(true, highest, null, results, Stopwatch.GetElapsedTime(start).TotalMilliseconds);
    }

    private static IEnumerable<VerificationRung> Rungs(VerificationRequest request)
    {
        yield return VerificationRung.Syntax;
        yield return VerificationRung.Compile;
        yield return VerificationRung.Analyzers;
        yield return VerificationRung.UnitTests;

        if (request.RunFullSuite)
        {
            yield return VerificationRung.FullSuite;
        }

        yield return VerificationRung.Critique;
    }

    private async Task<RungResult> RunAsync(
        VerificationRung rung,
        VerificationRequest request,
        IReadOnlyList<RungResult> results,
        CancellationToken cancellationToken)
    {
        long start = Stopwatch.GetTimestamp();

        switch (rung)
        {
            case VerificationRung.Syntax:
            {
                if (request.FilePath is null || request.FileText is null || !_analyzer.Handles(request.FilePath))
                {
                    return Skip(rung, "No single edited file to syntax-check.", start);
                }

                DiagnosticReport report = _analyzer.CheckSyntax(request.FilePath, request.FileText);
                DiagnosticSummary summary = _summarizer.Summarise(report, $"Syntax check of {request.FilePath}");
                return new RungResult(rung, report.Ok, summary.Text, Elapsed(start));
            }

            case VerificationRung.Compile:
            {
                if (request.ProjectPath is null)
                {
                    return Skip(rung, NothingToBuild, start);
                }

                ToolObservation<BuildResult> observation = await _build
                    .BuildAsync(request.ProjectPath, allowRestore: true, cancellationToken)
                    .ConfigureAwait(false);

                if (!observation.Ok)
                {
                    // The build could not be run at all - an unavailable sandbox, say. That is
                    // not a compile failure, and reporting it as one would send the agent
                    // hunting for a bug that is not in the code.
                    return Skip(rung, observation.Error?.Message ?? "The build could not be run.", start);
                }

                BuildResult build = observation.Data!;
                return new RungResult(rung, build.Succeeded, build.Diagnostics, Elapsed(start));
            }

            case VerificationRung.Analyzers:
            {
                if (!_options.RunAnalyzers)
                {
                    return Skip(rung, "Analyzers are disabled.", start);
                }

                if (request.ProjectPath is null)
                {
                    return Skip(rung, NothingToBuild, start);
                }

                // Rung 3 reports and never gates: Passed is true whatever it finds.
                DiagnosticReport report = await _analyzer
                    .CompileAsync(request.ProjectPath, cancellationToken)
                    .ConfigureAwait(false);

                DiagnosticSummary summary = _summarizer.Summarise(
                    [.. report.Diagnostics.Where(d => d.Severity == CodeSeverity.Warning)],
                    "Analyzer warnings (informational - these do not gate)");

                return new RungResult(rung, true, summary.Text, Elapsed(start));
            }

            case VerificationRung.UnitTests:
            case VerificationRung.FullSuite:
            {
                if (request.ProjectPath is null)
                {
                    return Skip(rung, NothingToBuild, start);
                }

                string? filter = rung == VerificationRung.UnitTests ? request.TestFilter : null;
                ToolObservation<TestRunResult> observation = await _tests
                    .RunTestsAsync(request.ProjectPath, filter, listOnly: false, cancellationToken)
                    .ConfigureAwait(false);

                if (!observation.Ok)
                {
                    return Skip(rung, observation.Error?.Message ?? "The tests could not be run.", start);
                }

                TestRunResult tests = observation.Data!;

                // Zero tests is said out loud in both directions: a clean run that verified
                // nothing is not the same reassurance as a passing suite, and a run that died
                // before its first test is not "0 of 0 tests failed".
                string summary = (tests.Ok, tests.Total) switch
                {
                    (true, 0) => "The test run exited cleanly but ran 0 tests - nothing was verified.",

                    // The suite-quality notices ride the rung report as well as the tool's own
                    // summary (workplan task 66), because this is the sentence the critics read
                    // when they are deciding whether "tests pass" means the work is done.
                    (true, _) => $"{tests.Passed} tests passed." + tests.Notices,
                    (false, 0) => "The test run failed before any test executed.",

                    // The assertion messages ride under the count (workplan task 69). This rung is
                    // what an inline edit_file verification reports through, and it used to give
                    // names alone while the tool record kept the runner's output - two organs, two
                    // views of one fact, and the model was reading the narrowed one. Given only
                    // "two tests failed", the visible repairs are loosening the assertion and
                    // deleting the oracle, and run d5edbc59 took both in that order.
                    _ => $"{tests.Failed} of {tests.Total} tests failed: " +
                         string.Join(", ", tests.FailedTests.Take(5)) +
                         TestOutputParser.Describe(tests.Failures),
                };

                // Zero tests is not green: it does not gate - a testless workspace is a fact,
                // not a failure - but it must not log or read as a passing suite either.
                return new RungResult(rung, tests.Ok, summary, Elapsed(start))
                {
                    Unverified = tests.Ok && tests.Total == 0,
                    Noticed = tests.Ok && tests.Total > 0 && !string.IsNullOrWhiteSpace(tests.Notices),
                };
            }

            case VerificationRung.Critique:
            {
                if (!_critics.CanCritique(request.CriticRole) || request.ChangeDescription is null)
                {
                    return Skip(rung, "Critique is not enabled for this run, or nothing was offered to refute.", start);
                }

                CritiqueResult critique = await _critics.CritiqueAsync(
                    request.Goal ?? "(no goal recorded)",
                    request.ChangeDescription,
                    string.Join(Environment.NewLine, results.Where(r => !r.Skipped).Select(r => r.Summary)),
                    request.CriticRole,
                    claim: null,
                    cancellationToken).ConfigureAwait(false);

                // Whether a refutation blocks or merely warns is configuration: a critic is a
                // model, and a model gating a compiler-verified change is a strong claim.
                bool passed = !critique.Refuted || !_options.CritiqueGates;
                return new RungResult(rung, passed, critique.Summary, Elapsed(start)) { Critique = critique };
            }

            default:
                return Skip(rung, "Unknown rung.", start);
        }
    }

    /// <summary>What the compile and test rungs say when the tree holds nothing buildable.</summary>
    private const string NothingToBuild =
        "No project or solution was found to build. Add one, or set GlassCoder:VerificationLadder:ProjectPath.";

    /// <summary>
    /// What <c>dotnet build</c> should be pointed at for this change.
    /// <para>
    /// An explicit request wins, then the configured override, then the project that owns the
    /// changed files. Null means there is nothing buildable, which the rungs skip on - an
    /// unbuildable tree is a fact about the repository, not a failing edit.
    /// </para>
    /// </summary>
    private string? ResolveTarget(VerificationRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.ProjectPath))
        {
            return request.ProjectPath;
        }

        if (!string.IsNullOrWhiteSpace(_options.ProjectPath))
        {
            return _options.ProjectPath;
        }

        IReadOnlyList<string>? relative = request.ChangedPaths
            ?? (request.FilePath is null ? null : [request.FilePath]);

        IEnumerable<string>? changed = relative?.Select(p =>
            Path.GetFullPath(Path.Combine(_guard.RepoRoot, p.Replace('/', Path.DirectorySeparatorChar))));

        string? target = ProjectLocator.ResolveBuildTarget(_guard.RepoRoot, changed);
        if (target is null)
        {
            _logger.LogWarning(
                "Nothing buildable found under {Root}; the compile and test rungs will be skipped", _guard.RepoRoot);
        }

        return target;
    }

    private static RungResult Skip(VerificationRung rung, string reason, long start) =>
        new(rung, true, reason, Elapsed(start), Skipped: true);

    private static double Elapsed(long start) => Stopwatch.GetElapsedTime(start).TotalMilliseconds;
}

/// <summary>Ladder settings (workplan task 18).</summary>
public sealed class VerificationLadderOptions
{
    /// <summary>Configuration section these options bind from.</summary>
    public const string SectionName = "GlassCoder:VerificationLadder";

    /// <summary>
    /// Whether the controller loop climbs the ladder after every step that applied a change
    /// (workplan task 36). On by default: this is the harness's central reliability mechanism,
    /// and a rung that cannot run - no sandbox, not a C# file - skips rather than fails, so
    /// switching it on cannot make an environment worse than switching it off.
    /// </summary>
    public bool VerifyAppliedChanges { get; set; } = true;

    /// <summary>Whether rung 3 runs at all. It never gates either way.</summary>
    public bool RunAnalyzers { get; set; } = true;

    /// <summary>
    /// What the compile and test rungs build, relative to the workspace root. Null works it out
    /// from the change: the project that owns the edited files, else a solution at the root, else
    /// the only project in the tree. Set this when a repository needs a target none of that
    /// finds - and note that a wrong value here is worse than null, because null skips the rung
    /// while a wrong path fails it.
    /// </summary>
    public string? ProjectPath { get; set; }

    /// <summary>
    /// Filter for the unit-test rung of the in-loop climb, so it stays cheaper than the full
    /// suite. Null runs every test - which makes rung 4 the full suite already, and
    /// <see cref="RunFullSuite"/> redundant until a filter narrows it.
    /// </summary>
    public string? TestFilter { get; set; }

    /// <summary>Whether the in-loop climb finishes with the full suite (rung 5).</summary>
    public bool RunFullSuite { get; set; }

    /// <summary>
    /// Cap on the diff text the in-loop climb hands to the critique rung, so one large edit
    /// cannot fill the critic's window.
    /// </summary>
    public int MaxChangeCharacters { get; set; } = 20_000;

    /// <summary>
    /// Whether a refuted critique blocks the change. Off by default: the critique rung's value
    /// is the recovery rate it drives, and a model refuting a compiler-verified change is a
    /// claim worth reading rather than obeying (CLAUDE.md §8).
    /// </summary>
    public bool CritiqueGates { get; set; }
}
