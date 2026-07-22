using System.Diagnostics;
using GlassCoder.Tools.Execution;
using Microsoft.Extensions.Logging;

namespace GlassCoder.Lab.TaskSuite;

/// <summary>
/// Materialises a suite task into a workspace and runs its oracle (workplan task 21).
/// <para>
/// The oracle is the fixture's own executable, run to completion: exit code zero means every
/// check held. No human reads the output to decide - that is what makes the suite usable as an
/// ablation harness, where dozens of runs are graded and nobody has time to read any of them
/// (CLAUDE.md §15, §16).
/// </para>
/// </summary>
public sealed class TaskSuiteRunner
{
    private readonly ICommandExecutor _executor;
    private readonly ILogger<TaskSuiteRunner> _logger;

    /// <summary>Creates the runner.</summary>
    public TaskSuiteRunner(ICommandExecutor executor, ILogger<TaskSuiteRunner>? logger = null)
    {
        _executor = executor;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<TaskSuiteRunner>.Instance;
    }

    /// <summary>Writes a task's fixture into a directory, replacing whatever was there.</summary>
    public static void Materialise(SuiteTask task, string directory)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }

        Directory.CreateDirectory(directory);

        foreach ((string relativePath, string content) in task.Files)
        {
            string full = Path.Combine(directory, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, content);
        }
    }

    /// <summary>
    /// Runs the fixture and reports whether its checks held. A fixture that will not build is a
    /// failure, not an error: the agent's job included leaving it buildable.
    /// </summary>
    public async Task<OracleResult> JudgeAsync(
        SuiteTask task,
        string directory,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(task);

        long start = Stopwatch.GetTimestamp();

        CommandResult result = await _executor.ExecuteAsync(
            new CommandRequest("dotnet", ["run", "--project", ".", "-v", "q", "--nologo"])
            {
                WorkingDirectory = directory,
                RequiresNetwork = true,
                Timeout = TimeSpan.FromMinutes(5),
            },
            cancellationToken).ConfigureAwait(false);

        double elapsed = Stopwatch.GetElapsedTime(start).TotalMilliseconds;

        if (result.FailureReason is not null)
        {
            // The oracle could not run at all. That is not a pass and not a fair fail either -
            // say so plainly rather than recording a verdict nobody can trust.
            _logger.LogError("Oracle for {TaskId} could not run: {Reason}", task.Id, result.FailureReason);
            return new OracleResult(task, false, $"The oracle could not run: {result.FailureReason}", elapsed);
        }

        bool passed = result.ExitCode == 0 &&
                      result.CombinedOutput.Contains("ALL TESTS PASSED", StringComparison.Ordinal);

        _logger.LogInformation(
            "Oracle for {TaskId}: {Outcome} (exit {ExitCode}) in {Elapsed:F0} ms",
            task.Id, passed ? "PASS" : "FAIL", result.ExitCode, elapsed);

        return new OracleResult(task, passed, result.CombinedOutput, elapsed);
    }

    /// <summary>
    /// Confirms a fixture is genuinely failing before the agent touches it. A task whose oracle
    /// already passes measures nothing.
    /// </summary>
    public async Task<bool> StartsRedAsync(SuiteTask task, string directory, CancellationToken cancellationToken = default)
    {
        Materialise(task, directory);
        OracleResult result = await JudgeAsync(task, directory, cancellationToken).ConfigureAwait(false);
        return !result.Passed;
    }
}
