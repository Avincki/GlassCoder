using GlassCoder.Wpf.Services;

namespace GlassCoder.Wpf.Tests;

/// <summary>
/// The desktop shell, faked. Shared rather than nested because more than one test drives the
/// workspace pane, and two fakes of one seam drift apart exactly where it matters.
/// </summary>
internal sealed class FakeShell : IDesktopShell
{
    public bool Answer { get; init; } = true;

    public string? LastQuestion { get; private set; }

    public string? LaunchedProject { get; private set; }

    public bool Confirm(string title, string message)
    {
        LastQuestion = message;
        return Answer;
    }

    /// <summary>The exit callback the pane handed over, so a test can end the app itself.</summary>
    public Action? OnAppExit { get; private set; }

    public string? LaunchApp(string projectFile, Action? onExit = null)
    {
        LaunchedProject = projectFile;
        OnAppExit = onExit;
        return LaunchFailure;
    }

    /// <summary>Set to make the launch fail, as an unbuildable project would.</summary>
    public string? LaunchFailure { get; set; }

    public void OpenFolder(string path)
    {
    }

    public void OpenFileViewer(string fullPath, string displayPath)
    {
    }

    public void Restart()
    {
    }

    public string? PickFolder(string title, string? initialDirectory) => null;

    public string? PickFileToOpen(string title, string filter, string? initialDirectory) => null;

    public string? PickFileToSave(
        string title, string filter, string defaultFileName, string? initialDirectory) => null;

    public string? PromptForPassphrase(string title, string message, bool confirm) => null;
}
