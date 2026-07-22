using System.Globalization;
using GlassCoder.Core.Agent;
using GlassCoder.Core.Hosting;
using GlassCoder.Core.Metrics;
using GlassCoder.Host;
using GlassCoder.Lab.Ablation;
using GlassCoder.Lab.TaskSuite;
using GlassCoder.Tools.Execution;
using GlassCoder.Tools.Guardrails;
using GlassCoder.Tools.Registry;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// The headless surface (CLAUDE.md §17, workplan task 30). Same services as the desktop app,
// no interaction, and exit codes CI can branch on.
HostCommand command = CommandLine.Parse(args);

if (command.Error is not null)
{
    Console.Error.WriteLine(command.Error);
    Console.Error.WriteLine();
    Console.Error.WriteLine(CommandLine.Usage);
    return HostExitCode.ConfigurationError;
}

if (command.Verb == "help")
{
    Console.WriteLine(CommandLine.Usage);
    return HostExitCode.Success;
}

using CancellationTokenSource cancellation = new();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cancellation.Cancel();
};

try
{
    HostApplicationBuilder builder = GlassCoderHost.CreateBuilder(args, command.ConfigPath);

    if (!string.IsNullOrWhiteSpace(command.RepoRoot))
    {
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            [$"{WorkspaceOptions.SectionName}:RepoRoot"] = Path.GetFullPath(command.RepoRoot),
        });
    }

    using IHost host = builder.Build();
    await host.StartAsync(cancellation.Token).ConfigureAwait(false);

    int exitCode = command.Verb switch
    {
        "tools" => ListTools(host),
        "run" => await RunGoalAsync(host, command, cancellation.Token).ConfigureAwait(false),
        "suite" => await RunSuiteAsync(host, command, cancellation.Token).ConfigureAwait(false),
        "fixtures" => await CheckFixturesAsync(host, command, cancellation.Token).ConfigureAwait(false),
        "ablate" => await RunAblationAsync(host, command, cancellation.Token).ConfigureAwait(false),
        _ => HostExitCode.ConfigurationError,
    };

    await host.StopAsync(cancellation.Token).ConfigureAwait(false);
    return exitCode;
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("Cancelled.");
    return HostExitCode.Cancelled;
}
catch (OptionsValidationFailure ex)
{
    Console.Error.WriteLine(ex.Message);
    return HostExitCode.ConfigurationError;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"GlassCoder failed: {ex.Message}");
    return HostExitCode.InternalError;
}

static int ListTools(IHost host)
{
    IToolRegistry tools = host.Services.GetRequiredService<IToolRegistry>();
    Console.WriteLine($"{tools.Functions.Count} tools, in the order they are advertised:");
    foreach (Microsoft.Extensions.AI.AIFunction function in tools.Functions)
    {
        Console.WriteLine($"  {function.Name,-14} {function.Description}");
    }

    return HostExitCode.Success;
}

static async Task<int> RunGoalAsync(IHost host, HostCommand command, CancellationToken cancellationToken)
{
    IAgentLoop loop = host.Services.GetRequiredService<IAgentLoop>();

    AgentRunResult result = await loop.RunAsync(
        new AgentRunRequest
        {
            TaskId = command.TaskId ?? "adhoc",
            Goal = command.Goal!,
        },
        cancellationToken).ConfigureAwait(false);

    Console.WriteLine();
    Console.WriteLine($"{result.StopReason} after {result.Steps} steps, {result.TotalTokens} tokens, "
        + $"{CommandLine.Duration(result.Elapsed)}, tool-call validity {result.ToolCallValidityRate:P0}");

    if (!string.IsNullOrWhiteSpace(result.FinalText))
    {
        Console.WriteLine();
        Console.WriteLine(result.FinalText);
    }

    return ExitCodeFor(result.StopReason);
}

static async Task<int> RunSuiteAsync(IHost host, HostCommand command, CancellationToken cancellationToken)
{
    IReadOnlyList<SuiteTask> tasks = command.SuiteTask is null
        ? TaskSuiteDefinition.All
        : TaskSuiteDefinition.Find(command.SuiteTask) is { } single
            ? [single]
            : [];

    if (tasks.Count == 0)
    {
        Console.Error.WriteLine($"No suite task named '{command.SuiteTask}'.");
        return HostExitCode.ConfigurationError;
    }

    string work = Path.GetFullPath(command.WorkDirectory ?? Path.Combine(Path.GetTempPath(), "glasscoder-suite"));
    IAgentLoop loop = host.Services.GetRequiredService<IAgentLoop>();
    IMetricsRecorder metrics = host.Services.GetRequiredService<IMetricsRecorder>();
    TaskSuiteRunner suite = new(
        host.Services.GetRequiredService<ICommandExecutor>(),
        host.Services.GetRequiredService<ILoggerFactory>().CreateLogger<TaskSuiteRunner>());

    int passed = 0;
    foreach (SuiteTask task in tasks)
    {
        string workspace = Path.Combine(work, task.Id);
        TaskSuiteRunner.Materialise(task, workspace);

        AgentRunResult result = await loop.RunAsync(
            new AgentRunRequest { TaskId = task.Id, Goal = task.Goal },
            cancellationToken).ConfigureAwait(false);

        OracleResult oracle = await suite.JudgeAsync(task, workspace, cancellationToken).ConfigureAwait(false);
        if (oracle.Passed)
        {
            passed++;
        }

        if (result.Metrics is { } measured)
        {
            metrics.Record(measured with
            {
                Source = "suite",
                OraclePassed = oracle.Passed,
                RecordedAt = DateTimeOffset.UtcNow,
            });
        }

        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"{(oracle.Passed ? "PASS" : "FAIL")}  {task.Id,-28} {result.Steps,3} steps  {result.TotalTokens,7} tokens  {CommandLine.Duration(result.Elapsed)}"));
    }

    Console.WriteLine();
    Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
        $"pass@1: {passed}/{tasks.Count} ({(double)passed / tasks.Count:P0})"));

    return passed == tasks.Count ? HostExitCode.Success : HostExitCode.TaskFailed;
}

static async Task<int> CheckFixturesAsync(IHost host, HostCommand command, CancellationToken cancellationToken)
{
    // Verifies the suite itself: a fixture that does not start in the state its task assumes
    // makes every pass@1 computed from it meaningless (workplan task 21).
    string work = Path.GetFullPath(command.WorkDirectory ?? Path.Combine(Path.GetTempPath(), "glasscoder-fixtures"));
    TaskSuiteRunner suite = new(
        host.Services.GetRequiredService<ICommandExecutor>(),
        host.Services.GetRequiredService<ILoggerFactory>().CreateLogger<TaskSuiteRunner>());

    int wrong = 0;
    foreach (SuiteTask task in TaskSuiteDefinition.All)
    {
        string workspace = Path.Combine(work, task.Id);
        TaskSuiteRunner.Materialise(task, workspace);

        OracleResult oracle = await suite.JudgeAsync(task, workspace, cancellationToken).ConfigureAwait(false);
        bool asExpected = oracle.Passed == task.StartsGreen;

        if (!asExpected)
        {
            wrong++;
        }

        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"{(asExpected ? "ok  " : "WRONG")}  {task.Id,-28} starts {(oracle.Passed ? "green" : "red")}, expected {(task.StartsGreen ? "green" : "red")}"));
    }

    Console.WriteLine();
    Console.WriteLine(wrong == 0
        ? "Every fixture starts in the state its task assumes."
        : $"{wrong} fixture(s) do not start as expected.");

    return wrong == 0 ? HostExitCode.Success : HostExitCode.TaskFailed;
}

static async Task<int> RunAblationAsync(IHost host, HostCommand command, CancellationToken cancellationToken)
{
    string work = Path.GetFullPath(command.WorkDirectory ?? Path.Combine(Path.GetTempPath(), "glasscoder-ablation"));

    AblationRunner runner = new(
        host.Services.GetRequiredService<IConfiguration>(),
        host.Services.GetRequiredService<IMetricsRecorder>(),
        host.Services.GetRequiredService<ILoggerFactory>());

    IReadOnlyList<SuiteTask> tasks = command.SuiteTask is null
        ? TaskSuiteDefinition.All
        : TaskSuiteDefinition.Find(command.SuiteTask) is { } single ? [single] : TaskSuiteDefinition.All;

    AblationReport report = await runner
        .RunAsync(StandardArms.Default, tasks, work, cancellationToken)
        .ConfigureAwait(false);

    Console.WriteLine();
    Console.WriteLine(report.ToText());

    return report.PassRate(StandardArms.Baseline.Name) > 0 ? HostExitCode.Success : HostExitCode.TaskFailed;
}

static int ExitCodeFor(AgentStopReason reason) => reason switch
{
    AgentStopReason.Completed => HostExitCode.Success,
    AgentStopReason.Cancelled => HostExitCode.Cancelled,
    AgentStopReason.ModelError => HostExitCode.ModelError,
    AgentStopReason.StepLimit or AgentStopReason.TokenLimit or AgentStopReason.TimeLimit
        or AgentStopReason.CostLimit or AgentStopReason.ToolFailureLimit => HostExitCode.LimitExceeded,
    _ => HostExitCode.InternalError,
};

/// <summary>Marker for configuration failures, so they map to their own exit code.</summary>
internal sealed class OptionsValidationFailure : Exception
{
    public OptionsValidationFailure(string message) : base(message)
    {
    }
}
