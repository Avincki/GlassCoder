using GlassCoder.Tools.Guardrails;
using GlassCoder.Tools.Verification;
using GlassCoder.TestSupport;
using Microsoft.Extensions.Options;

namespace GlassCoder.Tools.Tests;

/// <summary>
/// Whether a green suite exercises the product at all (workplan task 66).
/// <para>
/// Three consecutive desktop runs shipped a suite that verified nothing, in three shapes, and
/// every existing oracle waved all three through. What is defended here is not that the notice is
/// right - it is a question, not a verdict - but that it fires on each of the three shapes and
/// stays quiet on the suites that are fine, because a notice that cries wolf is worse than none.
/// </para>
/// </summary>
public sealed class TestSuiteNoticeTests : IDisposable
{
    private readonly TempWorkspace _workspace = new();

    public void Dispose() => _workspace.Dispose();

    /// <summary>
    /// Run <c>122e11c6</c>: five tests that multiply two literals and assert the answer. Delete
    /// the whole application and all five still pass.
    /// </summary>
    [Fact]
    public void A_suite_that_names_no_product_type_is_noticed()
    {
        Product("public class Calculator { public double Multiply(double a, double b) => a * b; }");
        Tests("""
            public class CalculatorTests
            {
                [Fact]
                public void Multiply_works()
                {
                    double a = 5.0;
                    double b = 3.0;
                    double result = a * b;
                    Assert.Equal(15.0, result);
                }
            }
            """);

        Describe().ShouldContain("reference no type declared outside the test projects");
    }

    [Fact]
    public void A_suite_that_calls_the_product_is_silent()
    {
        Product("public class Calculator { public double Multiply(double a, double b) => a * b; }");
        Product("public class App { public double Run() => new Calculator().Multiply(2, 3); }", "App.cs");
        Tests("""
            public class CalculatorTests
            {
                [Fact]
                public void Multiply_works()
                {
                    Assert.Equal(15.0, new Calculator().Multiply(5.0, 3.0));
                }
            }
            """);

        Describe().ShouldBeEmpty();
    }

    /// <summary>
    /// Run <c>d5edbc59</c>: the tests call a real workspace type that nothing shipping references,
    /// because the product multiplies inline in its click handler. Clause one stays silent on it,
    /// which is why the task grew a second clause before it was built.
    /// </summary>
    [Fact]
    public void A_product_type_only_the_tests_reference_is_noticed()
    {
        Product("public class MultiplyViewModel { public double Multiply(double a, double b) => a * b; }");
        Product(
            "public class MainWindow { private double Click(double a, double b) { return a * b; } }",
            "MainWindow.xaml.cs");

        Tests("""
            public class MultiplyViewModelTests
            {
                [Fact]
                public void Multiply_works()
                {
                    Assert.Equal(15.0, new MultiplyViewModel().Multiply(5.0, 3.0));
                }
            }
            """);

        string notice = Describe();
        notice.ShouldContain("MultiplyViewModel");
        notice.ShouldContain("no non-test source references");

        // And clause one must stay quiet: the tests really do name a product type.
        notice.ShouldNotContain("reference no type declared outside");
    }

    [Fact]
    public void A_type_the_markup_names_is_not_an_orphan()
    {
        // The likeliest false positive in a WPF workspace: a view model reached only through a
        // XAML DataContext is driven by the application at runtime.
        Product("public class MultiplyViewModel { public double Multiply(double a, double b) => a * b; }");
        _workspace.WriteFile(
            "src/App/MainWindow.xaml",
            "<Window><Window.DataContext><local:MultiplyViewModel /></Window.DataContext></Window>");

        Tests("""
            public class MultiplyViewModelTests
            {
                [Fact]
                public void Multiply_works()
                {
                    Assert.Equal(15.0, new MultiplyViewModel().Multiply(5.0, 3.0));
                }
            }
            """);

        Describe().ShouldNotContain("no non-test source references");
    }

    /// <summary>
    /// The same run's two edited assertions: expected computed with the expression under test, so
    /// they hold for any implementation of that shape.
    /// </summary>
    [Fact]
    public void An_assertion_computed_from_its_own_operands_is_noticed()
    {
        WiredProduct();
        Tests("""
            public class CalculatorTests
            {
                [Fact]
                public void Multiply_large_numbers()
                {
                    double firstNumber = 123456.789;
                    double secondNumber = 98765.4321;
                    double expected = firstNumber * secondNumber;
                    double result = new Calculator().Multiply(firstNumber, secondNumber);
                    Assert.Equal(expected, result, 2);
                }
            }
            """);

        string notice = Describe();
        notice.ShouldContain("Multiply_large_numbers");
        notice.ShouldContain("cannot fail");
    }

    [Fact]
    public void An_assertion_against_a_known_value_is_silent()
    {
        WiredProduct();
        Tests("""
            public class CalculatorTests
            {
                [Fact]
                public void Multiply_works()
                {
                    double expected = 15.0;
                    double result = new Calculator().Multiply(5.0, 3.0);
                    Assert.Equal(expected, result);
                }
            }
            """);

        Describe().ShouldBeEmpty();
    }

    [Fact]
    public void A_workspace_with_no_tests_says_nothing()
    {
        // Already reported as unverified by the ladder; a second voice adds nothing.
        Product("public class Calculator { public double Multiply(double a, double b) => a * b; }");

        Describe().ShouldBeEmpty();
    }

    /// <summary>
    /// A product whose own code calls the type under test, so the first two clauses are satisfied
    /// and a test can isolate the third. Building the fixture properly is the point: the first
    /// attempt at these two tests had `Calculator` referenced by nothing that ships, and the
    /// orphan clause fired - correctly.
    /// </summary>
    private void WiredProduct()
    {
        Product("public class Calculator { public double Multiply(double a, double b) => a * b; }");
        Product("public class App { public double Run() => new Calculator().Multiply(2, 3); }", "App.cs");
    }

    private void Product(string source, string fileName = "Calculator.cs")
    {
        _workspace.WriteFile("src/App/App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>
            """);
        _workspace.WriteFile($"src/App/{fileName}", source);
    }

    private void Tests(string source)
    {
        _workspace.WriteFile("tests/AppTests/AppTests.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
              <ItemGroup><PackageReference Include="xunit" Version="2.9.2" /></ItemGroup>
            </Project>
            """);
        _workspace.WriteFile("tests/AppTests/AppTests.cs", source);
    }

    private string Describe()
    {
        PathGuard guard = _workspace.Guard();
        return TestSuiteNotices.Describe(
            guard,
            new RoslynCodeAnalyzer(guard, Options.Create(new VerificationOptions())),
            new ToolsOptions().MaxFilesSearched);
    }
}
