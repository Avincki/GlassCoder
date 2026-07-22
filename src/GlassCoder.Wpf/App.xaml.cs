using System.Windows;
using GlassCoder.Core.Hosting;
using Microsoft.Extensions.Hosting;

namespace GlassCoder.Wpf;

/// <summary>
/// Application entry point. It owns the generic host so the UI resolves exactly the services
/// the console host does (CLAUDE.md §4, workplan task 3). The MVVM shell that consumes them
/// arrives in task 25.
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
        _host = GlassCoderHost.CreateBuilder(e?.Args).Build();
        _host.Start();

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
