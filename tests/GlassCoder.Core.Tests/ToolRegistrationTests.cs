using GlassCoder.TestSupport;
using GlassCoder.Tools.DependencyInjection;
using GlassCoder.Tools.Execution;
using GlassCoder.Tools.Registry;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace GlassCoder.Core.Tests;

/// <summary>
/// Which tools are advertised, and the configuration that decides it.
/// <para>
/// Both privileged capabilities are opt-in behind a single configuration key, and until now
/// nothing asserted that either key was spelled the way the settings file spells it. A typo
/// there does not fail: it silently ships a harness missing a capability somebody switched on.
/// </para>
/// </summary>
public sealed class ToolRegistrationTests
{
    /// <summary>
    /// In advertised order, which is also the order the work happens in: plan, read, search,
    /// survey the projects, write, then the two oracles with project wiring between them.
    /// </summary>
    private static readonly string[] BaseTools =
    [
        "update_todos", "list_changes", "read_file", "grep", "find_symbol", "glob", "list_projects",
        "create_file", "edit_file", "file_operation", "build", "dotnet_project", "run_tests",

        // Workplan task 71. Advertised by default and deliberately: the refutation it answers -
        // "no evidence the application runs" - is one the loop received twice with no way to reply,
        // and a tool switched off by default would leave it exactly as unanswerable.
        "launch_app",
    ];

    private static readonly string[] GitTools =
    [
        "git_status", "git_commit", "git_sync", "git_push", "create_pull_request",
    ];

    [Fact]
    public void The_privileged_tools_are_absent_by_default()
    {
        IReadOnlyList<string> tools = Tools();

        tools.ShouldBe(BaseTools);
        tools.ShouldNotContain("bash");
    }

    [Fact]
    public void The_bash_tool_appears_only_when_its_switch_is_on()
    {
        Tools(("GlassCoder:Sandbox:EnableBashTool", "true")).ShouldContain("bash");
    }

    [Fact]
    public void The_git_tools_appear_only_when_their_switch_is_on()
    {
        IReadOnlyList<string> tools = Tools(("GlassCoder:Git:Enabled", "true"));

        foreach (string tool in GitTools)
        {
            tools.ShouldContain(tool);
        }
    }

    [Fact]
    public void Both_switches_together_advertise_every_tool()
    {
        IReadOnlyList<string> tools = Tools(
            ("GlassCoder:Sandbox:EnableBashTool", "true"),
            ("GlassCoder:Git:Enabled", "true"));

        tools.Count.ShouldBe(BaseTools.Length + GitTools.Length + 1);
    }

    /// <summary>
    /// Builds the tool subsystem over the given configuration and returns what it advertises.
    /// The command executor is faked first so <c>TryAddSingleton</c> leaves it alone: resolving
    /// the real one would reach for a Docker daemon this test has no opinion about.
    /// </summary>
    private static IReadOnlyList<string> Tools(params (string Key, string Value)[] settings)
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings.Select(s => new KeyValuePair<string, string?>(s.Key, s.Value)))
            .Build();

        ServiceCollection services = new();
        services.AddSingleton<ICommandExecutor>(new ScriptedCommandExecutor());
        services.AddGlassCoderTools(configuration);

        using ServiceProvider provider = services.BuildServiceProvider();
        return [.. provider.GetRequiredService<IToolRegistry>().Functions.Select(f => f.Name)];
    }
}
