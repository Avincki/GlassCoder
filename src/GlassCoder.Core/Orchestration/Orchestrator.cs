using System.Diagnostics;
using System.Threading.Channels;
using GlassCoder.Core.Agent;
using GlassCoder.Core.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GlassCoder.Core.Orchestration;

/// <summary>One piece of work handed to a sub-agent.</summary>
/// <param name="Id">Identifier, used as the sub-run's task id.</param>
/// <param name="Goal">What the sub-agent is asked to do.</param>
/// <param name="Role">Role override, so a hard sub-goal can go to a bigger model.</param>
public sealed record SubTask(string Id, string Goal, string? Role = null);

/// <summary>What one sub-agent produced.</summary>
/// <param name="SubTask">The work it was given.</param>
/// <param name="Result">What its run did.</param>
public sealed record SubTaskResult(SubTask SubTask, AgentRunResult Result)
{
    /// <summary>Whether the sub-agent finished of its own accord.</summary>
    public bool Completed => Result.RanToCompletion;
}

/// <summary>What a fan-out produced.</summary>
/// <param name="Results">Per sub-task outcomes, in completion order.</param>
/// <param name="WallClockMs">Wall-clock for the fan-out as a whole.</param>
/// <param name="SerialWallClockMs">Summed wall-clock of the sub-runs, as if they had run one at a time.</param>
public sealed record FanOutResult(IReadOnlyList<SubTaskResult> Results, double WallClockMs, double SerialWallClockMs)
{
    /// <summary>Total tokens across the sub-runs - the cost side of the quality-delta trade.</summary>
    public long TotalTokens => Results.Sum(r => r.Result.TotalTokens);

    /// <summary>How many sub-agents finished.</summary>
    public int CompletedCount => Results.Count(r => r.Completed);

    /// <summary>
    /// Speed-up against running the same sub-tasks serially. Under 1 means the fan-out cost
    /// more wall-clock than it saved, which is worth knowing before believing in it.
    /// </summary>
    public double Speedup => WallClockMs <= 0 ? 0d : SerialWallClockMs / WallClockMs;
}

/// <summary>Orchestration settings (CLAUDE.md §17 phase 5, workplan task 33).</summary>
public sealed class OrchestrationOptions
{
    /// <summary>Configuration section these options bind from.</summary>
    public const string SectionName = "GlassCoder:Orchestration";

    /// <summary>Whether sub-agents may be used at all. Off by default - this is the last layer.</summary>
    public bool Enabled { get; set; }

    /// <summary>How many sub-agents run at once. Bounded, because they share one GPU.</summary>
    public int MaxDegreeOfParallelism { get; set; } = 3;

    /// <summary>Step limit for a sub-agent. Deliberately smaller than the parent's.</summary>
    public int SubAgentMaxSteps { get; set; } = 12;
}

/// <summary>
/// Sub-agents and parallel fan-out - built last, on purpose (CLAUDE.md §17, §18; workplan task 33).
/// <para>
/// This is a layer <em>over</em> a working loop, not a substitute for one. Every sub-agent is
/// the same <see cref="IAgentLoop"/> with the same tools, budgets and transcript, so a fan-out
/// is measurable against the solo baseline rather than merely impressive-looking. The metric
/// that matters is quality delta against solo, read next to the tokens it cost.
/// </para>
/// </summary>
public interface IOrchestrator
{
    /// <summary>Whether orchestration is switched on.</summary>
    bool Enabled { get; }

    /// <summary>Runs sub-tasks concurrently and collects their results.</summary>
    Task<FanOutResult> FanOutAsync(IReadOnlyList<SubTask> subTasks, CancellationToken cancellationToken = default);
}

/// <summary>Default <see cref="IOrchestrator"/>, using <see cref="Parallel.ForEachAsync{TSource}(IEnumerable{TSource}, ParallelOptions, Func{TSource, CancellationToken, ValueTask})"/> and a channel to collect results.</summary>
public sealed class Orchestrator : IOrchestrator
{
    private readonly Func<IAgentLoop> _loopFactory;
    private readonly OrchestrationOptions _options;
    private readonly AgentOptions _agentDefaults;
    private readonly ILogger<Orchestrator> _logger;

    /// <summary>Creates the orchestrator.</summary>
    /// <param name="loopFactory">
    /// Produces a fresh loop per sub-agent. A factory rather than a single instance because each
    /// sub-run needs its own budget, and sharing one would let a greedy sub-agent starve the rest.
    /// </param>
    /// <param name="options">Orchestration settings.</param>
    /// <param name="agentOptions">Loop defaults, narrowed for sub-agents.</param>
    /// <param name="logger">Logger.</param>
    public Orchestrator(
        Func<IAgentLoop> loopFactory,
        IOptions<OrchestrationOptions> options,
        IOptions<AgentOptions> agentOptions,
        ILogger<Orchestrator>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(agentOptions);

        _loopFactory = loopFactory;
        _options = options.Value;
        _agentDefaults = agentOptions.Value;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<Orchestrator>.Instance;
    }

    /// <inheritdoc />
    public bool Enabled => _options.Enabled;

    /// <inheritdoc />
    public async Task<FanOutResult> FanOutAsync(
        IReadOnlyList<SubTask> subTasks,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(subTasks);

        if (subTasks.Count == 0)
        {
            return new FanOutResult([], 0, 0);
        }

        long start = Stopwatch.GetTimestamp();
        Channel<SubTaskResult> results = Channel.CreateUnbounded<SubTaskResult>();

        using Activity? activity = GlassCoderActivity.Source.StartActivity("glasscoder.fanout");
        activity?.SetTag("glasscoder.subtask_count", subTasks.Count);

        await Parallel.ForEachAsync(
            subTasks,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = Math.Max(1, _options.MaxDegreeOfParallelism),
                CancellationToken = cancellationToken,
            },
            async (subTask, token) =>
            {
                SubTaskResult result = await RunSubAgentAsync(subTask, token).ConfigureAwait(false);
                await results.Writer.WriteAsync(result, token).ConfigureAwait(false);
            }).ConfigureAwait(false);

        results.Writer.Complete();

        List<SubTaskResult> collected = [];
        await foreach (SubTaskResult result in results.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            collected.Add(result);
        }

        double wallClock = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        double serial = collected.Sum(r => r.Result.Elapsed.TotalMilliseconds);

        _logger.LogInformation(
            "Fan-out of {Count} sub-agents finished in {WallClock:F0} ms ({Serial:F0} ms serial, speed-up {Speedup:F1}x), {Tokens} tokens",
            collected.Count, wallClock, serial, wallClock <= 0 ? 0 : serial / wallClock, collected.Sum(r => r.Result.TotalTokens));

        return new FanOutResult(collected, wallClock, serial);
    }

    private async Task<SubTaskResult> RunSubAgentAsync(SubTask subTask, CancellationToken cancellationToken)
    {
        // A sub-agent gets its own loop, its own budget and its own transcript - it is a run in
        // every sense, just a smaller one.
        AgentOptions limits = new()
        {
            Role = subTask.Role ?? _agentDefaults.Role,
            MaxSteps = _options.SubAgentMaxSteps,
            MaxTotalTokens = _agentDefaults.MaxTotalTokens,
            MaxWallClockSeconds = _agentDefaults.MaxWallClockSeconds,
            MaxCostUsd = _agentDefaults.MaxCostUsd,
            MaxConsecutiveInvalidToolCalls = _agentDefaults.MaxConsecutiveInvalidToolCalls,
            SystemPrompt = _agentDefaults.SystemPrompt,
        };

        AgentRunResult result = await _loopFactory().RunAsync(
            new AgentRunRequest
            {
                TaskId = subTask.Id,
                Goal = subTask.Goal,
                Role = subTask.Role,
                Limits = limits,
            },
            cancellationToken).ConfigureAwait(false);

        return new SubTaskResult(subTask, result);
    }
}
