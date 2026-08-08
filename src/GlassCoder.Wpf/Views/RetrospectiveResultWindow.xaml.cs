using System;
using System.Windows;
using System.Windows.Input;
using GlassCoder.Wpf.ViewModels;

namespace GlassCoder.Wpf.Views;

/// <summary>
/// The proposed workplan, in a window of its own (workplan task 67).
/// <para>
/// It opens when the third stage finishes, because by then the operator has been elsewhere for
/// minutes and this is the one part of a retrospective that wants a decision. Non-modal, and
/// closing it loses nothing: it shares the surface's view model, so the ticks are the same ticks
/// and the surface can open it again.
/// </para>
/// </summary>
public partial class RetrospectiveResultWindow : Window
{
    /// <summary>Creates the window over a retrospective that has already run.</summary>
    /// <param name="model">The surface's view model, shared rather than copied.</param>
    public RetrospectiveResultWindow(RetrospectiveViewModel model)
    {
        ArgumentNullException.ThrowIfNull(model);

        InitializeComponent();
        DataContext = model;
    }

    private void OnClose(object sender, ExecutedRoutedEventArgs e) => Close();
}
