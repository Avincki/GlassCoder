using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Threading;
using GlassCoder.Core.Configuration;
using GlassCoder.Core.Hosting;
using GlassCoder.Tools.Changes;
using GlassCoder.Tools.Guardrails;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using GlassCoder.Wpf.Services;
using GlassCoder.Wpf.ViewModels;
using GlassCoder.Wpf.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace GlassCoder.Wpf;

/// <summary>
/// Application entry point. It owns the generic host, so the UI resolves exactly the services
/// the console host does (CLAUDE.md §4, workplan tasks 3 and 25).
/// </summary>
public partial class App : Application
{
    private IHost? _host;

    /// <summary>Services resolved for the UI. Available once startup has run.</summary>
    public IServiceProvider Services =>
        _host?.Services ?? throw new InvalidOperationException("The host has not been built yet.");

    /// <inheritdoc />
    protected override void OnStartup(StartupEventArgs e)
    {
        HostApplicationBuilder builder = GlassCoderHost.CreateBuilder(e?.Args);
        UseDiscoveredWorkspaceWhenUnset(builder);

        // The UI's own registrations sit on top of the shared bootstrap: view models, the
        // dispatcher they marshal onto, and the interactive approval gate that replaces the
        // headless one (workplan task 28).
        builder.Services.AddSingleton(Dispatcher);
        builder.Services.AddSingleton<TranscriptViewModel>();

        // Built by hand because the git tools are optional: GetService returns null when
        // GlassCoder:Git:Enabled is false, and the pane then hides its git controls rather than
        // offering buttons that cannot work (workplan task 42).
        builder.Services.AddSingleton(sp => new ChangesViewModel(
            sp.GetRequiredService<IChangeLog>(),
            sp.GetRequiredService<Dispatcher>(),
            sp.GetService<GlassCoder.Tools.Git.GitTool>(),
            sp.GetService<GlassCoder.Core.Diagnostics.IStepLogger>()));

        builder.Services.AddSingleton<MetricsViewModel>();
        builder.Services.AddSingleton<WorkspaceViewModel>();
        builder.Services.AddSingleton<MainWindowViewModel>();
        builder.Services.AddSingleton<MainWindow>();
        builder.Services.Replace(ServiceDescriptor.Singleton<IApprovalGate, WpfApprovalGate>());

        // Settings: transient, so Cancel discards the edits rather than leaving a half-edited
        // view model behind for the next time the dialog opens.
        builder.Services.AddSingleton<IDesktopShell, DesktopShell>();
        builder.Services.AddSingleton<ISettingsDialog, SettingsDialog>();
        builder.Services.AddTransient<SettingsViewModel>();
        builder.Services.AddTransient<SettingsWindow>();

        builder.Services.AddSingleton<IAboutDialog, AboutDialog>();
        builder.Services.AddTransient<AboutViewModel>();
        builder.Services.AddTransient<AboutWindow>();

        _host = builder.Build();
        _host.Start();

        // Which repository the agent rooted itself in, said out loud - the diagnostic whose
        // absence let a workspace root of "." quietly mean the app's own build output.
        _host.Services.GetRequiredService<ILogger<App>>().LogInformation(
            "Workspace root: {RepoRoot}", _host.Services.GetRequiredService<IPathGuard>().RepoRoot);

        _host.Services.GetRequiredService<MainWindow>().Show();

        base.OnStartup(e!);
    }

    /// <summary>
    /// Roots the desktop app in the repository rather than in its own build output.
    /// <para>
    /// <c>"."</c> means the process working directory, which for a double-clicked window is the
    /// folder the executable sits in. Nobody wants the agent working on <c>bin\Debug</c>, so when
    /// <em>no</em> layer supplied a real root the app discovers one by walking up from the
    /// executable. This is appended, and therefore outranks the <c>"."</c> in
    /// <c>appsettings.json</c> - but it only runs when the resolved value was that placeholder,
    /// so anything actually chosen, saved or exported still wins.
    /// </para>
    /// </summary>
    private static void UseDiscoveredWorkspaceWhenUnset(HostApplicationBuilder builder)
    {
        const string key = "GlassCoder:Workspace:RepoRoot";

        if (!WorkspaceRootLocator.IsUnset(builder.Configuration[key]))
        {
            return;
        }

        if (WorkspaceRootLocator.Find() is not { } discovered)
        {
            return;
        }

        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { [key] = discovered });
    }

    /// <inheritdoc />
    protected override void OnExit(ExitEventArgs e)
    {
        if (_host is not null)
        {
            _host.StopAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            _host.Dispose();
            _host = null;
        }

        base.OnExit(e);
    }
}
