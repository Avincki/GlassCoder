using System.Globalization;
using System.Text.RegularExpressions;

namespace GlassCoder.Tools.Verification;

/// <summary>Counts from a <c>dotnet test</c> run.</summary>
/// <param name="Passed">Tests that passed.</param>
/// <param name="Failed">Tests that failed.</param>
/// <param name="Skipped">Tests that were skipped.</param>
/// <param name="Total">Tests that ran.</param>
/// <param name="FailedTests">Names of failing tests, as reported.</param>
public sealed record TestOutcome(int Passed, int Failed, int Skipped, int Total, IReadOnlyList<string> FailedTests)
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
    /// <summary>Parses a test run's output.</summary>
    public static TestOutcome Parse(string? output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return new TestOutcome(0, 0, 0, 0, []);
        }

        int passed = 0;
        int failed = 0;
        int skipped = 0;
        int total = 0;
        List<string> failedTests = [];

        foreach (Match match in SummaryLine().Matches(output))
        {
            failed += Number(match, "failed");
            passed += Number(match, "passed");
            skipped += Number(match, "skipped");
            total += Number(match, "total");
        }

        foreach (Match match in FailedTest().Matches(output))
        {
            string name = match.Groups["name"].Value.Trim();
            if (name.Length > 0 && !failedTests.Contains(name, StringComparer.Ordinal))
            {
                failedTests.Add(name);
            }
        }

        return new TestOutcome(passed, failed, skipped, total, failedTests);
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
}
