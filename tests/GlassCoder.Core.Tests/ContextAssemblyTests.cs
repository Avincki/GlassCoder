using System.Text.Json;
using GlassCoder.Core.Context;
using GlassCoder.TestSupport;
using GlassCoder.Tools;
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

    /// <summary>
    /// Run 48a7af6a spent six steps discovering a five-file workspace and later re-read two of
    /// the files. The opening window now carries that picture: every file listed, small ones
    /// inlined, all inside one bounded block.
    /// </summary>
    [Fact]
    public void A_small_workspace_is_fully_visible_at_step_zero()
    {
        _workspace.WriteFile("src/Pager.cs", "public class Pager { public int X => 1; }");
        _workspace.WriteFile("src/Proj.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");

        ContextAssembler assembler = TestContextAssembler.Create(
            new ContextOptions(), _workspace.Guard(), new Tools.FileSystem.WorkspaceMapBuilder(_workspace.Guard()));
        IReadOnlyList<ChatMessage> messages = assembler.CreateInitialMessages("system", "goal");

        messages.Count.ShouldBe(3);
        string map = messages[1].Text!;
        map.ShouldContain("src/Pager.cs");
        map.ShouldContain("src/Proj.csproj");
        map.ShouldContain("public int X => 1;", customMessage: "a small file's contents belong in the map, not behind a read_file step");
        messages[^1].Text.ShouldBe("goal");
    }

    [Fact]
    public void The_workspace_map_is_truncated_rather_than_allowed_to_grow()
    {
        for (int i = 0; i < 50; i++)
        {
            _workspace.WriteFile($"src/File{i:D2}.cs", "class C { }");
        }

        ContextOptions options = new() { MaxWorkspaceMapTokens = 50 };
        ContextAssembler assembler = TestContextAssembler.Create(
            options, _workspace.Guard(), new Tools.FileSystem.WorkspaceMapBuilder(_workspace.Guard()));
        IReadOnlyList<ChatMessage> messages = assembler.CreateInitialMessages("system", "goal");

        string map = messages[1].Text!;
        map.ShouldContain("truncated");
        map.Length.ShouldBeLessThan(600);
    }

    /// <summary>
    /// An empty workspace is when orientation matters most: run 21f25fea aimed its first
    /// scaffold at the unwritable root, was refused, and looped to the step limit. The map's
    /// one-line answer - empty, and here is where you may write - pre-empts the whole spiral.
    /// </summary>
    [Fact]
    public void An_empty_workspace_still_names_its_writable_roots()
    {
        ContextAssembler assembler = TestContextAssembler.Create(
            new ContextOptions(),
            _workspace.Guard("src"),
            new Tools.FileSystem.WorkspaceMapBuilder(
                _workspace.Guard("src"), TempWorkspace.Wrap(_workspace.Options("src"))));

        IReadOnlyList<ChatMessage> messages = assembler.CreateInitialMessages("system", "goal");

        messages.Count.ShouldBe(3);
        messages[1].Text.ShouldContain("empty");
        messages[1].Text.ShouldContain("Writable roots: src");
    }

    [Fact]
    public void The_workspace_map_can_be_switched_off()
    {
        _workspace.WriteFile("src/Pager.cs", "public class Pager { }");

        ContextAssembler assembler = TestContextAssembler.Create(
            new ContextOptions { IncludeWorkspaceMap = false },
            _workspace.Guard(),
            new Tools.FileSystem.WorkspaceMapBuilder(_workspace.Guard()));

        assembler.CreateInitialMessages("system", "goal").Count.ShouldBe(2);
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
    public void The_digest_keeps_each_calls_outcome_and_why_it_failed()
    {
        // The digest used to read only the calls, so every ok flag and refusal reason vanished
        // at the compaction horizon - and "do not repeat a call above" applied as readily to a
        // write that was refused ten times as to one that landed (run 5c071f37).
        DigestCompactor compactor = new(new HeuristicTokenEstimator(Options.Create(new ContextOptions())));
        List<ChatMessage> history =
        [
            new(ChatRole.System, "system"),
            new(ChatRole.User, "goal"),
            new(ChatRole.Assistant, [new FunctionCallContent("c1", "read_file", new Dictionary<string, object?> { ["path"] = "src/Pager.cs" })]),
            new(ChatRole.Tool, [new FunctionResultContent("c1", Observation.Ok("read_file", "content", "Read src/Pager.cs."))]),
            new(ChatRole.Assistant, [new FunctionCallContent("c2", "create_file", new Dictionary<string, object?> { ["path"] = "src/MainWindow.xaml.cs" })]),
            new(ChatRole.Tool, [new FunctionResultContent("c2", Observation.Fail<string>(
                "create_file",
                ToolErrorCodes.VerificationFailed,
                "'src/MainWindow.xaml.cs' was not written: it would not compile.\nThis file would introduce 5 new compile error(s)."))]),
            new(ChatRole.Assistant, new string('a', 4000)),
            new(ChatRole.Assistant, "Recent thinking."),
        ];

        CompactionResult result = compactor.Compact(history, tokenBudget: 100, keepRecentTurns: 1);

        string digest = result.Messages[2].Text!;
        digest.ShouldContain("✓ read_file(path=src/Pager.cs)");
        digest.ShouldContain("✗ create_file(path=src/MainWindow.xaml.cs)");
        digest.ShouldContain("verification_failed: 'src/MainWindow.xaml.cs' was not written: it would not compile.");
        digest.ShouldNotContain("introduce 5");   // only the stable first line of a failure belongs here
        digest.ShouldContain("Calls marked ✗ changed nothing");
    }

    [Fact]
    public void A_relayed_failure_behind_a_succeeded_call_is_marked_failed()
    {
        // dotnet_project relays a failed SDK command as ok:true; the digest must not file it
        // under "do not repeat" as though it had worked (run 4b562c91).
        DigestCompactor compactor = new(new HeuristicTokenEstimator(Options.Create(new ContextOptions())));
        List<ChatMessage> history =
        [
            new(ChatRole.System, "system"),
            new(ChatRole.User, "goal"),
            new(ChatRole.Assistant, [new FunctionCallContent("c1", "dotnet_project", new Dictionary<string, object?> { ["operation"] = "AddReference" })]),
            new(ChatRole.Tool, [new FunctionResultContent("c1", Observation.Ok(
                "dotnet_project", "exit 1", "dotnet add_reference failed with exit 1.", outcomeOk: false))]),
            new(ChatRole.Assistant, new string('a', 4000)),
            new(ChatRole.Assistant, "Recent thinking."),
        ];

        CompactionResult result = compactor.Compact(history, tokenBudget: 100, keepRecentTurns: 1);

        string digest = result.Messages[2].Text!;
        digest.ShouldContain("✗ dotnet_project(operation=AddReference)");
        digest.ShouldContain("dotnet add_reference failed with exit 1.");
        digest.ShouldContain("Calls marked ✗ changed nothing");
    }

    [Fact]
    public void An_outcome_read_off_the_wire_shape_still_marks_the_row()
    {
        // Live results arrive as the JsonElement the AI function layer serialised, not as the
        // observation object tests construct. Without reading that shape, live digests carry
        // no outcomes at all - and the outcome flag, serialised only when false, must survive
        // the round trip.
        DigestCompactor compactor = new(new HeuristicTokenEstimator(Options.Create(new ContextOptions())));
        JsonElement wire = JsonSerializer.SerializeToElement(
            Observation.Ok("dotnet_project", "exit 1", "dotnet add_reference failed with exit 1.", outcomeOk: false),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        List<ChatMessage> history =
        [
            new(ChatRole.System, "system"),
            new(ChatRole.User, "goal"),
            new(ChatRole.Assistant, [new FunctionCallContent("c1", "dotnet_project", new Dictionary<string, object?> { ["operation"] = "AddReference" })]),
            new(ChatRole.Tool, [new FunctionResultContent("c1", wire)]),
            new(ChatRole.Assistant, new string('a', 4000)),
            new(ChatRole.Assistant, "Recent thinking."),
        ];

        CompactionResult result = compactor.Compact(history, tokenBudget: 100, keepRecentTurns: 1);

        string digest = result.Messages[2].Text!;
        digest.ShouldContain("✗ dotnet_project(operation=AddReference)");
        digest.ShouldContain("dotnet add_reference failed with exit 1.");
    }

    [Fact]
    public void Identical_failures_collapse_into_one_line_that_counts_them()
    {
        // Ten identical refusals are one fact, not ten lines - and the count is the fact. The
        // detail lines vary between attempts (a strike countdown, a diagnostics total), so
        // aggregation keys on the stable first line, like every other repeat detector.
        DigestCompactor compactor = new(new HeuristicTokenEstimator(Options.Create(new ContextOptions())));
        List<ChatMessage> history = [new(ChatRole.System, "system"), new(ChatRole.User, "goal")];
        for (int attempt = 0; attempt < 3; attempt++)
        {
            history.Add(new ChatMessage(
                ChatRole.Assistant,
                [new FunctionCallContent($"c{attempt}", "create_file", new Dictionary<string, object?> { ["path"] = "src/A.cs" })]));
            history.Add(new ChatMessage(
                ChatRole.Tool,
                [new FunctionResultContent($"c{attempt}", Observation.Fail<string>(
                    "create_file",
                    ToolErrorCodes.VerificationFailed,
                    $"'src/A.cs' was not written: it would not compile.\nAttempt {attempt} detail."))]));
        }

        history.Add(new ChatMessage(ChatRole.Assistant, new string('a', 4000)));
        history.Add(new ChatMessage(ChatRole.Assistant, "Recent thinking."));

        CompactionResult result = compactor.Compact(history, tokenBudget: 100, keepRecentTurns: 1);

        string digest = result.Messages[2].Text!;
        digest.ShouldContain("(×3)");
        digest.Split("create_file(path=src/A.cs)").Length.ShouldBe(2, "three identical refusals are one row");
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
