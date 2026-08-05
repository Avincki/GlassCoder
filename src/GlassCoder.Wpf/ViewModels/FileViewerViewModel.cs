using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using GlassCoder.Core.Verification;
using GlassCoder.Wpf.Highlighting;
using GlassCoder.Wpf.Mvvm;
using GlassCoder.Wpf.Services;

namespace GlassCoder.Wpf.ViewModels;

/// <summary>One recommended change, and whether the operator wants it done.</summary>
public sealed class ReviewActionViewModel : ViewModelBase
{
    private bool _isAccepted;

    /// <summary>Wraps a reviewed action.</summary>
    /// <param name="action">What the reviewer proposed.</param>
    public ReviewActionViewModel(ReviewAction action)
    {
        ArgumentNullException.ThrowIfNull(action);

        Action = action;

        // Defects are ticked to begin with, everything else is not. The common case is "yes to
        // the bugs, let me read the rest", and starting from nothing ticked makes that four
        // clicks instead of none - while starting from everything ticked would quietly enrol
        // the operator in work they never read.
        _isAccepted = action.Priority == ReviewActionPriority.High;
    }

    /// <summary>The underlying proposal.</summary>
    public ReviewAction Action { get; }

    /// <summary>Short slug, shown as the action's handle.</summary>
    public string Id => Action.Id;

    /// <summary>What to do.</summary>
    public string Title => Action.Title;

    /// <summary>Why, and where.</summary>
    public string Detail => Action.Detail;

    /// <summary>How much it matters.</summary>
    public ReviewActionPriority Priority => Action.Priority;

    /// <summary>The priority as shown.</summary>
    public string PriorityLabel => Action.Priority.ToString();

    /// <summary>Whether the operator ticked this one.</summary>
    public bool IsAccepted
    {
        get => _isAccepted;
        set => SetProperty(ref _isAccepted, value);
    }
}

/// <summary>
/// One file, read and coloured for the viewer window, and what a reviewer made of it.
/// <para>
/// The file itself is read-only and read-once: nothing here writes to it, and nothing watches it
/// for changes - closing and reopening is the refresh, which is honest about the fact that this
/// is a snapshot. The review is the one thing on this window that reaches outside the process,
/// and it is only ever started by the button (workplan task 43).
/// </para>
/// </summary>
public sealed class FileViewerViewModel : ViewModelBase
{
    /// <summary>
    /// Past this, the file is refused outright. A viewer is not the tool for a hundred-megabyte
    /// artefact, and reading one into a string to find that out would be the slow way to fail.
    /// </summary>
    private const long MaximumBytes = 8 * 1024 * 1024;

    /// <summary>
    /// Past this, the file is shown without colouring. Scanning is linear and cheap, but the
    /// document it feeds is not: the runs become WPF objects, and a megabyte of them is a
    /// noticeable pause on a window that should open instantly.
    /// </summary>
    private const long MaximumHighlightedBytes = 1024 * 1024;

    /// <summary>How much of the head to inspect when deciding whether a file is text at all.</summary>
    private const int BinarySniffBytes = 8000;

    private readonly IFileReviewer? _reviewer;
    private readonly IReviewActionWriter? _writer;
    private readonly IDesktopShell? _shell;

    private bool _reviewAvailable;
    private string _reviewTooltip = "Checking whether a reviewer is available…";
    private bool _isReviewing;
    private string _reviewStatus = string.Empty;
    private string _instructions = string.Empty;
    private string? _exportedPath;
    private FileReview? _review;
    private IReadOnlyList<IReadOnlyList<HighlightedSpan>> _reportLines = [];
    private CancellationTokenSource? _cancellation;

    private FileViewerViewModel(
        string displayPath,
        string fullPath,
        string summary,
        string? message,
        IReadOnlyList<IReadOnlyList<HighlightedSpan>> lines,
        IFileReviewer? reviewer,
        IReviewActionWriter? writer,
        IDesktopShell? shell)
    {
        DisplayPath = displayPath;
        FullPath = fullPath;
        Summary = summary;
        Message = message;
        Lines = lines;
        _reviewer = reviewer;
        _writer = writer;
        _shell = shell;

        ReviewCommand = new RelayCommand(
            async () => await ReviewAsync().ConfigureAwait(true),
            () => ReviewAvailable && !IsReviewing);
        CancelReviewCommand = new RelayCommand(() => _cancellation?.Cancel(), () => IsReviewing);
        ExportCommand = new RelayCommand(Export, () => !IsReviewing && Actions.Any(a => a.IsAccepted));
        ShowExportCommand = new RelayCommand(ShowExport, () => _exportedPath is not null);

        if (_reviewer is null)
        {
            _reviewTooltip = "Reviewing is not available in this window.";
        }
        else
        {
            _ = InitialiseAsync();
        }
    }

    /// <summary>Repo-relative path, shown in the title bar.</summary>
    public string DisplayPath { get; }

    /// <summary>Absolute path, shown as the window's tooltip.</summary>
    public string FullPath { get; }

    /// <summary>Line count, language and size, for the status strip.</summary>
    public string Summary { get; }

    /// <summary>Why there is nothing to show, when there is nothing to show.</summary>
    public string? Message { get; }

    /// <summary>The coloured content, one entry per line.</summary>
    public IReadOnlyList<IReadOnlyList<HighlightedSpan>> Lines { get; }

    /// <summary>Whether <see cref="Lines"/> is worth rendering.</summary>
    public bool HasContent => Message is null;

    /// <summary>Optional direction for the reviewer - "look at the threading", say.</summary>
    public string Instructions
    {
        get => _instructions;
        set => SetProperty(ref _instructions, value);
    }

    /// <summary>Whether a reviewer answered its version probe.</summary>
    public bool ReviewAvailable
    {
        get => _reviewAvailable;
        private set => SetProperty(ref _reviewAvailable, value);
    }

    /// <summary>
    /// Why the button is enabled or disabled. A greyed-out control that does not say why is a
    /// bug report waiting to happen, and "the CLI is not installed" and "the feature is switched
    /// off" are different fixes.
    /// </summary>
    public string ReviewTooltip
    {
        get => _reviewTooltip;
        private set => SetProperty(ref _reviewTooltip, value);
    }

    /// <summary>Whether a review is in flight.</summary>
    public bool IsReviewing
    {
        get => _isReviewing;
        private set => SetProperty(ref _isReviewing, value);
    }

    /// <summary>What the review is doing, or what it last did.</summary>
    public string ReviewStatus
    {
        get => _reviewStatus;
        private set => SetProperty(ref _reviewStatus, value);
    }

    /// <summary>The review on screen, when there is one.</summary>
    public FileReview? Review
    {
        get => _review;
        private set
        {
            if (SetProperty(ref _review, value))
            {
                OnPropertyChanged(nameof(HasReview));
                OnPropertyChanged(nameof(ReviewHeadline));
            }
        }
    }

    /// <summary>Whether a review is on screen.</summary>
    public bool HasReview => Review is not null;

    /// <summary>What answered, how long it took and what it cost.</summary>
    public string ReviewHeadline
    {
        get
        {
            if (Review is not { } review)
            {
                return string.Empty;
            }

            return string.Create(
                CultureInfo.InvariantCulture,
                $"{review.Model} · {review.DurationMs / 1000:F1} s · ${review.EstimatedCostUsd:F4}");
        }
    }

    /// <summary>The report, coloured as Markdown.</summary>
    public IReadOnlyList<IReadOnlyList<HighlightedSpan>> ReportLines
    {
        get => _reportLines;
        private set => SetProperty(ref _reportLines, value);
    }

    /// <summary>The recommended actions, in the order the reviewer ranked them.</summary>
    public ObservableCollection<ReviewActionViewModel> Actions { get; } = [];

    /// <summary>Asks the reviewer what it makes of this file.</summary>
    public RelayCommand ReviewCommand { get; }

    /// <summary>Stops the review in flight.</summary>
    public RelayCommand CancelReviewCommand { get; }

    /// <summary>Writes the ticked actions out as a Markdown work order.</summary>
    public RelayCommand ExportCommand { get; }

    /// <summary>Opens the folder the work order was written to.</summary>
    public RelayCommand ShowExportCommand { get; }

    /// <summary>
    /// Reads and colours <paramref name="fullPath"/>. Every failure becomes a
    /// <see cref="Message"/> rather than an exception: this is opened by a double-click, and a
    /// double-click on an unreadable file should explain itself, not take the application down.
    /// </summary>
    /// <param name="fullPath">Absolute path to read.</param>
    /// <param name="displayPath">Repo-relative path, for the title.</param>
    /// <param name="reviewer">The reviewer behind the button. Null leaves the button off.</param>
    /// <param name="writer">Where accepted actions are written.</param>
    /// <param name="shell">Used only to reveal an exported file.</param>
    public static FileViewerViewModel Load(
        string fullPath,
        string displayPath,
        IFileReviewer? reviewer = null,
        IReviewActionWriter? writer = null,
        IDesktopShell? shell = null)
    {
        ArgumentNullException.ThrowIfNull(fullPath);
        ArgumentNullException.ThrowIfNull(displayPath);

        try
        {
            FileInfo info = new(fullPath);
            if (!info.Exists)
            {
                return Refused(displayPath, fullPath, "That file is no longer there.", reviewer, writer, shell);
            }

            if (info.Length > MaximumBytes)
            {
                return Refused(
                    displayPath,
                    fullPath,
                    $"Too large to open here ({Describe(info.Length)}). The limit is {Describe(MaximumBytes)}.",
                    reviewer,
                    writer,
                    shell);
            }

            if (LooksBinary(fullPath))
            {
                return Refused(
                    displayPath, fullPath, $"This looks like a binary file ({Describe(info.Length)}).",
                    reviewer, writer, shell);
            }

            string text = File.ReadAllText(fullPath, Encoding.UTF8);

            // Colouring is dropped rather than the file being refused: seeing a large file
            // uncoloured beats not seeing it.
            bool colour = info.Length <= MaximumHighlightedBytes;
            SyntaxLanguage language = colour ? SyntaxLanguageDetector.FromPath(fullPath) : SyntaxLanguage.None;
            IReadOnlyList<IReadOnlyList<HighlightedSpan>> lines = HighlightedDocument.Build(text, language);

            return new FileViewerViewModel(
                displayPath,
                fullPath,
                Summarise(lines.Count, info.Length, language, colour),
                message: null,
                lines,
                reviewer,
                writer,
                shell);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return Refused(displayPath, fullPath, $"Could not read the file: {ex.Message}", reviewer, writer, shell);
        }
    }

    /// <summary>
    /// Probes the reviewer once the window is up, so the button knows whether it can work before
    /// anyone presses it.
    /// </summary>
    private async Task InitialiseAsync()
    {
        if (_reviewer is null)
        {
            return;
        }

        try
        {
            ReviewerAvailability availability = await _reviewer.ProbeAsync().ConfigureAwait(true);
            ReviewAvailable = availability.IsAvailable;
            ReviewTooltip = availability.IsAvailable
                ? $"Review this file with Claude Code ({availability.Version}). It reads the file's callers " +
                  "and tests, and cannot change anything."
                : availability.Reason ?? "The reviewer is not available.";
        }
        catch (Exception ex)
        {
            ReviewAvailable = false;
            ReviewTooltip = $"The reviewer could not be probed: {ex.Message}";
        }
    }

    private async Task ReviewAsync()
    {
        if (_reviewer is null || IsReviewing)
        {
            return;
        }

        IsReviewing = true;
        Review = null;
        Actions.Clear();
        ReportLines = [];
        _exportedPath = null;
        ReviewStatus = "Reading the file and its surroundings…";

        _cancellation = new CancellationTokenSource();
        try
        {
            FileReview review = await _reviewer.ReviewAsync(
                new FileReviewRequest(DisplayPath) { Instructions = Instructions },
                _cancellation.Token).ConfigureAwait(true);

            Review = review;
            ReportLines = HighlightedDocument.Build(review.Report, SyntaxLanguage.Markdown);

            foreach (ReviewAction action in review.Actions)
            {
                Actions.Add(new ReviewActionViewModel(action));
            }

            ReviewStatus = review.Failure
                ?? string.Create(CultureInfo.InvariantCulture, $"{Actions.Count} recommended action(s).");
        }
        catch (OperationCanceledException)
        {
            ReviewStatus = "Cancelled.";
        }
        catch (Exception ex)
        {
            // A failed review must not take the viewer down with it - the file is still readable.
            ReviewStatus = $"The review failed: {ex.Message}";
        }
        finally
        {
            _cancellation?.Dispose();
            _cancellation = null;
            IsReviewing = false;
        }
    }

    /// <summary>
    /// Writes every proposal out, with the ticked ones marked. The rejected ones stay in the
    /// file because they are the context that explains the accepted ones.
    /// </summary>
    private void Export()
    {
        if (_writer is null || Review is not { } review)
        {
            return;
        }

        try
        {
            _exportedPath = _writer.Write(new ReviewActionPlan(
                DisplayPath,
                DateTimeOffset.UtcNow,
                review.Model,
                review.EstimatedCostUsd,
                review.Report,
                [.. Actions.Select(a => new ReviewActionItem(a.Action, a.IsAccepted))]));

            int accepted = Actions.Count(a => a.IsAccepted);
            ReviewStatus = string.Create(
                CultureInfo.InvariantCulture,
                $"{accepted} action(s) accepted · written to {_exportedPath}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            ReviewStatus = $"Could not write the actions: {ex.Message}";
        }
    }

    private void ShowExport()
    {
        if (_exportedPath is not null)
        {
            _shell?.OpenFolder(Path.GetDirectoryName(_exportedPath) ?? _exportedPath);
        }
    }

    private static FileViewerViewModel Refused(
        string displayPath,
        string fullPath,
        string message,
        IFileReviewer? reviewer,
        IReviewActionWriter? writer,
        IDesktopShell? shell) =>
        new(displayPath, fullPath, string.Empty, message, [], reviewer, writer, shell);

    private static string Summarise(int lines, long bytes, SyntaxLanguage language, bool coloured)
    {
        string name = language switch
        {
            SyntaxLanguage.CSharp => "C#",
            SyntaxLanguage.Xml => "XML",
            SyntaxLanguage.Json => "JSON",
            SyntaxLanguage.Markdown => "Markdown",
            _ => coloured ? "Plain text" : "Plain text - too large to colour",
        };

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{lines:N0} line(s) · {name} · {Describe(bytes)}");
    }

    private static string Describe(long bytes) => bytes switch
    {
        < 1024 => string.Create(CultureInfo.InvariantCulture, $"{bytes} B"),
        < 1024 * 1024 => string.Create(CultureInfo.InvariantCulture, $"{bytes / 1024.0:F1} KB"),
        _ => string.Create(CultureInfo.InvariantCulture, $"{bytes / (1024.0 * 1024.0):F1} MB"),
    };

    /// <summary>
    /// Whether the head of the file contains a NUL byte. Crude, and the same test <c>git</c>
    /// and <c>grep</c> use: text files do not contain one, and it is what stops a double-click
    /// on a PNG from filling the window with replacement characters.
    /// </summary>
    private static bool LooksBinary(string path)
    {
        using FileStream stream = File.OpenRead(path);
        Span<byte> head = stackalloc byte[BinarySniffBytes];
        int read = stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
        return head[..read].Contains((byte)0);
    }
}
