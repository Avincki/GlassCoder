using System.Diagnostics;
using System.Globalization;
using System.Windows;
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
    /// Most controls an unasked-for sweep will report. Ten is a window's worth of fields; past
    /// that the reading stops being evidence and starts being a page of chrome carried in every
    /// subsequent step's context.
    /// </summary>
    private const int MaxReadBack = 10;

    /// <summary>
    /// How far the walk goes when it is looking for an address rather than writing a report. Wider
    /// than the report's cap: an eleventh control is still addressable even though printing eleven
    /// would be noise.
    /// </summary>
    private const int MaxAddressable = 60;

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

    /// <inheritdoc />
    public Task<IReadOnlyList<UiProbeReading>> ReadAllAsync(
        int processId, CancellationToken cancellationToken = default) =>
        Task.Run<IReadOnlyList<UiProbeReading>>(() => ReadAll(processId, cancellationToken), cancellationToken);

    /// <summary>
    /// Every named text-bearing control, in tree order, capped.
    /// <para>
    /// Edit and Text are the two control types that carry what a user reads: a WPF TextBox answers
    /// to the first, a TextBlock or Label to the second. The cap is what keeps a window with a
    /// hundred labels from writing a hundred lines into every subsequent step's context - this
    /// string is carried for the rest of the run.
    /// </para>
    /// </summary>
    private List<UiProbeReading> ReadAll(int processId, CancellationToken cancellationToken)
    {
        List<UiProbeReading> readings = [];

        AutomationElement? window = FindWindow(processId, cancellationToken);
        if (window is null)
        {
            return readings;
        }

        if (TextBearing(window, _logger) is not { } found)
        {
            return readings;
        }

        Rect windowBounds = Bounds(window);

        foreach (Labelled labelled in Walk(found, MaxReadBack, cancellationToken))
        {
            // And whether it is where it can be read. XamlNotices has been telling runs that
            // "compile and tests cannot see clipping; launch_app can" while launch_app read
            // nothing but text - run 29356042 was warned at step 7, launched three times at 19-21
            // with the window in front of it, and never answered the warning. A rectangle is a
            // measurement, not a judgement about the design.
            readings.Add(new UiProbeReading($"{labelled.Label}?", Ok: true, Saw: labelled.Value, Problem: null)
            {
                Note = OutsideWindow(labelled.Element, windowBounds),
            });
        }

        return readings;
    }

    /// <summary>
    /// The window's text-bearing controls, each with the best identity it offers, in tree order.
    /// <para>
    /// One walk, two callers, and that is the point. The sweep prints these identities and the
    /// prober accepts them back, so a window that names nothing can still be driven. Run
    /// <c>457867c7</c> is what the open loop cost: three <c>no element by that name</c> refusals,
    /// four steps, and two <c>x:Name</c> attributes added to shipped markup for no reason except
    /// that the harness could not address what it had just printed - the interface editing the
    /// product.
    /// </para>
    /// </summary>
    private static List<Labelled> Walk(AutomationElementCollection found, int cap, CancellationToken cancellationToken)
    {
        List<Labelled> walked = [];

        // The last static text walked past, so an unnamed box can be reported as the one that
        // follows it. Run dd11ef7c's window carries no x:Name on anything, which is the ordinary
        // case for generated XAML - "Edit#2 reads 0" is a fact nobody can place, and the label it
        // sits next to is the only identity the window offers.
        string? preceding = null;

        for (int index = 0; index < found.Count && walked.Count < cap; index++)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            try
            {
                AutomationElement element = found[index];
                bool isText = Equals(element.Current.ControlType, ControlType.Text);
                string label = element.Current.AutomationId;
                string value = Read(element);

                if (string.IsNullOrEmpty(label))
                {
                    // A static label with no text is not a fact. An *editable* box with no text is
                    // both a fact and the most important address in the window: it is what a fresh
                    // window is made of, and what a probe types into. Dropping it left a window
                    // that names nothing with nothing to say and nothing to drive.
                    if (isText && value.Length == 0)
                    {
                        preceding = null;
                        continue;
                    }

                    // Tree order, stated as tree order. "The box after 'Celsius:'" is what the
                    // window looks like and is checkable; calling it the Celsius box would be a
                    // claim about labelling that nothing here established.
                    label = (isText, preceding) switch
                    {
                        (false, not null) => $"the box after \"{preceding}\"",
                        _ => string.Create(
                            CultureInfo.InvariantCulture,
                            $"{element.Current.ControlType.ProgrammaticName.Split('.')[^1]}#{index}"),
                    };
                }

                preceding = isText && value.Length > 0 ? value : preceding;
                walked.Add(new Labelled(label, value, element));
            }
            catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException)
            {
                // The window is being torn down around us, which is what a launch does next.
                break;
            }
        }

        return walked;
    }

    /// <summary>
    /// Why this control cannot be read, or null when it can.
    /// <para>
    /// The cheap half of the clipping oracle, and the half that is a measurement rather than a
    /// judgement. Run <c>ea9a1f66</c> shipped a result field below the bottom of its own window
    /// past a green build, green tests and 100% tool-call validity, and the only thing that ever
    /// caught it was a person looking at the screen. This does not replace them; it answers the
    /// question <c>XamlNotices</c> has been promising a launch could answer.
    /// </para>
    /// <para>
    /// Two facts, both from UI Automation: the framework's own <c>IsOffscreen</c>, and a rectangle
    /// that does not fit inside the window's. An empty rectangle means "not laid out" rather than
    /// "clipped" - a collapsed container is the ordinary case - and says nothing, on the same
    /// bargain the sweep already strikes for controls it cannot name.
    /// </para>
    /// </summary>
    private static string? OutsideWindow(AutomationElement element, Rect window)
    {
        try
        {
            if (element.Current.IsOffscreen)
            {
                return "not visible on screen";
            }

            Rect bounds = Bounds(element);
            if (bounds.IsEmpty || window.IsEmpty || bounds.Width <= 0 || bounds.Height <= 0)
            {
                return null;
            }

            // Any edge past the window's own is a control the operator cannot fully read. A
            // fraction of a pixel is rounding, not clipping.
            const double slack = 1.0;
            return bounds.Right > window.Right + slack || bounds.Bottom > window.Bottom + slack ||
                   bounds.Left < window.Left - slack || bounds.Top < window.Top - slack
                ? "outside the window"
                : null;
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException)
        {
            return null;
        }
    }

    private static Rect Bounds(AutomationElement element)
    {
        try
        {
            return element.Current.BoundingRectangle;
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException)
        {
            return Rect.Empty;
        }
    }

    /// <summary>Every text-bearing control the window has, for addressing and for reporting.</summary>
    private static AutomationElementCollection? TextBearing(AutomationElement window, ILogger logger)
    {
        try
        {
            return window.FindAll(
                TreeScope.Descendants,
                new OrCondition(
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit),
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text)));
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException)
        {
            logger.LogDebug(ex, "The window could not be walked");
            return null;
        }
    }

    private sealed record Labelled(string Label, string Value, AutomationElement Element);

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

        (AutomationElement? element, string? refusal) = Find(window, step.Element, cancellationToken);
        if (element is null)
        {
            return new UiProbeReading(described, Ok: false, Saw: null, Problem: refusal);
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

    /// <summary>
    /// The element a step names, by automation id, by accessible name, or by the identity the
    /// sweep would print for it - and, when nothing matches, the identities the window does offer.
    /// <para>
    /// The third lookup is what stops the harness's address space from editing the product. A
    /// positional identity is accepted for typing as well as reading, but only when it names
    /// exactly one control: the hazard is not the heuristic, it is an ambiguous match typing into
    /// a control nobody meant, and that is checkable rather than guessable.
    /// </para>
    /// </summary>
    private (AutomationElement? Element, string? Refusal) Find(
        AutomationElement window, string name, CancellationToken cancellationToken)
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
                    return (found, null);
                }
            }
            catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException)
            {
                return (null, "the window closed while it was being read");
            }

            if (clock.Elapsed >= ElementWait || cancellationToken.IsCancellationRequested)
            {
                break;
            }

            Thread.Sleep(100);
        }

        // Named lookup is exhausted. Fall back to what the sweep can see, which is the only
        // identity an unnamed control has.
        if (TextBearing(window, _logger) is not { } found2)
        {
            return (null, "no element by that name");
        }

        List<Labelled> walked = Walk(found2, MaxAddressable, cancellationToken);
        List<Labelled> matches =
            [.. walked.Where(l => string.Equals(l.Label, name, StringComparison.OrdinalIgnoreCase))];

        return matches.Count switch
        {
            1 => (matches[0].Element, null),
            > 1 => (null, $"{matches.Count} controls answer to that, so it is not an address"),
            // With what each of them holds. The walk is carrying the values already, and a bare
            // list of identities is the shape of input that invites a reader to fill in what it
            // remembers writing - which is what the model did at step 20 of run 29356042, narrating
            // an "Invalid input" that no control was showing.
            _ => (null, walked.Count == 0
                ? "no element by that name"
                : $"no element by that name; the window offers " +
                  string.Join(", ", walked.Take(MaxReadBack).Select(l => $"{l.Label}=\"{l.Value}\""))),
        };
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
