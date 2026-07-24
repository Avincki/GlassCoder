using System;
using System.Windows;
using GlassCoder.Wpf.ViewModels;

namespace GlassCoder.Wpf.Views;

/// <summary>
/// The settings dialog (CLAUDE.md §13). Code-behind does exactly what only a view can do:
/// hand the window its view model and turn "I am finished" into a dialog result.
/// </summary>
public partial class SettingsWindow : Window
{
    private readonly SettingsViewModel _viewModel;

    /// <summary>Creates the window over its view model.</summary>
    public SettingsWindow(SettingsViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);

        InitializeComponent();

        _viewModel = viewModel;
        DataContext = viewModel;
        _viewModel.CloseRequested += OnCloseRequested;
    }

    /// <inheritdoc />
    protected override void OnClosed(EventArgs e)
    {
        _viewModel.CloseRequested -= OnCloseRequested;
        base.OnClosed(e);
    }

    private void OnCloseRequested(object? sender, bool saved) => DialogResult = saved;
}
