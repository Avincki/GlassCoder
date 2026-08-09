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

    /// <summary>
    /// The file group must survive parentheses in the path: on a machine whose repos live under
    /// "Dropbox (Personal)", the old <c>[^(]</c> file group stopped mid-directory-name and every
    /// located diagnostic lost its file and line. Run d21eb210 received CS0101 as "across 0
    /// file(s)", guessed wrong about which files collided, and deleted its own deliverable.
    /// </summary>
    [Fact]
    public void A_path_containing_parentheses_keeps_its_location()
    {
        const string output =
            @"C:\Users\A\Dropbox (Personal)\repos\Test\src\Class1.cs(1,11): error CS0101: The namespace '<global namespace>' already contains a definition for 'ArrayProcessor' [C:\Users\A\Dropbox (Personal)\repos\Test\src\Proj.csproj]";

        CodeDiagnostic diagnostic = MsBuildOutputParser.Parse(output).ShouldHaveSingleItem();
        diagnostic.Id.ShouldBe("CS0101");
        diagnostic.FilePath.ShouldEndWith("Class1.cs");
        diagnostic.Line.ShouldBe(1);
        diagnostic.Column.ShouldBe(11);
    }

    /// <summary>
    /// Restore and SDK failures name the project file, not a source location, and the path is
    /// rooted. Before the prefix admitted that shape, these lines parsed to nothing and the
    /// model was told "Build failed with 0 error(s)" - seven times across the 2026-08-06 runs.
    /// </summary>
    [Fact]
    public void A_restore_error_prefixed_by_a_rooted_project_path_is_parsed()
    {
        const string output = """
            Determining projects to restore...
            C:\repo\src\Proj\Proj.csproj : error NU1101: Unable to find package Xunit. No packages exist with this id in source(s): local
            """;

        CodeDiagnostic diagnostic = MsBuildOutputParser.Parse(output).ShouldHaveSingleItem();
        diagnostic.Id.ShouldBe("NU1101");
        diagnostic.Severity.ShouldBe(CodeSeverity.Error);
        diagnostic.Message.ShouldStartWith("Unable to find package Xunit");
    }

    [Fact]
    public void An_sdk_error_about_a_missing_assets_file_is_parsed()
    {
        const string output =
            @"C:\repo\src\Tests\Tests.csproj : error NETSDK1005: Assets file 'C:\repo\src\Tests\obj\project.assets.json' not found. Run a NuGet package restore.";

        CodeDiagnostic diagnostic = MsBuildOutputParser.Parse(output).ShouldHaveSingleItem();
        diagnostic.Id.ShouldBe("NETSDK1005");
        diagnostic.Severity.ShouldBe(CodeSeverity.Error);
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

    /// <summary>
    /// The delta the model could not see (workplan task 69).
    /// <para>
    /// Run <c>d5edbc59</c> wrote a test whose expected literal was wrong by 5×10⁻³ and was told
    /// only "2 of 7 tests failed". It loosened the tolerance, which could not help, and then
    /// replaced the expected value with the expression under test, which made the assertion
    /// unfailable. The actual product was in the runner's output the whole time.
    /// </para>
    /// </summary>
    [Fact]
    public void A_failing_assertion_carries_its_expected_and_actual()
    {
        const string output = """
            [xUnit.net 00:00:00.61]     MultiplyAppTests.MultiplyViewModelTests.Multiply_Decimals [FAIL]
              Failed MultiplyAppTests.MultiplyViewModelTests.Multiply_Decimals [< 1 ms]
              Error Message:
               Assert.Equal() Failure: Values differ
               Expected: 7.011652
               Actual:   7.006652
              Stack Trace:
                 at MultiplyAppTests.MultiplyViewModelTests.Multiply_Decimals() in C:\w\Tests.cs:line 80
            Failed!  - Failed:     1, Passed:     6, Skipped:     0, Total:     7, Duration: 131 ms
            """;

        TestFailure failure = TestOutputParser.Parse(output).Failures.ShouldHaveSingleItem();

        failure.Name.ShouldBe("MultiplyAppTests.MultiplyViewModelTests.Multiply_Decimals");
        failure.Message.ShouldContain("7.011652");
        failure.Message.ShouldContain("7.006652");

        // The frames are dropped: a model repairing an assertion needs the numbers, not the stack.
        failure.Message.ShouldNotContain("Stack Trace");
        failure.Message.ShouldNotContain("Tests.cs:line 80");

        // And the timing that sits on the name's own line is not mistaken for the message.
        failure.Message.ShouldNotContain("ms]");
    }

    [Fact]
    public void Each_failing_test_gets_its_own_message()
    {
        const string output = """
              Failed A.B.First [1 ms]
              Error Message:
               Assert.True() Failure
              Stack Trace:
                 at A.B.First()
              Failed A.B.Second [2 ms]
              Error Message:
               Assert.Equal() Failure: Expected: 3 Actual: 4
              Stack Trace:
                 at A.B.Second()
            Failed!  - Failed:     2, Passed:     0, Skipped:     0, Total:     2, Duration: 9 ms
            """;

        IReadOnlyList<TestFailure> failures = TestOutputParser.Parse(output).Failures;

        failures.Count.ShouldBe(2);
        failures[0].Message.ShouldContain("Assert.True");
        failures[0].Message.ShouldNotContain("Expected: 3", Case.Sensitive, "one block must not bleed into the next");
        failures[1].Message.ShouldContain("Actual: 4");
    }

    [Fact]
    public void A_runner_that_says_nothing_about_a_failure_still_parses()
    {
        // The tolerance that matters: no Error Message label, no Stack Trace, no crash - and the
        // names, which are what the older behaviour gave, are still there.
        const string output = """
              Failed A.B.Silent [1 ms]
            Failed!  - Failed:     1, Passed:     0, Skipped:     0, Total:     1, Duration: 9 ms
            """;

        TestOutcome outcome = TestOutputParser.Parse(output);

        outcome.FailedTests.ShouldContain("A.B.Silent");
        outcome.Failures.ShouldSatisfyAllConditions(
            () => outcome.Failures.Count.ShouldBeLessThanOrEqualTo(1),
            () => outcome.Failures.All(f => f.Message.Length > 0).ShouldBeTrue());
    }

    /// <summary>
    /// The messages ride <em>under</em> the count line, never in it. The run progress sentry keys
    /// repeated failures on the first line, so a message spliced into it would make one recurring
    /// failure look like a new one on every step.
    /// </summary>
    [Fact]
    public void The_described_failures_never_disturb_the_first_line()
    {
        string described = TestOutputParser.Describe(
        [
            new TestFailure("A.B.First", "Assert.Equal() Failure: Expected: 3 Actual: 4"),
        ]);

        described.ShouldStartWith("\n");
        described.ShouldContain("A.B.First: Assert.Equal()");
        TestOutputParser.Describe([]).ShouldBeEmpty();
        TestOutputParser.Describe(null).ShouldBeEmpty();
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

    /// <summary>
    /// Keeping MSBuild resident is worth about 580 ms of every host build - measured on this
    /// machine, a no-op incremental build going from ~980 ms to ~400 ms. It is host-only on
    /// purpose: the container gets a fresh one per command, where a resident server has nothing
    /// to be resident in.
    /// </summary>
    [Fact]
    public async Task A_host_dotnet_command_asks_for_the_msbuild_server()
    {
        SandboxOptions options = new() { Mode = SandboxMode.Local, AllowUnsandboxedExecution = true };
        FakeProcessRunner runner = new();

        await new LocalCommandExecutor(runner, GlassCoder.TestSupport.TempWorkspace.Wrap(options))
            .ExecuteAsync(new CommandRequest("dotnet", ["build"]));

        runner.Requests.Single().Environment
            .ShouldNotBeNull()["DOTNET_CLI_USE_MSBUILD_SERVER"].ShouldBe("1");
    }

    [Fact]
    public async Task Anything_that_is_not_dotnet_is_left_alone()
    {
        // An environment variable handed to git, or to a model's shell command, is a side effect
        // nobody asked for and nobody would think to look for.
        SandboxOptions options = new() { Mode = SandboxMode.Local, AllowUnsandboxedExecution = true };
        FakeProcessRunner runner = new();

        await new LocalCommandExecutor(runner, GlassCoder.TestSupport.TempWorkspace.Wrap(options))
            .ExecuteAsync(new CommandRequest("git", ["status"]));

        runner.Requests.Single().Environment.ShouldBeNull();
    }

    [Fact]
    public async Task The_msbuild_server_can_be_switched_off()
    {
        // It leaves a ~170 MB MSBuild process alive holding handles under the workspace, and this
        // repository already carries a bounded build retry for lock flakes from exactly that.
        SandboxOptions options = new()
        {
            Mode = SandboxMode.Local,
            AllowUnsandboxedExecution = true,
            UseMsBuildServer = false,
        };
        FakeProcessRunner runner = new();

        await new LocalCommandExecutor(runner, GlassCoder.TestSupport.TempWorkspace.Wrap(options))
            .ExecuteAsync(new CommandRequest("dotnet", ["build"]));

        runner.Requests.Single().Environment.ShouldBeNull();
    }

    /// <summary>
    /// The shipped configuration: <c>Mode: Docker</c> with the fallback permitted. This is the
    /// branch every build on a machine without a container runtime now takes, so it is worth a
    /// test of its own rather than being inferred from the two Local-mode cases above.
    /// <para>
    /// The endpoint is a port nothing listens on, so the ping is refused on any machine - the
    /// assertion holds whether or not the developer running it has Docker.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Docker_mode_falls_back_to_the_host_when_the_daemon_is_unreachable()
    {
        SandboxOptions options = new()
        {
            Mode = SandboxMode.Docker,
            DockerEndpoint = UnreachableDockerEndpoint,
            AllowUnsandboxedExecution = true,
        };
        FakeProcessRunner runner = new();
        runner.Enqueue(0, "Build succeeded.");

        SandboxedCommandExecutor executor = new(
            new DockerCommandExecutor(GlassCoder.TestSupport.TempWorkspace.Wrap(options), new StubGuard()),
            new LocalCommandExecutor(runner, GlassCoder.TestSupport.TempWorkspace.Wrap(options)),
            GlassCoder.TestSupport.TempWorkspace.Wrap(options));

        CommandResult result = await executor.ExecuteAsync(new CommandRequest("dotnet", ["build"]));

        result.Succeeded.ShouldBeTrue();
        result.Sandbox.ShouldBe("host", "an unreachable daemon should degrade to the host, not to nothing");
        result.StandardOutput.ShouldContain("Build succeeded.");
    }

    /// <summary>
    /// The other half of that branch: the same unreachable daemon, with the fallback withheld,
    /// still refuses. Turning the fallback on is what changes the outcome - not Docker mode being
    /// configured, which on its own guarantees nothing about where a command ends up running.
    /// </summary>
    [Fact]
    public async Task Docker_mode_still_refuses_when_the_fallback_has_not_been_permitted()
    {
        SandboxOptions options = new()
        {
            Mode = SandboxMode.Docker,
            DockerEndpoint = UnreachableDockerEndpoint,
            AllowUnsandboxedExecution = false,
        };
        SandboxedCommandExecutor executor = new(
            new DockerCommandExecutor(GlassCoder.TestSupport.TempWorkspace.Wrap(options), new StubGuard()),
            new LocalCommandExecutor(new FakeProcessRunner(), GlassCoder.TestSupport.TempWorkspace.Wrap(options)),
            GlassCoder.TestSupport.TempWorkspace.Wrap(options));

        CommandResult result = await executor.ExecuteAsync(new CommandRequest("dotnet", ["build"]));

        result.Succeeded.ShouldBeFalse();
        result.FailureReason.ShouldContain("AllowUnsandboxedExecution");
    }

    /// <summary>A port nothing listens on, so the Docker ping is refused rather than answered.</summary>
    private const string UnreachableDockerEndpoint = "tcp://127.0.0.1:1";

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
