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
/// </summary>
internal static class TestApplication
{
    private static readonly Lock Gate = new();

    /// <summary>Creates the application if this host has none. Safe from any thread.</summary>
    internal static void Ensure()
    {
        lock (Gate)
        {
            if (Application.Current is not null)
            {
                return;
            }

            App application = new();
            application.InitializeComponent();
        }
    }
}
