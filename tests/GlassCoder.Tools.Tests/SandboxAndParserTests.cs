using Docker.DotNet.Models;
using GlassCoder.TestSupport;
using GlassCoder.Tools.Execution;
using GlassCoder.Tools.Verification;

namespace GlassCoder.Tools.Tests;

/// <summary>
/// The sandbox policy and the build-output parsers (workplan task 17).
/// <para>
/// The container spec is tested as a pure function because a sandbox whose rules can only be
/// checked by running it is a sandbox nobody can audit.
/// </para>
/// </summary>
public sealed class SandboxAndParserTests
{
    private const string RepoRoot = @"C:\repo";

    [Fact]
    public void The_container_mounts_the_repository_and_nothing_else()
    {
        CreateContainerParameters parameters = DockerRunSpec.Create(
            new CommandRequest("dotnet", ["build"]) { WorkingDirectory = @"C:\repo\src\Proj" },
            new SandboxOptions(),
            RepoRoot);

        parameters.HostConfig.Binds.ShouldHaveSingleItem().ShouldBe(@"C:\repo:/workspace");
        parameters.WorkingDir.ShouldBe("/workspace/src/Proj");
        parameters.Cmd.ShouldBe(["dotnet", "build"]);
    }

    [Fact]
    public void The_network_is_dropped_by_default()
    {
        string mode = DockerRunSpec.ResolveNetworkMode(new CommandRequest("dotnet", ["build"]), new SandboxOptions());

        mode.ShouldBe(DockerRunSpec.NoNetwork);
    }

    [Fact]
    public void A_restore_may_have_the_network_when_policy_allows_that_exception()
    {
        SandboxOptions options = new() { AllowNetwork = false, AllowNetworkForRestore = true };

        DockerRunSpec.ResolveNetworkMode(new CommandRequest("dotnet", ["restore"]) { RequiresNetwork = true }, options)
            .ShouldBe(DockerRunSpec.BridgeNetwork);

        DockerRunSpec.ResolveNetworkMode(new CommandRequest("dotnet", ["build"]) { RequiresNetwork = false }, options)
            .ShouldBe(DockerRunSpec.NoNetwork);
    }

    [Fact]
    public void A_restore_is_still_denied_the_network_when_policy_says_no()
    {
        SandboxOptions options = new() { AllowNetwork = false, AllowNetworkForRestore = false };

        DockerRunSpec.ResolveNetworkMode(new CommandRequest("dotnet", ["restore"]) { RequiresNetwork = true }, options)
            .ShouldBe(DockerRunSpec.NoNetwork);
    }

    [Fact]
    public void A_working_directory_outside_the_mount_is_refused()
    {
        Should.Throw<ArgumentException>(() => DockerRunSpec.Create(
            new CommandRequest("dotnet", ["build"]) { WorkingDirectory = @"C:\elsewhere" },
            new SandboxOptions(),
            RepoRoot));
    }

    [Fact]
    public void Msbuild_diagnostics_are_parsed_into_typed_records()
    {
        const string output = """
            Determining projects to restore...
            C:\repo\src\Pager.cs(12,34): error CS0103: The name 'x' does not exist in the current context [C:\repo\src\Proj.csproj]
            C:\repo\src\Pager.cs(20,5): warning CA1822: Member 'Do' can be marked as static [C:\repo\src\Proj.csproj]
            Build FAILED.
            """;

        IReadOnlyList<CodeDiagnostic> diagnostics = MsBuildOutputParser.Parse(output, p => p.Replace(@"C:\repo\", "").Replace('\\', '/'));

        diagnostics.Count.ShouldBe(2);
        diagnostics[0].Id.ShouldBe("CS0103");
        diagnostics[0].Severity.ShouldBe(CodeSeverity.Error);
        diagnostics[0].FilePath.ShouldBe("src/Pager.cs");
        diagnostics[0].Line.ShouldBe(12);
        diagnostics[0].Column.ShouldBe(34);
        diagnostics[0].Message.ShouldBe("The name 'x' does not exist in the current context");
        diagnostics[1].Severity.ShouldBe(CodeSeverity.Warning);
    }

    [Fact]
    public void A_diagnostic_repeated_per_project_is_reported_once()
    {
        // MSBuild emits the same diagnostic once per project and per target framework.
        const string output = """
            C:\repo\src\A.cs(1,1): error CS0103: nope [C:\repo\src\Proj.csproj]
            C:\repo\src\A.cs(1,1): error CS0103: nope [C:\repo\src\Other.csproj]
            """;

        MsBuildOutputParser.Parse(output).ShouldHaveSingleItem();
    }

    [Fact]
    public void A_diagnostic_without_a_location_is_still_parsed()
    {
        IReadOnlyList<CodeDiagnostic> diagnostics =
            MsBuildOutputParser.Parse("MSBUILD : error MSB1003: Specify a project or solution file.");

        CodeDiagnostic diagnostic = diagnostics.ShouldHaveSingleItem();
        diagnostic.Id.ShouldBe("MSB1003");
        diagnostic.FilePath.ShouldBeNull();
        diagnostic.Line.ShouldBe(0);
    }

    [Fact]
    public void Ordinary_build_prose_is_not_mistaken_for_a_diagnostic()
    {
        MsBuildOutputParser.Parse("  Determining projects to restore...\n  Restored C:\\repo\\src\\Proj.csproj (in 1.2 sec).")
            .ShouldBeEmpty();
    }

    [Fact]
    public void A_green_test_run_is_parsed()
    {
        TestOutcome outcome = TestOutputParser.Parse(
            "Passed!  - Failed:     0, Passed:    38, Skipped:     0, Total:    38, Duration: 752 ms");

        outcome.Ok.ShouldBeTrue();
        outcome.Passed.ShouldBe(38);
        outcome.Total.ShouldBe(38);
        outcome.FailedTests.ShouldBeEmpty();
    }

    [Fact]
    public void A_red_test_run_reports_the_failing_test_names()
    {
        const string output = """
            [xUnit.net 00:00:00.61]     GlassCoder.Core.Tests.LoggingTests.Redaction_works [FAIL]
              Failed GlassCoder.Core.Tests.LoggingTests.Redaction_works [38 ms]
            Failed!  - Failed:     1, Passed:    20, Skipped:     0, Total:    21, Duration: 131 ms
            """;

        TestOutcome outcome = TestOutputParser.Parse(output);

        outcome.Ok.ShouldBeFalse();
        outcome.Failed.ShouldBe(1);
        outcome.Passed.ShouldBe(20);
        outcome.FailedTests.ShouldContain("GlassCoder.Core.Tests.LoggingTests.Redaction_works");
    }

    [Fact]
    public async Task An_unavailable_sandbox_refuses_rather_than_falling_back_to_the_host()
    {
        // The failure mode this prevents: a silent downgrade from "containerised, no network"
        // to "your machine, full access".
        SandboxOptions options = new() { Mode = SandboxMode.Local, AllowUnsandboxedExecution = false };
        SandboxedCommandExecutor executor = new(
            new DockerCommandExecutor(GlassCoder.TestSupport.TempWorkspace.Wrap(options), new StubGuard()),
            new LocalCommandExecutor(new FakeProcessRunner(), GlassCoder.TestSupport.TempWorkspace.Wrap(options)),
            GlassCoder.TestSupport.TempWorkspace.Wrap(options));

        CommandResult result = await executor.ExecuteAsync(new CommandRequest("dotnet", ["build"]));

        result.Succeeded.ShouldBeFalse();
        result.FailureReason.ShouldContain("AllowUnsandboxedExecution");
    }

    [Fact]
    public async Task Local_execution_runs_when_it_has_been_explicitly_permitted()
    {
        SandboxOptions options = new() { Mode = SandboxMode.Local, AllowUnsandboxedExecution = true };
        FakeProcessRunner runner = new();
        runner.Enqueue(0, "Build succeeded.");

        SandboxedCommandExecutor executor = new(
            new DockerCommandExecutor(GlassCoder.TestSupport.TempWorkspace.Wrap(options), new StubGuard()),
            new LocalCommandExecutor(runner, GlassCoder.TestSupport.TempWorkspace.Wrap(options)),
            GlassCoder.TestSupport.TempWorkspace.Wrap(options));

        CommandResult result = await executor.ExecuteAsync(new CommandRequest("dotnet", ["build"]));

        result.Succeeded.ShouldBeTrue();
        result.Sandbox.ShouldBe("host");
        result.StandardOutput.ShouldContain("Build succeeded.");
    }

    private sealed class StubGuard : Guardrails.IPathGuard
    {
        public string RepoRoot => TestRoot;

        public bool HasWritablePaths => true;

        public Guardrails.PathGuardResult Resolve(string? path, Guardrails.PathAccess access) =>
            Guardrails.PathGuardResult.Allow(path ?? TestRoot, path ?? ".");

        public string ToRelativePath(string fullPath) => fullPath;

        private static string TestRoot => Path.Combine(Path.GetTempPath(), "glasscoder-stub-root");
    }
}
