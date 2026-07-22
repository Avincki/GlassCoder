using System.Reflection;
using GlassCoder.Core.Agent;

namespace GlassCoder.Core.Tests;

/// <summary>
/// The layering rule from CLAUDE.md §4 and workplan task 1, asserted rather than hoped for:
/// a UI reference leaking into Core is a defect, and it would silently make the harness
/// un-runnable headless in CI.
/// </summary>
public sealed class ArchitectureTests
{
    private static readonly string[] UiAssemblies =
    [
        "PresentationFramework",
        "PresentationCore",
        "WindowsBase",
        "System.Windows.Forms",
        "System.Drawing",
    ];

    [Theory]
    [InlineData("GlassCoder.Core")]
    [InlineData("GlassCoder.Tools")]
    [InlineData("GlassCoder.Models")]
    public void The_harness_libraries_have_no_ui_dependency(string assemblyName)
    {
        Assembly assembly = Assembly.Load(assemblyName);

        IEnumerable<string> referenced = assembly.GetReferencedAssemblies().Select(a => a.Name!);

        referenced.Intersect(UiAssemblies, StringComparer.OrdinalIgnoreCase).ShouldBeEmpty();
    }

    [Fact]
    public void Core_targets_a_framework_that_can_run_headless()
    {
        Assembly core = typeof(AgentLoop).Assembly;

        core.GetCustomAttribute<System.Runtime.Versioning.TargetFrameworkAttribute>()!
            .FrameworkName.ShouldNotContain("windows");
    }
}
