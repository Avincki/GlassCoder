using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace GlassCoder.Wpf.Highlighting;

/// <summary>
/// Puts a coloured document into a <see cref="RichTextBox"/> from a binding.
/// <para>
/// <see cref="Views.FileViewerWindow"/> builds its documents in code-behind, which is right for
/// one window showing one file: a <see cref="FlowDocument"/> is a tree of objects rather than a
/// value, and constructing it beats the converters that binding one would need. A retrospective
/// shows three reports inside an <c>ItemsControl</c>, where there is no code-behind to hang that
/// on - so the same construction lives here, reached by a binding.
/// </para>
/// </summary>
public static class HighlightedText
{
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

    /// <summary>The coloured lines to render, one entry per line.</summary>
    public static readonly DependencyProperty LinesProperty = DependencyProperty.RegisterAttached(
        "Lines",
        typeof(IReadOnlyList<IReadOnlyList<HighlightedSpan>>),
        typeof(HighlightedText),
        new PropertyMetadata(null, OnLinesChanged));

    /// <summary>Reads the attached lines.</summary>
    /// <param name="element">The box the lines were attached to.</param>
    public static IReadOnlyList<IReadOnlyList<HighlightedSpan>>? GetLines(DependencyObject element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return (IReadOnlyList<IReadOnlyList<HighlightedSpan>>?)element.GetValue(LinesProperty);
    }

    /// <summary>Attaches lines to a box.</summary>
    /// <param name="element">The box to render into.</param>
    /// <param name="value">The coloured lines.</param>
    public static void SetLines(DependencyObject element, IReadOnlyList<IReadOnlyList<HighlightedSpan>>? value)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.SetValue(LinesProperty, value);
    }

    private static void OnLinesChanged(DependencyObject element, DependencyPropertyChangedEventArgs e)
    {
        if (element is not RichTextBox box)
        {
            return;
        }

        if (e.NewValue is not IReadOnlyList<IReadOnlyList<HighlightedSpan>> lines)
        {
            box.Document = new FlowDocument();
            return;
        }

        // One paragraph of runs separated by breaks, not a paragraph per line: the same rendering,
        // and the difference between one block element and a few thousand on a long report.
        Paragraph paragraph = new() { Margin = default };
        for (int at = 0; at < lines.Count; at++)
        {
            if (at > 0)
            {
                paragraph.Inlines.Add(new LineBreak());
            }

            foreach (HighlightedSpan span in lines[at])
            {
                paragraph.Inlines.Add(new Run(span.Text) { Foreground = Brush(box, span.Kind) });
            }
        }

        box.Document = new FlowDocument(paragraph)
        {
            PagePadding = default,
            LineStackingStrategy = LineStackingStrategy.MaxHeight,
        };
    }

    /// <summary>
    /// The brush for one token kind. Falls back to the box's own foreground rather than throwing:
    /// a missing resource key must not take down a window that is only trying to show prose.
    /// </summary>
    private static Brush Brush(RichTextBox box, SyntaxTokenKind kind) =>
        BrushKeys.TryGetValue(kind, out string? key) && box.TryFindResource(key) is Brush found
            ? found
            : box.Foreground;
}
