using System.Windows;
using GlassCoder.Wpf.ViewModels;
using GlassCoder.Wpf.Views;

namespace GlassCoder.Wpf.Services;

/// <summary>
/// Opens the proposed workplan in its own window (workplan task 67).
/// <para>
/// Non-modal and single-instance, which is the difference between this and the About box. A modal
/// box arriving minutes after the operator turned to something else gets dismissed to get rid of
/// it - the same reason the rating strip is a strip. And a second press must bring the window
/// forward rather than stack another copy of the same list.
/// </para>
/// </summary>
public sealed class RetrospectiveResultDialog : IRetrospectiveResultDialog
{
    private RetrospectiveResultWindow? _window;

    /// <inheritdoc />
    public void Show(RetrospectiveViewModel model)
    {
        if (_window is not null)
        {
            _window.Activate();
            return;
        }

        // Built by hand rather than resolved: it takes the surface's own view model, so the two
        // faces show one set of ticks. A container-resolved copy would show a second set.
        _window = new RetrospectiveResultWindow(model)
        {
            Owner = Application.Current?.MainWindow,
        };

        _window.Closed += (_, _) => _window = null;
        _window.Show();
    }
}
