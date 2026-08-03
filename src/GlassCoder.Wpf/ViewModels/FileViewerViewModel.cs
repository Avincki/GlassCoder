using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using GlassCoder.Wpf.Highlighting;

namespace GlassCoder.Wpf.ViewModels;

/// <summary>
/// One file, read and coloured for the viewer window.
/// <para>
/// Read-only and read-once: the window is a way to look at what the agent is working on, not a
/// second editor. Nothing here writes, and nothing watches the file for changes - closing and
/// reopening is the refresh, which is honest about the fact that this is a snapshot.
/// </para>
/// </summary>
public sealed class FileViewerViewModel
{
    /// <summary>
    /// Past this, the file is refused outright. A viewer is not the tool for a hundred-megabyte
    /// artefact, and reading one into a string to find that out would be the slow way to fail.
    /// </summary>
    private const long MaximumBytes = 8 * 1024 * 1024;

    /// <summary>
    /// Past this, the file is shown without colouring. Scanning is linear and cheap, but the
    /// document it feeds is not: the runs become WPF objects, and a megabyte of them is a
    /// noticeable pause on a window that should open instantly.
    /// </summary>
    private const long MaximumHighlightedBytes = 1024 * 1024;

    /// <summary>How much of the head to inspect when deciding whether a file is text at all.</summary>
    private const int BinarySniffBytes = 8000;

    private FileViewerViewModel(
        string displayPath,
        string fullPath,
        string summary,
        string? message,
        IReadOnlyList<IReadOnlyList<HighlightedSpan>> lines)
    {
        DisplayPath = displayPath;
        FullPath = fullPath;
        Summary = summary;
        Message = message;
        Lines = lines;
    }

    /// <summary>Repo-relative path, shown in the title bar.</summary>
    public string DisplayPath { get; }

    /// <summary>Absolute path, shown as the window's tooltip.</summary>
    public string FullPath { get; }

    /// <summary>Line count, language and size, for the status strip.</summary>
    public string Summary { get; }

    /// <summary>Why there is nothing to show, when there is nothing to show.</summary>
    public string? Message { get; }

    /// <summary>The coloured content, one entry per line.</summary>
    public IReadOnlyList<IReadOnlyList<HighlightedSpan>> Lines { get; }

    /// <summary>Whether <see cref="Lines"/> is worth rendering.</summary>
    public bool HasContent => Message is null;

    /// <summary>
    /// Reads and colours <paramref name="fullPath"/>. Every failure becomes a
    /// <see cref="Message"/> rather than an exception: this is opened by a double-click, and a
    /// double-click on an unreadable file should explain itself, not take the application down.
    /// </summary>
    /// <param name="fullPath">Absolute path to read.</param>
    /// <param name="displayPath">Repo-relative path, for the title.</param>
    public static FileViewerViewModel Load(string fullPath, string displayPath)
    {
        ArgumentNullException.ThrowIfNull(fullPath);
        ArgumentNullException.ThrowIfNull(displayPath);

        try
        {
            FileInfo info = new(fullPath);
            if (!info.Exists)
            {
                return Refused(displayPath, fullPath, "That file is no longer there.");
            }

            if (info.Length > MaximumBytes)
            {
                return Refused(
                    displayPath,
                    fullPath,
                    $"Too large to open here ({Describe(info.Length)}). The limit is {Describe(MaximumBytes)}.");
            }

            if (LooksBinary(fullPath))
            {
                return Refused(displayPath, fullPath, $"This looks like a binary file ({Describe(info.Length)}).");
            }

            string text = File.ReadAllText(fullPath, Encoding.UTF8);

            // Colouring is dropped rather than the file being refused: seeing a large file
            // uncoloured beats not seeing it.
            bool colour = info.Length <= MaximumHighlightedBytes;
            SyntaxLanguage language = colour ? SyntaxLanguageDetector.FromPath(fullPath) : SyntaxLanguage.None;
            IReadOnlyList<IReadOnlyList<HighlightedSpan>> lines = HighlightedDocument.Build(text, language);

            return new FileViewerViewModel(
                displayPath,
                fullPath,
                Summarise(lines.Count, info.Length, language, colour),
                message: null,
                lines);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return Refused(displayPath, fullPath, $"Could not read the file: {ex.Message}");
        }
    }

    private static FileViewerViewModel Refused(string displayPath, string fullPath, string message) =>
        new(displayPath, fullPath, string.Empty, message, []);

    private static string Summarise(int lines, long bytes, SyntaxLanguage language, bool coloured)
    {
        string name = language switch
        {
            SyntaxLanguage.CSharp => "C#",
            SyntaxLanguage.Xml => "XML",
            SyntaxLanguage.Json => "JSON",
            SyntaxLanguage.Markdown => "Markdown",
            _ => coloured ? "Plain text" : "Plain text - too large to colour",
        };

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{lines:N0} line(s) · {name} · {Describe(bytes)}");
    }

    private static string Describe(long bytes) => bytes switch
    {
        < 1024 => string.Create(CultureInfo.InvariantCulture, $"{bytes} B"),
        < 1024 * 1024 => string.Create(CultureInfo.InvariantCulture, $"{bytes / 1024.0:F1} KB"),
        _ => string.Create(CultureInfo.InvariantCulture, $"{bytes / (1024.0 * 1024.0):F1} MB"),
    };

    /// <summary>
    /// Whether the head of the file contains a NUL byte. Crude, and the same test <c>git</c>
    /// and <c>grep</c> use: text files do not contain one, and it is what stops a double-click
    /// on a PNG from filling the window with replacement characters.
    /// </summary>
    private static bool LooksBinary(string path)
    {
        using FileStream stream = File.OpenRead(path);
        Span<byte> head = stackalloc byte[BinarySniffBytes];
        int read = stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
        return head[..read].Contains((byte)0);
    }
}
