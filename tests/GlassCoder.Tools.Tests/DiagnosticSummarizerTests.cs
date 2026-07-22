using GlassCoder.Tools.Verification;
using Microsoft.Extensions.Options;

namespace GlassCoder.Tools.Tests;

/// <summary>
/// The summariser (workplan task 15). Its whole job is to stand between a compiler cascade and
/// the context window, so the tests are about what it refuses to pass on.
/// </summary>
public sealed class DiagnosticSummarizerTests
{
    [Fact]
    public void A_200_error_cascade_summarises_to_a_capped_deduplicated_position_sorted_list()
    {
        // The acceptance criterion from the workplan, literally: one bad edit in one file
        // cascading into 200 errors across 20 files must not consume the window.
        List<CodeDiagnostic> cascade = [];
        for (int file = 0; file < 20; file++)
        {
            for (int error = 0; error < 10; error++)
            {
                cascade.Add(new CodeDiagnostic(
                    "CS0246",
                    CodeSeverity.Error,
                    "The type or namespace name 'Widget' could not be found",
                    $"src/File{file:00}.cs",
                    Line: 100 - error,
                    Column: error + 1));
            }
        }

        DiagnosticSummary summary = Summarizer(cap: 10).Summarise(cascade);

        summary.TotalErrors.ShouldBe(200);              // the true total is always reported
        summary.Entries.Count.ShouldBeLessThanOrEqualTo(10);
        summary.FilesAffected.ShouldBe(20);
        summary.Text.ShouldContain("200 error(s)");
        summary.CascadeRatio.ShouldBeGreaterThan(1d);
    }

    [Fact]
    public void Only_the_first_error_in_each_file_is_reported()
    {
        List<CodeDiagnostic> diagnostics =
        [
            new("CS0103", CodeSeverity.Error, "later", "src/A.cs", 50, 1),
            new("CS0201", CodeSeverity.Error, "earliest", "src/A.cs", 10, 5),
            new("CS0305", CodeSeverity.Error, "middle", "src/A.cs", 20, 1),
        ];

        DiagnosticSummary summary = Summarizer().Summarise(diagnostics);

        summary.TotalErrors.ShouldBe(3);
        CodeDiagnostic entry = summary.Entries.ShouldHaveSingleItem();
        entry.Line.ShouldBe(10);
        entry.Message.ShouldBe("earliest");
    }

    [Fact]
    public void Duplicate_error_codes_are_collapsed_across_files()
    {
        List<CodeDiagnostic> diagnostics =
        [
            new("CS0246", CodeSeverity.Error, "missing type", "src/A.cs", 5, 1),
            new("CS0246", CodeSeverity.Error, "missing type", "src/B.cs", 7, 1),
            new("CS0117", CodeSeverity.Error, "no such member", "src/C.cs", 9, 1),
        ];

        DiagnosticSummary summary = Summarizer().Summarise(diagnostics);

        summary.Entries.Select(e => e.Id).ShouldBe(["CS0246", "CS0117"], ignoreOrder: true);
        summary.Entries.Count.ShouldBe(2);
        summary.TotalErrors.ShouldBe(3);
    }

    [Fact]
    public void Entries_are_sorted_by_file_position_so_the_root_cause_comes_first()
    {
        List<CodeDiagnostic> diagnostics =
        [
            new("CS0003", CodeSeverity.Error, "third", "src/C.cs", 3, 1),
            new("CS0001", CodeSeverity.Error, "first", "src/A.cs", 1, 1),
            new("CS0002", CodeSeverity.Error, "second", "src/B.cs", 2, 1),
        ];

        DiagnosticSummary summary = Summarizer().Summarise(diagnostics);

        summary.Entries.Select(e => e.FilePath).ShouldBe(["src/A.cs", "src/B.cs", "src/C.cs"]);
    }

    [Fact]
    public void Warnings_only_appear_once_nothing_is_broken()
    {
        List<CodeDiagnostic> mixed =
        [
            new("CS0103", CodeSeverity.Error, "broken", "src/A.cs", 1, 1),
            new("CA1822", CodeSeverity.Warning, "make static", "src/A.cs", 2, 1),
        ];

        DiagnosticSummary withError = Summarizer().Summarise(mixed);
        withError.Entries.ShouldAllBe(e => e.Severity == CodeSeverity.Error);

        DiagnosticSummary warningsOnly = Summarizer().Summarise([mixed[1]]);
        warningsOnly.Ok.ShouldBeTrue();
        warningsOnly.Entries.ShouldHaveSingleItem();
    }

    [Fact]
    public void Truncation_is_stated_rather_than_silent()
    {
        List<CodeDiagnostic> diagnostics = [.. Enumerable.Range(0, 30).Select(i =>
            new CodeDiagnostic($"CS{i:0000}", CodeSeverity.Error, $"error {i}", $"src/File{i:00}.cs", 1, 1))];

        DiagnosticSummary summary = Summarizer(cap: 5).Summarise(diagnostics);

        summary.Truncated.ShouldBeTrue();
        summary.Entries.Count.ShouldBe(5);
        summary.Text.ShouldContain("withheld");
    }

    [Fact]
    public void A_clean_report_says_so()
    {
        DiagnosticSummary summary = Summarizer().Summarise(DiagnosticReport.Success(12));

        summary.Ok.ShouldBeTrue();
        summary.Text.ShouldContain("No diagnostics");
    }

    [Fact]
    public void A_rung_that_could_not_run_is_distinguished_from_a_rung_that_failed()
    {
        DiagnosticSummary summary = Summarizer().Summarise(DiagnosticReport.Inconclusive("Docker is unavailable."));

        summary.TotalErrors.ShouldBe(0);
        summary.Text.ShouldContain("Could not run");
    }

    private static DiagnosticSummarizer Summarizer(int cap = 10) =>
        new(Options.Create(new VerificationOptions { MaxSummarisedDiagnostics = cap }));
}
