using GlassCoder.Core.Verification;
using GlassCoder.TestSupport;
using GlassCoder.Tools.Processes;

namespace GlassCoder.Core.Tests;

/// <summary>
/// What happens to work a headless session finished and was then stopped for (workplan task 68).
/// <para>
/// Written from the first whole retrospective. Stage 3 read the harness for five minutes, called
/// <c>StructuredOutput</c> successfully, and lost every word of it because the CLI exited 1 on
/// <c>--max-budget-usd</c> six-tenths of a second later and the session checked only the exit code.
/// The log for that was <c>"The CLI failed with exit 1: "</c> - the reason lives in
/// <c>errors</c> and <c>subtype</c>, and nothing read them.
/// </para>
/// <para>
/// The property being defended cuts both ways: an answer that exists survives the exit code, and
/// an envelope that only *looks* like an answer does not. A session that never authenticated puts
/// its complaint in <c>result</c> and sets <c>is_error</c>, and that must stay a failure.
/// </para>
/// </summary>
public sealed class ClaudeCliCeilingTests
{
    [Fact]
    public async Task A_session_that_answered_and_then_hit_its_ceiling_keeps_the_answer()
    {
        // The 2026-08-08 shape exactly: structured output delivered, then exit 1 on the budget.
        FakeProcessRunner runner = new FakeProcessRunner().Enqueue(1,
            """
            {"type":"result","is_error":true,"subtype":"error_max_budget_usd","terminal_reason":"budget_exhausted",
             "errors":["Reached maximum budget ($2)"],"session_id":"sess","total_cost_usd":5.0,
             "structured_output":{"report":"what the harness should learn"}}
            """.ReplaceLineEndings(string.Empty));

        ClaudeCliResult result = await Session(runner).RunAsync(new ClaudeCliRequest("Look back."));

        result.Succeeded.ShouldBeTrue("the work was done and charged for; the exit code is about the ceiling");
        result.StructuredOutput.ShouldContain("what the harness should learn");
        result.SessionId.ShouldBe("sess");
        result.CostUsd.ShouldBe(5.0m);

        // Kept, but never silently: the caveat is what stops a truncated answer being read whole.
        result.Caveat.ShouldNotBeNull().ShouldContain("Reached maximum budget");
        result.Failure.ShouldBeNull();
    }

    [Fact]
    public async Task A_capped_session_that_answered_in_prose_keeps_that_too()
    {
        // No schema was asked for, so there is no structured output to lean on - only `result`,
        // which is trusted here because the CLI stopped itself at a ceiling it was given.
        FakeProcessRunner runner = new FakeProcessRunner().Enqueue(1,
            """{"type":"result","is_error":true,"subtype":"error_max_budget_usd","terminal_reason":"budget_exhausted","result":"As far as I got:"}""");

        ClaudeCliResult result = await Session(runner).RunAsync(new ClaudeCliRequest("Look back."));

        result.Succeeded.ShouldBeTrue();
        result.Result.ShouldBe("As far as I got:");
        result.Caveat.ShouldNotBeNull();
    }

    /// <summary>
    /// The path that actually broke. A watched retrospective streams, so the envelope is assembled
    /// from the final <c>result</c> event rather than parsed from one buffered blob - and a capped
    /// session's answer has to survive that route too, without triggering the buffered retry that
    /// would pay for the whole stage a second time.
    /// </summary>
    [Fact]
    public async Task The_streamed_route_salvages_it_too_and_never_pays_twice()
    {
        FakeProcessRunner runner = new FakeProcessRunner().Enqueue(1, string.Join('\n',
            """{"type":"system","subtype":"init","model":"claude-opus-5","tools":["Read"]}""",
            """{"type":"assistant","message":{"role":"assistant","content":[{"type":"tool_use","name":"Read","input":{"file_path":"WORKPLAN.md"}}]}}""",
            """{"type":"result","is_error":true,"subtype":"error_max_budget_usd","terminal_reason":"budget_exhausted","errors":["Reached maximum budget ($2)"],"session_id":"sess","total_cost_usd":5.0,"structured_output":{"report":"what the harness should learn"}}"""));

        List<ClaudeCliEvent> seen = [];
        ClaudeCliResult result = await Session(runner)
            .RunAsync(new ClaudeCliRequest("Look back.") { OnEvent = seen.Add });

        result.Succeeded.ShouldBeTrue();
        result.Streamed.ShouldBeTrue();
        result.StructuredOutput.ShouldContain("what the harness should learn");
        result.Caveat.ShouldNotBeNull();

        runner.Requests.Count.ShouldBe(1, "work was done and billed; re-running it would charge twice");
    }

    [Fact]
    public async Task A_failed_session_still_records_what_it_spent()
    {
        // Cut off before answering is still cut off after spending. A stage recorded at $0.00 with
        // no session id reads as one that cost nothing, which is what made the original failure so
        // hard to trace back to a budget.
        FakeProcessRunner runner = new FakeProcessRunner().Enqueue(1,
            """{"type":"result","is_error":true,"subtype":"error_max_budget_usd","terminal_reason":"budget_exhausted","errors":["Reached maximum budget ($2)"],"session_id":"sess","total_cost_usd":2.02}""");

        ClaudeCliResult result = await Session(runner).RunAsync(new ClaudeCliRequest("Look back."));

        result.Succeeded.ShouldBeFalse();
        result.CostUsd.ShouldBe(2.02m);
        result.SessionId.ShouldBe("sess", "so the CLI's own transcript can be found and read");
    }

    [Fact]
    public async Task A_session_that_never_authenticated_is_still_a_failure()
    {
        // The guard on the test above. `--bare` answers exactly this - is_error, exit 1, and the
        // complaint in `result` - and reading it as an answer would file "Not logged in" as a
        // review. Only a ceiling makes `result` trustworthy.
        FakeProcessRunner runner = new FakeProcessRunner().Enqueue(1,
            """{"type":"result","is_error":true,"subtype":"success","result":"Not logged in · Please run /login"}""");

        ClaudeCliResult result = await Session(runner).RunAsync(new ClaudeCliRequest("Review something."));

        result.Succeeded.ShouldBeFalse();
        result.Failure.ShouldNotBeNull().ShouldContain("Not logged in");
    }

    [Fact]
    public async Task A_capped_session_with_nothing_to_show_fails_and_names_the_budget()
    {
        // Cut off before it answered. Nothing to keep - but the reason must survive, because the
        // whole diagnosis of the first retrospective started from an empty explanation.
        FakeProcessRunner runner = new FakeProcessRunner().Enqueue(1,
            """{"type":"result","is_error":true,"subtype":"error_max_budget_usd","terminal_reason":"budget_exhausted","errors":["Reached maximum budget ($2)"]}""");

        ClaudeCliResult result = await Session(runner).RunAsync(new ClaudeCliRequest("Look back."));

        result.Succeeded.ShouldBeFalse();
        result.Failure.ShouldNotBeNull().ShouldContain("Reached maximum budget");
    }

    [Fact]
    public async Task A_failure_with_no_errors_and_no_result_still_says_which_kind_it_was()
    {
        // The subtype is the last thing standing, and it beats the empty sentence outright.
        FakeProcessRunner runner = new FakeProcessRunner().Enqueue(1,
            """{"type":"result","is_error":true,"subtype":"error_during_execution","terminal_reason":"crashed"}""");

        ClaudeCliResult result = await Session(runner).RunAsync(new ClaudeCliRequest("Look back."));

        result.Succeeded.ShouldBeFalse();
        result.Failure.ShouldNotBeNull().ShouldContain("error_during_execution");
        result.Failure.ShouldContain("crashed");
    }

    [Fact]
    public async Task Standard_error_still_wins_when_the_cli_wrote_any()
    {
        // Unchanged behaviour, asserted because everything above reorders what comes after it.
        FakeProcessRunner runner = new FakeProcessRunner()
            .Enqueue(1, standardError: "error: unknown option '--json-schema'");

        ClaudeCliResult result = await Session(runner).RunAsync(new ClaudeCliRequest("Review something."));

        result.Succeeded.ShouldBeFalse();
        result.Failure.ShouldNotBeNull().ShouldContain("unknown option");
    }

    /// <summary>
    /// The ceiling that was too low, now that there is a measurement rather than a guess: stage 3
    /// of the first retrospective cost about $5, against a default of $2.
    /// </summary>
    [Fact]
    public void The_per_stage_ceiling_clears_what_a_harness_stage_actually_costs()
    {
        new RetrospectiveOptions().MaxBudgetUsd.ShouldBeGreaterThan(5.00m);
    }

    private static ClaudeCliSession Session(IProcessRunner runner) =>
        new(runner, new ClaudeCliProfile("claude", "claude-opus-5", "plan", ["Read", "Grep", "Glob"], false, null));
}
