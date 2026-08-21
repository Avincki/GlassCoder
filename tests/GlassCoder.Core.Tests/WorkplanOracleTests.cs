using GlassCoder.Core.Planning;

namespace GlassCoder.Core.Tests;

/// <summary>
/// The per-task oracle as the run's test filter (workplan task 80).
/// <para>
/// A task's <c>**Oracle:**</c> line names the tests that decide it. Two things have to be true for
/// that to be worth anything: the named tests must be what actually gets run, and a filter that
/// names nothing must be louder than a failure rather than quieter. The second is the one that
/// makes an oracle worse than no oracle when it is got wrong - a filter matching zero tests runs
/// clean, and a box ticked on it is a green light nobody checked.
/// </para>
/// </summary>
public sealed class WorkplanOracleTests
{
    [Fact]
    public async Task The_oracles_filter_is_what_the_ladder_is_asked_to_run()
    {
        using WorkplanRunnerTests.Fixture fixture = new(Plan("dotnet test --filter FirstTests"));

        await fixture.RunAsync();

        fixture.Ladder.Filters.ShouldHaveSingleItem().ShouldBe("FirstTests");
    }

    [Fact]
    public async Task A_quoted_filter_expression_survives_intact()
    {
        using WorkplanRunnerTests.Fixture fixture = new(Plan("""dotnet test --filter "A|B" """));

        await fixture.RunAsync();

        fixture.Ladder.Filters.ShouldHaveSingleItem().ShouldBe("A|B");
    }

    [Fact]
    public async Task Only_the_filter_is_taken_never_the_command()
    {
        // The oracle names which tests decide the task; how tests run is the ladder's business.
        // Shelling out to the line verbatim would execute an arbitrary command out of a file the
        // agent under supervision can edit.
        using WorkplanRunnerTests.Fixture fixture = new(
            Plan("dotnet test --filter FirstTests --no-build -c Release"));

        await fixture.RunAsync();

        fixture.Ladder.Filters.ShouldHaveSingleItem().ShouldBe("FirstTests");
    }

    // ── The zero-match guard ──

    [Fact]
    public async Task A_filter_that_matches_no_tests_fails_the_task_loudly()
    {
        using WorkplanRunnerTests.Fixture fixture = new(Plan("dotnet test --filter NoSuchTests"));
        fixture.Ladder.Unverified = true;

        WorkplanRunReport report = await fixture.RunAsync();

        WorkplanTaskOutcome outcome = report.Outcomes.ShouldHaveSingleItem();
        outcome.Status.ShouldBe(WorkplanTaskStatus.OracleMatchedNothing);
        outcome.Detail.ShouldContain("matched no tests");
        fixture.Task("first").IsComplete.ShouldBeFalse();
    }

    [Fact]
    public async Task A_filter_that_matches_no_tests_is_not_a_pass_even_though_the_climb_was_green()
    {
        // The exact shape of the trap: the rung reports Passed, because nothing failed. Nothing
        // ran either.
        using WorkplanRunnerTests.Fixture fixture = new(Plan("dotnet test --filter NoSuchTests"));
        fixture.Ladder.Passed = true;
        fixture.Ladder.Unverified = true;

        await fixture.RunAsync();

        fixture.Task("first").IsComplete.ShouldBeFalse();
        fixture.Metrics.Recorded[0].OraclePassed.ShouldBe(false);
    }

    // ── Gating on the test rung specifically ──

    [Fact]
    public async Task A_task_whose_named_tests_failed_is_not_done_whatever_else_passed()
    {
        using WorkplanRunnerTests.Fixture fixture = new(Plan("dotnet test --filter FirstTests"));
        fixture.Ladder.Passed = false;

        await fixture.RunAsync();

        fixture.Task("first").IsComplete.ShouldBeFalse();
    }

    [Fact]
    public async Task A_task_whose_named_tests_passed_is_not_held_back_by_another_rung()
    {
        // Gated on this rung specifically. The critique rung is an opinion about the change; the
        // named tests are the plan's own statement of what done means, and the plan wins.
        using WorkplanRunnerTests.Fixture fixture = new(Plan("dotnet test --filter FirstTests"));
        fixture.Ladder.Passed = true;
        fixture.Ladder.RefuteCritique = true;

        WorkplanRunReport report = await fixture.RunAsync();

        report.Outcomes[0].Verification.ShouldNotBeNull().Passed.ShouldBeFalse();
        fixture.Task("first").IsComplete.ShouldBeTrue();
    }

    [Fact]
    public async Task A_test_rung_that_never_ran_fails_the_task()
    {
        // The ladder stops at the first failure, so a skipped test rung means something below it
        // broke - the oracle has said nothing and cannot be read as agreement.
        using WorkplanRunnerTests.Fixture fixture = new(Plan("dotnet test --filter FirstTests"));
        fixture.Ladder.SkipTests = true;

        WorkplanRunReport report = await fixture.RunAsync();

        report.Outcomes[0].Status.ShouldBe(WorkplanTaskStatus.Failed);
        report.Outcomes[0].Detail.ShouldContain("never ran");
        fixture.Task("first").IsComplete.ShouldBeFalse();
    }

    // ── No oracle at all ──

    [Fact]
    public async Task A_task_with_no_oracle_runs_but_is_never_ticked()
    {
        using WorkplanRunnerTests.Fixture fixture = new(Plan(oracle: null));

        WorkplanRunReport report = await fixture.RunAsync();

        fixture.Loop.Requests.ShouldHaveSingleItem();
        report.Outcomes[0].Status.ShouldBe(WorkplanTaskStatus.NeedsHumanDecision);
        fixture.Task("first").IsComplete.ShouldBeFalse();
    }

    [Fact]
    public async Task A_task_with_no_oracle_never_troubles_the_ladder()
    {
        using WorkplanRunnerTests.Fixture fixture = new(Plan(oracle: null));

        await fixture.RunAsync();

        fixture.Ladder.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task A_task_with_no_oracle_says_who_has_to_decide()
    {
        using WorkplanRunnerTests.Fixture fixture = new(Plan(oracle: null));

        WorkplanRunReport report = await fixture.RunAsync();

        report.Outcomes[0].Detail.ShouldContain("tick it yourself");
    }

    [Fact]
    public async Task An_oracle_that_carries_no_filter_is_reported_rather_than_ignored()
    {
        // A line that names a command but scopes nothing is not an oracle this harness can use,
        // and saying so is better than running the whole suite and calling it the task's.
        using WorkplanRunnerTests.Fixture fixture = new(Plan("make check"));

        WorkplanRunReport report = await fixture.RunAsync();

        report.Outcomes[0].Status.ShouldBe(WorkplanTaskStatus.NeedsHumanDecision);
        report.Outcomes[0].Detail.ShouldContain("no --filter");
        fixture.Ladder.Calls.ShouldBe(0);
    }

    private static string Plan(string? oracle) =>
        $$"""
        # Workplan

        ## 1. Do the first thing

        <!-- task:first -->

        - [ ] **Estimated time:** 1h

        {{(oracle is null ? string.Empty : $"**Oracle:** `{oracle.Trim()}`\n")}}
        The body of the first task.

        """.ReplaceLineEndings("\n");
}
