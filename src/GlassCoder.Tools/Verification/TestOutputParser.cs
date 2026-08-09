using System.Globalization;
using System.Text.RegularExpressions;

namespace GlassCoder.Tools.Verification;

/// <summary>
/// One failing test and what the runner said about it (workplan task 69).
/// </summary>
/// <param name="Name">The test, as the runner named it.</param>
/// <param name="Message">
/// The assertion message, flattened to one line and capped. This is the half that makes a red
/// suite repairable: <c>"Expected: 7.011652, Actual: 7.006652"</c> says which of the two numbers
/// is wrong, where "two tests failed" leaves only the moves that weaken the test.
/// </param>
public sealed record TestFailure(string Name, string Message);

/// <summary>Counts from a <c>dotnet test</c> run.</summary>
/// <param name="Passed">Tests that passed.</param>
/// <param name="Failed">Tests that failed.</param>
/// <param name="Skipped">Tests that were skipped.</param>
/// <param name="Total">Tests that ran.</param>
/// <param name="FailedTests">Names of failing tests, as reported.</param>
/// <param name="Failures">The same tests with their assertion messages, where the runner gave one.</param>
public sealed record TestOutcome(
    int Passed,
    int Failed,
    int Skipped,
    int Total,
    IReadOnlyList<string> FailedTests,
    IReadOnlyList<TestFailure> Failures)
{
    /// <summary>Whether the run was green.</summary>
    public bool Ok => Failed == 0;
}

/// <summary>
/// Reads the summary out of <c>dotnet test</c> output (workplan task 17).
/// <para>
/// The counts are what the loop needs; the failing test <em>names</em> are what the agent needs,
/// because "3 failed" is not actionable and "Passed: 37, Failed: 3" plus three names is.
/// </para>
/// </summary>
public static partial class TestOutputParser
{
    /// <summary>Longest assertion message kept for one test, before it is cut.</summary>
    private const int MaxMessageCharacters = 240;

    /// <summary>Lines of one failure block worth keeping. Expected and actual are always in the first few.</summary>
    private const int MaxMessageLines = 4;

    /// <summary>Parses a test run's output.</summary>
    public static TestOutcome Parse(string? output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return new TestOutcome(0, 0, 0, 0, [], []);
        }

        int passed = 0;
        int failed = 0;
        int skipped = 0;
        int total = 0;
        List<string> failedTests = [];
        List<TestFailure> failures = [];

        foreach (Match match in SummaryLine().Matches(output))
        {
            failed += Number(match, "failed");
            passed += Number(match, "passed");
            skipped += Number(match, "skipped");
            total += Number(match, "total");
        }

        MatchCollection reported = FailedTest().Matches(output);
        foreach (Match match in reported)
        {
            string name = match.Groups["name"].Value.Trim();
            if (name.Length == 0 || failedTests.Contains(name, StringComparer.Ordinal))
            {
                continue;
            }

            failedTests.Add(name);

            // The first failure per test, which is task 15's contract: one message each, and the
            // count stays the true one however many messages are kept.
            string message = ReadMessage(output, match.Index + match.Length, reported);
            if (message.Length > 0)
            {
                failures.Add(new TestFailure(name, message));
            }
        }

        return new TestOutcome(passed, failed, skipped, total, failedTests, failures);
    }

    /// <summary>
    /// The runner's own words about one failure, from just after its name to whatever ends it.
    /// <para>
    /// Deliberately tolerant rather than format-specific. VSTest writes <c>Error Message:</c> then
    /// <c>Stack Trace:</c>; other runners write neither, and a parser that insisted on them would
    /// answer nothing at all on the day the format moved - which is the failure mode this whole
    /// task exists to remove. Whatever sits between the test's name and the next boundary is a
    /// better answer than silence, and the stack trace is dropped because a model repairing an
    /// assertion needs the numbers rather than the frames.
    /// </para>
    /// </summary>
    private static string ReadMessage(string output, int from, MatchCollection reported)
    {
        // The name match stops inside its own line - on the "[" of "[12 ms]" - so the block starts
        // at the next line, not at the cursor. Without this the first thing kept is "12 ms]".
        int newline = output.IndexOf('\n', Math.Min(from, output.Length - 1));
        if (newline < 0)
        {
            return string.Empty;
        }

        from = newline + 1;
        if (from >= output.Length)
        {
            return string.Empty;
        }

        int end = output.Length;

        foreach (Match other in reported)
        {
            // The next failing test's line ends this one's block.
            if (other.Index > from && other.Index < end)
            {
                end = other.Index;
            }
        }

        int stack = output.IndexOf("Stack Trace:", from, StringComparison.OrdinalIgnoreCase);
        if (stack >= 0 && stack < end)
        {
            end = stack;
        }

        string block = output[from..end];

        List<string> lines = [];
        foreach (string raw in block.Split('\n'))
        {
            string line = raw.Trim().Trim('\r');

            // The label is scaffolding, not information.
            if (line.Length == 0 || line.Equals("Error Message:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            lines.Add(line);
            if (lines.Count == MaxMessageLines)
            {
                break;
            }
        }

        // One line, because this lands in a summary sentence and a multi-line summary reads as
        // several observations rather than one.
        string message = string.Join(" ", lines);
        return message.Length <= MaxMessageCharacters ? message : message[..MaxMessageCharacters] + "…";
    }

    /// <summary>
    /// The failing assertions as lines beneath a summary sentence (workplan task 69).
    /// <para>
    /// Always <em>after</em> the first line and never inside it. The first line is what the run
    /// progress sentry keys repeated failures on, so changing its shape would make one recurring
    /// failure look like a new one every step - the exact confusion naming the tests was added to
    /// remove.
    /// </para>
    /// </summary>
    /// <param name="failures">The parsed failures, in the order the runner reported them.</param>
    /// <param name="max">How many to spell out. The count in the summary stays the true one.</param>
    public static string Describe(IReadOnlyList<TestFailure>? failures, int max = 3) =>
        failures is null || failures.Count == 0
            ? string.Empty
            : string.Concat(failures.Take(max).Select(f => $"\n{f.Name}: {f.Message}"));

    /// <summary>
    /// Reads the names out of <c>dotnet test --list-tests</c> output (workplan task 51).
    /// <para>
    /// Parsed from the runner rather than scanned for attributes, which is the whole point: an
    /// attribute scan is cheaper and wrong, because it misses custom frameworks, theory
    /// expansions and anything generated. The authoritative list is the one the runner itself
    /// would execute, and it is worth the build it costs.
    /// </para>
    /// </summary>
    public static IReadOnlyList<string> ParseDiscovered(string? output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return [];
        }

        List<string> names = [];
        bool listing = false;

        foreach (string raw in output.Split('\n'))
        {
            string line = raw.Trim();

            if (!listing)
            {
                // The runner announces the list. Anything before that is build output, and a
                // build log holds plenty of dotted words that are not test names.
                listing = line.Contains("Tests are available", StringComparison.OrdinalIgnoreCase);
                continue;
            }

            if (line.Length == 0)
            {
                continue;
            }

            if (!TestName().IsMatch(line))
            {
                // The list is contiguous; the first thing that is not a name ends it.
                break;
            }

            names.Add(line);
        }

        return names;
    }

    private static int Number(Match match, string group) =>
        match.Groups[group].Success && int.TryParse(match.Groups[group].Value, CultureInfo.InvariantCulture, out int value)
            ? value
            : 0;

    // Failed!  - Failed:     3, Passed:    37, Skipped:     0, Total:    40, Duration: 1 s
    [GeneratedRegex(
        @"Failed:\s*(?<failed>\d+),\s*Passed:\s*(?<passed>\d+),\s*Skipped:\s*(?<skipped>\d+),\s*Total:\s*(?<total>\d+)",
        RegexOptions.IgnoreCase | RegexOptions.ExplicitCapture,
        1000)]
    private static partial Regex SummaryLine();

    //   Failed Namespace.Class.Method [12 ms]
    [GeneratedRegex(
        @"^\s*(?:\[xUnit\.net[^\]]*\]\s*)?(?:Failed|X)\s+(?<name>[A-Za-z_][\w.]*(?:\([^)]*\))?)\s*(?:\[|$)",
        RegexOptions.Multiline | RegexOptions.ExplicitCapture,
        1000)]
    private static partial Regex FailedTest();

    // Namespace.Class.Method, optionally with a theory's arguments: Method(value: 1, other: "x")
    [GeneratedRegex(@"^[A-Za-z_][\w.+]*\.[\w`<>+]+(?:\(.*\))?$", RegexOptions.ExplicitCapture, 1000)]
    private static partial Regex TestName();
}
