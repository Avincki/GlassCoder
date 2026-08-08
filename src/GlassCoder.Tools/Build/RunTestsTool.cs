using System.ComponentModel;
using GlassCoder.Tools.Execution;
using GlassCoder.Tools.Guardrails;
using GlassCoder.Tools.Registry;
using GlassCoder.Tools.Verification;
using Microsoft.Extensions.Options;

namespace GlassCoder.Tools.Build;

/// <summary>Result payload of <c>run_tests</c>.</summary>
/// <param name="Path">What was tested.</param>
/// <param name="Ok">Whether the run was green.</param>
/// <param name="Passed">Tests that passed.</param>
/// <param name="Failed">Tests that failed.</param>
/// <param name="Skipped">Tests that were skipped.</param>
/// <param name="Total">Tests that ran.</param>
/// <param name="FailedTests">Names of the failing tests.</param>
/// <param name="Output">Tail of the run output, for context on the failures.</param>
/// <param name="DurationMs">Wall-clock for the run.</param>
/// <param name="Sandbox">Where it ran: <c>docker</c> or <c>host</c>.</param>
/// <param name="Tests">Discovered test names, when the call was a discovery rather than a run.</param>
/// <param name="Truncated">Whether the discovered list was capped.</param>
public sealed record TestRunResult(
    [property: Description("The project or directory that was tested.")] string Path,
    [property: Description("True when no test failed.")] bool Ok,
    [property: Description("Number of tests that passed.")] int Passed,
    [property: Description("Number of tests that failed.")] int Failed,
    [property: Description("Number of tests that were skipped.")] int Skipped,
    [property: Description("Total number of tests - that ran, or that were discovered.")] int Total,
    [property: Description("Names of the failing tests.")] IReadOnlyList<string> FailedTests,
    [property: Description("Tail of the test output.")] string Output,
    [property: Description("Wall-clock milliseconds the run took.")] double DurationMs,
    [property: Description("Where the tests ran: docker or host.")] string Sandbox,
    [property: Description("Discovered test names, when listOnly was set. Pass one to filter.")]
    IReadOnlyList<string>? Tests = null,
    [property: Description("True when more tests were discovered than are listed here.")]
    bool Truncated = false);

/// <summary>
/// <c>run_tests</c> - the behavioural oracle (CLAUDE.md §7, §8; workplan task 17).
/// <para>
/// Ordered after <c>build</c> deliberately. Tests on code that does not compile tell the agent
/// nothing it did not already know, and cost minutes to say it.
/// </para>
/// </summary>
public sealed class RunTestsTool : IToolSet
{
    private const string ToolName = "run_tests";
    private const int MaxOutputCharacters = 4000;

    /// <summary>How many discovered names to return. The count is always the true one.</summary>
    private const int MaxDiscoveredTests = 100;

    private readonly ICommandExecutor _executor;
    private readonly IPathGuard _guard;
    private readonly SandboxOptions _sandbox;

    /// <summary>Creates the tool.</summary>
    public RunTestsTool(ICommandExecutor executor, IPathGuard guard, IOptions<SandboxOptions> sandbox)
    {
        ArgumentNullException.ThrowIfNull(sandbox);

        _executor = executor;
        _guard = guard;
        _sandbox = sandbox.Value;
    }

    /// <summary>Runs the tests for a project, solution or directory.</summary>
    [GlassCoderTool(ToolName, Order = 60)]
    [Description("Run tests with dotnet test, reporting pass, fail and skip counts and the names of failing "
        + "tests. Build first. Set listOnly to discover test names instead of running them.")]
    public async Task<ToolObservation<TestRunResult>> RunTestsAsync(
        [Description("Repo-relative project, solution or directory. '.' is everything.")]
        string path = ".",
        [Description("dotnet test --filter expression, e.g. 'FullyQualifiedName~AgentLoopTests'.")]
        string? filter = null,
        [Description("List the tests that would run, without running them.")]
        bool listOnly = false,
        CancellationToken cancellationToken = default)
    {
        PathGuardResult verdict = _guard.Resolve(path, PathAccess.Read);
        if (!verdict.Allowed || verdict.FullPath is null)
        {
            return Observation.Fail<TestRunResult>(ToolName, ToolErrorCodes.PathNotAllowed, verdict.Reason!);
        }

        bool isDirectory = Directory.Exists(verdict.FullPath);
        string workingDirectory = isDirectory ? verdict.FullPath : System.IO.Path.GetDirectoryName(verdict.FullPath)!;

        List<string> arguments = ["test", "--nologo"];
        if (!isDirectory)
        {
            arguments.Insert(1, System.IO.Path.GetFileName(verdict.FullPath));
        }

        if (!string.IsNullOrWhiteSpace(filter))
        {
            arguments.Add("--filter");
            arguments.Add(filter);
        }

        if (listOnly)
        {
            arguments.Add("--list-tests");
        }

        CommandResult result = await _executor.ExecuteAsync(
            new CommandRequest("dotnet", arguments)
            {
                WorkingDirectory = workingDirectory,
                RequiresNetwork = true,
                Timeout = TimeSpan.FromSeconds(_sandbox.CommandTimeoutSeconds),
            },
            cancellationToken).ConfigureAwait(false);

        if (result.FailureReason is not null)
        {
            return Observation.Fail<TestRunResult>(
                ToolName,
                ToolErrorCodes.SandboxUnavailable,
                result.FailureReason,
                "Running tests executes arbitrary repository code, so it will not be run outside the sandbox.");
        }

        if (result.TimedOut)
        {
            return Observation.Fail<TestRunResult>(
                ToolName,
                ToolErrorCodes.Timeout,
                $"The test run exceeded {_sandbox.CommandTimeoutSeconds} seconds and was stopped.",
                "Narrow the run with a --filter expression.");
        }

        if (listOnly)
        {
            return Discovered(verdict.RelativePath!, result);
        }

        TestOutcome outcome = TestOutputParser.Parse(result.CombinedOutput);
        TestRunResult payload = new(
            verdict.RelativePath!,
            outcome.Ok && result.ExitCode == 0,
            outcome.Passed,
            outcome.Failed,
            outcome.Skipped,
            outcome.Total,
            outcome.FailedTests,
            Tail(result.CombinedOutput),
            result.Duration.TotalMilliseconds,
            result.Sandbox);

        // "0 of 0 tests failed" reads as green and means the opposite - the run died before a
        // single test executed, or the target holds no tests. Both runs on 2026-08-06 took that
        // line as a pass and deferred the real fix. Zero tests is never a quiet outcome.
        string summary = (payload.Ok, outcome.Total) switch
        {
            (true, 0) =>
                $"The test run exited cleanly but ran 0 tests in '{verdict.RelativePath}'. Nothing was " +
                "verified - check the target is a test project and the filter matches something.",
            (true, _) => $"All {outcome.Total} tests passed.",
            (false, 0) =>
                $"The test run failed before any test executed (exit code {result.ExitCode}). The output " +
                $"ends:\n{Tail(result.CombinedOutput)}",

            // Named, because runs ea9a1f66 and 216360bf each looped on "N of M tests failed"
            // for five and four cycles - a count the model had to dig past to learn which test
            // kept refusing its fixes. The first line is also what the sentry keys repeated
            // failures on, and names make one recurring failure distinguishable from a new one.
            _ => $"{outcome.Failed} of {outcome.Total} tests failed: " +
                 string.Join(", ", outcome.FailedTests.Take(3)) +
                 (outcome.FailedTests.Count > 3 ? $" (+{outcome.FailedTests.Count - 3} more)." : "."),
        };

        // A red suite is information to the model and a failure to the progress machinery,
        // same contract as a refused dotnet command.
        return Observation.Ok(ToolName, payload, summary, outcomeOk: payload.Ok);
    }

    /// <summary>
    /// Turns a discovery run into an observation: the names, capped, and the true total.
    /// <para>
    /// The cap is on the same contract as the diagnostic summariser - say how many there are,
    /// then show a bounded number of them. Four hundred test names is not orientation, it is the
    /// context window spent on something a filter expression would have narrowed.
    /// </para>
    /// </summary>
    private static ToolObservation<TestRunResult> Discovered(string path, CommandResult result)
    {
        IReadOnlyList<string> all = TestOutputParser.ParseDiscovered(result.CombinedOutput);

        if (all.Count == 0)
        {
            // Exit code alone does not separate "no tests here" from "the build failed", and the
            // agent needs to know which. The output is the only thing that can say.
            return Observation.Ok(
                ToolName,
                new TestRunResult(
                    path, result.ExitCode == 0, 0, 0, 0, 0, [], Tail(result.CombinedOutput),
                    result.Duration.TotalMilliseconds, result.Sandbox, [], false),
                result.ExitCode == 0
                    ? $"No tests found in '{path}'."
                    : $"Could not list the tests in '{path}'; discovery has to build first.");
        }

        bool truncated = all.Count > MaxDiscoveredTests;
        IReadOnlyList<string> listed = truncated ? [.. all.Take(MaxDiscoveredTests)] : all;

        TestRunResult payload = new(
            path, true, 0, 0, 0, all.Count, [], string.Empty,
            result.Duration.TotalMilliseconds, result.Sandbox, listed, truncated);

        string summary = truncated
            ? $"{all.Count} tests in '{path}'; listing the first {listed.Count}. Narrow with a filter."
            : $"{all.Count} tests in '{path}'.";

        return Observation.Ok(ToolName, payload, summary);
    }

    private static string Tail(string output) =>
        output.Length <= MaxOutputCharacters
            ? output
            : string.Concat("… [earlier output trimmed]\n", output.AsSpan(output.Length - MaxOutputCharacters));
}
