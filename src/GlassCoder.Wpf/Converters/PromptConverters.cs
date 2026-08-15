using System;
using System.Globalization;
using System.Windows.Data;
using GlassCoder.Wpf.Services;

namespace GlassCoder.Wpf.Converters;

/// <summary>
/// A remembered prompt as the single line a dropdown row can hold.
/// <para>
/// The goal box accepts newlines, and a <c>TextBlock</c> honours them whether or not it wraps - so
/// without this a five-line prompt is a five-line row, and twenty of them are a dropdown taller
/// than the window. Trimming to the width belongs to the row itself
/// (<c>TextTrimming</c>), which is the only place the width is known.
/// </para>
/// </summary>
public sealed class PromptSummaryConverter : IValueConverter
{
    /// <inheritdoc />
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string goal ? PromptHistory.Summarize(goal) : string.Empty;

    /// <summary>One-way: a row is a view of a prompt, never a way to edit one.</summary>
    /// <inheritdoc />
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}
