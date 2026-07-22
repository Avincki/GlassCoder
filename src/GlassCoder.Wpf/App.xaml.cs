using System;
using System.Windows;
using System.Windows.Threading;
using GlassCoder.Core.Hosting;
using GlassCoder.Tools.Changes;
using GlassCoder.Wpf.Services;
using GlassCoder.Wpf.ViewModels;
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

        // The UI's own registrations sit on top of the shared bootstrap: view models, the
        // dispatcher they marshal onto, and the interactive approval gate that replaces the
        // headless one (workplan task 28).
        builder.Services.AddSingleton(Dispatcher);
        builder.Services.AddSingleton<TranscriptViewModel>();
        builder.Services.AddSingleton<ChangesViewModel>();
        builder.Services.AddSingleton<MetricsViewModel>();
        builder.Services.AddSingleton<MainWindowViewModel>();
        builder.Services.AddSingleton<MainWindow>();
        builder.Services.Replace(ServiceDescriptor.Singleton<IApprovalGate, WpfApprovalGate>());

        _host = builder.Build();
        _host.Start();

        _host.Services.GetRequiredService<MainWindow>().Show();

        base.OnStartup(e!);
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
