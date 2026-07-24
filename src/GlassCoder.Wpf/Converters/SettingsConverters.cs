using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using GlassCoder.Models;

namespace GlassCoder.Wpf.Converters;

/// <summary>
/// Text-box editing for a nullable number, where empty means "leave it to the server".
/// <para>
/// The default binding converter cannot express that: it parses an empty box as a failed
/// conversion, silently keeps the old value, and the operator is left unable to clear a seed or
/// a cost ceiling they set by accident.
/// </para>
/// </summary>
public sealed class NullableNumberConverter : IValueConverter
{
    /// <inheritdoc />
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is null ? string.Empty : System.Convert.ToString(value, culture) ?? string.Empty;

    /// <inheritdoc />
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        string text = (value as string ?? string.Empty).Trim();
        Type target = Nullable.GetUnderlyingType(targetType) ?? targetType;

        if (text.Length == 0)
        {
            return Nullable.GetUnderlyingType(targetType) is null
                ? Binding.DoNothing
                : null;
        }

        try
        {
            return System.Convert.ChangeType(text, target, culture);
        }
        catch (Exception ex) when (ex is FormatException or OverflowException or InvalidCastException)
        {
            // Keep the last good value rather than writing nonsense into the configuration.
            return Binding.DoNothing;
        }
    }
}

/// <summary>Colours a connection check by how it went.</summary>
public sealed class ConnectionOutcomeToBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush Ok = new(Color.FromRgb(0x1B, 0x5E, 0x20));
    private static readonly SolidColorBrush Warning = new(Color.FromRgb(0x8D, 0x6E, 0x00));
    private static readonly SolidColorBrush Failed = new(Color.FromRgb(0xB0, 0x00, 0x20));
    private static readonly SolidColorBrush Unknown = new(Color.FromRgb(0x54, 0x6E, 0x7A));

    /// <inheritdoc />
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value switch
        {
            ConnectionCheckOutcome.Ok => Ok,
            ConnectionCheckOutcome.Warning => Warning,
            ConnectionCheckOutcome.Failed => Failed,
            _ => Unknown,
        };

    /// <inheritdoc />
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Shows an element only when a bound flag is false - the other half of a toggle.</summary>
public sealed class InverseBooleanToVisibilityConverter : IValueConverter
{
    /// <inheritdoc />
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;

    /// <inheritdoc />
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is not System.Windows.Visibility.Visible;
}

/// <summary>Shows an element only when a collection or string has something in it.</summary>
public sealed class NotEmptyToVisibilityConverter : IValueConverter
{
    /// <inheritdoc />
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool any = value switch
        {
            null => false,
            string text => !string.IsNullOrWhiteSpace(text),
            System.Collections.ICollection collection => collection.Count > 0,
            _ => true,
        };

        return any ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
    }

    /// <inheritdoc />
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
