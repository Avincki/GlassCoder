using GlassCoder.Tools.Processes;

namespace GlassCoder.Tools.Tests;

/// <summary>
/// The probe as the model writes it. One string, three verbs, and a hard ceiling on how much of a
/// live application a launch may drive.
/// <para>
/// The shape is a budget decision as much as a usability one - a list-of-objects parameter would
/// have cost several hundred characters on every request of every run - so what matters here is
/// that the terse form is unambiguous, that a malformed step costs a sentence rather than the
/// launch, and that the cap is real.
/// </para>
/// </summary>
public sealed class UiProbeScriptTests
{
    [Fact]
    public void The_three_verbs_read_as_themselves()
    {
        UiProbeScript script = UiProbeScript.Parse("Celsius=100; Convert!; Fahrenheit?");

        script.Steps.Count.ShouldBe(3);
        script.Steps[0].ShouldBe(new UiProbeStep(UiProbeAction.Set, "Celsius", "100"));
        script.Steps[1].ShouldBe(new UiProbeStep(UiProbeAction.Invoke, "Convert", null));
        script.Steps[2].ShouldBe(new UiProbeStep(UiProbeAction.Read, "Fahrenheit", null));
        script.Problem.ShouldBeNull();
    }

    [Fact]
    public void A_bare_name_is_read_rather_than_refused()
    {
        // The commonest thing a model will write when it means "and what does this say now".
        // Guessing wrong reports one extra fact; refusing reports none.
        UiProbeScript script = UiProbeScript.Parse("Fahrenheit");

        script.Steps.ShouldHaveSingleItem().ShouldBe(new UiProbeStep(UiProbeAction.Read, "Fahrenheit", null));
    }

    [Fact]
    public void A_typed_value_keeps_its_spaces_and_its_signs()
    {
        UiProbeScript script = UiProbeScript.Parse("Celsius=-40.5");

        script.Steps.ShouldHaveSingleItem().Value.ShouldBe("-40.5");
    }

    [Fact]
    public void Nothing_asked_for_is_nothing_run()
    {
        UiProbeScript.Parse(null).Steps.ShouldBeEmpty();
        UiProbeScript.Parse("   ").Steps.ShouldBeEmpty();
        UiProbeScript.Parse(null).Problem.ShouldBeNull("silence is not a complaint");
    }

    [Fact]
    public void The_cap_is_real_and_says_it_was_applied()
    {
        // A launch drives a live application inside a timeout. Anything longer than a handful of
        // fields belongs in a test that can be re-run without a window - and a silent truncation
        // would read as "all of it ran".
        UiProbeScript script = UiProbeScript.Parse(string.Join(';', Enumerable.Range(0, 20).Select(n => $"Box{n}?")));

        script.Steps.Count.ShouldBe(UiProbeScript.MaxSteps);
        script.Problem.ShouldNotBeNull().ShouldContain("first 6 steps");
    }

    [Fact]
    public void A_step_that_makes_no_sense_costs_a_sentence_not_the_launch()
    {
        UiProbeScript script = UiProbeScript.Parse("=100; Fahrenheit?");

        script.Steps.ShouldHaveSingleItem().Element.ShouldBe("Fahrenheit");
        script.Problem.ShouldNotBeNull().ShouldContain("is not a step");
    }
}
