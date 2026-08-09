using GlassCoder.Core.Diagnostics;
using GlassCoder.Wpf.ViewModels;

namespace GlassCoder.Wpf.Tests;

/// <summary>
/// The replayed conversation in the step detail pane.
/// <para>
/// It used to print <c>[{Role}] {Text}</c> and nothing else, which for the two most common kinds
/// of message is a label and a blank space: an assistant turn that only called a tool has no text,
/// and neither does the tool's answer. The names were being recorded all along and only the
/// formatter dropped them, so these read out of transcripts already on disk.
/// </para>
/// </summary>
public sealed class TranscriptDetailTests
{
    private static readonly DateTimeOffset Origin = new(2026, 8, 9, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void An_assistant_turn_that_only_called_a_tool_names_the_tool()
    {
        StepRowViewModel row = Row(Step(new TranscriptMessage("assistant", null, ["read_file", "grep"])));

        row.Detail.ShouldContain("[assistant] → read_file, grep");
    }

    [Fact]
    public void A_turn_that_both_spoke_and_called_keeps_both()
    {
        StepRowViewModel row = Row(Step(new TranscriptMessage("assistant", "Looking at the parser.", ["read_file"])));

        row.Detail.ShouldContain("[assistant] → read_file Looking at the parser.");
    }

    [Fact]
    public void A_tool_result_shows_its_observation()
    {
        StepRowViewModel row = Row(Step(new TranscriptMessage("tool", """{"ok":true,"summary":"read 40 lines"}""")));

        row.Detail.ShouldContain("""[tool] {"ok":true,"summary":"read 40 lines"}""");
    }

    [Fact]
    public void A_message_with_nothing_to_show_says_so_rather_than_leaving_a_blank()
    {
        // A content kind nothing here reads yet. Saying nothing would look identical to the bug
        // this pane just stopped having.
        StepRowViewModel row = Row(Step(new TranscriptMessage("user", "   ")));

        row.Detail.ShouldContain("[user] (no text)");
    }

    [Fact]
    public void The_steps_own_tool_calls_stay_distinguishable_from_the_replay()
    {
        // The replayed history and this step's own work share the [tool] label; the arrow and the
        // status are what separate them.
        StepRowViewModel row = Row(Step(new TranscriptMessage("tool", "earlier observation")) with
        {
            ToolCalls =
            [
                new ToolCallRecord("c1", "edit_file", null, "Succeeded", true, 12, "applied 1 change", null, null),
            ],
        });

        row.Detail.ShouldContain("[tool] earlier observation");
        row.Detail.ShouldContain("[tool edit_file → Succeeded] applied 1 change");
    }

    private static StepRowViewModel Row(StepRecord record) => new(record, Origin);

    private static StepRecord Step(params TranscriptMessage[] prompt) => new()
    {
        RunId = "run-1",
        TaskId = "task-1",
        StepIndex = 4,
        Role = "worker",
        StartedAt = Origin,
        Prompt = prompt,
        ToolCalls = [],
        ModelLatencyMs = 100,
        StepLatencyMs = 120,
        Outcome = "continued",
    };
}
