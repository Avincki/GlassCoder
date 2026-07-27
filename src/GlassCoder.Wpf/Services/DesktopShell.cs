using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;

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
    /// Restarts the application. Settings are bound once at startup through
    /// <c>IOptions&lt;T&gt;</c>, so this is what makes a saved change the one in force.
    /// </summary>
    void Restart();

    /// <summary>Asks the user for a folder. The chosen path, or null when they cancelled.</summary>
    string? PickFolder(string title, string? initialDirectory);
}

/// <summary>The Windows implementation of <see cref="IDesktopShell"/>.</summary>
public sealed class DesktopShell : IDesktopShell
{
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
}
