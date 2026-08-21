using System.Text;
using System.Text.RegularExpressions;

namespace GlassCoder.Core.Planning;

/// <summary>
/// One task in a workplan, in the format GlassContext emits (workplan task 78).
/// <para>
/// The mirror image of GlassContext's own <c>WorkplanTask</c>, deliberately field for field. Two
/// programs that disagree about this format disagree about which task a run's metrics belong to,
/// and the disagreement is silent - so the fixtures in <c>WorkplanReaderTests</c> are ported from
/// its <c>WorkplanFormatV2Tests</c> rather than written afresh.
/// </para>
/// </summary>
public sealed class WorkplanTask
{
    /// <summary>The heading text, without its number.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Everything under the task that is not one of the recognised fields.</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>The human estimate, as written - <c>2h</c>, <c>0.5d</c>.</summary>
    public string EstimatedTime { get; set; } = string.Empty;

    /// <summary>
    /// The stable identifier from <c>&lt;!-- task:slug --&gt;</c>.
    /// <para>
    /// The join key for run metrics, and the one thing in this format that cannot be got wrong.
    /// Position cannot serve: a plan is renumbered whenever it is reordered, so joining on "task
    /// 7" silently re-points every historical outcome at whatever is seventh today. Empty on a v1
    /// plan, where <see cref="EffectiveSlug"/> derives one from the title instead.
    /// </para>
    /// </summary>
    public string Slug { get; set; } = string.Empty;

    /// <summary>Files the task is expected to change. Advisory - the runner never enforces them.</summary>
    public IList<string> TargetFiles { get; set; } = [];

    /// <summary>
    /// The command that decides completion - in practice a <c>dotnet test --filter</c> expression.
    /// Empty when the task has no machine-checkable oracle, and the runner then refuses to tick
    /// the box on the model's own judgement.
    /// </summary>
    public string Oracle { get; set; } = string.Empty;

    /// <summary>Rough size in agent steps, when the plan says. Human hours and steps do not convert.</summary>
    public int? Steps { get; set; }

    /// <summary>Checkbox state, as read from the plan.</summary>
    public bool IsComplete { get; set; }

    /// <summary>
    /// The number on the heading, when it had one.
    /// <para>
    /// Kept so a plan numbered 77 to 84 - which this repository's own is - renders back as itself
    /// rather than as 1 to 8. GlassContext renumbers from one because it generates the document
    /// and owns it; this reader is handed documents it did not write, and a renderer that quietly
    /// renumbered them would make its round trip a claim it cannot support.
    /// </para>
    /// </summary>
    public int? Number { get; set; }

    /// <summary>The slug to run under and join metrics on: explicit, else derived from the title.</summary>
    public string EffectiveSlug =>
        string.IsNullOrWhiteSpace(Slug) ? Workplan.Slugify(Title) : Slug.Trim();

    /// <summary>
    /// The <c>--filter</c> expression from <see cref="Oracle"/>, when it carries one.
    /// <para>
    /// Only the filter is taken, never the whole command. The oracle names which tests decide the
    /// task; <em>how</em> tests run is the ladder's business, and a runner that shelled out to the
    /// oracle verbatim would be running an arbitrary command line out of a file the agent it
    /// supervises can edit.
    /// </para>
    /// </summary>
    public string? TestFilter
    {
        get
        {
            if (string.IsNullOrWhiteSpace(Oracle))
            {
                return null;
            }

            Match match = Workplan.FilterPattern().Match(Oracle);
            if (!match.Success)
            {
                return null;
            }

            string value = match.Groups["filter"].Value.Trim().Trim('"', '\'').Trim();
            return value.Length > 0 ? value : null;
        }
    }
}

/// <summary>
/// A workplan, read from and written back to its Markdown form (workplan task 78).
/// <para>
/// Reading is separated from executing on purpose: this type knows the format and nothing about
/// agents, so the format can be tested against GlassContext's fixtures without a model in the
/// loop. <see cref="WorkplanRunner"/> is what executes.
/// </para>
/// <para>
/// Tolerant of v1 - no slug, no oracle, no target files - because every plan in this repository is
/// v1 today, and anything unrecognised stays in the description rather than being dropped.
/// </para>
/// </summary>
public sealed partial class Workplan
{
    /// <summary>The tasks, in the order the plan lists them.</summary>
    public IList<WorkplanTask> Tasks { get; } = [];

    /// <summary>
    /// Everything above the first task heading, kept verbatim.
    /// <para>
    /// Kept rather than regenerated, which is where this reader deliberately differs from
    /// GlassContext's writer. GlassContext owns the document it generates and re-emits its own
    /// header; this harness is a guest in a file a developer edits by hand, and a round trip that
    /// silently dropped their notes, their totals or their contract table would be a worse bug
    /// than any it was added to fix.
    /// </para>
    /// </summary>
    public string Preamble { get; set; } = string.Empty;

    /// <summary>
    /// The line ending the source used, so a plan written with one convention is not rewritten in
    /// the other the first time a checkbox is ticked.
    /// </summary>
    public string NewLine { get; set; } = Environment.NewLine;

    /// <summary>
    /// Whether the source ended on a blank line, which the canonical format does and a
    /// hand-trimmed file often does not.
    /// <para>
    /// One newline, remembered for the same reason the preamble and the heading numbers are: a
    /// round trip that is faithful except for a byte is not a round trip, and the whole value of
    /// this reader to a file it does not own is being able to put back exactly what it was given.
    /// </para>
    /// </summary>
    public bool EndsWithBlankLine { get; set; } = true;

    /// <summary>Reads a workplan from Markdown. Never throws on shape; unknown text is description.</summary>
    /// <param name="markdown">The plan's text.</param>
    public static Workplan Parse(string markdown)
    {
        Workplan plan = new();
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return plan;
        }

        plan.NewLine = markdown.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";

        plan.EndsWithBlankLine = markdown.EndsWith(plan.NewLine + plan.NewLine, StringComparison.Ordinal);

        string[] lines = markdown.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');

        List<string> preamble = [];
        List<string> description = [];
        WorkplanTask? current = null;

        void Flush()
        {
            if (current is null)
            {
                return;
            }

            current.Description = string.Join(plan.NewLine, TrimBlankEdges(description)).Trim();
            plan.Tasks.Add(current);
            description.Clear();
        }

        foreach (string line in lines)
        {
            Match heading = TaskHeadingPattern().Match(line);
            if (heading.Success)
            {
                if (current is null)
                {
                    plan.Preamble = string.Join(plan.NewLine, TrimTrailingBlanks(preamble));
                }

                Flush();
                current = new WorkplanTask
                {
                    Title = heading.Groups["title"].Value.Trim(),
                    Number = int.TryParse(heading.Groups["number"].Value, out int number) ? number : null,
                };
                continue;
            }

            if (current is null)
            {
                preamble.Add(line);
                continue;
            }

            Match slug = SlugCommentPattern().Match(line);
            if (slug.Success)
            {
                // First wins. The declaring comment sits directly under the heading; a later match
                // is prose quoting the marker - and letting that win would repoint the task's whole
                // run history, which is the one thing the slug exists to prevent. GlassContext
                // learned this in its own commit f2e5531; the rule is copied, not re-derived.
                if (current.Slug.Length == 0)
                {
                    current.Slug = slug.Groups["slug"].Value.Trim();
                }

                continue;
            }

            Match estimate = EstimatePattern().Match(line);
            if (estimate.Success)
            {
                current.IsComplete = estimate.Groups["mark"].Value.Trim().Length > 0;
                current.EstimatedTime = estimate.Groups["estimate"].Value.Trim();
                if (int.TryParse(estimate.Groups["steps"].Value, out int steps))
                {
                    current.Steps = steps;
                }

                continue;
            }

            Match oracle = OraclePattern().Match(line);
            if (oracle.Success)
            {
                current.Oracle = oracle.Groups["oracle"].Value.Trim().Trim('`').Trim();
                continue;
            }

            Match targets = TargetFilesPattern().Match(line);
            if (targets.Success)
            {
                current.TargetFiles =
                [
                    .. targets.Groups["files"].Value
                        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .Select(file => file.Trim().Trim('`').Trim())
                        .Where(file => file.Length > 0),
                ];
                continue;
            }

            description.Add(line);
        }

        if (current is null)
        {
            plan.Preamble = string.Join(plan.NewLine, TrimTrailingBlanks(preamble));
        }

        Flush();
        return plan;
    }

    /// <summary>
    /// Renders the plan back to Markdown, field for field as GlassContext writes it, under
    /// whatever preamble the source carried.
    /// </summary>
    public string ToMarkdown()
    {
        StringBuilder text = new();

        if (Preamble.Length > 0)
        {
            text.Append(Preamble).Append(NewLine).Append(NewLine);
        }

        for (int index = 0; index < Tasks.Count; index++)
        {
            WorkplanTask task = Tasks[index];

            Line(text, task.Number is { } number ? $"## {number}. {task.Title}" : $"## {task.Title}");
            Line(text, string.Empty);

            string slug = task.EffectiveSlug;
            if (slug.Length > 0)
            {
                Line(text, $"<!-- task:{slug} -->");
                Line(text, string.Empty);
            }

            string estimate = $"{(task.IsComplete ? "- [x]" : "- [ ]")} **Estimated time:** {task.EstimatedTime}";
            if (task.Steps is { } steps)
            {
                estimate += $" · **Steps:** ~{steps}";
            }

            Line(text, estimate);
            Line(text, string.Empty);

            if (task.TargetFiles.Count > 0)
            {
                Line(text, $"**Target files:** {string.Join(", ", task.TargetFiles.Select(file => $"`{file}`"))}");
                Line(text, string.Empty);
            }

            if (!string.IsNullOrWhiteSpace(task.Oracle))
            {
                Line(text, $"**Oracle:** `{task.Oracle.Trim()}`");
                Line(text, string.Empty);
            }

            Line(text, task.Description);
            Line(text, string.Empty);
        }

        // The last task's closing blank line, dropped when the source did not carry one.
        if (!EndsWithBlankLine && Tasks.Count > 0 && text.Length >= NewLine.Length)
        {
            text.Length -= NewLine.Length;
        }

        return text.ToString();

        void Line(StringBuilder builder, string value) => builder.Append(value).Append(NewLine);
    }

    /// <summary>
    /// Ticks one task's checkbox in the plan's own text, changing nothing else.
    /// <para>
    /// A line edit rather than a re-render, and the distinction is the whole reason this method
    /// exists beside <see cref="ToMarkdown"/>. Re-rendering would renumber headings, normalise
    /// spacing and rewrite the developer's own formatting every time the harness ticked a box -
    /// a diff nobody asked for, on a file somebody else owns. Ticking touches one character.
    /// </para>
    /// </summary>
    /// <param name="markdown">The plan's current text.</param>
    /// <param name="slug">The task to tick, by effective slug.</param>
    /// <returns>The updated text, or the original when the task was not found or already ticked.</returns>
    public static string Tick(string markdown, string slug)
    {
        ArgumentNullException.ThrowIfNull(markdown);
        ArgumentException.ThrowIfNullOrWhiteSpace(slug);

        string newLine = markdown.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        string[] lines = markdown.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');

        // Walk the same way Parse does, so "which task is this line in" cannot answer differently
        // in the two places. Tracking the heading rather than searching for the slug means a plan
        // whose body quotes another task's marker still ticks the right box.
        WorkplanTask? current = null;
        int estimateLine = -1;

        for (int index = 0; index < lines.Length; index++)
        {
            string line = lines[index];

            if (TaskHeadingPattern().Match(line) is { Success: true } heading)
            {
                if (Matches(current, slug) && estimateLine >= 0)
                {
                    break;
                }

                current = new WorkplanTask { Title = heading.Groups["title"].Value.Trim() };
                estimateLine = -1;
                continue;
            }

            if (current is null)
            {
                continue;
            }

            if (SlugCommentPattern().Match(line) is { Success: true } marker && current.Slug.Length == 0)
            {
                current.Slug = marker.Groups["slug"].Value.Trim();
                continue;
            }

            if (estimateLine < 0 && EstimatePattern().IsMatch(line))
            {
                estimateLine = index;
            }
        }

        if (!Matches(current, slug) || estimateLine < 0)
        {
            return markdown;
        }

        lines[estimateLine] = TickPattern().Replace(lines[estimateLine], "${lead}[x]", 1);
        return string.Join(newLine, lines);

        static bool Matches(WorkplanTask? task, string slug) =>
            task is not null && string.Equals(task.EffectiveSlug, slug, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A kebab-case identifier derived from a title - the fallback slug for a plan written before
    /// the marker existed. Character for character what GlassContext's <c>Slugify</c> does, so a
    /// v1 plan gets the same key on both sides.
    /// </summary>
    /// <param name="title">The task title.</param>
    public static string Slugify(string title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return string.Empty;
        }

        StringBuilder text = new(title.Length);
        foreach (char character in title.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(character))
            {
                text.Append(character);
            }
            else if (text.Length > 0 && text[^1] != '-')
            {
                text.Append('-');
            }
        }

        string slug = text.ToString().Trim('-');
        const int MaxLength = 48;

        return slug.Length > MaxLength ? slug[..MaxLength].TrimEnd('-') : slug;
    }

    private static List<string> TrimBlankEdges(List<string> lines)
    {
        int start = 0;
        int end = lines.Count - 1;

        while (start <= end && string.IsNullOrWhiteSpace(lines[start]))
        {
            start++;
        }

        while (end >= start && string.IsNullOrWhiteSpace(lines[end]))
        {
            end--;
        }

        return start > end ? [] : lines.GetRange(start, end - start + 1);
    }

    private static List<string> TrimTrailingBlanks(List<string> lines)
    {
        int end = lines.Count - 1;
        while (end >= 0 && string.IsNullOrWhiteSpace(lines[end]))
        {
            end--;
        }

        return end < 0 ? [] : lines.GetRange(0, end + 1);
    }

    /// <summary>`## 7. Title` - the number is optional, so a hand-written plan still parses.</summary>
    [GeneratedRegex(@"^##\s+(?:(?<number>\d+)\.\s*)?(?<title>.+?)\s*$")]
    private static partial Regex TaskHeadingPattern();

    [GeneratedRegex(@"<!--\s*task:(?<slug>[A-Za-z0-9._-]+)\s*-->")]
    private static partial Regex SlugCommentPattern();

    /// <summary>`- [x] **Estimated time:** 2h · **Steps:** ~12`</summary>
    [GeneratedRegex(
        @"^\s*[-*]\s*\[(?<mark>[^\]]?)\]\s*\*\*Estimated time:\*\*\s*(?<estimate>[^·|]*?)\s*(?:[·|]\s*\*\*Steps:\*\*\s*~?(?<steps>\d+)\s*)?$",
        RegexOptions.IgnoreCase)]
    private static partial Regex EstimatePattern();

    [GeneratedRegex(@"^\s*\*\*Oracle:\*\*\s*(?<oracle>.+?)\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex OraclePattern();

    [GeneratedRegex(@"^\s*\*\*Target files:\*\*\s*(?<files>.+?)\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex TargetFilesPattern();

    /// <summary>The checkbox itself, so ticking replaces one bracketed character and nothing else.</summary>
    [GeneratedRegex(@"(?<lead>^\s*[-*]\s*)\[[^\]]?\]")]
    private static partial Regex TickPattern();

    /// <summary>`--filter X` or `--filter "A|B"` inside an oracle command line.</summary>
    [GeneratedRegex(@"--filter[=\s]+(?<filter>""[^""]+""|'[^']+'|\S+)", RegexOptions.IgnoreCase)]
    internal static partial Regex FilterPattern();
}
