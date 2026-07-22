namespace GlassCoder.Lab.TaskSuite;

/// <summary>
/// The eight tasks from CLAUDE.md §16, ordered by the skill they stress (workplan task 21).
/// <para>
/// Each fixture is a self-contained console project with <b>no package references</b>, whose
/// <c>Main</c> asserts the required behaviour and returns a non-zero exit code when it does not
/// hold. That is the oracle: no human grading, no test framework to restore, and it runs
/// identically inside the sandbox with the network dropped.
/// </para>
/// <para>
/// The fixtures live here as text rather than as files on disk so the suite is hermetic - every
/// run starts from a byte-identical repository, which is what makes two ablation arms
/// comparable at all.
/// </para>
/// </summary>
public static class TaskSuiteDefinition
{
    private const string Csproj = """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <OutputType>Exe</OutputType>
            <TargetFramework>net10.0</TargetFramework>
            <Nullable>enable</Nullable>
            <ImplicitUsings>enable</ImplicitUsings>
            <AssemblyName>Fixture</AssemblyName>
            <RootNamespace>Fixture</RootNamespace>
          </PropertyGroup>
        </Project>
        """;

    private const string Harness = """
        namespace Fixture;

        /// <summary>Minimal assertion harness. Exit code 0 means every check held.</summary>
        public static class Check
        {
            private static int _failures;

            public static void That(bool condition, string what)
            {
                if (condition)
                {
                    Console.WriteLine($"  pass  {what}");
                    return;
                }

                _failures++;
                Console.WriteLine($"  FAIL  {what}");
            }

            public static void Equal<T>(T expected, T actual, string what) =>
                That(EqualityComparer<T>.Default.Equals(expected, actual), $"{what} (expected {expected}, got {actual})");

            public static int Exit()
            {
                Console.WriteLine(_failures == 0 ? "ALL TESTS PASSED" : $"{_failures} TEST(S) FAILED");
                return _failures == 0 ? 0 : 1;
            }
        }
        """;

    /// <summary>The suite, in the order it should be run.</summary>
    public static IReadOnlyList<SuiteTask> All { get; } =
    [
        new SuiteTask(
            "suite-01-failing-test",
            1,
            "Make one failing unit test pass",
            "Navigation and local reasoning",
            "The test suite in this repository is failing. Run it, find the one failing check, fix the "
            + "source so it passes, and prove it by running the tests again. Do not change the tests.",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Fixture.csproj"] = Csproj,
                ["Check.cs"] = Harness,
                ["Greeter.cs"] = """
                    namespace Fixture;

                    public static class Greeter
                    {
                        public static string Greet(string name) => $"Hi, {name}!";
                    }
                    """,
                ["Program.cs"] = """
                    using Fixture;

                    Check.Equal("Hello, Ada!", Greeter.Greet("Ada"), "Greeter greets with Hello");
                    return Check.Exit();
                    """,
            }),

        new SuiteTask(
            "suite-02-documented-empty",
            2,
            "Implement a documented but empty function",
            "Reasoning from a specification",
            "The method Statistics.Median is documented but not implemented. Implement it so that it "
            + "matches its documentation, then run the tests to prove it.",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Fixture.csproj"] = Csproj,
                ["Check.cs"] = Harness,
                ["Statistics.cs"] = """
                    namespace Fixture;

                    public static class Statistics
                    {
                        /// <summary>
                        /// Returns the median of the values: the middle value once sorted, or the mean of the
                        /// two middle values when the count is even. Throws ArgumentException when empty.
                        /// The input must not be modified.
                        /// </summary>
                        public static double Median(IReadOnlyList<double> values)
                        {
                            throw new NotImplementedException();
                        }
                    }
                    """,
                ["Program.cs"] = """
                    using Fixture;

                    Check.Equal(2d, Statistics.Median([3, 1, 2]), "odd count takes the middle value");
                    Check.Equal(2.5d, Statistics.Median([4, 1, 2, 3]), "even count averages the middle pair");
                    Check.Equal(7d, Statistics.Median([7]), "single value");

                    double[] source = [3, 1, 2];
                    Statistics.Median(source);
                    Check.Equal(3d, source[0], "the input is not reordered");

                    try
                    {
                        Statistics.Median([]);
                        Check.That(false, "empty input throws ArgumentException");
                    }
                    catch (ArgumentException)
                    {
                        Check.That(true, "empty input throws ArgumentException");
                    }

                    return Check.Exit();
                    """,
            }),

        new SuiteTask(
            "suite-03-off-by-one",
            3,
            "Find and fix an off-by-one bug",
            "Debugging",
            "A regression test is failing because of an off-by-one error. Find it, fix it, and run the "
            + "tests to prove the fix.",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Fixture.csproj"] = Csproj,
                ["Check.cs"] = Harness,
                ["Pager.cs"] = """
                    namespace Fixture;

                    public sealed class Pager
                    {
                        public Pager(int itemCount, int pageSize)
                        {
                            ItemCount = itemCount;
                            PageSize = pageSize;
                        }

                        public int ItemCount { get; }

                        public int PageSize { get; }

                        /// <summary>Number of pages needed to show every item.</summary>
                        public int PageCount => ItemCount / PageSize;
                    }
                    """,
                ["Program.cs"] = """
                    using Fixture;

                    Check.Equal(3, new Pager(30, 10).PageCount, "exact fit needs no extra page");
                    Check.Equal(4, new Pager(31, 10).PageCount, "a partial page still counts");
                    Check.Equal(1, new Pager(1, 10).PageCount, "one item is one page");
                    Check.Equal(0, new Pager(0, 10).PageCount, "no items is no pages");
                    return Check.Exit();
                    """,
            }),

        new SuiteTask(
            "suite-04-thread-parameter",
            4,
            "Thread a new parameter through a call chain",
            "Multi-file editing",
            "Add a 'culture' parameter of type string to Formatter.Format, thread it through every caller "
            + "in this repository, and use it in the formatting. The tests describe the expected behaviour. "
            + "Run them to prove the change.",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Fixture.csproj"] = Csproj,
                ["Check.cs"] = Harness,
                ["Formatter.cs"] = """
                    namespace Fixture;

                    public static class Formatter
                    {
                        public static string Format(decimal amount) => $"{amount:0.00}";
                    }
                    """,
                ["Report.cs"] = """
                    namespace Fixture;

                    public static class Report
                    {
                        public static string Line(string label, decimal amount) => $"{label}: {Formatter.Format(amount)}";
                    }
                    """,
                ["Program.cs"] = """
                    using Fixture;

                    // Format and Line must both take a culture, and "de" must use a comma separator.
                    Check.Equal("1.50", Formatter.Format(1.5m, "en"), "en formats with a dot");
                    Check.Equal("1,50", Formatter.Format(1.5m, "de"), "de formats with a comma");
                    Check.Equal("Total: 1,50", Report.Line("Total", 1.5m, "de"), "the culture reaches the caller");
                    return Check.Exit();
                    """,
            }),

        new SuiteTask(
            "suite-05-refactor",
            5,
            "Refactor a function with behaviour unchanged",
            "Restraint",
            "Refactor Calculator.Apply so it no longer uses a chain of if statements - a switch expression "
            + "or a dictionary is fine. Behaviour must not change. Run the tests to prove nothing broke.",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Fixture.csproj"] = Csproj,
                ["Check.cs"] = Harness,
                ["Calculator.cs"] = """
                    namespace Fixture;

                    public static class Calculator
                    {
                        public static double Apply(string op, double left, double right)
                        {
                            if (op == "add") { return left + right; }
                            if (op == "sub") { return left - right; }
                            if (op == "mul") { return left * right; }
                            if (op == "div") { return right == 0 ? double.NaN : left / right; }
                            throw new ArgumentException($"Unknown operator '{op}'.", nameof(op));
                        }
                    }
                    """,
                ["Program.cs"] = """
                    using Fixture;

                    Check.Equal(5d, Calculator.Apply("add", 2, 3), "add");
                    Check.Equal(-1d, Calculator.Apply("sub", 2, 3), "sub");
                    Check.Equal(6d, Calculator.Apply("mul", 2, 3), "mul");
                    Check.Equal(2d, Calculator.Apply("div", 6, 3), "div");
                    Check.That(double.IsNaN(Calculator.Apply("div", 1, 0)), "divide by zero is NaN");

                    try
                    {
                        Calculator.Apply("pow", 2, 3);
                        Check.That(false, "an unknown operator throws");
                    }
                    catch (ArgumentException)
                    {
                        Check.That(true, "an unknown operator throws");
                    }

                    return Check.Exit();
                    """,
            },
            StartsGreen: true),

        new SuiteTask(
            "suite-06-wire-module",
            6,
            "Wire a new module into the build",
            "Build and configuration knowledge",
            "The file Modules/Slugger.cs exists but is excluded from the build, so the code does not "
            + "compile. Include it properly and make the build and tests pass.",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Fixture.csproj"] = """
                    <Project Sdk="Microsoft.NET.Sdk">
                      <PropertyGroup>
                        <OutputType>Exe</OutputType>
                        <TargetFramework>net10.0</TargetFramework>
                        <Nullable>enable</Nullable>
                        <ImplicitUsings>enable</ImplicitUsings>
                        <AssemblyName>Fixture</AssemblyName>
                        <RootNamespace>Fixture</RootNamespace>
                      </PropertyGroup>
                      <ItemGroup>
                        <Compile Remove="Modules/**/*.cs" />
                      </ItemGroup>
                    </Project>
                    """,
                ["Check.cs"] = Harness,
                ["Modules/Slugger.cs"] = """
                    namespace Fixture;

                    public static class Slugger
                    {
                        public static string Slug(string title) =>
                            string.Join('-', title.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries));
                    }
                    """,
                ["Program.cs"] = """
                    using Fixture;

                    Check.Equal("hello-world", Slugger.Slug("Hello World"), "slug lowercases and joins");
                    return Check.Exit();
                    """,
            }),

        new SuiteTask(
            "suite-07-feature-three-files",
            7,
            "Add a feature spanning three files",
            "Long-horizon planning",
            "Add discount support to the basket: a Discount type with a percentage, Basket.ApplyDiscount, "
            + "and Receipt.Render showing the discounted total. The tests describe exactly what is "
            + "expected. Plan the work first, then run the tests to prove it.",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Fixture.csproj"] = Csproj,
                ["Check.cs"] = Harness,
                ["Basket.cs"] = """
                    namespace Fixture;

                    public sealed class Basket
                    {
                        private readonly List<decimal> _items = [];

                        public void Add(decimal price) => _items.Add(price);

                        public decimal Total => _items.Sum();
                    }
                    """,
                ["Receipt.cs"] = """
                    namespace Fixture;

                    public static class Receipt
                    {
                        public static string Render(Basket basket) => $"Total: {basket.Total:0.00}";
                    }
                    """,
                ["Program.cs"] = """
                    using Fixture;

                    Basket basket = new();
                    basket.Add(10m);
                    basket.Add(30m);

                    Check.Equal(40m, basket.Total, "total before discount");

                    basket.ApplyDiscount(new Discount(25));
                    Check.Equal(30m, basket.Total, "25 percent off 40 is 30");
                    Check.Equal("Total: 30.00", Receipt.Render(basket), "the receipt shows the discounted total");

                    return Check.Exit();
                    """,
            }),

        new SuiteTask(
            "suite-08-reproduce-then-fix",
            8,
            "Reproduce a bug from a description, then fix it",
            "Comprehension and reproduction",
            "BUG REPORT: 'Trimmer.Collapse leaves a trailing space when the input ends with several "
            + "spaces, and it does not handle tabs.' First add a check to Program.cs that reproduces the "
            + "bug and watch it fail, then fix Trimmer so every check passes.",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Fixture.csproj"] = Csproj,
                ["Check.cs"] = Harness,
                ["Trimmer.cs"] = """
                    namespace Fixture;

                    public static class Trimmer
                    {
                        /// <summary>Collapses runs of whitespace into single spaces and trims the ends.</summary>
                        public static string Collapse(string text)
                        {
                            string[] parts = text.Split(' ');
                            List<string> kept = [];
                            foreach (string part in parts)
                            {
                                if (part.Length > 0)
                                {
                                    kept.Add(part);
                                }
                            }

                            return string.Join(' ', kept) + (text.EndsWith("  ") ? " " : string.Empty);
                        }
                    }
                    """,
                ["Program.cs"] = """
                    using Fixture;

                    Check.Equal("a b", Trimmer.Collapse("a  b"), "runs of spaces collapse");
                    Check.Equal("a b", Trimmer.Collapse("  a b  "), "the ends are trimmed");
                    return Check.Exit();
                    """,
            }),
    ];

    /// <summary>Looks a task up by id.</summary>
    public static SuiteTask? Find(string id) =>
        All.FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase));
}
