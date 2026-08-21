using System.Windows;

namespace GlassCoder.Wpf.Tests;

/// <summary>
/// The one <see cref="Application"/> a test host may own.
/// <para>
/// Windows and controls resolve their brushes from <c>App.xaml</c>, so a <c>StaticResource</c>
/// only binds once an application holds those resources. WPF permits exactly one
/// <see cref="Application"/> per AppDomain and throws on a second.
/// </para>
/// <para>
/// It lives here rather than as a private helper per test class because two classes each doing
/// their own <c>if (Application.Current is null)</c> is a race, not a guard: xUnit runs classes
/// in parallel, each on its own STA thread, and both can see null and both construct. That is
/// exactly what happened once a second class needed an application.
/// </para>
/// <para>
/// <em>Which</em> thread constructs it matters as much as how many do. The brushes in
/// <c>App.xaml</c> are Freezables, and a Freezable belongs to the thread that made it: a window
/// shown on any other thread dies with "Cannot use a DependencyObject that belongs to a different
/// thread than its parent Freezable". While every caller got a throwaway thread, which thread won
/// this race decided which test class could show a window - so the suite passed or failed on the
/// order xUnit happened to pick. The application is therefore built on
/// <see cref="UiThread.RunOnApplicationThread{T}"/>'s single long-lived thread, and anything that
/// shows a window runs there too.
/// </para>
/// </summary>
internal static class TestApplication
{
    /// <summary>
    /// Makes sure this host has an application, on the one thread that is allowed to own it.
    /// Safe from any thread, and safe to call from that thread.
    /// </summary>
    internal static void Ensure() => UiThread.EnsureApplicationThread();

    /// <summary>
    /// Constructs the application. Called only by the thread that owns it - going through
    /// <see cref="Ensure"/> from anywhere else is what keeps that true.
    /// </summary>
    internal static void CreateOnThisThread()
    {
        if (Application.Current is not null)
        {
            return;
        }

        App application = new();
        application.InitializeComponent();
    }
}
