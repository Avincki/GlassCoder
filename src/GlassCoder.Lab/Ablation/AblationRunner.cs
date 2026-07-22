using System.Globalization;
using System.Text;
using GlassCoder.Core.Agent;
using GlassCoder.Core.DependencyInjection;
using GlassCoder.Core.Metrics;
using GlassCoder.Lab.TaskSuite;
using GlassCoder.Tools.Execution;
using GlassCoder.Tools.Guardrails;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace GlassCoder.Lab.Ablation;

/// <summary>
/// One configuration variant to measure (CLAUDE.md §2, workplan task 22).
/// </summary>
/// <param name="Name">Short name, recorded on every metrics row the arm produces.</param>
/// <param name="Description">What this arm is testing.</param>
/// <param name="Settings">
/// Configuration overrides, as flat keys - <c>GlassCoder:Context:EnableCompaction</c> and so on.
/// An arm is <em>only</em> ever configuration: the moment an arm needs a code change, two arms
/// stop being comparable.
/// </param>
public sealed record AblationArm(string Name, string Description, IReadOnlyDictionary<string, string?> Settings);

/// <summary>What one arm did on one task.</summary>
/// <param name="Arm">The arm.</param>
/// <param name="Task">The task.</param>
/// <param name="Passed">Whether the oracle went green.</param>
/// <param name="Metrics">The measured indicators.</param>
public sealed record AblationCell(AblationArm Arm, SuiteTask Task, bool Passed, RunMetrics Metrics);

/// <summary>What a whole ablation produced.</summary>
/// <param name="Cells">Every arm-by-task cell.</param>
public sealed record AblationReport(IReadOnlyList<AblationCell> Cells)
{
    /// <summary>pass@1 for one arm.</summary>
    public double PassRate(string arm)
    {
        List<AblationCell> cells = [.. Cells.Where(c => c.Arm.Name == arm)];
        return cells.Count == 0 ? 0d : (double)cells.Count(c => c.Passed) / cells.Count;
    }

    /// <summary>Renders the comparison as a table.</summary>
    public string ToText()
    {
        CultureInfo culture = CultureInfo.InvariantCulture;
        StringBuilder text = new();
        text.AppendLine("arm                  pass@1   tokens    wall-clock  validity  edits  cost");

        foreach (IGrouping<string, AblationCell> group in Cells.GroupBy(c => c.Arm.Name))
        {
            List<AblationCell> cells = [.. group];
            text.AppendLine(culture,
                $"{group.Key,-20} {PassRate(group.Key),6:P0}   " +
                $"{cells.Sum(c => c.Metrics.TotalTokens),7}   " +
                $"{cells.Sum(c => c.Metrics.WallClockMs) / 1000,9:F1}s  " +
                $"{cells.Average(c => c.Metrics.ToolCallValidityRate),7:P0}  " +
                $"{cells.Sum(c => c.Metrics.Edits),5}  " +
                $"{cells.Sum(c => c.Metrics.CostUsd),5:F3}");
        }

        return text.ToString();
    }
}

/// <summary>
/// Runs arms across the task suite and writes comparable metrics (workplan task 22).
/// <para>
/// Every arm builds its own service provider from the same base configuration plus its own
/// overrides, and runs the same tasks from byte-identical fixtures. That is the entire
/// experimental design: change one variable, hold everything else still, and let the oracle
/// decide (CLAUDE.md §17 - one variable at a time).
/// </para>
/// </summary>
public sealed class AblationRunner
{
    private readonly IConfiguration _baseConfiguration;
    private readonly IMetricsRecorder _metrics;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<AblationRunner> _logger;

    /// <summary>Creates the runner.</summary>
    public AblationRunner(
        IConfiguration baseConfiguration,
        IMetricsRecorder metrics,
        ILoggerFactory? loggerFactory = null)
    {
        _baseConfiguration = baseConfiguration;
        _metrics = metrics;
        _loggerFactory = loggerFactory ?? Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance;
        _logger = _loggerFactory.CreateLogger<AblationRunner>();
    }

    /// <summary>Runs every arm across every task.</summary>
    /// <param name="arms">The variants to compare.</param>
    /// <param name="tasks">The tasks to run them on.</param>
    /// <param name="workspaceRoot">Directory under which each arm-task workspace is materialised.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    public async Task<AblationReport> RunAsync(
        IReadOnlyList<AblationArm> arms,
        IReadOnlyList<SuiteTask> tasks,
        string workspaceRoot,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(arms);
        ArgumentNullException.ThrowIfNull(tasks);

        List<AblationCell> cells = [];

        foreach (AblationArm arm in arms)
        {
            foreach (SuiteTask task in tasks)
            {
                cancellationToken.ThrowIfCancellationRequested();

                string workspace = Path.Combine(workspaceRoot, arm.Name, task.Id);
                TaskSuiteRunner.Materialise(task, workspace);

                AblationCell cell = await RunCellAsync(arm, task, workspace, cancellationToken).ConfigureAwait(false);
                cells.Add(cell);

                _logger.LogInformation(
                    "Arm {Arm} on {Task}: {Outcome} ({Steps} steps, {Tokens} tokens)",
                    arm.Name, task.Id, cell.Passed ? "PASS" : "FAIL", cell.Metrics.Steps, cell.Metrics.TotalTokens);
            }
        }

        return new AblationReport(cells);
    }

    private async Task<AblationCell> RunCellAsync(
        AblationArm arm,
        SuiteTask task,
        string workspace,
        CancellationToken cancellationToken)
    {
        // The arm's configuration: the base, then its overrides, then the workspace it runs in.
        Dictionary<string, string?> settings = new(arm.Settings, StringComparer.OrdinalIgnoreCase)
        {
            [$"{WorkspaceOptions.SectionName}:RepoRoot"] = workspace,
        };

        IConfigurationRoot configuration = new ConfigurationBuilder()
            .AddConfiguration(_baseConfiguration)
            .AddInMemoryCollection(settings)
            .Build();

        ServiceCollection services = new();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddSingleton(_loggerFactory);
        services.AddLogging();
        services.AddGlassCoder(configuration);

        // Metrics go to the shared recorder so every arm lands in one comparable file.
        services.AddSingleton(_metrics);

        await using ServiceProvider provider = services.BuildServiceProvider();

        IAgentLoop loop = provider.GetRequiredService<IAgentLoop>();
        TaskSuiteRunner suite = new(
            provider.GetRequiredService<ICommandExecutor>(),
            _loggerFactory.CreateLogger<TaskSuiteRunner>());

        AgentRunResult result = await loop.RunAsync(
            new AgentRunRequest { TaskId = task.Id, Goal = task.Goal },
            cancellationToken).ConfigureAwait(false);

        OracleResult oracle = await suite.JudgeAsync(task, workspace, cancellationToken).ConfigureAwait(false);

        RunMetrics metrics = (result.Metrics ?? new RunMetricsCollector().Build(result, arm.Name, oracle.Passed, DateTimeOffset.UtcNow))
            with
            {
                Source = $"ablation:{arm.Name}",
                Arm = arm.Name,
                OraclePassed = oracle.Passed,
                RecordedAt = DateTimeOffset.UtcNow,
            };

        _metrics.Record(metrics);
        return new AblationCell(arm, task, oracle.Passed, metrics);
    }
}
