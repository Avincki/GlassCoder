using GlassCoder.Tools.Changes;
using GlassCoder.Tools.Registry;

namespace GlassCoder.Tools.Tests;

/// <summary>
/// The ledger for notices that rode on a successful result (run <c>31983adb</c>).
/// <para>
/// The rules are the refusal ledger's, transposed onto success: the same thing said about the same
/// subject over and over is one unanswered notice, a source with nothing to say answers it, and a
/// different subject is a different notice rather than a continuation.
/// </para>
/// </summary>
public sealed class AdvisoryNoticeTests : IDisposable
{
    public AdvisoryNoticeTests() => RunContext.Set(new RunContext("run-1", "task-1"));

    public void Dispose() => RunContext.Clear();

    [Fact]
    public void Three_emissions_about_one_subject_are_an_unanswered_notice()
    {
        AdvisoryNotices notices = new();

        notices.Observe("update_todos (ladder item)", "Build and run tests");
        notices.Observe("update_todos (ladder item)", "Build and run tests");
        notices.Summary().ShouldBeNull("twice is a coincidence");

        notices.Observe("update_todos (ladder item)", "Build and run tests");

        string summary = notices.Summary().ShouldNotBeNull();
        summary.ShouldContain("update_todos (ladder item)");
        summary.ShouldContain("Build and run tests");
        summary.ShouldContain("3 times");
    }

    [Fact]
    public void A_source_with_nothing_to_say_answers_its_own_notice()
    {
        AdvisoryNotices notices = new();

        for (int i = 0; i < 4; i++)
        {
            notices.Observe("the layout note", "src/MainWindow.xaml");
        }

        notices.Summary().ShouldNotBeNull();

        notices.Observe("the layout note", null);

        notices.Summary().ShouldBeNull("the source spoke and had nothing to say, which is what answered means");
    }

    [Fact]
    public void A_different_subject_starts_a_new_notice_rather_than_continuing_the_last()
    {
        // Otherwise a source that says a true thing about three different files in three calls
        // reads as one thing said three times and ignored.
        AdvisoryNotices notices = new();

        notices.Observe("the layout note", "src/A.xaml");
        notices.Observe("the layout note", "src/B.xaml");
        notices.Observe("the layout note", "src/C.xaml");

        notices.Summary().ShouldBeNull();

        notices.Observe("the layout note", "src/C.xaml");
        notices.Observe("the layout note", "src/C.xaml");

        notices.Summary().ShouldNotBeNull().ShouldContain("src/C.xaml");
    }

    [Fact]
    public void Notices_belong_to_the_run_that_raised_them()
    {
        AdvisoryNotices notices = new();

        for (int i = 0; i < 3; i++)
        {
            notices.Observe("the layout note", "src/MainWindow.xaml");
        }

        RunContext.Set(new RunContext("run-2", "task-1"));

        notices.Summary().ShouldBeNull("a later run does not inherit the last one's unanswered notices");
    }
}
