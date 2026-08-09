using System.Diagnostics;

namespace GlassCoder.Tools.Processes;

/// <summary>
/// Whether a process has put a window on the screen (workplan task 71).
/// <para>
/// A seam of its own, and not a method on <see cref="IProcessRunner"/>, because it is the one
/// piece of this that is platform knowledge: everything else about launching an application is
/// the same everywhere, and a test that wants to say "the window appeared after 400ms" should not
/// need a desktop to say it.
/// </para>
/// </summary>
public interface IWindowPresence
{
    /// <summary>Whether the process is showing a top-level window right now.</summary>
    /// <param name="processId">The process to ask about.</param>
    bool HasVisibleWindow(int processId);
}

/// <summary>
/// The real implementation, over <see cref="Process.MainWindowHandle"/>.
/// <para>
/// Windows-only, and false everywhere else - which is a fallback rather than a failure. A caller
/// that never sees true simply waits out its timeout, which is exactly the behaviour that existed
/// before this seam did, so no platform loses a capability it had.
/// </para>
/// <para>
/// <strong>The handle belongs to the process that owns the window, and that is why the caller
/// launches the application's own executable rather than <c>dotnet run</c>.</strong> Under
/// <c>dotnet run</c> the window belongs to a grandchild, this returns false for the whole
/// timeout, and the answer looks like "it never drew anything" when it drew immediately.
/// </para>
/// </summary>
public sealed class WindowPresence : IWindowPresence
{
    /// <inheritdoc />
    public bool HasVisibleWindow(int processId)
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            using Process process = Process.GetProcessById(processId);

            // Without this the handle is whatever it was when the Process object was created,
            // which for a window that has just opened is zero, forever.
            process.Refresh();
            return process.MainWindowHandle != IntPtr.Zero;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            // The process has already gone. That is not a window, and it is not an error either -
            // the caller is about to notice the exit on its own.
            return false;
        }
    }
}
