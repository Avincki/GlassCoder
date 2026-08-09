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

    /// <summary>
    /// The point of <see cref="ProcessRunRequest.ReadyWhen"/> (workplan task 71): a process that
    /// is never going to exit can still say it has done the thing that was worth waiting for, and
    /// the wait ends there instead of at the timeout.
    /// </summary>
    [Fact]
    public async Task A_ready_signal_ends_the_wait_without_spending_the_timeout()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        ProcessRunner runner = new();
        int polls = 0;

        // ping -n 20 runs for about nineteen seconds; the timeout is fifteen. Both are far enough
        // from the assertion below that a slow machine cannot turn a pass into a failure.
        ProcessRunResult result = await runner.RunAsync(
            new ProcessRunRequest("cmd.exe", ["/c", "ping", "-n", "20", "127.0.0.1"])
            {
                Timeout = TimeSpan.FromSeconds(15),
                ReadyPollInterval = TimeSpan.FromMilliseconds(50),
                ReadyWhen = _ => ++polls >= 2,
            });

        result.ReadySignalled.ShouldBeTrue();
        result.TimedOut.ShouldBeFalse();
        result.Duration.ShouldBeLessThan(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task A_ready_signal_that_never_comes_leaves_the_timeout_in_charge()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        ProcessRunner runner = new();

        ProcessRunResult result = await runner.RunAsync(
            new ProcessRunRequest("cmd.exe", ["/c", "ping", "-n", "20", "127.0.0.1"])
            {
                Timeout = TimeSpan.FromSeconds(1),
                ReadyPollInterval = TimeSpan.FromMilliseconds(50),
                ReadyWhen = _ => false,
            });

        result.TimedOut.ShouldBeTrue();
        result.ReadySignalled.ShouldBeFalse();
    }

    [Fact]
    public async Task A_process_that_exits_before_it_is_ready_still_reports_its_own_exit_code()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        ProcessRunner runner = new();

        ProcessRunResult result = await runner.RunAsync(
            new ProcessRunRequest("cmd.exe", ["/c", "exit /b 7"])
            {
                Timeout = TimeSpan.FromSeconds(15),
                ReadyPollInterval = TimeSpan.FromMilliseconds(20),
                ReadyWhen = _ => false,
            });

        result.ExitCode.ShouldBe(7);
        result.ReadySignalled.ShouldBeFalse();
        result.TimedOut.ShouldBeFalse();
    }

    /// <summary>
    /// A predicate that throws is read as not-ready, never as a reason to tear down the launch -
    /// the bargain <see cref="ProcessRunRequest.OnOutputLine"/> already strikes.
    /// </summary>
    [Fact]
    public async Task A_readiness_predicate_that_throws_does_not_kill_the_process()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        ProcessRunner runner = new();

        ProcessRunResult result = await runner.RunAsync(
            new ProcessRunRequest("cmd.exe", ["/c", "echo glasscoder"])
            {
                Timeout = TimeSpan.FromSeconds(15),
                ReadyPollInterval = TimeSpan.FromMilliseconds(20),
                ReadyWhen = _ => throw new InvalidOperationException("the watcher is broken"),
            });

        result.ExitCode.ShouldBe(0);
        result.StandardOutput.ShouldContain("glasscoder");
        result.ReadySignalled.ShouldBeFalse();
    }

    /// <summary>
    /// The .NET CLI's progress reporter writes cursor-control sequences even into a redirected
    /// pipe. Run 4c7de12b received 21 of them, and step 9's Compile-rung failure summary was
    /// nothing but escapes - the model was told verification FAILED and given no legible cause.
    /// Stripped at the one place both streams are collected, not in each parser.
    /// </summary>
    [Theory]
    [InlineData("\x1B[?25l\x1B[1Fcsproj\x1B[?25h", "csproj")]
    [InlineData("\x1B[120G\x1B[6D(0.1s)", "(0.1s)")]
    [InlineData("\x1B[32mBuild succeeded.\x1B[0m", "Build succeeded.")]
    // \u0007 rather than \x07: a \x escape is variable-length, so "\x07error"
    // would parse as the single character \x07e followed by "rror".
    [InlineData("\x1B]0;a title\u0007error CS0103: broken", "error CS0103: broken")]
    [InlineData("nothing to strip", "nothing to strip")]
    public void Terminal_control_sequences_are_stripped(string raw, string expected) =>
        TerminalCodes.Strip(raw).ShouldBe(expected);

    [Fact]
    public void A_line_with_no_escapes_is_returned_unchanged()
    {
        // The overwhelmingly common case, and worth not allocating for.
        const string Line = "error CS0246: The type or namespace name 'Widget' could not be found";

        TerminalCodes.Strip(Line).ShouldBeSameAs(Line);
    }

    [Fact]
    public async Task Captured_output_reaches_the_caller_without_control_sequences()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        ProcessRunner runner = new();

        // cmd echoes the literal escape byte back through the redirected pipe.
        ProcessRunResult result = await runner.RunAsync(
            new ProcessRunRequest("cmd.exe", ["/c", "echo \x1B[32mglasscoder\x1B[0m"]));

        result.StandardOutput.ShouldContain("glasscoder");
        result.StandardOutput.ShouldNotContain("\x1B");
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
