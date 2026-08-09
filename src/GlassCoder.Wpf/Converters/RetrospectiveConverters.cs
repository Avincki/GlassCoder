using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using GlassCoder.Core.Verification;

namespace GlassCoder.Wpf.Converters;

/// <summary>
/// Colours a stage card by which of the three questions it answers.
/// <para>
/// The three arrive minutes apart, so the colour is the thing that says "this is a new one" from
/// across a desk, before any of the words are read. Grey belongs to the empty state that precedes
/// them all - so the pane goes grey, red, green, blue as the retrospective works through it, and
/// the colour is progress rather than judgement. None of these mean bad or good; a stage that
/// could not answer says so in its own amber box, which is the surface's existing word for that.
/// </para>
/// </summary>
public sealed class StageKindToBackgroundConverter : IValueConverter
{
    private static readonly SolidColorBrush Code = new(Color.FromRgb(0xFF, 0xEB, 0xEE));
    private static readonly SolidColorBrush Process = new(Color.FromRgb(0xE8, 0xF5, 0xE9));
    private static readonly SolidColorBrush Harness = new(Color.FromRgb(0xE7, 0xF1, 0xFB));

    /// <inheritdoc />
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value switch
        {
            RetrospectiveStageKind.Code => Code,
            RetrospectiveStageKind.Process => Process,
            _ => Harness,
        };

    /// <inheritdoc />
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// The border for a stage card, one step darker than its background.
/// <para>
/// A separate converter rather than a parameter on the one above, because a brush that is only
/// ever right beside its own background is the kind of pairing that goes wrong silently when one
/// of the two is changed.
/// </para>
/// </summary>
public sealed class StageKindToBorderConverter : IValueConverter
{
    private static readonly SolidColorBrush Code = new(Color.FromRgb(0xF1, 0xBF, 0xC3));
    private static readonly SolidColorBrush Process = new(Color.FromRgb(0xBF, 0xDF, 0xC2));
    private static readonly SolidColorBrush Harness = new(Color.FromRgb(0xB9, 0xD4, 0xEE));

    /// <inheritdoc />
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value switch
        {
            RetrospectiveStageKind.Code => Code,
            RetrospectiveStageKind.Process => Process,
            _ => Harness,
        };

    /// <inheritdoc />
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
