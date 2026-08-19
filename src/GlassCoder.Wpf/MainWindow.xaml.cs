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

        // The header band asks the model servers what they are serving. Here rather than in the
        // constructor or in OnStartup because it must not delay the window by a single frame: the
        // usual answer is "not available", and waiting on a closed port to say so would make a
        // model server that is not running into a slow launch. What is asked stays in the view
        // model - this only says when (CLAUDE.md §14).
        Loaded += (_, _) => _ = viewModel.DescribeModelsAsync();
    }
}
