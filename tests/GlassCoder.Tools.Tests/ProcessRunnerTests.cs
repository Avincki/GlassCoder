using System.Text;
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
    public async Task A_child_that_writes_utf8_is_read_as_utf8()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        // The defect this pins. .NET decodes a redirected stream with the console code page while
        // everything this harness launches emits UTF-8, so an em dash arrived as "â€"" and a
        // section sign as "Â§". The retrospective then wrote that into its own reports,
        // faithfully, as UTF-8: the damage was baked in at the boundary, and no amount of care in
        // the file writing could undo it.
        //
        // The parent's console encoding is forced to a single-byte one for the duration, and that
        // is not ceremony - it is the reason this shipped. The runner inherits whatever the
        // *parent* has, and a test host's console is not a WPF application's, so the natural form
        // of this test passes against a harness that is broken in the app. Latin1 rather than
        // 1252 because .NET Core carries it without an encoding provider, and it mangles UTF-8
        // the same way.
        Encoding original;
        try
        {
            original = Console.OutputEncoding;
            Console.OutputEncoding = Encoding.Latin1;
        }
        catch (IOException)
        {
            // No console attached to stand in for the app's. Nothing to prove here.
            return;
        }

        try
        {
            ProcessRunner runner = new();

            ProcessRunResult result = await runner.RunAsync(
                new ProcessRunRequest("powershell.exe", Emit("em—dash · §7 … 2.5×4")));

            result.ExitCode.ShouldBe(0);
            result.StandardOutput.ShouldContain("em—dash · §7 … 2.5×4");
            result.StandardOutput.ShouldNotContain("â", customMessage: "the mojibake signature of a byte-wise read");
        }
        finally
        {
            Console.OutputEncoding = original;
        }
    }

    [Fact]
    public async Task What_goes_in_on_stdin_survives_the_journey()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        // The other direction, and the one that compounded. The retrospective hands stage 1's
        // report to stage 2 on stdin and both to stage 3, so a mis-encoded write corrupted the
        // next session's material as well as its own output.
        ProcessRunner runner = new();

        // The child reads the raw pipe as UTF-8 and echoes raw UTF-8 bytes back, so what is
        // asserted is the bytes this runner wrote and not PowerShell's own console encoding -
        // which transliterates an em dash to a hyphen given the chance. Claude Code, being node,
        // reads its stdin as UTF-8 exactly like this.
        ProcessRunResult result = await runner.RunAsync(new ProcessRunRequest(
            "powershell.exe",
            Script(
                "$r = New-Object IO.StreamReader([Console]::OpenStandardInput(), [Text.Encoding]::UTF8); " +
                "$b = [Text.Encoding]::UTF8.GetBytes($r.ReadToEnd()); " +
                "$o = [Console]::OpenStandardOutput(); $o.Write($b, 0, $b.Length); $o.Flush()"))
        {
            StandardInput = "round—trip · §7",
        });

        result.ExitCode.ShouldBe(0);
        result.StandardOutput.ShouldContain("round—trip · §7");
    }

    /// <summary>
    /// A child that writes <paramref name="text"/> to its standard output as raw UTF-8 bytes.
    /// <para>
    /// Bytes rather than <c>Write-Output</c> on purpose: PowerShell encodes its own output with
    /// whatever console encoding it ends up with, and will happily transliterate an em dash to a
    /// hyphen. That would make this a test of PowerShell rather than of the runner.
    /// </para>
    /// </summary>
    private static string[] Emit(string text) => Script(
        $"$b = [Text.Encoding]::UTF8.GetBytes('{text}'); " +
        "$o = [Console]::OpenStandardOutput(); $o.Write($b, 0, $b.Length); $o.Flush()");

    /// <summary>
    /// A PowerShell child, handed its script as base64 UTF-16.
    /// <para>
    /// <c>-EncodedCommand</c> rather than <c>-Command</c> because the script itself contains the
    /// characters under test, and a command line is one more encoding boundary between the
    /// assertion and what it is trying to measure.
    /// </para>
    /// </summary>
    private static string[] Script(string script) =>
        ["-NoProfile", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(script))];

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
