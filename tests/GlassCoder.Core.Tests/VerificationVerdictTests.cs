using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using GlassCoder.Core.Verification;

namespace GlassCoder.Core.Tests;

/// <summary>
/// The verdict vocabulary, and a build-time guard that every surface uses it.
/// <para>
/// The defect this exists for has now shipped three times, each time in a renderer written by
/// someone who had not read the other two: the model-facing header on 2026-08-09, the critique
/// tally on 2026-08-11, and run <c>ae72c5ad</c>'s retrospective transcript, which told both of its
/// reviewers that a test rung had passed in a workspace holding no tests. Every one of those
/// surfaces had the flag that would have told it otherwise, sitting on the same record it was
/// reading. Each was fixed where it was found; none of the fixes stopped the next one.
/// </para>
/// </summary>
public sealed class VerificationVerdictTests
{
    [Fact]
    public void A_climb_that_verified_nothing_does_not_read_as_a_clean_pass()
    {
        VerificationVerdict.Describe(passed: true, unverified: true).ShouldBe("passed (0 tests)");
    }

    [Fact]
    public void A_pass_with_something_to_say_says_so()
    {
        VerificationVerdict.Describe(passed: true, unverified: false, noticed: true)
            .ShouldBe("passed (with a notice)");
    }

    [Theory]
    [InlineData(true, false, false, "passed")]
    [InlineData(false, false, false, "FAILED")]
    [InlineData(false, true, true, "FAILED")]
    public void Everything_else_reads_as_it_did(bool passed, bool unverified, bool noticed, string expected)
    {
        // A failure stays a failure whatever the flags say: nothing was verified because nothing
        // got that far, and "FAILED (0 tests)" would read as two problems where there is one.
        VerificationVerdict.Describe(passed, unverified, noticed).ShouldBe(expected);
    }

    /// <summary>
    /// The invariant, as a text scan over the source for the shape that keeps coming back: a
    /// verification's pass flag turned straight into the word for a pass.
    /// <para>
    /// A scan rather than reflection, and over the whole of <c>src</c>, because none of the three
    /// instances lived anywhere reflection could reach - they were interpolated strings inside a
    /// log call, a transcript renderer and a view model. The remedy a hit wants is
    /// <see cref="VerificationVerdict.Describe"/>, which cannot lose a flag it is handed.
    /// </para>
    /// <para>
    /// Consequence for whoever reads this next: a comment in <c>src</c> about this rule cannot
    /// write the shape out, because the scan does not read C# and must not - a guard that skips
    /// comments is a guard with a place to hide. Here in the test the shape is safe to write: the
    /// scan reads <c>src</c> only.
    /// </para>
    /// </summary>
    [Fact]
    public void No_surface_renders_a_verification_verdict_from_the_pass_flag_alone()
    {
        string source = Path.Combine(RepositoryRoot(), "src");
        Directory.Exists(source).ShouldBeTrue($"the source tree should sit at {source}");

        Regex shape = new("Passed\\s*\\?\\s*\"passed\"", RegexOptions.None, TimeSpan.FromSeconds(5));

        List<string> offences = [];
        foreach (string file in Directory.EnumerateFiles(source, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
                file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            {
                continue;
            }

            // The one file allowed to choose the words is the one whose whole job is choosing them.
            if (Path.GetFileName(file) == $"{nameof(VerificationVerdict)}.cs")
            {
                continue;
            }

            string[] lines = File.ReadAllLines(file);
            for (int index = 0; index < lines.Length; index++)
            {
                if (shape.IsMatch(lines[index]))
                {
                    offences.Add($"{Path.GetFileName(file)}:{index + 1}");
                }
            }
        }

        offences.ShouldBeEmpty(
            "a surface is deciding the verdict from the pass flag by itself, which is how a climb " +
            "that verified nothing comes to read as a clean pass. Call VerificationVerdict.Describe " +
            "and hand it the flags on the record you are already holding: " + string.Join("; ", offences));
    }

    /// <summary>
    /// The repository root, from this file's own compile-time path. Sturdier than climbing out of
    /// the test binary's directory, which changes with configuration and target framework.
    /// </summary>
    private static string RepositoryRoot([CallerFilePath] string thisFile = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", ".."));
}
