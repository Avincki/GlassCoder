using System.Reflection;
using GlassCoder.Core.Verification;

namespace GlassCoder.Core.Tests;

/// <summary>
/// What it takes to actually launch Claude Code on Windows (workplan tasks 43 and 67).
/// <para>
/// Three things had to be true before a single review ran, and none of them was: the executable
/// has to be findable, the arguments have to survive the shim it is found behind, and the session
/// has to be able to authenticate. All three were established by running the real CLI; these
/// tests are what stop each from quietly coming back.
/// </para>
/// </summary>
public sealed class ClaudeCliLaunchTests
{
    /// <summary>
    /// The defect that greyed out both features on a machine where <c>claude</c> works perfectly
    /// in a terminal.
    /// <para>
    /// npm installs Claude Code on Windows as <c>claude</c>, <c>claude.cmd</c> and
    /// <c>claude.ps1</c> - there is no <c>claude.exe</c>. <c>Process.Start</c> with
    /// <c>UseShellExecute = false</c> goes through <c>CreateProcess</c>, which appends
    /// <c>.exe</c> and does not consult <c>PATHEXT</c>, so it looked for a file that is never
    /// there and threw "the system cannot find the file specified" on every probe.
    /// </para>
    /// </summary>
    [Fact]
    public void A_bare_command_name_resolves_to_a_launchable_file()
    {
        using GlassCoder.TestSupport.TempWorkspace workspace = new();

        string directory = workspace.CreateDirectory("bin");
        File.WriteAllText(Path.Combine(directory, "faketool.cmd"), "@echo off\r\n");

        string original = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        try
        {
            Environment.SetEnvironmentVariable("PATH", directory + Path.PathSeparator + original);

            string resolved = ExecutableResolver.Resolve("faketool");

            if (OperatingSystem.IsWindows())
            {
                resolved.ShouldBe(Path.Combine(directory, "faketool.cmd"));
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", original);
        }
    }

    [Fact]
    public void A_configured_path_is_taken_as_given()
    {
        // The operator pointing CliPath at a specific install is a decision, not a hint.
        string configured = Path.Combine(Path.GetTempPath(), "somewhere", "claude.cmd");
        ExecutableResolver.Resolve(configured).ShouldBe(configured);
    }

    [Fact]
    public void A_command_that_is_nowhere_is_returned_unchanged_so_the_launch_can_say_so()
    {
        ExecutableResolver.Resolve("glasscoder-no-such-command").ShouldBe("glasscoder-no-such-command");
    }

    /// <summary>
    /// Every schema handed to <c>--json-schema</c> is one line.
    /// <para>
    /// On Windows the resolved executable is npm's <c>.cmd</c> shim, so arguments pass through
    /// <c>cmd.exe</c> - which cannot carry a newline. The schemas were written as indented raw
    /// string literals, and the CLI answered <c>"--json-schema is not valid JSON: Property name
    /// must be a string literal"</c>: the argument arrived truncated at the first line break.
    /// Compact JSON survives it, which the real CLI confirmed by honouring the schema.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(typeof(ClaudeCodeFileReviewer), "ResponseSchema")]
    [InlineData(typeof(ClaudeCodeRetrospectiveReviewer), "ReportSchema")]
    [InlineData(typeof(ClaudeCodeRetrospectiveReviewer), "RecommendationSchema")]
    public void Every_response_schema_is_a_single_line(Type owner, string constant)
    {
        FieldInfo field = owner
            .GetField(constant, BindingFlags.NonPublic | BindingFlags.Static)
            .ShouldNotBeNull($"{owner.Name}.{constant} has been renamed; this guard is now checking nothing");

        string schema = (string)field.GetRawConstantValue()!;

        schema.ShouldNotContain("\n", Case.Sensitive,
            $"{owner.Name}.{constant} spans lines, which cmd.exe cannot pass as one argument");

        // And it is still valid JSON after being flattened.
        System.Text.Json.JsonDocument.Parse(schema).RootElement
            .GetProperty("type").GetString().ShouldBe("object");
    }

    /// <summary>
    /// <c>--bare</c> stays off by default. It skips the user configuration, and the subscription
    /// login lives there - a bare session answers "Not logged in · Please run /login" and every
    /// review fails. Measured against the real CLI: the identical call succeeded the moment the
    /// flag came off.
    /// </summary>
    [Fact]
    public void The_minimal_mode_that_cannot_authenticate_is_off_by_default()
    {
        new FileReviewOptions().Bare.ShouldBeFalse();
        new RetrospectiveOptions().Bare.ShouldBeFalse();
    }
}
