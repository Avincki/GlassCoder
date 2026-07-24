using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;

namespace GlassCoder.Wpf.Services;

/// <summary>
/// The two things the settings dialog needs from the operating system. A seam rather than direct
/// calls so the view models stay free of <c>Process.Start</c> and of <c>Application.Current</c>
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
}
