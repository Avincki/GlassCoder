using System;
using System.Collections.Generic;
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
/// <see cref="SyntaxHighlighter"/>.
/// <para>
/// The document is built in code rather than bound. A <see cref="FlowDocument"/> is a tree of
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

    /// <summary>Creates the window over an already-loaded file.</summary>
    /// <param name="file">The file to show, read and coloured.</param>
    public FileViewerWindow(FileViewerViewModel file)
    {
        ArgumentNullException.ThrowIfNull(file);

        InitializeComponent();

        Title = file.DisplayPath;
        FullPath.Text = file.FullPath;
        FullPath.ToolTip = file.FullPath;
        Summary.Text = file.Summary;

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

        Code.Document = BuildDocument(file.Lines);
        Loaded += OnLoaded;
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

    private FlowDocument BuildDocument(IReadOnlyList<IReadOnlyList<HighlightedSpan>> lines)
    {
        Dictionary<SyntaxTokenKind, Brush> brushes = [];
        foreach ((SyntaxTokenKind kind, string key) in BrushKeys)
        {
            brushes[kind] = (Brush)FindResource(key);
        }

        // One paragraph of runs separated by breaks, not a paragraph per line. Same rendering,
        // and it is the difference between a few thousand block elements and one on a long file.
        Paragraph paragraph = new() { Margin = default, LineHeight = CodeLineHeight };
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

        return new FlowDocument(paragraph)
        {
            PagePadding = default,
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
            // Wrapping would break the gutter - one logical line would occupy two rows while the
            // numbers beside it advanced by one. Making the page wider than the longest line
            // turns the wrap into a horizontal scrollbar instead.
            PageWidth = (longest * MeasureCharacterWidth()) + 32,
        };
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
