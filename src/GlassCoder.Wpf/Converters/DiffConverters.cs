using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using GlassCoder.Tools.Changes;

namespace GlassCoder.Wpf.Converters;

/// <summary>Shows an element only when a bound flag is true.</summary>
public sealed class BooleanToVisibilityConverter : IValueConverter
{
    /// <inheritdoc />
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? Visibility.Visible : Visibility.Collapsed;

    /// <inheritdoc />
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is Visibility.Visible;
}

/// <summary>Colours diff text by what happened to the line.</summary>
public sealed class DiffKindToBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush Added = new(Color.FromRgb(0x1B, 0x5E, 0x20));
    private static readonly SolidColorBrush Removed = new(Color.FromRgb(0xB0, 0x00, 0x20));
    private static readonly SolidColorBrush Context = new(Color.FromRgb(0x33, 0x33, 0x33));

    /// <inheritdoc />
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value switch
        {
            DiffKind.Added => Added,
            DiffKind.Removed => Removed,
            _ => Context,
        };

    /// <inheritdoc />
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Tints diff rows so additions and removals are separable at a glance.</summary>
public sealed class DiffKindToBackgroundConverter : IValueConverter
{
    private static readonly SolidColorBrush Added = new(Color.FromRgb(0xE8, 0xF5, 0xE9));
    private static readonly SolidColorBrush Removed = new(Color.FromRgb(0xFF, 0xEB, 0xEE));

    /// <inheritdoc />
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value switch
        {
            DiffKind.Added => Added,
            DiffKind.Removed => Removed,
            _ => Brushes.Transparent,
        };

    /// <inheritdoc />
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Turns a 0-to-1 fraction into a bar width, so the dashboard needs no chart library.</summary>
public sealed class FractionToWidthConverter : IValueConverter
{
    /// <inheritdoc />
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        double fraction = value is double d ? d : 0;
        double maximum = parameter is string text && double.TryParse(text, NumberStyles.Any, culture, out double parsed)
            ? parsed
            : 160;

        return Math.Max(1, Math.Min(1, fraction) * maximum);
    }

    /// <inheritdoc />
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
