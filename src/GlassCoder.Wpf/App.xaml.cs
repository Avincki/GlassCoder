using System;
using System.Collections.Generic;
using System.Windows;
using GlassCoder.Core.Configuration;
using GlassCoder.Core.Hosting;
using GlassCoder.Tools.Guardrails;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using GlassCoder.Wpf.DependencyInjection;
using GlassCoder.Wpf.Views;
using Microsoft.Extensions.DependencyInjection;
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

        // The UI's own registrations sit on top of the shared bootstrap. They live in
        // AddGlassCoderDesktop so the graph the app builds is the graph a test can build.
        builder.Services.AddGlassCoderDesktop(Dispatcher);

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
