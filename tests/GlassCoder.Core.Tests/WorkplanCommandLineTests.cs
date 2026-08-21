using GlassCoder.Host;

namespace GlassCoder.Core.Tests;

/// <summary>
/// The <c>workplan</c> verb, and the promise that adding it changed nothing else (workplan task 79).
/// <para>
/// A plan is a way to drive this harness and never a thing it requires. Every other verb reaches
/// the agent loop without passing through a plan, and that has to stay assertable rather than
/// merely believed - which until now it was not, because nothing tested the command line at all.
/// </para>
/// </summary>
public sealed class WorkplanCommandLineTests
{
    [Fact]
    public void The_workplan_verb_takes_a_plan()
    {
        HostCommand command = CommandLine.Parse(["workplan", "--plan", "WORKPLAN.md"]);

        command.Verb.ShouldBe("workplan");
        command.PlanPath.ShouldBe("WORKPLAN.md");
        command.Error.ShouldBeNull();
    }

    [Fact]
    public void A_workplan_with_no_plan_is_refused_before_anything_starts()
    {
        CommandLine.Parse(["workplan"]).Error.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public void The_workplan_verb_takes_the_same_repo_and_config_as_every_other()
    {
        HostCommand command = CommandLine.Parse(
            ["workplan", "--plan", "p.md", "--repo", "C:/repo", "--config", "arm.json"]);

        command.RepoRoot.ShouldBe("C:/repo");
        command.ConfigPath.ShouldBe("arm.json");
        command.Error.ShouldBeNull();
    }

    [Theory]
    [InlineData("suite")]
    [InlineData("ablate")]
    [InlineData("fixtures")]
    [InlineData("tools")]
    [InlineData("help")]
    public void Every_other_verb_still_parses_with_no_plan_in_sight(string verb)
    {
        HostCommand command = CommandLine.Parse([verb]);

        command.Verb.ShouldBe(verb);
        command.Error.ShouldBeNull();
        command.PlanPath.ShouldBeNull();
    }

    [Fact]
    public void An_ordinary_run_still_needs_only_a_goal()
    {
        HostCommand command = CommandLine.Parse(["run", "--goal", "List the C# files."]);

        command.Error.ShouldBeNull();
        command.Goal.ShouldBe("List the C# files.");
        command.PlanPath.ShouldBeNull();
    }

    [Fact]
    public void An_ordinary_run_is_still_refused_without_a_goal()
    {
        CommandLine.Parse(["run"]).Error.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public void An_unknown_verb_is_still_an_unknown_verb()
    {
        CommandLine.Parse(["workplans"]).Error.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public void The_usage_text_names_the_new_verb_and_keeps_the_old_ones()
    {
        // The help output is what GlassContext's availability check reads, and what a person
        // reaches for first. A verb that exists and is undocumented may as well not.
        CommandLine.Usage.ShouldContain("glasscoder workplan --plan");
        CommandLine.Usage.ShouldContain("glasscoder run");
        CommandLine.Usage.ShouldContain("glasscoder suite");
        CommandLine.Usage.ShouldContain("glasscoder ablate");
    }
}
