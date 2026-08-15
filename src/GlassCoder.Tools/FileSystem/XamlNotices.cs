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

        // A code-behind is half of a window, and the half that says which object the markup is
        // supposed to be showing. Reading only the markup is why the pair could disagree for
        // eleven steps of run 457867c7 with every rung green.
        if (fullPath.EndsWith(".xaml.cs", StringComparison.OrdinalIgnoreCase))
        {
            return UnusedDataContextNotice(fullPath, codeBehind: content, markup: null);
        }

        if (!Path.GetExtension(fullPath).Equals(".xaml", StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        return TestProjectNotice(fullPath) + ClipRiskNotice(content) +
               UnusedDataContextNotice(fullPath, codeBehind: null, markup: content);
    }

    /// <summary>
    /// The window sets a view model and displays nothing from it.
    /// <para>
    /// Every notice before this one asked about a single artifact. This one asks whether two
    /// artifacts are the same feature, which is the question run <c>457867c7</c> answered wrongly
    /// for eleven steps: a <c>ViewModel</c> with change notification that eight tests drove, and a
    /// window that assigned it to <c>DataContext</c>, bound nothing to it, and ran its own
    /// code-behind handlers instead. Twelve verification passes, all honest - the files compiled,
    /// the suite really passed, the application really launched - and the suite was exactly as
    /// hollow as the one task 66 was written for. The ladder asks whether an artifact holds up. It
    /// has never asked whether the artifact is the one that runs.
    /// </para>
    /// <para>
    /// The seam is C#-to-XAML, which is where WPF puts the wiring and where no rung looks. Kept a
    /// notice: programmatic binding, DI-composed views and templates set elsewhere are known false
    /// positives, and a gate here would be the deadlock this file already warns about twice.
    /// </para>
    /// </summary>
    private static string UnusedDataContextNotice(string fullPath, string? codeBehind, string? markup)
    {
        string markupPath = codeBehind is null ? fullPath : fullPath[..^3];
        string codeBehindPath = codeBehind is null ? fullPath + ".cs" : fullPath;

        markup ??= ReadIfPresent(markupPath);
        codeBehind ??= ReadIfPresent(codeBehindPath);
        if (markup is null || codeBehind is null)
        {
            return string.Empty;
        }

        // Assigning the DataContext is the claim that the window shows that object.
        if (!codeBehind.Contains("DataContext", StringComparison.Ordinal))
        {
            return string.Empty;
        }

        // Anything that binds - in the markup, or built in code - means the pair is wired and this
        // has nothing to say.
        if (markup.Contains("{Binding", StringComparison.Ordinal) ||
            markup.Contains("{x:Bind", StringComparison.Ordinal) ||
            markup.Contains("{TemplateBinding", StringComparison.Ordinal) ||
            codeBehind.Contains("SetBinding(", StringComparison.Ordinal) ||
            codeBehind.Contains("new Binding(", StringComparison.Ordinal) ||
            codeBehind.Contains("BindingOperations", StringComparison.Ordinal))
        {
            return string.Empty;
        }

        return " Note: this window sets a DataContext and its markup binds to nothing, so what the " +
               "window shows and what a test of that object covers are two different code paths. " +
               "Every rung can pass while the running window uses neither.";
    }

    private static string? ReadIfPresent(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A notice is worth nothing and costs nothing; it never justifies failing a write.
            return null;
        }
    }

    private static string TestProjectNotice(string fullPath)
    {
        if (ProjectLocator.FindProjectFile(fullPath) is not { } project)
        {
            return string.Empty;
        }

        return ProjectLocator.IsTestProject(project)
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
