using System.Diagnostics;
using System.Globalization;
using System.Windows.Automation;
using GlassCoder.Tools.Processes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace GlassCoder.Tools.Windows;

/// <summary>
/// The rung above "a window drew": UI Automation against the window the launch just opened.
/// <para>
/// Everything here is bounded and nothing here throws. The caller is holding a live application it
/// is about to kill and is inside a launch timeout, so an element that cannot be found is a
/// sentence in the report, not an exception - the same bargain <c>launch_app</c> itself strikes.
/// </para>
/// <para>
/// <strong>Element names are the ones the model wrote.</strong> A WPF control's <c>x:Name</c>
/// becomes its <c>AutomationId</c>, which is why that is looked up first; the accessible
/// <c>Name</c> is the fallback, and it is what a <c>TextBlock</c> or a labelled button answers to.
/// </para>
/// </summary>
public sealed class UiAutomationProbe : IUiProbe
{
    /// <summary>How long to keep looking for the window, which may still be laying out.</summary>
    private static readonly TimeSpan WindowWait = TimeSpan.FromSeconds(3);

    /// <summary>How long to keep looking for one element before reporting that it is not there.</summary>
    private static readonly TimeSpan ElementWait = TimeSpan.FromSeconds(1);

    /// <summary>
    /// A beat after typing, before anything is read.
    /// <para>
    /// A WPF binding with the default <c>UpdateSourceTrigger</c> does not commit until the box
    /// loses focus, so this pairs with the focus taken before a read: the read moves focus, the
    /// pause lets the handler that fires on the way out do its work. Without the pair, a converter
    /// bound the default way reads back its previous value and the probe reports a defect that is
    /// its own.
    /// </para>
    /// </summary>
    private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(250);

    private readonly ILogger<UiAutomationProbe> _logger;

    /// <summary>Creates the probe.</summary>
    public UiAutomationProbe(ILogger<UiAutomationProbe>? logger = null) =>
        _logger = logger ?? NullLogger<UiAutomationProbe>.Instance;

    /// <inheritdoc />
    public Task<IReadOnlyList<UiProbeReading>> RunAsync(
        int processId,
        IReadOnlyList<UiProbeStep> steps,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(steps);

        // UI Automation is blocking COM. Off the caller's thread, so the launch's own clock keeps
        // running while this works.
        return Task.Run<IReadOnlyList<UiProbeReading>>(() => Run(processId, steps, cancellationToken), cancellationToken);
    }

    private List<UiProbeReading> Run(int processId, IReadOnlyList<UiProbeStep> steps, CancellationToken cancellationToken)
    {
        List<UiProbeReading> readings = [];

        AutomationElement? window = FindWindow(processId, cancellationToken);
        if (window is null)
        {
            readings.Add(new UiProbeReading(
                Describe(steps[0]), Ok: false, Saw: null, Problem: "the window could not be attached to"));
            return readings;
        }

        foreach (UiProbeStep step in steps)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                readings.Add(new UiProbeReading(Describe(step), Ok: false, Saw: null, Problem: "the launch timed out first"));
                break;
            }

            readings.Add(Perform(window, step, cancellationToken));
        }

        return readings;
    }

    private UiProbeReading Perform(AutomationElement window, UiProbeStep step, CancellationToken cancellationToken)
    {
        string described = Describe(step);

        AutomationElement? element = Find(window, step.Element, cancellationToken);
        if (element is null)
        {
            return new UiProbeReading(described, Ok: false, Saw: null, Problem: "no element by that name");
        }

        try
        {
            switch (step.Action)
            {
                case UiProbeAction.Set:
                    if (!element.TryGetCurrentPattern(ValuePattern.Pattern, out object valuePattern))
                    {
                        return new UiProbeReading(described, Ok: false, Saw: null, Problem: "it does not take typed text");
                    }

                    ((ValuePattern)valuePattern).SetValue(step.Value ?? string.Empty);
                    Thread.Sleep(Settle);
                    return new UiProbeReading(described, Ok: true, Saw: null, Problem: null);

                case UiProbeAction.Invoke:
                    if (!element.TryGetCurrentPattern(InvokePattern.Pattern, out object invokePattern))
                    {
                        return new UiProbeReading(described, Ok: false, Saw: null, Problem: "it is not something that can be pressed");
                    }

                    ((InvokePattern)invokePattern).Invoke();
                    Thread.Sleep(Settle);
                    return new UiProbeReading(described, Ok: true, Saw: null, Problem: null);

                default:
                    // Focus first: see Settle. Failing to take focus is not a failure to read - a
                    // TextBlock cannot be focused and has nothing to commit either.
                    TakeFocus(element);
                    Thread.Sleep(Settle);
                    return new UiProbeReading(described, Ok: true, Saw: Read(element), Problem: null);
            }
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or ElementNotEnabledException or InvalidOperationException)
        {
            // The application closed the element, disabled it, or was never going to allow this.
            // All three are facts about the window, which is what the probe is here to report.
            _logger.LogDebug(ex, "Probe step {Step} could not be carried out", described);
            return new UiProbeReading(described, Ok: false, Saw: null, Problem: ex.GetType().Name);
        }
    }

    /// <summary>
    /// What the element shows: the value it holds, or failing that the accessible name, which is
    /// where a <c>TextBlock</c>'s text lives.
    /// </summary>
    private static string Read(AutomationElement element)
    {
        if (element.TryGetCurrentPattern(ValuePattern.Pattern, out object pattern))
        {
            string value = ((ValuePattern)pattern).Current.Value ?? string.Empty;
            if (value.Length > 0)
            {
                return value;
            }
        }

        return element.Current.Name ?? string.Empty;
    }

    private static void TakeFocus(AutomationElement element)
    {
        try
        {
            if (element.Current.IsKeyboardFocusable)
            {
                element.SetFocus();
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or ElementNotAvailableException)
        {
            // Focus is a means here, never the evidence.
        }
    }

    /// <summary>
    /// The application's top-level window, through the same handle
    /// <see cref="WindowPresence"/> polls - so the probe attaches to whatever the launch declared
    /// ready, rather than to some other window of the same process.
    /// </summary>
    private AutomationElement? FindWindow(int processId, CancellationToken cancellationToken)
    {
        Stopwatch clock = Stopwatch.StartNew();
        while (clock.Elapsed < WindowWait && !cancellationToken.IsCancellationRequested)
        {
            try
            {
                using Process process = Process.GetProcessById(processId);
                process.Refresh();
                if (process.MainWindowHandle != IntPtr.Zero)
                {
                    return AutomationElement.FromHandle(process.MainWindowHandle);
                }
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or ElementNotAvailableException)
            {
                _logger.LogDebug(ex, "The window of process {ProcessId} could not be attached to", processId);
                return null;
            }

            Thread.Sleep(100);
        }

        return null;
    }

    private static AutomationElement? Find(AutomationElement window, string name, CancellationToken cancellationToken)
    {
        Stopwatch clock = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                AutomationElement? found =
                    window.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, name)) ??
                    window.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.NameProperty, name));

                if (found is not null)
                {
                    return found;
                }
            }
            catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException)
            {
                return null;
            }

            if (clock.Elapsed >= ElementWait || cancellationToken.IsCancellationRequested)
            {
                return null;
            }

            Thread.Sleep(100);
        }
    }

    /// <summary>The step in the model's own notation, so its report reads back as what it asked for.</summary>
    private static string Describe(UiProbeStep step) => step.Action switch
    {
        UiProbeAction.Set => string.Create(CultureInfo.InvariantCulture, $"{step.Element}={step.Value}"),
        UiProbeAction.Invoke => $"{step.Element}!",
        _ => $"{step.Element}?",
    };
}

/// <summary>Registers the Windows-only tool implementations.</summary>
public static class WindowsToolsServiceCollectionExtensions
{
    /// <summary>
    /// Gives the harness the ability to read and drive a launched window.
    /// <para>
    /// Called by a composition root that is itself targeted at Windows. A host that does not call
    /// it launches applications exactly as before and says, when asked to probe one, that it has
    /// no probe - which is a different sentence from "nothing was there to see", and the
    /// difference is the whole point of task 71's wording.
    /// </para>
    /// </summary>
    public static IServiceCollection AddWindowsTools(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<IUiProbe, UiAutomationProbe>();
        return services;
    }
}
