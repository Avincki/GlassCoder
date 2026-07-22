using System.Globalization;
using System.Text;
using GlassCoder.Tools.Guardrails;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GlassCoder.Core.Context;

/// <summary>
/// Default <see cref="IContextAssembler"/> (workplan task 12).
/// <para>
/// The window is: system prompt + a lean always-loaded root + the conversation so far, with
/// older turns compacted once the budget is crossed. Retrieval is not done here - it is what
/// <c>read_file</c>, <c>grep</c> and <c>glob</c> are for. That separation is the point: the
/// agent pulls what it needs when it needs it, instead of the harness pushing a doc tree into
/// every step (CLAUDE.md §12).
/// </para>
/// </summary>
public sealed class ContextAssembler : IContextAssembler
{
    private readonly ContextOptions _options;
    private readonly ITokenEstimator _estimator;
    private readonly IConversationCompactor _compactor;
    private readonly IPathGuard _guard;
    private readonly ILogger<ContextAssembler> _logger;
    private string? _rootContext;

    /// <summary>Creates the assembler.</summary>
    public ContextAssembler(
        IOptions<ContextOptions> options,
        ITokenEstimator estimator,
        IConversationCompactor compactor,
        IPathGuard guard,
        ILogger<ContextAssembler>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        _options = options.Value;
        _estimator = estimator;
        _compactor = compactor;
        _guard = guard;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<ContextAssembler>.Instance;
    }

    /// <inheritdoc />
    public IReadOnlyList<ChatMessage> CreateInitialMessages(string systemPrompt, string goal)
    {
        List<ChatMessage> messages = [new ChatMessage(ChatRole.System, systemPrompt)];

        string root = LoadRootContext();
        if (!string.IsNullOrWhiteSpace(root))
        {
            messages.Add(new ChatMessage(ChatRole.System, root));
        }

        messages.Add(new ChatMessage(ChatRole.User, goal));
        return messages;
    }

    /// <inheritdoc />
    public AssembledContext Assemble(IReadOnlyList<ChatMessage> history)
    {
        ArgumentNullException.ThrowIfNull(history);

        int budget = CompactionBudget();
        int tokens = _estimator.Estimate(history);

        if (!_options.EnableCompaction || tokens <= budget)
        {
            return new AssembledContext(history, tokens, Compacted: false, TurnsSummarised: 0);
        }

        CompactionResult compaction = _compactor.Compact(history, budget, _options.KeepRecentTurns);
        int compactedTokens = _estimator.Estimate(compaction.Messages);

        if (compaction.Compacted)
        {
            _logger.LogInformation(
                "Compacted {TurnsSummarised} turns: {BeforeTokens} → {AfterTokens} estimated tokens (budget {Budget})",
                compaction.TurnsSummarised, tokens, compactedTokens, budget);
        }
        else
        {
            _logger.LogWarning(
                "Context is {Tokens} estimated tokens, over the {Budget} budget, but nothing could be compacted",
                tokens, budget);
        }

        return new AssembledContext(
            compaction.Messages,
            compactedTokens,
            compaction.Compacted,
            compaction.TurnsSummarised);
    }

    private int CompactionBudget()
    {
        double threshold = _options.CompactionThreshold is > 0 and <= 1 ? _options.CompactionThreshold : 0.8;
        return (int)(_options.MaxContextTokens * threshold);
    }

    /// <summary>
    /// Loads the always-on root once per process. Kept lean by construction: a token ceiling
    /// that truncates rather than a promise to be careful.
    /// </summary>
    private string LoadRootContext()
    {
        if (_rootContext is not null)
        {
            return _rootContext;
        }

        if (_options.RootContextFiles.Count == 0)
        {
            return _rootContext = string.Empty;
        }

        CultureInfo culture = CultureInfo.InvariantCulture;
        StringBuilder root = new();
        root.AppendLine("Always-loaded project context. Retrieve anything else with grep, glob and read_file.");
        int remaining = _options.MaxRootContextTokens;

        foreach (string file in _options.RootContextFiles)
        {
            PathGuardResult verdict = _guard.Resolve(file, PathAccess.Read);
            if (!verdict.Allowed || verdict.FullPath is null || !File.Exists(verdict.FullPath))
            {
                _logger.LogWarning("Root context file {File} was skipped: {Reason}", file, verdict.Reason ?? "not found");
                continue;
            }

            string content;
            try
            {
                content = File.ReadAllText(verdict.FullPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Root context file {File} could not be read", file);
                continue;
            }

            int cost = _estimator.Estimate(content);
            if (cost > remaining)
            {
                content = Truncate(content, remaining);
                cost = remaining;
            }

            if (cost <= 0)
            {
                _logger.LogWarning("Root context budget exhausted before {File} was loaded", file);
                break;
            }

            root.AppendLine();
            root.AppendLine(culture, $"--- {verdict.RelativePath} ---");
            root.AppendLine(content);
            remaining -= cost;
        }

        return _rootContext = root.ToString();
    }

    private string Truncate(string content, int tokenBudget)
    {
        int characters = (int)(tokenBudget * _options.CharactersPerToken);
        return characters >= content.Length
            ? content
            : string.Concat(content.AsSpan(0, Math.Max(characters, 0)), "\n… [root context truncated to stay lean]");
    }
}
