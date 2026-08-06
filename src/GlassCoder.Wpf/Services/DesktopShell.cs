using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using GlassCoder.Core.Verification;
using GlassCoder.Wpf.ViewModels;
using GlassCoder.Wpf.Views;

namespace GlassCoder.Wpf.Services;

/// <summary>
/// What the view models need from the operating system. A seam rather than direct calls so they
/// stay free of <c>Process.Start</c>, of <c>Application.Current</c> and of dialog classes
/// (CLAUDE.md §14).
/// </summary>
public interface IDesktopShell
{
    /// <summary>Opens a folder in the file browser, creating it if it does not exist yet.</summary>
    void OpenFolder(string path);

    /// <summary>
    /// Opens a read-only viewer on a file from the workspace.
    /// <para>
    /// Modeless, and one window per call: reading a file is not a decision the shell is waiting
    /// on, and comparing two files means having both open at once.
    /// </para>
    /// </summary>
    /// <param name="fullPath">Absolute path to read.</param>
    /// <param name="displayPath">Repo-relative path, for the title bar.</param>
    void OpenFileViewer(string fullPath, string displayPath);

    /// <summary>
    /// Restarts the application. Settings are bound once at startup through
    /// <c>IOptions&lt;T&gt;</c>, so this is what makes a saved change the one in force.
    /// </summary>
    void Restart();

    /// <summary>Asks the user for a folder. The chosen path, or null when they cancelled.</summary>
    string? PickFolder(string title, string? initialDirectory);

    /// <summary>Asks the user for an existing file. The chosen path, or null when they cancelled.</summary>
    /// <param name="title">Dialog title.</param>
    /// <param name="filter">A Win32 file filter, e.g. <c>Config files|*.json</c>.</param>
    /// <param name="initialDirectory">Where to start, when it exists.</param>
    string? PickFileToOpen(string title, string filter, string? initialDirectory);

    /// <summary>Asks the user where to write a file. The chosen path, or null when they cancelled.</summary>
    /// <param name="title">Dialog title.</param>
    /// <param name="filter">A Win32 file filter, e.g. <c>Config files|*.json</c>.</param>
    /// <param name="defaultFileName">The name offered.</param>
    /// <param name="initialDirectory">Where to start, when it exists.</param>
    string? PickFileToSave(string title, string filter, string defaultFileName, string? initialDirectory);

    /// <summary>
    /// Asks for the passphrase that protects an exported file's API keys.
    /// <para>
    /// Three answers, not two: the passphrase, an empty string meaning "carry on without the
    /// keys", and null meaning cancel. Collapsing the last two would make cancelling a dialog
    /// silently export a file with the keys missing.
    /// </para>
    /// </summary>
    /// <param name="title">Dialog title.</param>
    /// <param name="message">What the passphrase is for, in the operator's terms.</param>
    /// <param name="confirm">Whether to ask twice, which an export wants and an import does not.</param>
    string? PromptForPassphrase(string title, string message, bool confirm);

    /// <summary>
    /// Asks before a destructive action. True only when the user explicitly said yes: closing
    /// the dialog is a no, because the safe reading of an unanswered question is "do not".
    /// </summary>
    /// <param name="title">Dialog title.</param>
    /// <param name="message">What is about to be destroyed, in the operator's terms.</param>
    bool Confirm(string title, string message);
}

/// <summary>The Windows implementation of <see cref="IDesktopShell"/>.</summary>
public sealed class DesktopShell : IDesktopShell
{
    private readonly IFileReviewer? _reviewer;
    private readonly IReviewActionWriter? _writer;

    /// <summary>
    /// Creates the shell.
    /// <para>
    /// The reviewer and its writer are optional so a host that never registered them still gets
    /// a working viewer - the Review button simply says it is unavailable, which is the same
    /// answer a machine without the CLI installed gets.
    /// </para>
    /// </summary>
    public DesktopShell(IFileReviewer? reviewer = null, IReviewActionWriter? writer = null)
    {
        _reviewer = reviewer;
        _writer = writer;
    }

    /// <inheritdoc />
    public void OpenFolder(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            using Process? _ = Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or UnauthorizedAccessException)
        {
            // Not being able to open a folder is not worth taking the application down for.
        }
    }

    /// <inheritdoc />
    public void OpenFileViewer(string fullPath, string displayPath)
    {
        // Read before the window exists, so a file that cannot be read becomes the window's
        // message rather than an exception thrown mid-construction.
        FileViewerWindow window = new(FileViewerViewModel.Load(fullPath, displayPath, _reviewer, _writer, this))
        {
            Owner = Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive),
        };

        window.Show();
    }

    /// <inheritdoc />
    public void Restart()
    {
        string? executable = Environment.ProcessPath;
        if (!string.IsNullOrEmpty(executable))
        {
            try
            {
                using Process? _ = Process.Start(new ProcessStartInfo(executable) { UseShellExecute = true });
            }
            catch (Win32Exception)
            {
                // Fall through to the shutdown: better to exit than to leave the operator
                // looking at settings that are not the ones in force.
            }
        }

        Application.Current?.Shutdown();
    }

    /// <inheritdoc />
    public string? PickFolder(string title, string? initialDirectory)
    {
        Microsoft.Win32.OpenFolderDialog dialog = new() { Title = title };
        if (!string.IsNullOrWhiteSpace(initialDirectory) && Directory.Exists(initialDirectory))
        {
            dialog.InitialDirectory = initialDirectory;
        }

        return dialog.ShowDialog() == true ? dialog.FolderName : null;
    }

    /// <inheritdoc />
    public string? PickFileToOpen(string title, string filter, string? initialDirectory)
    {
        Microsoft.Win32.OpenFileDialog dialog = new()
        {
            Title = title,
            Filter = filter,
            CheckFileExists = true,
        };

        SetInitialDirectory(dialog, initialDirectory);
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    /// <inheritdoc />
    public string? PickFileToSave(string title, string filter, string defaultFileName, string? initialDirectory)
    {
        Microsoft.Win32.SaveFileDialog dialog = new()
        {
            Title = title,
            Filter = filter,
            FileName = defaultFileName,
            OverwritePrompt = true,
            AddExtension = true,
        };

        SetInitialDirectory(dialog, initialDirectory);
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    /// <inheritdoc />
    public string? PromptForPassphrase(string title, string message, bool confirm)
    {
        PassphraseWindow window = new(title, message, confirm)
        {
            Owner = Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive),
        };

        return window.ShowDialog() == true ? window.Passphrase : null;
    }

    /// <inheritdoc />
    public bool Confirm(string title, string message)
    {
        Window? owner = Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive);
        MessageBoxResult answer = owner is null
            ? MessageBox.Show(message, title, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No)
            : MessageBox.Show(owner, message, title, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);

        return answer == MessageBoxResult.Yes;
    }

    private static void SetInitialDirectory(Microsoft.Win32.FileDialog dialog, string? initialDirectory)
    {
        if (!string.IsNullOrWhiteSpace(initialDirectory) && Directory.Exists(initialDirectory))
        {
            dialog.InitialDirectory = initialDirectory;
        }
    }
}
