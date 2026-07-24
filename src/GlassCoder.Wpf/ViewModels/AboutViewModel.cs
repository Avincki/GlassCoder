using System;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using GlassCoder.Core.Configuration;
using GlassCoder.Tools.Registry;
using GlassCoder.Wpf.Mvvm;
using GlassCoder.Wpf.Services;

namespace GlassCoder.Wpf.ViewModels;

/// <summary>
/// What the About box says. A view model rather than literals in XAML because half of it is
/// discovered at run time - the build it is actually running, the runtime under it, and the tools
/// this session registered.
/// </summary>
public sealed class AboutViewModel : ViewModelBase
{
    private readonly IUserSettingsStore _settings;
    private readonly IDesktopShell _shell;

    /// <summary>Creates the view model.</summary>
    public AboutViewModel(IToolRegistry tools, IUserSettingsStore settings, IDesktopShell shell)
    {
        ArgumentNullException.ThrowIfNull(tools);

        _settings = settings;
        _shell = shell;

        ToolCount = tools.Functions.Count;
        OpenSettingsFolderCommand = new RelayCommand(() => _shell.OpenFolder(_settings.DirectoryPath));
    }

    // The fixed strings are static and the view reaches them with {x:Static}. Making them
    // instance properties so a plain {Binding} could find them would be four members that never
    // touch instance state - which is exactly what CA1822 objects to.

    /// <summary>The application name.</summary>
    public static string Product => "GlassCoder";

    /// <summary>
    /// What the application is for, in one line. The name is the claim: the loop is visible all
    /// the way through rather than a box that returns a diff.
    /// </summary>
    public static string Purpose =>
        "A glass-box coding agent for local models — every step logged, every change visible, every run measured.";

    /// <summary>Who built it.</summary>
    public static string Builder => "Build by Bad Boy at Kintsunai";

    /// <summary>The author.</summary>
    public static string Author => "Dr. Ing. Alex Vinckier";

    /// <summary>The build actually running, taken from the assembly rather than written down twice.</summary>
    public static string Version
    {
        get
        {
            Assembly assembly = typeof(AboutViewModel).Assembly;

            string? informational = assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

            // SourceLink appends +<commit> to the informational version; the hash belongs on its
            // own line rather than in the middle of a version number.
            if (!string.IsNullOrWhiteSpace(informational))
            {
                int plus = informational.IndexOf('+', StringComparison.Ordinal);
                return plus > 0 ? informational[..plus] : informational;
            }

            return assembly.GetName().Version?.ToString() ?? "unknown";
        }
    }

    /// <summary>The runtime underneath, which is the first thing a bug report needs.</summary>
    public static string Runtime => RuntimeInformation.FrameworkDescription;

    /// <summary>Tools registered this session - the honest inventory of what the model can call.</summary>
    public int ToolCount { get; }

    /// <summary>Where this installation keeps its settings and its keys.</summary>
    public string SettingsPath => _settings.DirectoryPath;

    /// <summary>One line of build facts, for pasting into a bug report.</summary>
    public string BuildLine => string.Create(
        CultureInfo.InvariantCulture,
        $"Version {Version} · {Runtime} · {ToolCount} tools registered");

    /// <summary>Opens the folder holding the settings and the protected keys.</summary>
    public RelayCommand OpenSettingsFolderCommand { get; }
}
