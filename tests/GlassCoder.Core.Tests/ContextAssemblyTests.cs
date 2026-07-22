using GlassCoder.Core.Context;
using GlassCoder.TestSupport;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace GlassCoder.Core.Tests;

/// <summary>
/// Context assembly and compaction (workplan task 12): the window stays inside its budget under
/// a long run, and what survives compaction is what the agent still needs.
/// </summary>
public sealed class ContextAssemblyTests : IDisposable
{
    private readonly TempWorkspace _workspace = new();

    public void Dispose() => _workspace.Dispose();

    [Fact]
    public void The_opening_window_is_the_system_prompt_and_the_goal()
    {
        ContextAssembler assembler = TestContextAssembler.Create();

        IReadOnlyList<ChatMessage> messages = assembler.CreateInitialMessages("You are GlassCoder.", "Fix the bug.");

        messages.Count.ShouldBe(2);
        messages[0].Role.ShouldBe(ChatRole.System);
        messages[1].Text.ShouldBe("Fix the bug.");
    }

    [Fact]
    public void The_lean_root_context_is_loaded_once_and_only_what_was_configured()
    {
        _workspace.WriteFile("CLAUDE.md", "# Project rules\nAlways run the tests.");
        _workspace.WriteFile("NOTES.md", "This file must not be loaded.");

        ContextOptions options = new();
        options.RootContextFiles.Add("CLAUDE.md");

        ContextAssembler assembler = TestContextAssembler.Create(options, _workspace.Guard());
        IReadOnlyList<ChatMessage> messages = assembler.CreateInitialMessages("system", "goal");

        messages.Count.ShouldBe(3);
        messages[1].Text.ShouldContain("Always run the tests.");
        messages[1].Text.ShouldNotContain("must not be loaded");
    }

    [Fact]
    public void The_root_context_is_truncated_rather_than_allowed_to_grow()
    {
        _workspace.WriteFile("BIG.md", new string('x', 40_000));

        ContextOptions options = new() { MaxRootContextTokens = 100 };
        options.RootContextFiles.Add("BIG.md");

        ContextAssembler assembler = TestContextAssembler.Create(options, _workspace.Guard());
        IReadOnlyList<ChatMessage> messages = assembler.CreateInitialMessages("system", "goal");

        messages[1].Text.ShouldContain("truncated to stay lean");
        messages[1].Text!.Length.ShouldBeLessThan(2000);
    }

    [Fact]
    public void A_missing_root_context_file_is_skipped_not_fatal()
    {
        ContextOptions options = new();
        options.RootContextFiles.Add("DOES-NOT-EXIST.md");

        ContextAssembler assembler = TestContextAssembler.Create(options, _workspace.Guard());

        Should.NotThrow(() => assembler.CreateInitialMessages("system", "goal"));
    }

    [Fact]
    public void A_window_inside_its_budget_is_left_alone()
    {
        ContextAssembler assembler = TestContextAssembler.Create(new ContextOptions { MaxContextTokens = 10_000 });
        List<ChatMessage> history = [new(ChatRole.System, "system"), new(ChatRole.User, "goal")];

        AssembledContext assembled = assembler.Assemble(history);

        assembled.Compacted.ShouldBeFalse();
        assembled.Messages.ShouldBeSameAs(history);
    }

    [Fact]
    public void A_long_run_compacts_back_inside_its_budget()
    {
        ContextOptions options = new() { MaxContextTokens = 2_000, CompactionThreshold = 0.8, KeepRecentTurns = 4 };
        ContextAssembler assembler = TestContextAssembler.Create(options);

        List<ChatMessage> history = [new(ChatRole.System, "system"), new(ChatRole.User, "Fix the failing test.")];
        for (int i = 0; i < 40; i++)
        {
            history.Add(new ChatMessage(ChatRole.Assistant, new string('a', 400)));
            history.Add(new ChatMessage(ChatRole.Tool, new string('t', 400)));
        }

        AssembledContext assembled = assembler.Assemble(history);

        assembled.Compacted.ShouldBeTrue();
        assembled.TurnsSummarised.ShouldBeGreaterThan(0);
        assembled.EstimatedTokens.ShouldBeLessThan((int)(options.MaxContextTokens * options.CompactionThreshold));
        history.Count.ShouldBe(82); // the caller's history is never mutated - it is the transcript
    }

    [Fact]
    public void Compaction_preserves_the_system_prompt_and_the_goal()
    {
        // Losing these is worse than losing history: the agent forgets what it is doing.
        DigestCompactor compactor = new(new HeuristicTokenEstimator(Options.Create(new ContextOptions())));
        List<ChatMessage> history =
        [
            new(ChatRole.System, "You are GlassCoder."),
            new(ChatRole.User, "Fix the off-by-one in Pager."),
        ];

        for (int i = 0; i < 20; i++)
        {
            history.Add(new ChatMessage(ChatRole.Assistant, new string('a', 500)));
        }

        CompactionResult result = compactor.Compact(history, tokenBudget: 200, keepRecentTurns: 2);

        result.Compacted.ShouldBeTrue();
        result.Messages[0].Text.ShouldBe("You are GlassCoder.");
        result.Messages[1].Text.ShouldBe("Fix the off-by-one in Pager.");
        result.Messages.Count.ShouldBeLessThan(history.Count);
    }

    [Fact]
    public void The_digest_tells_the_agent_which_tools_it_has_already_run()
    {
        DigestCompactor compactor = new(new HeuristicTokenEstimator(Options.Create(new ContextOptions())));
        List<ChatMessage> history =
        [
            new(ChatRole.System, "system"),
            new(ChatRole.User, "goal"),
            new(ChatRole.Assistant, [new FunctionCallContent("c1", "read_file", new Dictionary<string, object?> { ["path"] = "src/Pager.cs" })]),
            new(ChatRole.Tool, new string('t', 4000)),
            new(ChatRole.Assistant, [new FunctionCallContent("c2", "grep", new Dictionary<string, object?> { ["pattern"] = "index" })]),
            new(ChatRole.Tool, new string('t', 4000)),
            new(ChatRole.Assistant, "Recent thinking."),
        ];

        CompactionResult result = compactor.Compact(history, tokenBudget: 100, keepRecentTurns: 1);

        string digest = result.Messages[2].Text!;
        digest.ShouldContain("read_file(path=src/Pager.cs)");
        digest.ShouldContain("grep(pattern=index)");
        digest.ShouldContain("Do not repeat a call above");
    }

    [Fact]
    public void Compaction_gives_up_rather_than_dropping_the_turns_the_agent_is_working_from()
    {
        // When even the recent turns exceed the budget, silently discarding them would leave the
        // agent reasoning about nothing. The token limit is the right thing to stop the run.
        DigestCompactor compactor = new(new HeuristicTokenEstimator(Options.Create(new ContextOptions())));
        List<ChatMessage> history =
        [
            new(ChatRole.System, "system"),
            new(ChatRole.User, "goal"),
            new(ChatRole.Assistant, new string('a', 10_000)),
        ];

        CompactionResult result = compactor.Compact(history, tokenBudget: 10, keepRecentTurns: 6);

        result.Compacted.ShouldBeFalse();
        result.Messages.Count.ShouldBe(3);
    }

    [Fact]
    public void The_estimator_scales_with_content_and_counts_tool_calls()
    {
        HeuristicTokenEstimator estimator = new(Options.Create(new ContextOptions { CharactersPerToken = 4 }));

        estimator.Estimate("12345678").ShouldBe(2);
        estimator.Estimate(new ChatMessage(ChatRole.User, "12345678")).ShouldBe(6); // 2 + overhead
        estimator.Estimate(new ChatMessage(ChatRole.Assistant,
            [new FunctionCallContent("c1", "read_file", new Dictionary<string, object?> { ["path"] = "a.cs" })]))
            .ShouldBeGreaterThan(4);
    }
}
