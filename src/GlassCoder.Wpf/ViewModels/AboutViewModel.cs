using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using GlassCoder.Core.Configuration;
using GlassCoder.Tools.Registry;
using GlassCoder.Tools.Retrieval;
using GlassCoder.Wpf.Mvvm;
using GlassCoder.Wpf.Services;
using Microsoft.Extensions.Options;

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
    public AboutViewModel(
        IToolRegistry tools,
        IUserSettingsStore settings,
        IDesktopShell shell,
        IOptions<RetrievalOptions>? retrieval = null)
    {
        ArgumentNullException.ThrowIfNull(tools);

        _settings = settings;
        _shell = shell;

        ToolCount = tools.Functions.Count;

        // Retrieval is passed in so the MCP tools appear when they are switched off too. They are
        // adapted from a server rather than declared as methods, so neither the type sweep nor
        // the registry can account for them while they are inactive - and a default install has
        // them all inactive, which is exactly when someone opens this window to ask what exists.
        Tools = [.. ToolCatalog.Describe(tools, retrieval?.Value).Select(ToolRow.From)];
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

    /// <summary>
    /// Every tool the build knows about, active or not.
    /// <para>
    /// Listing only what is registered answers "what can it do right now" and hides the more
    /// useful question - what it could do, and what is switching it off. Several are absent on a
    /// default install, and each absence is a configuration decision somebody made rather than a
    /// gap in the product, so each says which setting to change.
    /// </para>
    /// </summary>
    public IReadOnlyList<ToolRow> Tools { get; }

    /// <summary>How many of how many, for the heading above the list.</summary>
    public string ToolHeading => string.Create(
        CultureInfo.InvariantCulture,
        $"Tools · {ToolCount} of {Tools.Count} active");

    /// <summary>Where this installation keeps its settings and its keys.</summary>
    public string SettingsPath => _settings.DirectoryPath;

    /// <summary>One line of build facts, for pasting into a bug report.</summary>
    public string BuildLine => string.Create(
        CultureInfo.InvariantCulture,
        $"Version {Version} · {Runtime} · {ToolCount} tools registered");

    /// <summary>Opens the folder holding the settings and the protected keys.</summary>
    public RelayCommand OpenSettingsFolderCommand { get; }

    /// <summary>
    /// One row of the tool list. The catalogue entry carries facts; this carries the phrasing,
    /// which belongs on this side of the seam - <see cref="ToolCatalogEntry"/> has no business
    /// knowing how a window words "off".
    /// </summary>
    /// <param name="Name">The wire name, as the model calls it.</param>
    /// <param name="Description">What it is for - the text the model is given, verbatim.</param>
    /// <param name="IsActive">Whether this session offers it.</param>
    /// <param name="Detail">The one fact worth putting beside the name.</param>
    public sealed record ToolRow(string Name, string Description, bool IsActive, string Detail)
    {
        /// <summary>Renders a catalogue entry for display.</summary>
        public static ToolRow From(ToolCatalogEntry entry)
        {
            ArgumentNullException.ThrowIfNull(entry);

            return new ToolRow(entry.Name, entry.Description, entry.Active, Describe(entry));
        }

        private static string Describe(ToolCatalogEntry entry)
        {
            if (entry.Active)
            {
                // The generated schema, not what reaches the wire - the client re-serialises it
                // indented and it arrives about a third larger. Said plainly here because the
                // number invites comparison with the budget test, which measures at the socket.
                return entry.SchemaCharacters is { } characters
                    ? string.Create(CultureInfo.InvariantCulture, $"{characters:N0} char schema")
                    : string.Empty;
            }

            // The "GlassCoder:" every key starts with says nothing a reader of this window needs.
            if (entry.EnabledBy is { } key)
            {
                return "off · " + key.Replace("GlassCoder:", string.Empty, StringComparison.Ordinal);
            }

            // Switched on and still absent: a setting is not what is missing, so saying "off"
            // would send someone to a checkbox that is already ticked.
            return entry.Unavailable ?? "not registered by any path";
        }
    }
}
