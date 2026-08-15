using System.Globalization;
using GlassCoder.Wpf.Converters;
using GlassCoder.Wpf.Services;

namespace GlassCoder.Wpf.Tests;

/// <summary>
/// The shape of the remembered prompts: newest first, no duplicates, twenty at most - and the one
/// line each of them becomes in the dropdown. The rule lives apart from the registry so it can be
/// stated here without a machine's HKCU in the loop.
/// </summary>
public sealed class PromptHistoryTests
{
    [Fact]
    public void The_newest_prompt_comes_first()
    {
        IReadOnlyList<string> history = PromptHistory.With(["older", "oldest"], "newest");

        history.ShouldBe(["newest", "older", "oldest"]);
    }

    [Fact]
    public void Running_the_same_prompt_again_moves_it_up_rather_than_repeating_it()
    {
        // The common case, and the one a naive prepend gets wrong: re-running the last-but-one
        // prompt twice would otherwise fill the dropdown with the same sentence.
        IReadOnlyList<string> history = PromptHistory.With(["a", "b", "c"], "c");

        history.ShouldBe(["c", "a", "b"]);
    }

    [Fact]
    public void Prompts_differing_only_in_whitespace_are_two_prompts()
    {
        IReadOnlyList<string> history = PromptHistory.With(["sort the list"], "sort the list ");

        history.Count.ShouldBe(2, "they are two prompts to the model, so they are two entries here");
    }

    [Fact]
    public void The_oldest_falls_off_the_end_past_twenty()
    {
        IReadOnlyList<string> history = [];
        foreach (int n in Enumerable.Range(1, PromptHistory.Capacity + 5))
        {
            history = PromptHistory.With(history, n.ToString(CultureInfo.InvariantCulture));
        }

        history.Count.ShouldBe(PromptHistory.Capacity);
        history[0].ShouldBe("25", "the last run is at the top");
        history[^1].ShouldBe("6", "and the five before the window are gone");
    }

    [Fact]
    public void An_empty_prompt_changes_nothing()
    {
        PromptHistory.With(["the real prompt"], "   ").ShouldBe(["the real prompt"]);
    }

    [Fact]
    public void A_multi_line_prompt_becomes_one_line_in_the_dropdown()
    {
        // Straight into a TextBlock this is three rows tall, and twenty of those are a dropdown
        // taller than the window.
        string summary = PromptHistory.Summarize("make a wpf app\r\n\r\n  - it multiplies\n  - it divides");

        summary.ShouldBe("make a wpf app - it multiplies - it divides");
    }

    [Fact]
    public void The_dropdown_row_reads_the_prompt_through_the_converter()
    {
        PromptSummaryConverter converter = new();

        object row = converter.Convert("first line\nsecond line", typeof(string), null, CultureInfo.CurrentCulture);

        row.ShouldBe("first line second line");
    }
}
