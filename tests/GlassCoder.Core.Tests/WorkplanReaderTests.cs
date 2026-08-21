using GlassCoder.Core.Planning;

namespace GlassCoder.Core.Tests;

/// <summary>
/// Workplan format v2, as GlassContext emits it (workplan task 78).
/// <para>
/// The fixtures are ported from GlassContext's own <c>WorkplanFormatV2Tests</c> rather than
/// written afresh, and that is the point of them. Two programs that quietly disagree about this
/// format disagree about which task a run's metrics belong to - and nothing fails, it just
/// attaches the wrong history to the wrong work. Shared fixtures are what stop the two sides
/// drifting.
/// </para>
/// </summary>
public sealed class WorkplanReaderTests
{
    // ── Reading v2 ──

    [Fact]
    public void Every_v2_field_is_recovered()
    {
        WorkplanTask task = Plan().Tasks[0];

        task.Title.ShouldBe("Add retry to the transport");
        task.Slug.ShouldBe("retry-transport");
        task.EstimatedTime.ShouldBe("2h");
        task.Steps.ShouldBe(12);
        task.TargetFiles.ShouldBe(["src/Core/Transport.cs", "tests/TransportTests.cs"]);
        task.Oracle.ShouldBe("dotnet test --filter TransportRetryTests");
        task.Description.ShouldBe("Retry 429 and 5xx with backoff.");
        task.IsComplete.ShouldBeFalse();
    }

    [Fact]
    public void A_ticked_checkbox_is_read_as_complete()
    {
        Workplan plan = Workplan.Parse("""
            # Workplan

            ## 1. Finished

            - [x] **Estimated time:** 1h

            Body.

            ## 2. Open

            - [ ] **Estimated time:** 1h

            Body.
            """);

        plan.Tasks[0].IsComplete.ShouldBeTrue();
        plan.Tasks[1].IsComplete.ShouldBeFalse();
    }

    [Fact]
    public void A_slug_quoted_in_prose_does_not_beat_the_declared_one()
    {
        // A plan that documents the format quotes the marker in its own body - this repository's
        // own plan does exactly that. Taking the last match would repoint the task's run history,
        // which is the single thing the slug exists to prevent.
        Workplan plan = Workplan.Parse("""
            # Workplan

            ## 1. Read workplan format v2

            <!-- task:parse-workplan-v2 -->

            - [ ] **Estimated time:** 1d

            Recover the slug from its <!-- task:slug --> comment.
            """);

        plan.Tasks[0].Slug.ShouldBe("parse-workplan-v2");
        plan.Tasks[0].EffectiveSlug.ShouldBe("parse-workplan-v2");
    }

    [Fact]
    public void An_unnumbered_heading_is_still_a_task()
    {
        Workplan plan = Workplan.Parse("""
            # Workplan

            ## Hand-written task

            - [ ] **Estimated time:** 3h

            Body.
            """);

        plan.Tasks.ShouldHaveSingleItem().Title.ShouldBe("Hand-written task");
    }

    // ── Tolerating v1 ──

    [Fact]
    public void A_v1_plan_without_slugs_or_oracles_still_parses()
    {
        // Every plan in this repository is v1 today, so a reader that needed v2 could not be
        // tried on anything real.
        Workplan plan = Workplan.Parse("""
            # Workplan

            **Total estimated time:** 10h

            ## 1. Set up solution

            - [ ] **Estimated time:** 2h

            Create the projects and wire DI.

            ## 2. Implement parser

            - [ ] **Estimated time:** 1d

            Doxygen and XML doc comments.
            """);

        plan.Tasks.Count.ShouldBe(2);
        plan.Tasks[0].Title.ShouldBe("Set up solution");
        plan.Tasks[0].EstimatedTime.ShouldBe("2h");
        plan.Tasks[0].Description.ShouldBe("Create the projects and wire DI.");
        plan.Tasks[0].Slug.ShouldBeEmpty();
        plan.Tasks[0].Oracle.ShouldBeEmpty();
        plan.Tasks[0].TargetFiles.ShouldBeEmpty();
    }

    [Fact]
    public void A_task_with_no_slug_still_has_a_join_key()
    {
        // Derived the same way GlassContext derives it, so a v1 plan gets the same key on both
        // sides and the metrics join is not a coin toss.
        Workplan plan = Workplan.Parse("""
            # Workplan

            ## 1. Set up the solution

            - [ ] **Estimated time:** 2h

            Body.
            """);

        plan.Tasks[0].EffectiveSlug.ShouldBe("set-up-the-solution");
    }

    [Theory]
    [InlineData("Set up the solution", "set-up-the-solution")]
    [InlineData("  Trim  me  ", "trim-me")]
    [InlineData("Punctuation: it's here!", "punctuation-it-s-here")]
    [InlineData("", "")]
    public void Slugify_matches_the_producing_side(string title, string expected) =>
        Workplan.Slugify(title).ShouldBe(expected);

    [Fact]
    public void A_slug_is_capped_at_forty_eight_characters_without_a_trailing_hyphen()
    {
        string slug = Workplan.Slugify(new string('a', 40) + " " + new string('b', 40));

        slug.Length.ShouldBeLessThanOrEqualTo(48);
        slug.ShouldNotEndWith("-");
    }

    // ── The round trip ──

    [Fact]
    public void Parsing_and_re_rendering_gives_back_the_same_bytes()
    {
        string original = Markdown();

        Workplan.Parse(original).ToMarkdown().ShouldBe(original);
    }

    [Fact]
    public void The_preamble_survives_the_round_trip()
    {
        // Where this reader deliberately differs from GlassContext's writer, which re-emits its
        // own header. This harness is a guest in a file a developer edits by hand: a round trip
        // that dropped their contract table would be a worse bug than any it was added to fix.
        string original = """
            # Workplan — GlassContext compatibility

            <!-- Authored by hand. Verified against GlassCoder at f2944de. -->

            **Total estimated time:** 52h (~6.5d)

            ### The contract

            GlassContext is the producer, GlassCoder the consumer.

            ## 77. Load the profile

            <!-- task:load-agent-profile -->

            - [ ] **Estimated time:** 0.5d · **Steps:** ~8

            Body.


            """.ReplaceLineEndings("\n");

        Workplan.Parse(original).ToMarkdown().ShouldBe(original);
    }

    [Fact]
    public void A_plan_that_does_not_end_on_a_blank_line_does_not_gain_one()
    {
        // This repository's own plan is trimmed this way, and it is the plan the runner will be
        // pointed at first. A round trip faithful except for a byte is not a round trip.
        string original = Markdown().TrimEnd('\n') + "\n";

        Workplan.Parse(original).ToMarkdown().ShouldBe(original);
    }

    [Fact]
    public void A_plan_written_with_one_line_ending_is_not_rewritten_in_the_other()
    {
        string crlf = Markdown().ReplaceLineEndings("\r\n");

        Workplan.Parse(crlf).ToMarkdown().ShouldBe(crlf);
    }

    // ── The oracle's filter ──

    [Theory]
    [InlineData("dotnet test --filter TransportRetryTests", "TransportRetryTests")]
    [InlineData("dotnet test --filter=TransportRetryTests", "TransportRetryTests")]
    [InlineData("""dotnet test --filter "A|B" """, "A|B")]
    [InlineData("dotnet test --filter FullyQualifiedName~Workplan --no-build", "FullyQualifiedName~Workplan")]
    public void The_filter_expression_is_taken_out_of_the_oracle(string oracle, string expected) =>
        new WorkplanTask { Oracle = oracle }.TestFilter.ShouldBe(expected);

    [Theory]
    [InlineData("")]
    [InlineData("dotnet test")]
    [InlineData("make check")]
    public void An_oracle_with_no_filter_yields_none(string oracle) =>
        new WorkplanTask { Oracle = oracle }.TestFilter.ShouldBeNull();

    // ── Ticking ──

    [Fact]
    public void Ticking_changes_one_character_and_nothing_else()
    {
        // A line edit rather than a re-render. Re-rendering would renumber headings and rewrite
        // the developer's spacing every time a box was ticked - a diff nobody asked for.
        string original = Markdown();

        string ticked = Workplan.Tick(original, "retry-transport");

        ticked.ShouldNotBe(original);
        Workplan.Parse(ticked).Tasks[0].IsComplete.ShouldBeTrue();
        Workplan.Parse(ticked).Tasks[1].IsComplete.ShouldBeFalse();

        // Everything that is not the checkbox is byte-identical.
        ticked.Replace("- [x] **Estimated time:** 2h", "- [ ] **Estimated time:** 2h", StringComparison.Ordinal)
            .ShouldBe(original);
    }

    [Fact]
    public void Ticking_a_task_that_is_not_there_leaves_the_plan_alone()
    {
        string original = Markdown();

        Workplan.Tick(original, "no-such-task").ShouldBe(original);
    }

    [Fact]
    public void Ticking_finds_the_task_by_its_derived_slug_too()
    {
        string original = Markdown();

        string ticked = Workplan.Tick(original, "polish-the-settings-screen");

        Workplan.Parse(ticked).Tasks[1].IsComplete.ShouldBeTrue();
    }

    [Fact]
    public void Ticking_is_not_confused_by_another_tasks_marker_quoted_in_a_body()
    {
        string plan = """
            # Workplan

            ## 1. First

            <!-- task:first -->

            - [ ] **Estimated time:** 1h

            Mentions <!-- task:second --> in passing.

            ## 2. Second

            <!-- task:second -->

            - [ ] **Estimated time:** 1h

            Body.
            """.ReplaceLineEndings("\n");

        Workplan parsed = Workplan.Parse(Workplan.Tick(plan, "second"));

        parsed.Tasks[0].IsComplete.ShouldBeFalse();
        parsed.Tasks[1].IsComplete.ShouldBeTrue();
    }

    [Fact]
    public void An_already_ticked_task_is_left_as_it_is()
    {
        string ticked = Workplan.Tick(Markdown(), "retry-transport");

        Workplan.Tick(ticked, "retry-transport").ShouldBe(ticked);
    }

    /// <summary>
    /// The two-task fixture from GlassContext's <c>WorkplanFormatV2Tests</c>: one task carrying
    /// every v2 field, one carrying none of them.
    /// </summary>
    private static string Markdown() =>
        """
        # Workplan

        <!-- Generated by GlassContext — review and adjust before creating YouTrack tickets -->

        **Total estimated time:** 10h (~1.3d)

        ## 1. Add retry to the transport

        <!-- task:retry-transport -->

        - [ ] **Estimated time:** 2h · **Steps:** ~12

        **Target files:** `src/Core/Transport.cs`, `tests/TransportTests.cs`

        **Oracle:** `dotnet test --filter TransportRetryTests`

        Retry 429 and 5xx with backoff.

        ## 2. Polish the settings screen

        <!-- task:polish-the-settings-screen -->

        - [ ] **Estimated time:** 1d

        Tooltips and tab order.


        """.ReplaceLineEndings("\n");

    private static Workplan Plan() => Workplan.Parse(Markdown());
}
