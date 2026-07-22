using System.Globalization;
using System.Text.RegularExpressions;

namespace GlassCoder.Tools.Verification;

/// <summary>
/// Turns <c>dotnet build</c> output into typed diagnostics (workplan task 17).
/// </summary>
/// <remarks>
/// <para>
/// CLAUDE.md §8.1 says never to regex over compiler text - and this class is a regex over
/// compiler text, so the exception is worth stating. That rule is about <em>per-document</em>
/// feedback, where Roslyn hands over real <c>Diagnostic</c> objects and scraping prose would be
/// choosing the worse source. Rung 2 does exactly that (task 14).
/// </para>
/// <para>
/// But <c>dotnet build</c> is the authoritative gate precisely because it is what CI runs, and
/// its output is the only channel it offers. The pattern matched here is MSBuild's canonical
/// diagnostic format, which is stable and documented; what this parser must never do is try to
/// interpret free-form build prose beyond it.
/// </para>
/// </remarks>
public static partial class MsBuildOutputParser
{
    /// <summary>Parses build output into diagnostics, de-duplicated and in first-seen order.</summary>
    /// <param name="output">Combined stdout and stderr from the build.</param>
    /// <param name="makeRelative">Optional mapping from an absolute path to a repo-relative one.</param>
    public static IReadOnlyList<CodeDiagnostic> Parse(string? output, Func<string, string>? makeRelative = null)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return [];
        }

        List<CodeDiagnostic> diagnostics = [];
        HashSet<string> seen = new(StringComparer.Ordinal);

        foreach (string rawLine in output.Split('\n'))
        {
            string line = rawLine.TrimEnd('\r', ' ', '\t');
            if (line.Length == 0)
            {
                continue;
            }

            CodeDiagnostic? diagnostic = ParseLocated(line, makeRelative) ?? ParseUnlocated(line);
            if (diagnostic is null)
            {
                continue;
            }

            // MSBuild repeats a diagnostic once per project and per target framework.
            string fingerprint = $"{diagnostic.Id}|{diagnostic.FilePath}|{diagnostic.Line}|{diagnostic.Column}|{diagnostic.Message}";
            if (seen.Add(fingerprint))
            {
                diagnostics.Add(diagnostic);
            }
        }

        return diagnostics;
    }

    private static CodeDiagnostic? ParseLocated(string line, Func<string, string>? makeRelative)
    {
        Match match = LocatedDiagnostic().Match(line);
        if (!match.Success)
        {
            return null;
        }

        string file = match.Groups["file"].Value.Trim();
        if (makeRelative is not null && Path.IsPathRooted(file))
        {
            file = makeRelative(file);
        }

        return new CodeDiagnostic(
            match.Groups["id"].Value,
            Severity(match.Groups["severity"].Value),
            match.Groups["message"].Value.Trim(),
            file,
            int.Parse(match.Groups["line"].Value, CultureInfo.InvariantCulture),
            int.Parse(match.Groups["column"].Value, CultureInfo.InvariantCulture));
    }

    private static CodeDiagnostic? ParseUnlocated(string line)
    {
        Match match = UnlocatedDiagnostic().Match(line);
        return match.Success
            ? new CodeDiagnostic(
                match.Groups["id"].Value,
                Severity(match.Groups["severity"].Value),
                match.Groups["message"].Value.Trim())
            : null;
    }

    private static CodeSeverity Severity(string value) =>
        value.Equals("error", StringComparison.OrdinalIgnoreCase) ? CodeSeverity.Error : CodeSeverity.Warning;

    // Path\File.cs(12,34): error CS0103: The name 'x' does not exist [C:\repo\Project.csproj]
    [GeneratedRegex(
        @"^\s*(?<file>[^(\r\n]+?)\((?<line>\d+),(?<column>\d+)\)\s*:\s*(?<severity>error|warning)\s+(?<id>[A-Za-z]+[0-9]+)\s*:\s*(?<message>.*?)(?:\s*\[[^\]]*\])?$",
        RegexOptions.IgnoreCase | RegexOptions.ExplicitCapture,
        1000)]
    private static partial Regex LocatedDiagnostic();

    // MSBUILD : error MSB1003: Specify a project or solution file.
    [GeneratedRegex(
        @"^\s*(?:[A-Za-z0-9_.]+\s*:\s*)?(?<severity>error|warning)\s+(?<id>[A-Za-z]+[0-9]+)\s*:\s*(?<message>.*?)(?:\s*\[[^\]]*\])?$",
        RegexOptions.IgnoreCase | RegexOptions.ExplicitCapture,
        1000)]
    private static partial Regex UnlocatedDiagnostic();
}
