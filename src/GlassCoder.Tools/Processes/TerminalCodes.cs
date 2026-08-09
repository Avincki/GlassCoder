using System.Text.RegularExpressions;

namespace GlassCoder.Tools.Processes;

/// <summary>
/// Removes terminal control sequences from captured process output.
/// <para>
/// The .NET CLI's progress reporter writes cursor-control sequences even into a redirected pipe,
/// and they travel all the way to the model. Run <c>4c7de12b</c> received 21 of them: every
/// <c>dotnet_project</c> observation carried some, and step 9's Compile-rung failure summary was
/// <em>nothing but</em> escape sequences - <c>[?25l[1F csproj [?25h[?25l[2F [120G[6D(0.1s)</c> -
/// so the model was told verification FAILED and given no legible cause. It recovered from its own
/// knowledge of WPF rather than from anything the harness said.
/// </para>
/// <para>
/// Stripped here, at the one place both streams are collected, rather than in each parser. Task
/// 15's rule is that raw compiler output never reaches the model; it holds wherever a parser
/// succeeds and is bypassed by every "print the raw tail when the parser found nothing" fallback,
/// of which this repository now has several.
/// </para>
/// </summary>
public static partial class TerminalCodes
{
    /// <summary>
    /// CSI (<c>ESC [</c> … final byte), OSC (<c>ESC ]</c> … BEL or ST), and the two-character
    /// escapes. Deliberately not a general ANSI parser: these are the shapes a build tool emits,
    /// and a regex that tried to be exhaustive would start eating real output.
    /// </summary>
    [GeneratedRegex(
        @"\x1B\[[0-9;?]*[ -/]*[@-~]|\x1B\][^\x07\x1B]*(?:\x07|\x1B\\)|\x1B[@-Z\\-_]",
        RegexOptions.None,
        matchTimeoutMilliseconds: 1000)]
    private static partial Regex Sequences();

    /// <summary>
    /// The line without its control sequences. Returns the input unchanged when there are none,
    /// which is the overwhelmingly common case and worth not allocating for.
    /// </summary>
    public static string Strip(string? text)
    {
        if (string.IsNullOrEmpty(text) || !text.Contains('\x1B', StringComparison.Ordinal))
        {
            return text ?? string.Empty;
        }

        try
        {
            return Sequences().Replace(text, string.Empty);
        }
        catch (RegexMatchTimeoutException)
        {
            // A line pathological enough to time out is not worth failing a build over; the
            // model reading one ugly line beats the run dying over its punctuation.
            return text;
        }
    }
}
