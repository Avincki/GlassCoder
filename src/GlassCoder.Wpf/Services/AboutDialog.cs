using System;
using System.Windows;
using GlassCoder.Wpf.Views;
using Microsoft.Extensions.DependencyInjection;

namespace GlassCoder.Wpf.Services;

/// <summary>
/// Opens the About box. Same shape as <see cref="ISettingsDialog"/>, and for the same reason: the
/// shell view model asks for a dialog rather than constructing a <see cref="Window"/> itself.
/// </summary>
public interface IAboutDialog
{
    /// <summary>Shows the About box modally.</summary>
    void Show();
}

/// <summary>Resolves the window from the container so its view model gets its dependencies.</summary>
public sealed class AboutDialog : IAboutDialog
{
    private readonly IServiceProvider _services;

    /// <summary>Creates the dialog service.</summary>
    public AboutDialog(IServiceProvider services) => _services = services;

    /// <inheritdoc />
    public void Show()
    {
        AboutWindow window = _services.GetRequiredService<AboutWindow>();
        window.Owner = Application.Current?.MainWindow;
        window.ShowDialog();
    }
}
