using GlassCoder.Tools.FileSystem;
using GlassCoder.Tools.Guardrails;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace GlassCoder.Tools.Verification;

/// <summary>
/// Whether a green suite is exercising the product at all (workplan task 66).
/// <para>
/// Three consecutive desktop runs shipped a suite that verified nothing about the application,
/// in three different shapes, and every one of them cleared every oracle this harness has:
/// compile green, <c>run_tests</c> green, the ladder's UnitTests rung passed, the completion
/// critique accepted, the post-run review accepted, and a human rated the running window 5/5.
/// None of those judgements was wrong. Nothing in the harness asks whether the tests and the
/// product are connected to each other, and that is the missing oracle.
/// </para>
/// <list type="number">
/// <item>Run <c>122e11c6</c>: five tests that multiply two literals with <c>*</c> and assert the
/// answer. Delete the whole application and all five still pass.</item>
/// <item>Run <c>d5edbc59</c>: tests that call a real workspace type - which the application never
/// constructs, because the product multiplies inline in a click handler. The same hollowness one
/// indirection deeper, and the first clause stays silent on it.</item>
/// <item>The same run: two assertions whose expected value is computed with the expression under
/// test, both edited into that shape to clear a real failure.</item>
/// </list>
/// <para>
/// <strong>Notices, never gates.</strong> The signal is good and not certain: tests that drive the
/// product through DI, reflection, XAML or a launched process look identical from the syntax tree
/// to tests that drive nothing. This repository has twice been taught what a confident refusal
/// costs - the deadlocks of <c>5c071f37</c> and <c>a408b61b</c> - and the XAML handler pre-check
/// was designed and dropped for exactly this reason. Every clause here is worded as the question
/// rather than the verdict, and refuses nothing.
/// </para>
/// <para>
/// Syntax only, through the analyzer's warm tree cache: no reference resolution, so none of the
/// false-negative risk that kept <c>find_references</c> unbuilt (task 48).
/// </para>
/// </summary>
public static class TestSuiteNotices
{
    /// <summary>Assertion methods whose first argument is the expected value.</summary>
    private static readonly string[] ExpectedFirstAssertions =
        ["Equal", "AreEqual", "Same", "AreSame", "Equivalent"];

    /// <summary>
    /// What this workspace's tests are worth saying out loud, each notice starting with a space so
    /// a caller can append the result to its summary verbatim. Empty when there is nothing to say,
    /// which is the normal case.
    /// </summary>
    /// <param name="guard">The path allow-list, which also hides bin/ and obj/.</param>
    /// <param name="analyzer">The tree cache.</param>
    /// <param name="maxFiles">Sweep cap, from <see cref="ToolsOptions.MaxFilesSearched"/>.</param>
    /// <param name="cancellationToken">Cancels the sweep.</param>
    public static string Describe(
        IPathGuard guard,
        RoslynCodeAnalyzer analyzer,
        int maxFiles,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(guard);
        ArgumentNullException.ThrowIfNull(analyzer);

        try
        {
            Survey survey = Sweep(guard, analyzer, maxFiles, cancellationToken);
            if (survey.TestFiles == 0 || survey.ProductTypes.Count == 0)
            {
                // No tests, or nothing to exercise. Neither is this notice's business: a testless
                // tree is already reported as unverified, and a workspace with no product types
                // has nothing for a test to be connected to.
                return string.Empty;
            }

            return HollowSuiteNotice(survey) + OrphanTypeNotice(survey) + TautologyNotice(survey);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            // A notice that cannot be computed is a notice that is not there. It must never be
            // the reason a test run fails to report.
            return string.Empty;
        }
    }

    /// <summary>Clause one: the tests mention nothing the product declares.</summary>
    private static string HollowSuiteNotice(Survey survey)
    {
        if (survey.TestIdentifiers.Overlaps(survey.ProductTypes))
        {
            return string.Empty;
        }

        return " Note: these tests reference no type declared outside the test projects - they may " +
               "not be exercising the code under test at all. Check that the suite calls the " +
               "product rather than recomputing what it should do.";
    }

    /// <summary>
    /// Clause two: a product type only the tests use.
    /// <para>
    /// The shape clause one misses. The type is real and really called, and nothing that ships
    /// mentions it - so breaking the product cannot make the suite red.
    /// </para>
    /// <para>
    /// The wording carries a scar. It used to end "if the product duplicates that logic rather
    /// than calling it, have it call this instead", and run 46231701 did exactly that: one line
    /// in a click handler, which moved the type into <see cref="Survey.ProductIdentifiers"/> and
    /// silenced this clause while the parse, format and error branches it authored stayed
    /// unreachable by any test. A remedy this detector can see satisfied is a remedy that costs
    /// one line and buys nothing. The clause now names the trap instead of prescribing the
    /// shortcut: diagnosis is ours, the fix is the model's to find.
    /// </para>
    /// </summary>
    private static string OrphanTypeNotice(Survey survey)
    {
        List<string> orphans =
        [
            .. survey.ProductTypes
                .Where(survey.TestIdentifiers.Contains)
                .Where(type => !survey.ProductIdentifiers.Contains(type))
                .Where(type => !survey.MarkupIdentifiers.Contains(type))
                .Order(StringComparer.Ordinal)
                .Take(3)
        ];

        if (orphans.Count == 0)
        {
            return string.Empty;
        }

        return $" Note: the tests exercise {Join(orphans)}, which no non-test source references - " +
               "so the shipped path is untested even though the suite is green. Making the product " +
               "call it would silence this note without testing anything more: the tests have to " +
               "reach the logic that actually runs - the parsing, the formatting, the error " +
               "branches - and not only the expression that was extracted.";
    }

    /// <summary>Clause three: an assertion whose expected value is computed from its own inputs.</summary>
    private static string TautologyNotice(Survey survey)
    {
        if (survey.Tautologies.Count == 0)
        {
            return string.Empty;
        }

        return $" Note: {Join([.. survey.Tautologies.Take(3)])} compare a result against an expected " +
               "value computed from the same operands - if that expression matches the " +
               "implementation, the assertion holds for any implementation of that shape and " +
               "cannot fail. Assert against a value worked out independently.";
    }

    private static string Join(IReadOnlyList<string> names) =>
        names.Count == 1
            ? $"`{names[0]}`"
            : string.Join(", ", names.Take(names.Count - 1).Select(n => $"`{n}`")) + $" and `{names[^1]}`";

    private static Survey Sweep(
        IPathGuard guard, RoslynCodeAnalyzer analyzer, int maxFiles, CancellationToken cancellationToken)
    {
        Survey survey = new();
        Dictionary<string, bool> projectIsTest = new(StringComparer.OrdinalIgnoreCase);

        foreach (string file in WorkspaceFiles.Enumerate(guard, guard.RepoRoot, "**/*.cs", maxFiles, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (analyzer.ParseFile(file, cancellationToken) is not { } tree)
            {
                continue;
            }

            if (IsTestFile(file, projectIsTest))
            {
                survey.TestFiles++;
                ReadTestFile(tree, survey, guard);
            }
            else
            {
                ReadProductFile(tree, survey);
            }
        }

        // Markup counts as a reference. A view model named only in a XAML DataContext is driven by
        // the application at runtime, and calling that untested would be plainly wrong - this is
        // the most likely false positive in a WPF workspace, so it is bought off directly.
        if (survey.ProductTypes.Count > 0)
        {
            foreach (string markup in WorkspaceFiles.Enumerate(
                guard, guard.RepoRoot, "**/*.xaml", maxFiles, cancellationToken))
            {
                string text = File.ReadAllText(markup);
                foreach (string name in survey.ProductTypes)
                {
                    if (text.Contains(name, StringComparison.Ordinal))
                    {
                        survey.MarkupIdentifiers.Add(name);
                    }
                }
            }
        }

        return survey;
    }

    /// <summary>
    /// Whether the file belongs to a project that references a test framework. Cached per project,
    /// because a suite is many files and one project file.
    /// </summary>
    private static bool IsTestFile(string fullPath, Dictionary<string, bool> cache)
    {
        if (ProjectLocator.FindProjectFile(fullPath) is not { } project)
        {
            return false;
        }

        if (cache.TryGetValue(project, out bool known))
        {
            return known;
        }

        bool isTest = ProjectLocator.IsTestProject(project);

        cache[project] = isTest;
        return isTest;
    }

    /// <summary>What a test file uses, and which of its assertions cannot fail.</summary>
    private static void ReadTestFile(SyntaxTree tree, Survey survey, IPathGuard guard)
    {
        SyntaxNode root = tree.GetRoot();

        // Identifiers used inside method bodies only. A using directive or a field type would
        // count a suite as connected when nothing in it ever calls the product - which is
        // precisely run 122e11c6's shape: `using MultiplyApp;` present and unused.
        foreach (BlockSyntax body in root.DescendantNodes().OfType<BlockSyntax>())
        {
            foreach (IdentifierNameSyntax identifier in body.DescendantNodes().OfType<IdentifierNameSyntax>())
            {
                survey.TestIdentifiers.Add(identifier.Identifier.ValueText);
            }
        }

        foreach (MethodDeclarationSyntax method in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
        {
            if (IsTautological(method))
            {
                survey.Tautologies.Add(method.Identifier.ValueText);
            }
        }
    }

    /// <summary>What the product declares, and what it uses.</summary>
    private static void ReadProductFile(SyntaxTree tree, Survey survey)
    {
        SyntaxNode root = tree.GetRoot();

        foreach (BaseTypeDeclarationSyntax declaration in root.DescendantNodes().OfType<BaseTypeDeclarationSyntax>())
        {
            survey.ProductTypes.Add(declaration.Identifier.ValueText);
        }

        foreach (IdentifierNameSyntax identifier in root.DescendantNodes().OfType<IdentifierNameSyntax>())
        {
            survey.ProductIdentifiers.Add(identifier.Identifier.ValueText);
        }

        // A type named in another declaration's base list is used by the product too.
        foreach (BaseTypeSyntax baseType in root.DescendantNodes().OfType<BaseTypeSyntax>())
        {
            foreach (IdentifierNameSyntax identifier in baseType.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>())
            {
                survey.ProductIdentifiers.Add(identifier.Identifier.ValueText);
            }
        }
    }

    /// <summary>
    /// Whether a test method asserts a result against an expected value computed from the same
    /// operands the call under test was given.
    /// <para>
    /// The rule is deliberately narrow. Expected must be a computed expression rather than a
    /// literal - a wrong literal is a bug the test can catch, which is the whole point of one -
    /// and every identifier in it must be among the arguments handed to the call producing the
    /// actual value. That is the shape of both assertions run <c>d5edbc59</c> edited into
    /// existence: <c>Assert.Equal(firstNumber * secondNumber, Multiply(firstNumber, secondNumber))</c>,
    /// and the same with the operands written as literals.
    /// </para>
    /// </summary>
    private static bool IsTautological(MethodDeclarationSyntax method)
    {
        foreach (InvocationExpressionSyntax call in method.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (call.Expression is not MemberAccessExpressionSyntax member ||
                !ExpectedFirstAssertions.Contains(member.Name.Identifier.ValueText, StringComparer.Ordinal) ||
                call.ArgumentList.Arguments.Count < 2)
            {
                continue;
            }

            ExpressionSyntax expected = call.ArgumentList.Arguments[0].Expression;
            ExpressionSyntax actual = call.ArgumentList.Arguments[1].Expression;

            // Both arguments are usually locals; follow each to what it was assigned.
            ExpressionSyntax expectedValue = Resolve(method, expected);
            ExpressionSyntax actualValue = Resolve(method, actual);

            if (actualValue is not InvocationExpressionSyntax underTest)
            {
                continue;
            }

            // A literal expected value is exactly what a good test carries.
            if (expectedValue is not BinaryExpressionSyntax computed)
            {
                continue;
            }

            HashSet<string> inputs =
            [
                .. underTest.ArgumentList.Arguments
                    .SelectMany(a => a.Expression.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>())
                    .Select(i => i.Identifier.ValueText)
            ];

            HashSet<string> used =
            [
                .. computed.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>()
                    .Select(i => i.Identifier.ValueText)
            ];

            if (used.IsSubsetOf(inputs))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The expression a local was assigned, or the expression itself when it is not a local. One
    /// hop only: a test that launders its expected value through three variables is not the
    /// pattern this was written from, and chasing it would trade precision for reach.
    /// </summary>
    private static ExpressionSyntax Resolve(MethodDeclarationSyntax method, ExpressionSyntax expression)
    {
        if (expression is not IdentifierNameSyntax name)
        {
            return expression;
        }

        foreach (VariableDeclaratorSyntax declarator in method.DescendantNodes().OfType<VariableDeclaratorSyntax>())
        {
            if (string.Equals(declarator.Identifier.ValueText, name.Identifier.ValueText, StringComparison.Ordinal) &&
                declarator.Initializer?.Value is { } value)
            {
                return value;
            }
        }

        return expression;
    }

    /// <summary>What one sweep learned about the workspace.</summary>
    private sealed class Survey
    {
        public int TestFiles { get; set; }

        /// <summary>Types declared outside the test projects.</summary>
        public HashSet<string> ProductTypes { get; } = new(StringComparer.Ordinal);

        /// <summary>Identifiers used anywhere in non-test sources.</summary>
        public HashSet<string> ProductIdentifiers { get; } = new(StringComparer.Ordinal);

        /// <summary>Identifiers used inside test method bodies.</summary>
        public HashSet<string> TestIdentifiers { get; } = new(StringComparer.Ordinal);

        /// <summary>Product type names that appear in markup, which is a runtime reference.</summary>
        public HashSet<string> MarkupIdentifiers { get; } = new(StringComparer.Ordinal);

        /// <summary>Test methods whose assertion computes its own expected value.</summary>
        public List<string> Tautologies { get; } = [];
    }
}
