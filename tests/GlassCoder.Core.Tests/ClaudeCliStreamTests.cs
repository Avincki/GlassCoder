using GlassCoder.Core.Verification;
using GlassCoder.TestSupport;
using GlassCoder.Tools.Processes;

namespace GlassCoder.Core.Tests;

/// <summary>
/// Watching a headless session while it works (workplan task 67).
/// <para>
/// A retrospective is minutes of silence otherwise, and silence is what makes an operator wonder
/// whether it is stuck. The narration comes from <c>--output-format stream-json</c>, and the
/// property worth defending is that it can never cost anything: a line the reader does not
/// understand is skipped, a CLI too old for the flag falls back to the buffered call, and neither
/// turns into a failed review.
/// </para>
/// </summary>
public sealed class ClaudeCliStreamTests
{
    [Fact]
    public async Task The_session_narrates_its_tool_calls_and_its_text()
    {
        FakeProcessRunner runner = new FakeProcessRunner().Enqueue(0, Stream(
            """{"type":"system","subtype":"init","model":"claude-opus-5","tools":["Read","Grep"]}""",
            """{"type":"assistant","message":{"role":"assistant","content":[{"type":"text","text":"Reading the tests first."}]}}""",
            """{"type":"assistant","message":{"role":"assistant","content":[{"type":"tool_use","name":"Read","input":{"file_path":"src/A.cs"}}]}}""",
            """{"type":"user","message":{"role":"user","content":[{"type":"tool_result","content":"…"}]}}""",
            """{"type":"result","is_error":false,"session_id":"sess","total_cost_usd":0.5,"result":"done"}"""));

        List<ClaudeCliEvent> seen = [];
        ClaudeCliResult result = await Session(runner).RunAsync(Request(e => seen.Add(e)));

        result.Succeeded.ShouldBeTrue();
        result.Streamed.ShouldBeTrue();
        result.CostUsd.ShouldBe(0.5m);
        result.SessionId.ShouldBe("sess");

        seen.Select(e => e.Kind).ShouldBe(
        [
            ClaudeCliEventKind.Started,
            ClaudeCliEventKind.Text,
            ClaudeCliEventKind.ToolCall,
        ]);

        // The tool's name alone would say "Read" fourteen times over; what it is reading is the
        // half that makes the line worth showing.
        seen[2].Text.ShouldBe("Read src/A.cs");
        seen[0].Text.ShouldContain("claude-opus-5");
    }

    [Fact]
    public async Task Streaming_asks_for_the_flag_the_cli_requires_with_it()
    {
        // stream-json is refused in print mode without --verbose, so the two travel together or
        // the launch fails on a technicality nobody would guess from the symptom.
        FakeProcessRunner runner = new FakeProcessRunner().Enqueue(0, Stream(
            """{"type":"result","is_error":false,"result":"done"}"""));

        await Session(runner).RunAsync(Request(_ => { }));

        List<string> arguments = [.. runner.Requests[^1].Arguments];
        arguments[arguments.IndexOf("--output-format") + 1].ShouldBe("stream-json");
        arguments.ShouldContain("--verbose");
    }

    [Fact]
    public async Task A_caller_that_is_not_watching_gets_the_buffered_call()
    {
        FakeProcessRunner runner = new FakeProcessRunner().Enqueue(0, """{"type":"result","is_error":false,"result":"done"}""");

        await Session(runner).RunAsync(Request(onEvent: null));

        List<string> arguments = [.. runner.Requests[^1].Arguments];
        arguments[arguments.IndexOf("--output-format") + 1].ShouldBe("json");
        arguments.ShouldNotContain("--verbose");
    }

    [Fact]
    public async Task A_line_the_reader_cannot_understand_is_skipped_rather_than_fatal()
    {
        FakeProcessRunner runner = new FakeProcessRunner().Enqueue(0, Stream(
            "Debugger listening on ws://127.0.0.1:9229",
            """{"type":"assistant","message":{"role":"assistant","content":[{"type":"tool_use","name":"Grep","input":{"pattern":"IChangeLog"}}]}}""",
            "{ this is not json",
            """{"type":"result","is_error":false,"session_id":"sess","result":"done"}"""));

        List<ClaudeCliEvent> seen = [];
        ClaudeCliResult result = await Session(runner).RunAsync(Request(e => seen.Add(e)));

        result.Succeeded.ShouldBeTrue();
        seen.Count.ShouldBe(1);
        seen[0].Text.ShouldBe("Grep IChangeLog");
    }

    [Fact]
    public async Task A_cli_too_old_to_stream_falls_back_and_says_so_once()
    {
        // The launch is refused outright, so nothing ran and nothing was billed - which is what
        // makes retrying it safe. The operator loses the narration and keeps the retrospective.
        FakeProcessRunner runner = new FakeProcessRunner()
            .Enqueue(1, standardError: "error: unknown option '--output-format stream-json'")
            .Enqueue(0, """{"type":"result","is_error":false,"session_id":"sess","result":"the review"}""");

        List<ClaudeCliEvent> seen = [];
        ClaudeCliResult result = await Session(runner).RunAsync(Request(e => seen.Add(e)));

        result.Succeeded.ShouldBeTrue();
        result.Result.ShouldBe("the review");
        seen.ShouldHaveSingleItem().Kind.ShouldBe(ClaudeCliEventKind.Note);
        seen[0].Text.ShouldContain("cannot narrate");

        // Two launches: the refused stream, then the buffered retry.
        runner.Requests.Count.ShouldBe(2);
        runner.Requests[^1].Arguments.ShouldNotContain("--verbose");
    }

    [Fact]
    public async Task A_session_that_failed_after_starting_is_never_re_run()
    {
        // The expensive mistake this avoids: a stage that did ten minutes of work and then hit
        // its budget must not be silently paid for twice. Anything streamed means work happened.
        FakeProcessRunner runner = new FakeProcessRunner().Enqueue(1, Stream(
            """{"type":"system","subtype":"init","model":"claude-opus-5"}""",
            """{"type":"assistant","message":{"role":"assistant","content":[{"type":"tool_use","name":"Read","input":{"file_path":"src/A.cs"}}]}}"""));

        ClaudeCliResult result = await Session(runner).RunAsync(Request(_ => { }));

        result.Succeeded.ShouldBeFalse();
        runner.Requests.Count.ShouldBe(1, "one attempt, and no retry after work was done");
    }

    [Fact]
    public async Task Everything_shown_has_been_through_the_redactor()
    {
        // Every one of these lines came out of a subprocess reading a repository, and it is going
        // straight onto a screen. Scrubbing at the source means no watcher can forget to.
        FakeProcessRunner runner = new FakeProcessRunner().Enqueue(0, Stream(
            """{"type":"assistant","message":{"role":"assistant","content":[{"type":"text","text":"Found sk-ant-api03-0123456789abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789ab in the settings."}]}}""",
            """{"type":"result","is_error":false,"result":"done"}"""));

        List<ClaudeCliEvent> seen = [];
        await Session(runner).RunAsync(Request(e => seen.Add(e)));

        seen.ShouldHaveSingleItem().Text.ShouldNotContain("sk-ant-api03-0123456789");
    }

    private static ClaudeCliRequest Request(Action<ClaudeCliEvent>? onEvent) =>
        new("Review something.") { OnEvent = onEvent };

    private static ClaudeCliSession Session(IProcessRunner runner) =>
        new(runner, new ClaudeCliProfile("claude", "claude-opus-5", "plan", ["Read", "Grep", "Glob"], true, null));

    /// <summary>One JSON object per line, as <c>stream-json</c> emits them.</summary>
    private static string Stream(params string[] lines) => string.Join('\n', lines);
}
