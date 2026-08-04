using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using GlassCoder.Wpf.Highlighting;
using GlassCoder.Wpf.ViewModels;

namespace GlassCoder.Wpf.Views;

/// <summary>
/// A read-only look at one file from the workspace tree, coloured by
/// <see cref="SyntaxHighlighter"/>, with the reviewer's second opinion beside it.
/// <para>
/// The documents are built in code rather than bound. A <see cref="FlowDocument"/> is a tree of
/// <see cref="Inline"/> objects, not a value, and the binding needed to produce one per line
/// would cost more in converters and templates than the twenty lines of construction it
/// replaced - with the same result and less control over how many objects a large file makes.
/// </para>
/// </summary>
public partial class FileViewerWindow : Window
{
    /// <summary>
    /// Fixed on both the gutter and the document, so the two cannot disagree. Left to their
    /// defaults, a <see cref="TextBlock"/> and a <see cref="Paragraph"/> derive line height
    /// separately, and a fraction of a pixel of drift per line is a gutter that is a whole line
    /// out by the bottom of a long file.
    /// </summary>
    private const double CodeLineHeight = 17;

    /// <summary>
    /// How wide the review opens the first time one arrives. A fixed width rather than a star:
    /// the code keeps the space it had, and the splitter is there for anyone who disagrees.
    /// </summary>
    private const double ReviewColumnWidth = 430;

    private static readonly Dictionary<SyntaxTokenKind, string> BrushKeys = new()
    {
        [SyntaxTokenKind.Plain] = "SyntaxPlain",
        [SyntaxTokenKind.Keyword] = "SyntaxKeyword",
        [SyntaxTokenKind.Comment] = "SyntaxComment",
        [SyntaxTokenKind.StringLiteral] = "SyntaxString",
        [SyntaxTokenKind.Number] = "SyntaxNumber",
        [SyntaxTokenKind.Preprocessor] = "SyntaxPreprocessor",
        [SyntaxTokenKind.Tag] = "SyntaxTag",
        [SyntaxTokenKind.Attribute] = "SyntaxAttribute",
    };

    private readonly FileViewerViewModel _file;

    /// <summary>Creates the window over an already-loaded file.</summary>
    /// <param name="file">The file to show, read and coloured.</param>
    public FileViewerWindow(FileViewerViewModel file)
    {
        ArgumentNullException.ThrowIfNull(file);

        InitializeComponent();

        _file = file;
        DataContext = file;

        Title = file.DisplayPath;
        FullPath.Text = file.FullPath;
        FullPath.ToolTip = file.FullPath;
        Summary.Text = file.Summary;

        // Watched rather than bound: what changes is a document, and building one is exactly the
        // kind of view mechanics that belongs here (CLAUDE.md §14).
        file.PropertyChanged += OnFileChanged;
        Closed += (_, _) => file.PropertyChanged -= OnFileChanged;

        if (!file.HasContent)
        {
            Body.Visibility = Visibility.Collapsed;
            Notice.Visibility = Visibility.Visible;
            Notice.Text = file.Message;
            return;
        }

        Gutter.LineHeight = CodeLineHeight;
        Gutter.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
        Gutter.Text = BuildGutter(file.Lines.Count);

        Code.Document = BuildDocument(file.Lines, wrap: false);
        Loaded += OnLoaded;
    }

    private void OnFileChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(FileViewerViewModel.ReportLines):
                Report.Document = BuildDocument(_file.ReportLines, wrap: true);
                break;

            case nameof(FileViewerViewModel.HasReview) when _file.HasReview:
                OpenReviewPane();
                break;
        }
    }

    /// <summary>
    /// Gives the review its column the first time there is a review to put in it.
    /// <para>
    /// Left at zero until then, so a window opened just to read a file is all file. Only the
    /// first review moves it - after that the width is the operator's, and reviewing a second
    /// time must not undo where they dragged the splitter.
    /// </para>
    /// </summary>
    private void OpenReviewPane()
    {
        ReviewSplitter.Visibility = Visibility.Visible;

        if (ReviewColumn.Width.Value == 0)
        {
            ReviewColumn.Width = new GridLength(ReviewColumnWidth);
            ReviewColumn.MinWidth = 260;
        }
    }

    /// <summary>Right-aligned line numbers, one per line of the document.</summary>
    private static string BuildGutter(int lines)
    {
        StringBuilder text = new(lines * 5);
        for (int line = 1; line <= lines; line++)
        {
            if (line > 1)
            {
                text.Append('\n');
            }

            text.Append(line.ToString(CultureInfo.InvariantCulture));
        }

        return text.ToString();
    }

    /// <summary>
    /// Builds one coloured document.
    /// </summary>
    /// <param name="lines">The coloured spans, one entry per line.</param>
    /// <param name="wrap">
    /// Whether long lines wrap. Code does not - a wrapped line would occupy two rows while the
    /// gutter beside it advanced by one. Prose does, because there is no gutter to disagree with
    /// and a review that scrolls sideways is a review nobody reads.
    /// </param>
    private FlowDocument BuildDocument(IReadOnlyList<IReadOnlyList<HighlightedSpan>> lines, bool wrap)
    {
        Dictionary<SyntaxTokenKind, Brush> brushes = [];
        foreach ((SyntaxTokenKind kind, string key) in BrushKeys)
        {
            brushes[kind] = (Brush)FindResource(key);
        }

        // One paragraph of runs separated by breaks, not a paragraph per line. Same rendering,
        // and it is the difference between a few thousand block elements and one on a long file.
        Paragraph paragraph = new() { Margin = default };
        if (!wrap)
        {
            paragraph.LineHeight = CodeLineHeight;
        }

        int longest = 0;

        for (int i = 0; i < lines.Count; i++)
        {
            if (i > 0)
            {
                paragraph.Inlines.Add(new LineBreak());
            }

            int width = 0;
            foreach (HighlightedSpan span in lines[i])
            {
                paragraph.Inlines.Add(new Run(span.Text) { Foreground = brushes[span.Kind] });
                width += span.Text.Length;
            }

            longest = Math.Max(longest, width);
        }

        FlowDocument document = new(paragraph)
        {
            PagePadding = default,
            LineStackingStrategy = wrap ? LineStackingStrategy.MaxHeight : LineStackingStrategy.BlockLineHeight,
        };

        if (!wrap)
        {
            // Making the page wider than the longest line turns the wrap into a horizontal
            // scrollbar instead.
            document.PageWidth = (longest * MeasureCharacterWidth()) + 32;
        }

        return document;
    }

    /// <summary>
    /// The advance width of one character. Sound because the window is monospaced: measuring a
    /// single glyph gives the width of every glyph, so the longest line can be sized from its
    /// character count without laying out the document to find out.
    /// </summary>
    private double MeasureCharacterWidth()
    {
        FormattedText sample = new(
            "0",
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            new Typeface(FontFamily, FontStyle, FontWeight, FontStretch),
            FontSize,
            Brushes.Black,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);

        return sample.Width;
    }

    /// <summary>
    /// Ties the gutter to the code's scroll position. The offset is applied as a negative top
    /// margin inside a clipping border rather than by giving the gutter a ScrollViewer of its
    /// own: one scroller means there is nothing to fall out of step.
    /// </summary>
    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Code.ApplyTemplate();
        if (Code.Template.FindName("PART_ContentHost", Code) is ScrollViewer viewer)
        {
            viewer.ScrollChanged += OnCodeScrolled;
        }

        Code.Focus();
    }

    private void OnCodeScrolled(object sender, ScrollChangedEventArgs e) =>
        Gutter.Margin = new Thickness(0, -e.VerticalOffset, 0, 0);

    private void OnClose(object sender, ExecutedRoutedEventArgs e) => Close();
}
