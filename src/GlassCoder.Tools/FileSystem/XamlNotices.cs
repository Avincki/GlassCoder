using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using GlassCoder.Tools.Verification;

namespace GlassCoder.Tools.FileSystem;

/// <summary>
/// What a written XAML file is worth warning about, said in the write's own summary.
/// <para>
/// Two notices, both from the 2026-08-08 UI runs. The clip risk: run ea9a1f66's operator saw
/// the result field outside the dialog because a fixed <c>Height="300"</c> window held five
/// rows of content - the build stays green, the tests stay green, and the defect is invisible
/// until a human runs the app. The test-project note: run 216360bf copied the app's
/// <c>MainWindow.xaml</c> into the test project trying to make XAML-parsing layout tests
/// loadable, which they never became; the copy outlived the deleted tests as cruft.
/// </para>
/// <para>
/// Notices, never refusals: a warning about rendering is a judgement no compiler backs, and
/// the gate-refusal deadlocks of 5c071f37 and a408b61b all began as judgements the harness
/// was too sure of.
/// </para>
/// </summary>
public static class XamlNotices
{
    /// <summary>Below this fixed height, dense content is at risk of clipping.</summary>
    private const double ShortWindowHeight = 450;

    /// <summary>Rows plus controls at which a short fixed window earns the note.</summary>
    private const int DenseContentThreshold = 5;

    private static readonly string[] TestFrameworkPackages =
        ["xunit", "nunit", "MSTest", "Microsoft.NET.Test.Sdk"];

    private static readonly string[] ContentControls =
        ["TextBox", "TextBlock", "Button", "Label", "ComboBox", "CheckBox", "RadioButton", "ListBox"];

    /// <summary>
    /// The notices this write earns, each starting with a space so the caller can append the
    /// result to its summary verbatim. Empty for non-XAML paths and unremarkable markup.
    /// </summary>
    public static string Describe(string fullPath, string content)
    {
        ArgumentNullException.ThrowIfNull(fullPath);
        ArgumentNullException.ThrowIfNull(content);

        if (!Path.GetExtension(fullPath).Equals(".xaml", StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        return TestProjectNotice(fullPath) + ClipRiskNotice(content);
    }

    private static string TestProjectNotice(string fullPath)
    {
        if (ProjectLocator.FindProjectFile(fullPath) is not { } project)
        {
            return string.Empty;
        }

        bool testProject = ProjectLocator.ReadReferences(project).Packages
            .Any(package => TestFrameworkPackages
                .Any(framework => package.Contains(framework, StringComparison.OrdinalIgnoreCase)));

        return testProject
            ? " Note: a XAML file in a test project is almost never loadable by tests - markup belongs " +
              "to the app project. Test behaviour through the app's own types; rendering is confirmed " +
              "by launch_app, and by the operator's Run app, not by tests."
            : string.Empty;
    }

    private static string ClipRiskNotice(string content)
    {
        try
        {
            if (XDocument.Parse(content).Root is not { } root ||
                !root.Name.LocalName.Equals("Window", StringComparison.Ordinal))
            {
                return string.Empty;
            }

            // SizeToContent (other than Manual) grows the window to fit; no clip risk to name.
            string? sizeToContent = root.Attribute("SizeToContent")?.Value;
            if (sizeToContent is not null &&
                !sizeToContent.Equals("Manual", StringComparison.OrdinalIgnoreCase))
            {
                return string.Empty;
            }

            if (root.Attribute("Height")?.Value is not { } declared ||
                !double.TryParse(declared, NumberStyles.Float, CultureInfo.InvariantCulture, out double height) ||
                height >= ShortWindowHeight)
            {
                return string.Empty;
            }

            int rows = root.Descendants().Count(e => e.Name.LocalName == "RowDefinition");
            int controls = root.Descendants().Count(e =>
                ContentControls.Contains(e.Name.LocalName, StringComparer.Ordinal));

            if (rows + controls < DenseContentThreshold)
            {
                return string.Empty;
            }

            return $" Layout note: fixed Height={height.ToString("0", CultureInfo.InvariantCulture)} with " +
                $"{controls} controls in {rows} rows can clip content at runtime - consider " +
                "SizeToContent=\"Height\" or a taller window. Compile and tests cannot see clipping; " +
                "launch_app can, and the operator's Run app is the richer second look.";
        }
        catch (XmlException)
        {
            // Malformed markup is the build's error to report, not a notice's.
            return string.Empty;
        }
    }
}
