using System;
using System.Windows;
using GlassCoder.Wpf.Views;
using Microsoft.Extensions.DependencyInjection;

namespace GlassCoder.Wpf.Services;

/// <summary>
/// Opens the settings window. The shell view model asks for a dialog rather than constructing a
/// <see cref="Window"/> itself, so it stays testable and free of view types (CLAUDE.md §14).
/// </summary>
public interface ISettingsDialog
{
    /// <summary>Shows the dialog modally. Returns whether settings were saved.</summary>
    bool Show();
}

/// <summary>Resolves a fresh window and its view model per invocation, so Cancel really discards.</summary>
public sealed class SettingsDialog : ISettingsDialog
{
    private readonly IServiceProvider _services;

    /// <summary>Creates the dialog service.</summary>
    public SettingsDialog(IServiceProvider services) => _services = services;

    /// <inheritdoc />
    public bool Show()
    {
        SettingsWindow window = _services.GetRequiredService<SettingsWindow>();
        window.Owner = Application.Current?.MainWindow;

        return window.ShowDialog() == true;
    }
}
