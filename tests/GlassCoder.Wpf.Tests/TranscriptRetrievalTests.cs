using GlassCoder.Core.Diagnostics;
using GlassCoder.Tools.Retrieval;
using GlassCoder.Wpf.ViewModels;

namespace GlassCoder.Wpf.Tests;

/// <summary>
/// Retrieval calls are visible in the transcript without reading it: a step that reached a
/// server outside this machine is the one thing in a run that did.
/// </summary>
public sealed class TranscriptRetrievalTests
{
    private static readonly IReadOnlySet<string> RetrievalTools =
        new HashSet<string>(StringComparer.Ordinal) { "learn_search", "learn_fetch", "gh_symbol_exists" };

    [Fact]
    public void A_step_that_called_a_retrieval_tool_is_marked()
    {
        Row("learn_search").IsRetrieval.ShouldBeTrue();
        Row("read_file").IsRetrieval.ShouldBeFalse();
        Row("read_file", "learn_fetch").IsRetrieval.ShouldBeTrue("one call in the step is enough");
    }

    /// <summary>
    /// Reaching outside is a fact about the step, not a complaint about it - so it must not move
    /// the severity filter, which is what someone uses to find the steps that went wrong.
    /// </summary>
    [Fact]
    public void Marking_a_retrieval_step_does_not_change_its_severity()
    {
        Row("learn_search").Severity.ShouldBe("info");
    }

    /// <summary>Without configured names nothing is marked, so an unconfigured install is plain.</summary>
    [Fact]
    public void With_no_retrieval_configured_nothing_is_marked()
    {
        new StepRowViewModel(Record("learn_search"), DateTimeOffset.UnixEpoch).IsRetrieval.ShouldBeFalse();
    }

    /// <summary>
    /// The names come from configuration, not from a prefix: a guess at "learn_*" stops being
    /// true the first time somebody renames one, and renaming is a configuration edit.
    /// </summary>
    [Fact]
    public void A_renamed_retrieval_tool_is_still_marked()
    {
        HashSet<string> renamed = new(StringComparer.Ordinal) { "docs" };

        new StepRowViewModel(Record("docs"), DateTimeOffset.UnixEpoch, renamed).IsRetrieval.ShouldBeTrue();
    }

    /// <summary>Every configured tool counts, whether or not its server is switched on.</summary>
    [Fact]
    public void The_names_are_read_from_configuration_for_both_servers()
    {
        RetrievalOptions options = new();
        options.Learn.Tools.Add(new RetrievalToolOptions { ServerTool = "s", Name = "learn_search", Description = "d" });
        options.GitHub.Tools.Add(new RetrievalToolOptions { ServerTool = "c", Name = "gh_symbol_exists", Description = "d" });

        TranscriptViewModel transcript = new(
            new FakeBus(), System.Windows.Threading.Dispatcher.CurrentDispatcher,
            Microsoft.Extensions.Options.Options.Create(options));

        transcript.Steps.ShouldBeEmpty();
    }

    private static StepRowViewModel Row(params string[] tools) =>
        new(Record(tools), DateTimeOffset.UnixEpoch, RetrievalTools);

    private static StepRecord Record(params string[] tools) => new()
    {
        RunId = "run",
        TaskId = "task",
        StepIndex = 1,
        Role = "worker",
        StartedAt = DateTimeOffset.UnixEpoch,
        Prompt = [],
        ToolCalls = [.. tools.Select(t => new ToolCallRecord("c", t, null, "Succeeded", true, 1, null, null))],
        ModelLatencyMs = 1,
        StepLatencyMs = 1,
        Outcome = "ok",
    };

    private sealed class FakeBus : ITranscriptBus
    {
        public IReadOnlyList<StepRecord> Steps => [];

        public IReadOnlyList<ReviewRecord> Reviews => [];

        public IReadOnlyList<RunRecord> Runs => [];

        public event EventHandler<StepRecord>? StepRecorded;

        public event EventHandler<ReviewRecord>? ReviewRecorded;

        public event EventHandler<RunRecord>? RunRecorded;

        public void Publish(StepRecord record) => StepRecorded?.Invoke(this, record);

        public void Publish(ReviewRecord review) => ReviewRecorded?.Invoke(this, review);

        public int NextStepIndex(string runId) => 0;

        public void Clear()
        {
        }
    }
}
