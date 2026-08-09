using System;
using System.Globalization;
using System.Windows.Data;

namespace GlassCoder.Wpf.Converters;

/// <summary>
/// Binds one radio button in a scale to the single score the view model holds.
/// <para>
/// The button is checked when the score equals its own <c>ConverterParameter</c>, and checking it
/// writes that number back. A parameter rather than a binding because <c>ConverterParameter</c> is
/// not a dependency property and cannot be bound - which is the whole reason the scale is six
/// spelled-out buttons in the XAML rather than an <c>ItemsControl</c> over a list.
/// </para>
/// </summary>
public sealed class RatingToCheckedConverter : IValueConverter
{
    /// <inheritdoc />
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is int score && Score(parameter) == score;

    /// <summary>
    /// The score a checked button stands for.
    /// <para>
    /// Only the button being switched <em>on</em> carries information. Choosing a new score also
    /// unchecks the old button, and that raises a second write with <c>false</c>; answering it
    /// with "no score" would clear the number the new selection had just set, on an ordering
    /// WPF does not promise. <see cref="Binding.DoNothing"/> is how a converter declines to
    /// answer at all.
    /// </para>
    /// </summary>
    /// <inheritdoc />
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true && Score(parameter) is { } score ? score : Binding.DoNothing;

    /// <summary>
    /// The parameter as a number. Parsed invariantly: it is authored in the XAML, not typed by
    /// anyone, so the operator's culture has no business deciding what "5" means.
    /// </summary>
    private static int? Score(object? parameter) => parameter switch
    {
        int direct => direct,
        string text when int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) =>
            parsed,
        _ => null,
    };
}
