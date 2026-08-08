using System.Collections.Specialized;
using System.Windows.Controls;

namespace GlassCoder.Wpf.Views;

/// <summary>
/// The retrospective surface (workplan task 67).
/// <para>
/// The only mechanics here are the feed's auto-scroll. A narration the operator has to drag to
/// keep up with is not narration, and "follow the newest line" is a property of the control
/// rather than of the retrospective - so it belongs on this side of the seam (CLAUDE.md §14).
/// </para>
/// </summary>
public partial class RetrospectiveView : UserControl
{
    /// <summary>Creates the surface.</summary>
    public RetrospectiveView()
    {
        InitializeComponent();

        // The control's own item collection rather than the bound source: it exists before the
        // binding resolves and survives the source being replaced, so there is no moment where
        // the feed is live and unwatched.
        ((INotifyCollectionChanged)ActivityList.Items).CollectionChanged += OnFeedChanged;
    }

    private void OnFeedChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action is NotifyCollectionChangedAction.Add)
        {
            ActivityScroller.ScrollToEnd();
        }
    }
}
