using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Threading;
using GlassCoder.Core.Verification;
using GlassCoder.TestSupport;
using GlassCoder.Wpf.ViewModels;
using GlassCoder.Wpf.Views;

namespace GlassCoder.Wpf.Tests;

/// <summary>
/// The file viewer window (workplan tasks 39 and 43).
/// <para>
/// XAML is not checked by the compiler. A missing <c>StaticResource</c> key, a converter that is
/// not where the markup says it is, or a trigger on a property the view model does not have all
/// build cleanly and throw when the window is first constructed - which, for a window opened by
/// a double-click, means in front of the operator. Constructing it here is what turns that into
/// a failing test.
/// </para>
/// </summary>
public sealed class FileViewerWindowTests
{
    [Fact]
    public void The_window_builds_over_a_readable_file()
    {
        using TempWorkspace workspace = new();
        string file = workspace.WriteFile("src/A.cs", "public sealed class A\n{\n}\n");

        (string Title, bool Reviewing, Visibility Splitter) shown = UiThread.Run(_ =>
        {
            TestApplication.Ensure();
            FileViewerViewModel model = FileViewerViewModel.Load(file, "src/A.cs");
            FileViewerWindow window = new(model);

            return (window.Title, model.IsReviewing, Splitter(window).Visibility);
        });

        shown.Title.ShouldBe("src/A.cs");
        shown.Reviewing.ShouldBeFalse();
        shown.Splitter.ShouldBe(Visibility.Collapsed, "there is no review to make room for yet");
    }

    [Fact]
    public void The_window_builds_over_a_file_it_will_not_show()
    {
        // The other branch of the constructor. It collapses the body and shows a notice, and it
        // is reached by a double-click on a PNG.
        using TempWorkspace workspace = new();
        string file = workspace.WriteFile("assets/logo.png", "\0\0\0binary\0");

        string? message = UiThread.Run(dispatcher =>
        {
            TestApplication.Ensure();
            FileViewerViewModel model = FileViewerViewModel.Load(file, "assets/logo.png");
            FileViewerWindow window = new(model);
            window.ShouldNotBeNull();
            return model.Message;
        });

        message.ShouldNotBeNull();
        message.ShouldContain("binary");
    }

    [Fact]
    public void A_review_fills_the_report_and_opens_the_pane()
    {
        using TempWorkspace workspace = new();
        string file = workspace.WriteFile("src/A.cs", "public sealed class A\n{\n}\n");

        (double Width, bool HasReport, int Actions, int Accepted, Visibility Splitter) after =
            UiThread.Run(dispatcher =>
            {
                // The view model marshals its continuations back onto whatever context started
                // them, which on the real UI thread is the dispatcher's.
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
                TestApplication.Ensure();

                FileViewerViewModel model = FileViewerViewModel.Load(file, "src/A.cs", new StubReviewer(Review()));
                FileViewerWindow window = new(model);

                Pump(dispatcher, model, () => model.ReviewCommand.Execute(null));

                ColumnDefinition column = (ColumnDefinition)window.FindName("ReviewColumn");
                RichTextBox report = (RichTextBox)window.FindName("Report");

                return (
                    column.Width.Value,
                    new TextRange(report.Document.ContentStart, report.Document.ContentEnd).Text.Contains(
                        "guard is missing", StringComparison.Ordinal),
                    model.Actions.Count,
                    model.Actions.Count(a => a.IsAccepted),
                    Splitter(window).Visibility);
            });

        after.Width.ShouldBeGreaterThan(0, "the first review opens the column it goes in");
        after.Splitter.ShouldBe(Visibility.Visible);
        after.HasReport.ShouldBeTrue();
        after.Actions.ShouldBe(2);

        // Defects start ticked, everything else does not: "yes to the bugs, let me read the rest"
        // is the common press, and pre-ticking the lot would enrol the operator in unread work.
        after.Accepted.ShouldBe(1);
    }

    /// <summary>Runs <paramref name="start"/> and pumps the dispatcher until the review lands.</summary>
    private static void Pump(Dispatcher dispatcher, FileViewerViewModel model, Action start)
    {
        DispatcherFrame frame = new();
        model.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(FileViewerViewModel.IsReviewing) && !model.IsReviewing)
            {
                frame.Continue = false;
            }
        };

        // A hard stop, so a review that never completes fails this test rather than hanging the
        // run until UiThread's own budget expires.
        _ = dispatcher.BeginInvoke(
            DispatcherPriority.Background,
            new Action(async () =>
            {
                await Task.Delay(TimeSpan.FromSeconds(10)).ConfigureAwait(true);
                frame.Continue = false;
            }));

        start();
        Dispatcher.PushFrame(frame);
    }

    private static GridSplitter Splitter(FileViewerWindow window) =>
        (GridSplitter)window.FindName("ReviewSplitter");

    private static FileReview Review() => new()
    {
        Reviewed = true,
        Report = "# Findings\n\nThe guard is missing.",
        Model = "claude-opus-5",
        DurationMs = 41_200,
        EstimatedCostUsd = 0.214m,
        Actions =
        [
            new ReviewAction("guard", "Reject '..'", "line 233", ReviewActionPriority.High),
            new ReviewAction("tidy", "Rename the probe", string.Empty, ReviewActionPriority.Optional),
        ],
    };

    /// <summary>An <see cref="IFileReviewer"/> that answers immediately with a canned review.</summary>
    private sealed class StubReviewer : IFileReviewer
    {
        private readonly FileReview _review;

        public StubReviewer(FileReview review) => _review = review;

        public bool Enabled => true;

        public Task<ReviewerAvailability> ProbeAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(ReviewerAvailability.Available("test"));

        public Task<FileReview> ReviewAsync(FileReviewRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(_review);
    }
}
