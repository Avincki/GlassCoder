using System;
using System.Windows.Threading;
using GlassCoder.Tools.Changes;
using GlassCoder.Tools.Guardrails;
using GlassCoder.Wpf.Services;
using GlassCoder.Wpf.ViewModels;
using GlassCoder.Wpf.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace GlassCoder.Wpf.DependencyInjection;

/// <summary>
/// The desktop half of the composition root: everything the window needs on top of the shared
/// bootstrap in <c>AddGlassCoder</c> (CLAUDE.md §4, workplan task 25).
/// <para>
/// It lives here rather than inline in <see cref="App"/> so a test can build the same graph the
/// application builds. A composition root that only exists inside <c>OnStartup</c> is a
/// composition root nothing can check, and the one defect it hid - a dependency cycle that
/// deadlocked before the window appeared - is exactly the kind that never reaches a unit test
/// otherwise.
/// </para>
/// </summary>
public static class DesktopServiceCollectionExtensions
{
    /// <summary>
    /// Registers the view models, the dispatcher they marshal onto, the dialogs, and the
    /// interactive approval gate that replaces the headless one (workplan task 28).
    /// </summary>
    /// <param name="services">The collection the shared bootstrap has already been added to.</param>
    /// <param name="dispatcher">The UI dispatcher. In the app, the application's own.</param>
    public static IServiceCollection AddGlassCoderDesktop(this IServiceCollection services, Dispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton(dispatcher);
        services.AddSingleton<TranscriptViewModel>();

        // Built by hand because the git tools are optional: GetService returns null when
        // GlassCoder:Git:Enabled is false, and the pane then hides its git controls rather than
        // offering buttons that cannot work (workplan task 42).
        services.AddSingleton(sp => new ChangesViewModel(
            sp.GetRequiredService<IChangeLog>(),
            sp.GetRequiredService<Dispatcher>(),
            sp.GetService<GlassCoder.Tools.Git.GitTool>(),
            sp.GetService<GlassCoder.Core.Diagnostics.IStepLogger>()));

        // The approval gate asks the change view for a decision, and the change view holds the
        // git tool, which holds the gate. Handing the gate an accessor rather than the view
        // itself is what keeps that a chain instead of a loop (see WpfApprovalGate).
        services.AddSingleton<Func<ChangesViewModel>>(sp => sp.GetRequiredService<ChangesViewModel>);

        services.AddSingleton<MetricsViewModel>();
        services.AddSingleton<WorkspaceViewModel>();
        services.AddSingleton<MainWindowViewModel>();
        services.AddSingleton<MainWindow>();
        services.Replace(ServiceDescriptor.Singleton<IApprovalGate, WpfApprovalGate>());

        // UI state, not settings: it lives in the registry precisely so it never reaches
        // IConfiguration, whose hash is what makes a run's arm identifiable.
        services.AddSingleton<IUiStateStore, RegistryUiStateStore>();

        // Settings: transient, so Cancel discards the edits rather than leaving a half-edited
        // view model behind for the next time the dialog opens.
        services.AddSingleton<IDesktopShell, DesktopShell>();
        services.AddSingleton<ISettingsDialog, SettingsDialog>();
        services.AddTransient<SettingsViewModel>();
        services.AddTransient<SettingsWindow>();

        services.AddSingleton<IAboutDialog, AboutDialog>();
        services.AddTransient<AboutViewModel>();
        services.AddTransient<AboutWindow>();

        return services;
    }
}
