using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using GlassCoder.Tools.Guardrails;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GlassCoder.Core.Verification;

/// <summary>One reviewed action and whether the operator wants it done.</summary>
/// <param name="Action">What the reviewer proposed.</param>
/// <param name="Accepted">Whether it was ticked.</param>
public sealed record ReviewActionItem(ReviewAction Action, bool Accepted);

/// <summary>A review as it survives on disk: the prose, the proposals, and which were accepted.</summary>
/// <param name="File">Repo-relative path of the file that was reviewed.</param>
/// <param name="ReviewedAt">When the review ran.</param>
/// <param name="Model">The model that answered.</param>
/// <param name="CostUsd">What the review cost.</param>
/// <param name="Report">The review itself, as Markdown.</param>
/// <param name="Items">Every proposal, accepted or not.</param>
public sealed record ReviewActionPlan(
    string File,
    DateTimeOffset ReviewedAt,
    string Model,
    decimal CostUsd,
    string Report,
    IReadOnlyList<ReviewActionItem> Items)
{
    /// <summary>
    /// What kind of document this is, which is how a reader tells a file review's work order from
    /// a retrospective's. Defaults to <see cref="ReviewActionFile.Kind"/>.
    /// </summary>
    public string Kind { get; init; } = ReviewActionFile.Kind;

    /// <summary>
    /// What the accepted work is to be done to, when that is not the reviewed file itself.
    /// <c>harness</c> for a retrospective, whose actions change GlassCoder rather than the
    /// workspace it was pointed at (workplan task 67).
    /// </summary>
    public string? Target { get; init; }

    /// <summary>The run these actions came out of, when they came out of one.</summary>
    public string? RunId { get; init; }

    /// <summary>The document's heading. Null takes the reviewed file's.</summary>
    public string? Heading { get; init; }

    /// <summary>
    /// What to say after the actions - the instruction block that makes the file usable by an
    /// agent that was not in the room. Rendered after the list and ignored by the parser.
    /// </summary>
    public string? Closing { get; init; }

    /// <summary>Just the ticked actions - the work order, as opposed to the record.</summary>
    public IEnumerable<ReviewAction> Accepted => Items.Where(i => i.Accepted).Select(i => i.Action);
}

/// <summary>
/// The on-disk form of a review (workplan task 43).
/// <para>
/// Markdown rather than JSON because two audiences read it: a person, who wants to see what was
/// found without a tool, and later GlassCoder itself, which needs to know which items to act on.
/// A ticked checkbox serves both - it is the ordinary Markdown idiom for "this one", and it
/// parses in one regex.
/// </para>
/// <para>
/// Every proposal is written, not only the accepted ones, with <c>[x]</c> marking the accepted.
/// The rejected ones are the context that explains the accepted ones, and a file where
/// everything is ticked would say nothing about what was considered and turned down.
/// </para>
/// </summary>
public static partial class ReviewActionFile
{
    /// <summary>Front-matter marker identifying a file review's work order.</summary>
    public const string Kind = "review-actions";

    /// <summary>Front-matter marker identifying a retrospective's work order (workplan task 67).</summary>
    public const string RetrospectiveKind = "retrospective-actions";

    /// <summary>Format version, so a future reader can tell what it is looking at.</summary>
    public const int Version = 1;

    /// <summary>Renders the plan as the Markdown document that goes on disk.</summary>
    public static string Render(ReviewActionPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        StringBuilder text = new();
        text.AppendLine("---");
        text.AppendLine(CultureInfo.InvariantCulture, $"glasscoder: {plan.Kind}");
        text.AppendLine(CultureInfo.InvariantCulture, $"version: {Version}");
        text.AppendLine(CultureInfo.InvariantCulture, $"file: {plan.File}");

        if (!string.IsNullOrWhiteSpace(plan.Target))
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"target: {plan.Target}");
        }

        if (!string.IsNullOrWhiteSpace(plan.RunId))
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"runId: {plan.RunId}");
        }

        text.AppendLine(CultureInfo.InvariantCulture, $"reviewedAt: {plan.ReviewedAt.UtcDateTime:yyyy-MM-ddTHH:mm:ssZ}");
        text.AppendLine("via: claude-code");
        text.AppendLine(CultureInfo.InvariantCulture, $"model: {plan.Model}");
        text.AppendLine(CultureInfo.InvariantCulture, $"costUsd: {plan.CostUsd.ToString("0.0000", CultureInfo.InvariantCulture)}");
        text.AppendLine("---");
        text.AppendLine();
        text.AppendLine(CultureInfo.InvariantCulture, $"# {plan.Heading ?? $"Review - {plan.File}"}");
        text.AppendLine();
        text.AppendLine(plan.Report.TrimEnd());
        text.AppendLine();
        text.AppendLine("## Actions");
        text.AppendLine();

        if (plan.Items.Count == 0)
        {
            text.AppendLine("_The reviewer proposed nothing._");
            AppendClosing(text, plan);
            return text.ToString();
        }

        text.AppendLine("<!-- Ticked items are the ones to act on. -->");
        text.AppendLine();

        foreach (ReviewActionItem item in plan.Items)
        {
            text.AppendLine(CultureInfo.InvariantCulture,
                $"- [{(item.Accepted ? "x" : " ")}] **{item.Action.Priority}** `{item.Action.Id}` - {OneLine(item.Action.Title)}");

            string detail = OneLine(item.Action.Detail);
            if (detail.Length > 0)
            {
                // Indented, so it belongs to the bullet in every Markdown renderer and the
                // parser can tell continuation from the next item without counting blank lines.
                text.AppendLine(CultureInfo.InvariantCulture, $"      {detail}");
            }
        }

        AppendClosing(text, plan);
        return text.ToString();
    }

    private static void AppendClosing(StringBuilder text, ReviewActionPlan plan)
    {
        if (string.IsNullOrWhiteSpace(plan.Closing))
        {
            return;
        }

        text.AppendLine();
        text.AppendLine(plan.Closing.TrimEnd());
    }

    /// <summary>
    /// Reads a document this class wrote. False when the front matter is missing or says it is
    /// something else - a permissive parser here would happily "read" an unrelated Markdown file
    /// and hand back an empty plan.
    /// </summary>
    public static bool TryParse(string? text, out ReviewActionPlan? plan)
    {
        plan = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        string[] lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        if (lines.Length == 0 || lines[0].Trim() != "---")
        {
            return false;
        }

        Dictionary<string, string> front = new(StringComparer.OrdinalIgnoreCase);
        int at = 1;
        for (; at < lines.Length && lines[at].Trim() != "---"; at++)
        {
            int colon = lines[at].IndexOf(':', StringComparison.Ordinal);
            if (colon > 0)
            {
                front[lines[at][..colon].Trim()] = lines[at][(colon + 1)..].Trim();
            }
        }

        if (at >= lines.Length ||
            !front.TryGetValue("glasscoder", out string? kind) ||
            !(string.Equals(kind, Kind, StringComparison.OrdinalIgnoreCase) ||
              string.Equals(kind, RetrospectiveKind, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        at++;

        List<ReviewActionItem> items = [];
        List<string> reportLines = [];
        bool inActions = false;

        for (; at < lines.Length; at++)
        {
            string line = lines[at];

            if (line.TrimStart().StartsWith("## Actions", StringComparison.OrdinalIgnoreCase))
            {
                inActions = true;
                continue;
            }

            // Any later heading ends the list. Without this, a closing instruction block would be
            // read as more of the last item's detail - the indented-continuation rule below has
            // no way to know that the list is over.
            if (inActions && line.StartsWith("## ", StringComparison.Ordinal))
            {
                break;
            }

            if (!inActions)
            {
                // The rendered heading is ours, not part of what the reviewer wrote.
                if (!(reportLines.Count == 0 && line.StartsWith("# Review - ", StringComparison.Ordinal)))
                {
                    reportLines.Add(line);
                }

                continue;
            }

            Match match = ActionLine().Match(line);
            if (match.Success)
            {
                items.Add(new ReviewActionItem(
                    new ReviewAction(
                        match.Groups["id"].Value,
                        match.Groups["title"].Value.Trim(),
                        string.Empty,
                        ParsePriority(match.Groups["priority"].Value)),
                    match.Groups["state"].Value is "x" or "X"));
                continue;
            }

            // An indented, non-empty line after an item is that item's detail.
            if (items.Count > 0 && line.Length > 0 && char.IsWhiteSpace(line[0]) && line.Trim().Length > 0)
            {
                ReviewActionItem last = items[^1];
                string detail = (last.Action.Detail.Length > 0 ? last.Action.Detail + " " : string.Empty) + line.Trim();
                items[^1] = last with { Action = last.Action with { Detail = detail } };
            }
        }

        plan = BuildPlan(front, reportLines, items, kind);
        return true;
    }

    private static ReviewActionPlan BuildPlan(
        Dictionary<string, string> front,
        List<string> reportLines,
        List<ReviewActionItem> items,
        string kind) =>
        new ReviewActionPlan(
            front.GetValueOrDefault("file", string.Empty),
            DateTimeOffset.TryParse(
                front.GetValueOrDefault("reviewedAt"),
                CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
                out DateTimeOffset when)
                ? when
                : default,
            front.GetValueOrDefault("model", string.Empty),
            decimal.TryParse(
                front.GetValueOrDefault("costUsd"),
                NumberStyles.Number,
                CultureInfo.InvariantCulture,
                out decimal cost)
                ? cost
                : 0m,
            string.Join(Environment.NewLine, reportLines).Trim(),
            items)
        {
            Kind = kind,
            Target = front.GetValueOrDefault("target"),
            RunId = front.GetValueOrDefault("runId"),
        };

    /// <summary>The file name a review is offered under: readable in the tree, and unique enough.</summary>
    public static string SuggestFileName(string displayPath, DateTimeOffset when)
    {
        ArgumentNullException.ThrowIfNull(displayPath);

        string name = displayPath.Replace('\\', '/').Split('/').LastOrDefault() ?? "review";
        foreach (char invalid in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(invalid, '-');
        }

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{name}-{when.UtcDateTime:yyyyMMdd-HHmmss}.md");
    }

    /// <summary>
    /// The file name a retrospective's work order is offered under: when it was taken, and
    /// nothing else.
    /// <para>
    /// The run id used to lead the timestamp. It was eight characters of hexadecimal that no
    /// reader could date, order or recognise, and the file says which run it is about three
    /// times over - <c>runId</c> and <c>file</c> in its own front matter, and the heading on its
    /// first line - so the name was carrying a fact the file already carries better.
    /// </para>
    /// <para>
    /// <paramref name="when"/> is formatted in whatever offset it arrives in, so the caller
    /// chooses the clock. The callers pass local time: this name is read by a person looking at
    /// a directory listing beside the wall clock they took the retrospective by, and a UTC name
    /// is an hour or two adrift of that for most of the world.
    /// </para>
    /// </summary>
    /// <param name="when">When the work order was written, in the offset it should be named for.</param>
    public static string SuggestRetrospectiveFileName(DateTimeOffset when) =>
        string.Create(CultureInfo.InvariantCulture, $"retro-{when:yyyyMMdd-HHmmss}.md");

    private static ReviewActionPriority ParsePriority(string value) =>
        Enum.TryParse(value, ignoreCase: true, out ReviewActionPriority priority)
            ? priority
            : ReviewActionPriority.Optional;

    /// <summary>Flattens a value onto one line, so it cannot break the list it sits in.</summary>
    private static string OneLine(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : WhitespaceRun().Replace(value.Trim(), " ");

    [GeneratedRegex(@"^\s*-\s\[(?<state>[ xX])\]\s+\*\*(?<priority>\w+)\*\*\s+`(?<id>[^`]+)`(?:\s*-\s*(?<title>.*))?$")]
    private static partial Regex ActionLine();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRun();
}

/// <summary>Writes an accepted review out as a Markdown work order.</summary>
public interface IReviewActionWriter
{
    /// <summary>
    /// Writes the plan under the configured review directory and returns the full path.
    /// <para>
    /// The write is a human action rather than an agent one, so it does not go through the path
    /// guard's writable allow-list - that guard exists to bound what the <em>model</em> may
    /// change. It is still confined to the workspace root, because a review of a file in this
    /// repository has no business landing anywhere else.
    /// </para>
    /// </summary>
    string Write(ReviewActionPlan plan);
}

/// <summary>Default <see cref="IReviewActionWriter"/>, writing under the workspace root.</summary>
public sealed class ReviewActionWriter : IReviewActionWriter
{
    private readonly IPathGuard _guard;
    private readonly FileReviewOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<ReviewActionWriter> _logger;

    /// <summary>Creates the writer.</summary>
    public ReviewActionWriter(
        IPathGuard guard,
        IOptions<FileReviewOptions> options,
        ILogger<ReviewActionWriter>? logger = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        _guard = guard;
        _options = options.Value;
        _time = timeProvider ?? TimeProvider.System;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<ReviewActionWriter>.Instance;
    }

    /// <inheritdoc />
    public string Write(ReviewActionPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(_guard.RepoRoot));
        string directory = Path.GetFullPath(Path.Combine(root, _options.OutputDirectory));

        if (!directory.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(directory, root, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"The review output directory '{_options.OutputDirectory}' resolves outside the workspace.");
        }

        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, ReviewActionFile.SuggestFileName(plan.File, _time.GetUtcNow()));

        File.WriteAllText(path, ReviewActionFile.Render(plan), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        _logger.LogInformation("Wrote {Count} review action(s) to {Path}", plan.Items.Count, path);
        return path;
    }
}
