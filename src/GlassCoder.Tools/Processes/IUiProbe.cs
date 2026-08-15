using System.Globalization;

namespace GlassCoder.Tools.Processes;

/// <summary>What one probe step does to one named element.</summary>
public enum UiProbeAction
{
    /// <summary>Put text into it.</summary>
    Set,

    /// <summary>Press it.</summary>
    Invoke,

    /// <summary>Read what it shows.</summary>
    Read,
}

/// <summary>One step of a probe: an action against one named element.</summary>
/// <param name="Action">What to do.</param>
/// <param name="Element">The element's automation id or name - in WPF, its <c>x:Name</c>.</param>
/// <param name="Value">The text to put in, for <see cref="UiProbeAction.Set"/>.</param>
public sealed record UiProbeStep(UiProbeAction Action, string Element, string? Value);

/// <summary>What one step saw.</summary>
/// <param name="Step">The step, as the model wrote it.</param>
/// <param name="Ok">Whether the step did what it asked to do.</param>
/// <param name="Saw">The text read back, for a read that found its element.</param>
/// <param name="Problem">Why the step could not be carried out, when it could not.</param>
public sealed record UiProbeReading(string Step, bool Ok, string? Saw, string? Problem)
{
    /// <summary>One line for the launch summary.</summary>
    public string Describe() => (Ok, Saw) switch
    {
        (true, not null) => string.Create(CultureInfo.InvariantCulture, $"{Step} → \"{Saw}\""),
        (true, null) => $"{Step} ok",
        _ => $"{Step} - {Problem ?? "did not happen"}",
    };
}

/// <summary>
/// Reading and driving a running application's window (the rung above "it drew something").
/// <para>
/// Task 71 shipped <c>launch_app</c> knowing it topped out at "a window appeared", and said so.
/// Three runs later the refutation it cannot answer is still being made and is still correct:
/// <em>runtime evidence only proves a window drew</em>. Run <c>ae72c5ad</c> shipped a temperature
/// converter whose defects - a value stranded on backspace, a raw 37.77777777777778 in the box -
/// are all thirty seconds of typing away and all invisible to every static rung. This is the seam
/// that types.
/// </para>
/// <para>
/// A seam of its own for the same reason <see cref="IWindowPresence"/> is one: it is platform
/// knowledge, and the managed UI Automation client lives in the Windows desktop framework, which a
/// cross-platform library cannot reference. A host that cannot supply one leaves it null, and the
/// tool says that it read nothing rather than implying there was nothing to read.
/// </para>
/// </summary>
public interface IUiProbe
{
    /// <summary>
    /// Runs the steps against the window of <paramref name="processId"/>, in order, and reports
    /// what each saw.
    /// <para>
    /// The contract is task 71's, one level up: a step that finds no element by that name reports
    /// that it did not, and nothing here throws. The caller is holding a live application it is
    /// about to kill; an exception out of here would turn a piece of missing evidence into a
    /// failed launch.
    /// </para>
    /// </summary>
    Task<IReadOnlyList<UiProbeReading>> RunAsync(
        int processId,
        IReadOnlyList<UiProbeStep> steps,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// The probe as the model writes it: <c>Celsius=100; Convert!; Fahrenheit?</c>.
/// <para>
/// A single string rather than a list of objects, and that is a budget decision as much as a
/// usability one. Tool schemas are re-sent on every request of every run and are already 96% of a
/// step-0 request (<c>PromptBudgetTests</c>); an array-of-objects parameter for this would have
/// cost several hundred characters a step forever. One string costs a line, and the three verbs
/// are short enough to be described in one.
/// </para>
/// </summary>
public sealed record UiProbeScript(IReadOnlyList<UiProbeStep> Steps, string? Problem)
{
    /// <summary>
    /// Most steps a probe may carry. This runs against a live application inside a launch timeout,
    /// so it is a check of a few fields, not a test suite - anything longer belongs in a test that
    /// can be re-run without a window.
    /// </summary>
    public const int MaxSteps = 6;

    /// <summary>Longest an element name or a typed value may be.</summary>
    private const int MaxTextLength = 120;

    /// <summary>Nothing asked for.</summary>
    public static UiProbeScript None { get; } = new([], null);

    /// <summary>
    /// Reads the script. A malformed step is reported rather than thrown: the launch is the
    /// valuable part and must happen even when the probe was written wrongly.
    /// </summary>
    public static UiProbeScript Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return None;
        }

        List<UiProbeStep> steps = [];
        List<string> complaints = [];

        foreach (string raw in text.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (steps.Count == MaxSteps)
            {
                complaints.Add($"only the first {MaxSteps} steps were run");
                break;
            }

            if (Step(raw) is { } step)
            {
                steps.Add(step);
            }
            else
            {
                complaints.Add($"'{Cap(raw)}' is not a step");
            }
        }

        return new UiProbeScript(steps, complaints.Count == 0 ? null : string.Join("; ", complaints));
    }

    private static UiProbeStep? Step(string raw)
    {
        int assign = raw.IndexOf('=', StringComparison.Ordinal);
        if (assign >= 0)
        {
            // An assignment with nothing on the left is a step with no element, and the bare-name
            // fallback below must not quietly adopt it as the name of something to read.
            string name = raw[..assign].Trim();
            return name.Length == 0 ? null : new UiProbeStep(UiProbeAction.Set, Cap(name), Cap(raw[(assign + 1)..].Trim()));
        }

        if (raw.Length > 1 && raw[^1] is '!' or '?')
        {
            string name = raw[..^1].Trim();
            return name.Length == 0
                ? null
                : new UiProbeStep(raw[^1] == '!' ? UiProbeAction.Invoke : UiProbeAction.Read, Cap(name), null);
        }

        // A bare name is the commonest thing a model will write when it means "read this", and
        // guessing wrong here costs nothing: reading an element it did not mean to read reports one
        // extra fact, where refusing reports none.
        string bare = raw.Trim();
        return bare.Length == 0 ? null : new UiProbeStep(UiProbeAction.Read, Cap(bare), null);
    }

    private static string Cap(string value) =>
        value.Length <= MaxTextLength ? value : value[..MaxTextLength];
}
