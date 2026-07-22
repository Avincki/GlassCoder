using GlassCoder.TestSupport;
using GlassCoder.Tools.Processes;

namespace GlassCoder.Tools.Tests;

/// <summary>
/// The process seam (workplan task 8): a real implementation that captures both streams and
/// honours a timeout, and a fake that lets every other test avoid launching anything at all.
/// </summary>
public sealed class ProcessRunnerTests
{
    [Fact]
    public async Task The_fake_records_requests_and_replays_scripted_results()
    {
        FakeProcessRunner runner = new();
        runner.Enqueue(0, "Build succeeded.").Enqueue(1, standardError: "error CS1002: ; expected");

        ProcessRunResult first = await runner.RunAsync(new ProcessRunRequest("dotnet", ["build"]));
        ProcessRunResult second = await runner.RunAsync(new ProcessRunRequest("dotnet", ["test"]));

        first.Succeeded.ShouldBeTrue();
        first.StandardOutput.ShouldBe("Build succeeded.");
        second.ExitCode.ShouldBe(1);
        second.StandardError.ShouldContain("CS1002");
        runner.Requests.Select(r => r.Arguments[0]).ShouldBe(["build", "test"]);
    }

    [Fact]
    public async Task The_fake_falls_back_to_its_default_result()
    {
        FakeProcessRunner runner = new() { Default = new ProcessRunResult(42, "out", "err", TimeSpan.Zero, false) };

        ProcessRunResult result = await runner.RunAsync(new ProcessRunRequest("anything", []));

        result.ExitCode.ShouldBe(42);
        result.Succeeded.ShouldBeFalse();
    }

    [Fact]
    public async Task The_real_runner_captures_stdout_and_the_exit_code()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        ProcessRunner runner = new();

        ProcessRunResult result = await runner.RunAsync(
            new ProcessRunRequest("cmd.exe", ["/c", "echo glasscoder& exit /b 3"]));

        result.ExitCode.ShouldBe(3);
        result.StandardOutput.ShouldContain("glasscoder");
        result.TimedOut.ShouldBeFalse();
    }

    [Fact]
    public async Task The_real_runner_kills_a_process_that_outlives_its_timeout()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        ProcessRunner runner = new();

        ProcessRunResult result = await runner.RunAsync(new ProcessRunRequest(
            "cmd.exe",
            ["/c", "ping -n 30 127.0.0.1 > nul"])
        {
            Timeout = TimeSpan.FromMilliseconds(300),
        });

        result.TimedOut.ShouldBeTrue();
        result.ExitCode.ShouldBe(-1);
    }
}
