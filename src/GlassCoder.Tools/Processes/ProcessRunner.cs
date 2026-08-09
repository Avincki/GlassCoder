using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;

namespace GlassCoder.Tools.Processes;

/// <summary>
/// Real <see cref="IProcessRunner"/>: launches a child process, captures both streams without
/// deadlocking, and kills the whole process tree on timeout or cancellation.
/// </summary>
/// <remarks>
/// This runs a process on the host. From task 17 onward the build and test tools wrap it in a
/// container, because a build is arbitrary code execution (CLAUDE.md §8.4).
/// </remarks>
public sealed class ProcessRunner : IProcessRunner
{
    /// <summary>
    /// How every redirected stream is read and written. Without a BOM, because one written to a
    /// child's stdin is three bytes of rubbish at the head of its prompt.
    /// </summary>
    /// <remarks>
    /// Not a default worth leaving alone. On Windows .NET decodes a redirected stream with the
    /// <em>console</em> code page - 1252 on this machine - and every tool this harness launches
    /// emits UTF-8. So an em dash came back as <c>â€"</c>, an ellipsis as <c>â€¦</c>, and a
    /// section sign as <c>Â§</c>; the retrospective then wrote that faithfully into its own
    /// reports as UTF-8, which baked the damage in. The reports were unreadable in exactly the
    /// places a reviewer had bothered to punctuate carefully.
    /// <para>
    /// Both directions matter. Stage 2 is handed stage 1's report on stdin and stage 3 is handed
    /// both, so a mis-encoded write corrupts the <em>input</em> to the next session as well - the
    /// later a stage ran, the more mangled its material was.
    /// </para>
    /// <para>
    /// Safe for a tool that emits pure ASCII, which decodes identically either way, and lenient
    /// by construction: invalid bytes become U+FFFD rather than throwing, because a run must not
    /// fail over one unexpected byte in a build log.
    /// </para>
    /// </remarks>
    private static readonly UTF8Encoding StreamEncoding = new(encoderShouldEmitUTF8Identifier: false);

    private readonly ILogger<ProcessRunner> _logger;

    /// <summary>Creates the runner.</summary>
    public ProcessRunner(ILogger<ProcessRunner>? logger = null) =>
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<ProcessRunner>.Instance;

    /// <inheritdoc />
    public async Task<ProcessRunResult> RunAsync(ProcessRunRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        ProcessStartInfo startInfo = new()
        {
            FileName = request.FileName,
            WorkingDirectory = request.WorkingDirectory ?? Environment.CurrentDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = request.StandardInput is not null,
            StandardOutputEncoding = StreamEncoding,
            StandardErrorEncoding = StreamEncoding,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        // Only when there is a stdin to encode: Process.Start refuses an encoding for a stream it
        // was not asked to redirect.
        if (startInfo.RedirectStandardInput)
        {
            startInfo.StandardInputEncoding = StreamEncoding;
        }

        foreach (string argument in request.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        if (request.Environment is not null)
        {
            foreach ((string key, string? value) in request.Environment)
            {
                if (value is null)
                {
                    startInfo.Environment.Remove(key);
                }
                else
                {
                    startInfo.Environment[key] = value;
                }
            }
        }

        StringBuilder stdout = new();
        StringBuilder stderr = new();
        long start = Stopwatch.GetTimestamp();

        using Process process = new() { StartInfo = startInfo, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) =>
        {
            Append(stdout, e.Data);
            Watch(request.OnOutputLine, e.Data);
        };
        process.ErrorDataReceived += (_, e) => Append(stderr, e.Data);

        using CancellationTokenSource timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (request.Timeout is { } timeout)
        {
            timeoutSource.CancelAfter(timeout);
        }

        _logger.LogDebug("Running {FileName} {Arguments}", request.FileName, string.Join(' ', request.Arguments));
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        if (request.StandardInput is not null)
        {
            await process.StandardInput.WriteAsync(request.StandardInput).ConfigureAwait(false);
            process.StandardInput.Close();
        }

        bool timedOut = false;
        bool ready = false;
        try
        {
            if (request.ReadyWhen is { } readyWhen)
            {
                ready = await WaitForReadyOrExitAsync(
                    process, readyWhen, request.ReadyPollInterval, timeoutSource.Token).ConfigureAwait(false);
            }
            else
            {
                await process.WaitForExitAsync(timeoutSource.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            timedOut = !cancellationToken.IsCancellationRequested;
            Kill(process);

            if (!timedOut)
            {
                throw;
            }
        }

        // Ready means the process is still running and has told us what we waited to learn, so it
        // is stopped on the same terms the timeout would have stopped it - just sooner.
        if (ready)
        {
            Kill(process);
        }

        return new ProcessRunResult(
            timedOut || ready ? -1 : process.ExitCode,
            stdout.ToString(),
            stderr.ToString(),
            Stopwatch.GetElapsedTime(start),
            timedOut)
        {
            ReadySignalled = ready,
        };
    }

    /// <summary>
    /// Waits for whichever comes first: the predicate saying the process is ready, or the process
    /// exiting. Returns whether it was the former. The timeout arrives as cancellation on the
    /// token, so it surfaces through the same <see cref="OperationCanceledException"/> path a
    /// plain wait uses and needs no second branch at the call site.
    /// </summary>
    private async Task<bool> WaitForReadyOrExitAsync(
        Process process,
        Func<int, bool> readyWhen,
        TimeSpan interval,
        CancellationToken cancellationToken)
    {
        // Never zero: a poll interval of nothing is a spin loop competing with the process it is
        // waiting for, on a machine that is also compiling.
        TimeSpan poll = interval > TimeSpan.Zero ? interval : TimeSpan.FromMilliseconds(200);

        while (!process.HasExited)
        {
            if (IsReady(readyWhen, process.Id))
            {
                return true;
            }

            await Task.Delay(poll, cancellationToken).ConfigureAwait(false);
        }

        return false;
    }

    /// <summary>
    /// Asks the predicate, treating a throw as "not yet". Guarded for the reason
    /// <see cref="Watch"/> is: whoever was watching getting it wrong must not kill the launch.
    /// </summary>
    private bool IsReady(Func<int, bool> readyWhen, int processId)
    {
        try
        {
            return readyWhen(processId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "A readiness predicate threw and was read as not-ready");
            return false;
        }
    }

    private static void Append(StringBuilder builder, string? line)
    {
        if (line is not null)
        {
            builder.AppendLine(line);
        }
    }

    /// <summary>
    /// Hands one line to a watching caller. Guarded because this runs on the reader thread: an
    /// exception escaping here would be unobserved at best and would tear down the capture at
    /// worst, and neither is a fair price for a caller that only wanted to display progress.
    /// </summary>
    private void Watch(Action<string>? watcher, string? line)
    {
        if (watcher is null || line is null)
        {
            return;
        }

        try
        {
            watcher(line);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "An output watcher threw and was ignored");
        }
    }

    private void Kill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or SystemException)
        {
            _logger.LogWarning(ex, "Could not kill process {ProcessName}", process.StartInfo.FileName);
        }
    }
}
