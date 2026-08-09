using System.Globalization;
using System.Windows.Data;
using GlassCoder.Wpf.Converters;

namespace GlassCoder.Wpf.Tests;

/// <summary>
/// The binding behind the 0-to-5 row. Six radio buttons share one nullable score, so the whole
/// scale is one converter used six times with a different parameter.
/// </summary>
public sealed class RatingConverterTests
{
    private static readonly RatingToCheckedConverter Converter = new();

    [Theory]
    [InlineData(0, "0", true)]
    [InlineData(5, "5", true)]
    [InlineData(3, "4", false)]
    [InlineData(0, "5", false)]
    public void Only_the_button_holding_the_score_is_checked(int score, string parameter, bool expected) =>
        Convert(score, parameter).ShouldBe(expected);

    [Fact]
    public void No_score_checks_nothing()
    {
        // What the strip looks like when it opens: AppRating is set to null for each new
        // application, and that has to clear every button rather than leave the last one lit.
        foreach (string parameter in (string[])["0", "1", "2", "3", "4", "5"])
        {
            Convert(null, parameter).ShouldBe(false, $"button {parameter} must be clear");
        }
    }

    [Fact]
    public void Checking_a_button_writes_its_number_back()
    {
        Converter.ConvertBack(true, typeof(int?), "4", CultureInfo.CurrentCulture).ShouldBe(4);
    }

    [Fact]
    public void Unchecking_declines_to_write_rather_than_clearing_the_score()
    {
        // The trap this exists for. Choosing a new score unchecks the old button, which raises a
        // second write carrying false; answering it with null would erase the number the new
        // selection had just set, on an ordering WPF does not promise.
        Converter.ConvertBack(false, typeof(int?), "4", CultureInfo.CurrentCulture)
            .ShouldBe(Binding.DoNothing);
    }

    [Fact]
    public void A_parameter_that_is_not_a_score_is_not_an_answer()
    {
        Convert(3, "three").ShouldBe(false);
        Converter.ConvertBack(true, typeof(int?), "three", CultureInfo.CurrentCulture)
            .ShouldBe(Binding.DoNothing);
    }

    [Fact]
    public void The_parameter_is_read_the_same_way_in_every_culture()
    {
        // Authored in the XAML, not typed by anyone - so the operator's culture has no say in
        // what "5" means.
        Convert(5, "5", new CultureInfo("de-DE")).ShouldBe(true);
        Converter.ConvertBack(true, typeof(int?), "5", new CultureInfo("de-DE")).ShouldBe(5);
    }

    private static object Convert(int? score, string parameter, CultureInfo? culture = null) =>
        Converter.Convert(score, typeof(bool), parameter, culture ?? CultureInfo.CurrentCulture);
}
