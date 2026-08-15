using System.Runtime.CompilerServices;

namespace GlassCoder.Core.Tests;

/// <summary>
/// A build-time guard over what the harness tells the model it cannot do.
/// <para>
/// The most reproducible defect class this repository has: a sentence written as the scar of one
/// run, asserting a limitation that a later task removed, still shipping years of runs later.
/// HISTORY records six instances before this test existed - the critic prompt on 2026-08-09, then
/// three more found by run <c>46231701</c>'s retrospective, all of them still denying
/// <c>launch_app</c> months after task 71 shipped it. Every one was true when written. None was
/// anchored to the capability it asserted, so nothing failed when the capability arrived.
/// </para>
/// <para>
/// Deliberately a text scan over the source rather than reflection over constants: not one of the
/// six lived in a reflectable field. They were interpolated returns, local consts and inline
/// string concatenations - which is what a model-facing sentence normally looks like. A dumb scan
/// sees all of them.
/// </para>
/// <para>
/// The consequence for whoever reads this next: a comment explaining one of these phrases cannot
/// quote it. Describe it instead. The scan does not read C# and must not - a guard that skips
/// comments is a guard with a place to hide.
/// </para>
/// </summary>
public sealed class ModelFacingPromptTests
{
    /// <summary>
    /// Phrases that deny the model a capability it has. Narrow on purpose: this is not a style
    /// rule about mentioning the operator, and <c>launch_app</c>'s own summary correctly says that
    /// whether a window looks right is the operator's to judge. What is banned is telling the
    /// model the launching itself is not its to do - and, since the launch began reading the
    /// window's text back on 2026-08-15, telling anyone that it cannot see what the window shows.
    /// <para>
    /// Exact phrases, which is the limit of this guard as much as its point: it stops the sentence
    /// that shipped, not every future rewording of it. What makes it worth having anyway is that
    /// all five recorded instances were the *same* sentence surviving the task that falsified it,
    /// not a new one being invented.
    /// </para>
    /// </summary>
    private static readonly string[] Denials =
        ["never by you", "only the operator", "cannot see what is on the screen"];

    /// <summary>The projects whose strings reach the model.</summary>
    private static readonly string[] Projects = ["GlassCoder.Core", "GlassCoder.Tools"];

    [Fact]
    public void No_model_facing_string_denies_the_model_a_tool_it_has()
    {
        string source = Path.Combine(RepositoryRoot(), "src");
        Directory.Exists(source).ShouldBeTrue($"the source tree should sit at {source}");

        List<string> offences = [];

        foreach (string project in Projects)
        {
            foreach (string file in Directory.EnumerateFiles(
                Path.Combine(source, project), "*.cs", SearchOption.AllDirectories))
            {
                // Generated and build output are nobody's prose.
                if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
                    file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                {
                    continue;
                }

                string[] lines = File.ReadAllLines(file);
                for (int index = 0; index < lines.Length; index++)
                {
                    foreach (string denial in Denials)
                    {
                        if (lines[index].Contains(denial, StringComparison.OrdinalIgnoreCase))
                        {
                            offences.Add($"{Path.GetFileName(file)}:{index + 1} says \"{denial}\"");
                        }
                    }
                }
            }
        }

        offences.ShouldBeEmpty(
            "a model-facing string is denying the model something it can do. Point it at the tool " +
            "instead: " + string.Join("; ", offences));
    }

    /// <summary>
    /// The repository root, from this file's own compile-time path. Sturdier than climbing out of
    /// the test binary's directory, which changes with configuration and target framework.
    /// </summary>
    private static string RepositoryRoot([CallerFilePath] string thisFile = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", ".."));
}
