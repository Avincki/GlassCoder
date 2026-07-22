using System.Diagnostics;
using GlassCoder.Tools;
using GlassCoder.Tools.Build;
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
    /// oracle is another model rather than a compiler or a test.
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
    bool Skipped = false);

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
}

/// <summary>Everything the ladder needs to know about what it is verifying.</summary>
/// <param name="FilePath">The edited file, when a single file was changed.</param>
/// <param name="FileText">That file's new content, for the syntax rung.</param>
/// <param name="ProjectPath">Project or directory to compile and test.</param>
/// <param name="TestFilter">Filter for the unit-test rung, so it stays cheaper than the full suite.</param>
/// <param name="RunFullSuite">Whether to finish with the whole suite.</param>
/// <param name="Goal">What the change was meant to achieve, for the critique rung.</param>
/// <param name="ChangeDescription">The change itself, for the critique rung.</param>
public sealed record VerificationRequest(
    string? FilePath = null,
    string? FileText = null,
    string ProjectPath = ".",
    string? TestFilter = null,
    bool RunFullSuite = false,
    string? Goal = null,
    string? ChangeDescription = null);

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
    private readonly VerificationLadderOptions _options;
    private readonly ILogger<VerificationLadder> _logger;

    /// <summary>Creates the ladder.</summary>
    public VerificationLadder(
        ICodeAnalyzer analyzer,
        DiagnosticSummarizer summarizer,
        BuildTool build,
        RunTestsTool tests,
        ICriticPanel critics,
        IOptions<VerificationLadderOptions> options,
        ILogger<VerificationLadder>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        _analyzer = analyzer;
        _summarizer = summarizer;
        _build = build;
        _tests = tests;
        _critics = critics;
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
                rung, result.Passed ? "passed" : "FAILED", result.DurationMs);

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
                string? filter = rung == VerificationRung.UnitTests ? request.TestFilter : null;
                ToolObservation<TestRunResult> observation = await _tests
                    .RunTestsAsync(request.ProjectPath, filter, cancellationToken)
                    .ConfigureAwait(false);

                if (!observation.Ok)
                {
                    return Skip(rung, observation.Error?.Message ?? "The tests could not be run.", start);
                }

                TestRunResult tests = observation.Data!;
                string summary = tests.Ok
                    ? $"{tests.Passed} tests passed."
                    : $"{tests.Failed} of {tests.Total} tests failed: {string.Join(", ", tests.FailedTests.Take(5))}";

                return new RungResult(rung, tests.Ok, summary, Elapsed(start));
            }

            case VerificationRung.Critique:
            {
                if (!_critics.Enabled || request.ChangeDescription is null)
                {
                    return Skip(rung, "Critique is not enabled for this run.", start);
                }

                CritiqueResult critique = await _critics.CritiqueAsync(
                    request.Goal ?? "(no goal recorded)",
                    request.ChangeDescription,
                    string.Join(Environment.NewLine, results.Where(r => !r.Skipped).Select(r => r.Summary)),
                    cancellationToken).ConfigureAwait(false);

                // Whether a refutation blocks or merely warns is configuration: a critic is a
                // model, and a model gating a compiler-verified change is a strong claim.
                bool passed = !critique.Refuted || !_options.CritiqueGates;
                return new RungResult(rung, passed, critique.Summary, Elapsed(start));
            }

            default:
                return Skip(rung, "Unknown rung.", start);
        }
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

    /// <summary>Whether rung 3 runs at all. It never gates either way.</summary>
    public bool RunAnalyzers { get; set; } = true;

    /// <summary>
    /// Whether a refuted critique blocks the change. Off by default: the critique rung's value
    /// is the recovery rate it drives, and a model refuting a compiler-verified change is a
    /// claim worth reading rather than obeying (CLAUDE.md §8).
    /// </summary>
    public bool CritiqueGates { get; set; }
}
