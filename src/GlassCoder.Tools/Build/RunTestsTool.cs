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
public sealed record TestRunResult(
    [property: Description("The project or directory that was tested.")] string Path,
    [property: Description("True when no test failed.")] bool Ok,
    [property: Description("Number of tests that passed.")] int Passed,
    [property: Description("Number of tests that failed.")] int Failed,
    [property: Description("Number of tests that were skipped.")] int Skipped,
    [property: Description("Total number of tests that ran.")] int Total,
    [property: Description("Names of the failing tests.")] IReadOnlyList<string> FailedTests,
    [property: Description("Tail of the test output.")] string Output,
    [property: Description("Wall-clock milliseconds the run took.")] double DurationMs,
    [property: Description("Where the tests ran: docker or host.")] string Sandbox);

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
    [Description("Run tests with dotnet test and report pass, fail and skip counts plus the names of failing "
        + "tests. Build first: tests on code that does not compile tell you nothing.")]
    public async Task<ToolObservation<TestRunResult>> RunTestsAsync(
        [Description("Project, solution or directory to test, relative to the repository root. Use '.' for everything.")]
        string path = ".",
        [Description("Optional dotnet test --filter expression, for example 'FullyQualifiedName~AgentLoopTests'.")]
        string? filter = null,
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

        string summary = payload.Ok
            ? $"All {outcome.Total} tests passed."
            : $"{outcome.Failed} of {outcome.Total} tests failed.";

        return Observation.Ok(ToolName, payload, summary);
    }

    private static string Tail(string output) =>
        output.Length <= MaxOutputCharacters
            ? output
            : string.Concat("… [earlier output trimmed]\n", output.AsSpan(output.Length - MaxOutputCharacters));
}
