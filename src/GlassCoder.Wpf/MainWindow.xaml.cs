using System.Windows;
using GlassCoder.Wpf.ViewModels;

namespace GlassCoder.Wpf;

/// <summary>
/// The shell window. Its only job is to hold the view model - all behaviour lives there
/// (CLAUDE.md §14: no business logic in code-behind).
/// </summary>
public partial class MainWindow : Window
{
    /// <summary>Creates the window and binds it to the shell view model.</summary>
    public MainWindow(MainWindowViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
