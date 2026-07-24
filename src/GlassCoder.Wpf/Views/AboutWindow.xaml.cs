using System;
using System.Windows;
using GlassCoder.Wpf.ViewModels;

namespace GlassCoder.Wpf.Views;

/// <summary>
/// The About box. Code-behind does the one thing only a view can: hand the window its view model.
/// </summary>
public partial class AboutWindow : Window
{
    /// <summary>Creates the window over its view model.</summary>
    public AboutWindow(AboutViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);

        InitializeComponent();
        DataContext = viewModel;
    }
}
