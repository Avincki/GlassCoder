using GlassCoder.TestSupport;
using GlassCoder.Tools.Build;
using GlassCoder.Tools.Execution;
using GlassCoder.Tools.Verification;
using Microsoft.Extensions.Options;

namespace GlassCoder.Tools.Tests;

/// <summary>
/// Discovering tests before running them (workplan task 51).
/// <para>
/// <c>run_tests</c> has taken a <c>--filter</c> since task 17, so the harness could already run a
/// subset - what was missing was any way to learn what the subset should be. The list comes from
/// the runner rather than from scanning for attributes, because an attribute scan is cheaper and
/// wrong: it misses custom frameworks, theory expansions and anything generated.
/// </para>
/// </summary>
public sealed class TestDiscoveryTests : IDisposable
{
    /// <summary>Real <c>dotnet test --list-tests</c> output, build chatter included.</summary>
    private const string ListOutput = """
        Test run for C:\repo\tests\Demo.Tests\bin\Debug\net10.0\Demo.Tests.dll (.NETCoreApp,Version=v10.0)
        The following Tests are available:
            Demo.Tests.PagerTests.A_page_holds_ten_items
            Demo.Tests.PagerTests.The_last_page_may_be_short
            Demo.Tests.PagerTests.Sizes(size: 3)
            Demo.Tests.WidgetTests.A_widget_scales
        """;

    private readonly TempWorkspace _workspace = new();

    public void Dispose() => _workspace.Dispose();

    [Fact]
    public void The_names_are_read_out_of_the_runner_s_own_list()
    {
        IReadOnlyList<string> names = TestOutputParser.ParseDiscovered(ListOutput);

        names.Count.ShouldBe(4);
        names[0].ShouldBe("Demo.Tests.PagerTests.A_page_holds_ten_items");
        names.ShouldContain("Demo.Tests.PagerTests.Sizes(size: 3)", "a theory expansion is a test the runner would run");
    }

    [Fact]
    public void Build_chatter_before_the_list_is_not_mistaken_for_a_test()
    {
        // The line above the header is a path with dots in it, which is exactly what a test name
        // looks like. The header is what separates them.
        TestOutputParser.ParseDiscovered(ListOutput)
            .ShouldNotContain(name => name.Contains("Demo.Tests.dll", StringComparison.Ordinal));
    }

    [Fact]
    public void Output_with_no_list_in_it_discovers_nothing()
    {
        TestOutputParser.ParseDiscovered("error MSB1003: Specify a project or solution file.").ShouldBeEmpty();
        TestOutputParser.ParseDiscovered(null).ShouldBeEmpty();
    }

    [Fact]
    public async Task Listing_asks_the_runner_to_list_rather_than_run()
    {
        ScriptedCommandExecutor executor = new();
        executor.Enqueue(0, ListOutput);
        _workspace.CreateDirectory("tests");

        ToolObservation<TestRunResult> observation = await Tool(executor).RunTestsAsync("tests", listOnly: true);

        observation.Ok.ShouldBeTrue(observation.Error?.Message);
        executor.Commands.Single().Arguments.ShouldContain("--list-tests");
        observation.Data!.Tests!.Count.ShouldBe(4);
        observation.Data.Total.ShouldBe(4);
        observation.Data.Passed.ShouldBe(0, "nothing ran");
    }

    [Fact]
    public async Task A_long_list_is_capped_and_says_the_true_total()
    {
        // Same contract as the diagnostic summariser: say how many there are, then show a bounded
        // number of them. Four hundred names is not orientation, it is the context window.
        ScriptedCommandExecutor executor = new();
        executor.Enqueue(0, "The following Tests are available:\n" +
            string.Join("\n", Enumerable.Range(1, 250).Select(i => $"    Demo.Tests.Case{i}.Works")));
        _workspace.CreateDirectory("tests");

        ToolObservation<TestRunResult> observation = await Tool(executor).RunTestsAsync("tests", listOnly: true);

        observation.Data!.Total.ShouldBe(250);
        observation.Data.Tests!.Count.ShouldBe(100);
        observation.Data.Truncated.ShouldBeTrue();
        observation.Summary.ShouldContain("250");
    }

    [Fact]
    public async Task A_discovery_that_could_not_build_says_so_rather_than_reporting_no_tests()
    {
        // Exit code alone does not separate "no tests here" from "the build failed", and the
        // agent needs to know which.
        ScriptedCommandExecutor executor = new();
        executor.Enqueue(1, "error MSB1003: Specify a project or solution file.");
        _workspace.CreateDirectory("tests");

        ToolObservation<TestRunResult> observation = await Tool(executor).RunTestsAsync("tests", listOnly: true);

        observation.Data!.Tests.ShouldBeEmpty();
        observation.Summary.ShouldContain("Could not list");
        observation.Data.Output.ShouldContain("MSB1003");
    }

    [Fact]
    public async Task A_filter_narrows_the_discovery_too()
    {
        ScriptedCommandExecutor executor = new();
        executor.Enqueue(0, ListOutput);
        _workspace.CreateDirectory("tests");

        await Tool(executor).RunTestsAsync("tests", "FullyQualifiedName~PagerTests", listOnly: true);

        IReadOnlyList<string> arguments = executor.Commands.Single().Arguments;
        arguments.ShouldContain("--filter");
        arguments.ShouldContain("--list-tests");
    }

    private RunTestsTool Tool(ScriptedCommandExecutor executor) =>
        new(executor, _workspace.Guard(), Options.Create(new SandboxOptions()));
}
