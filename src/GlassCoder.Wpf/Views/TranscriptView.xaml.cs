using System.Collections.Specialized;
using System.Windows.Controls;
using System.Windows.Threading;

namespace GlassCoder.Wpf.Views;

/// <summary>
/// The live transcript (workplan task 26). The view model owns the rows and the selection;
/// what lives here is only what a view model cannot do - scrolling.
/// <para>
/// The transcript follows the run: each new step selects itself and the detail pane sits at
/// its end, where the tool results and the verification verdict land. Clicking an earlier row
/// pins the view on that step - a transcript that yanks the selection away mid-read is
/// unreadable during exactly the runs worth reading - and selecting the newest row again is
/// what resumes following. The newest row is thereby both a row and the "follow live" control,
/// which spares the view a checkbox nobody would find.
/// </para>
/// </summary>
public partial class TranscriptView : UserControl
{
    private bool _followTail = true;
    private bool _autoSelecting;

    /// <summary>Creates the view and wires the follow-the-tail behaviour.</summary>
    public TranscriptView()
    {
        InitializeComponent();

        // The ItemCollection survives ItemsSource changes, so one subscription covers the
        // lifetime of the view, filters included.
        ((INotifyCollectionChanged)StepGrid.Items).CollectionChanged += OnRowsChanged;
        StepGrid.SelectionChanged += OnSelectionChanged;
        Loaded += (_, _) => FollowTail();
    }

    private void OnRowsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_followTail)
        {
            FollowTail();
        }
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_autoSelecting || StepGrid.Items.Count == 0)
        {
            return;
        }

        // A hand on the newest row means "follow the run"; a hand on any other row means
        // "stay here". The detail pane matches: pinned steps read from the top, the live tail
        // reads from the end, where the newest content lands.
        _followTail = ReferenceEquals(StepGrid.SelectedItem, StepGrid.Items[StepGrid.Items.Count - 1]);
        ScrollDetail(toEnd: _followTail);
    }

    private void FollowTail()
    {
        if (StepGrid.Items.Count == 0)
        {
            return;
        }

        _autoSelecting = true;
        try
        {
            object last = StepGrid.Items[StepGrid.Items.Count - 1];
            StepGrid.SelectedItem = last;
            StepGrid.ScrollIntoView(last);
        }
        finally
        {
            _autoSelecting = false;
        }

        ScrollDetail(toEnd: true);
    }

    /// <summary>
    /// Scrolls the detail pane once the binding has caught up - at input priority, the text
    /// the pane shows is still the previous step's.
    /// </summary>
    private void ScrollDetail(bool toEnd) =>
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            if (toEnd)
            {
                DetailScroll.ScrollToEnd();
            }
            else
            {
                DetailScroll.ScrollToTop();
            }
        });
}
