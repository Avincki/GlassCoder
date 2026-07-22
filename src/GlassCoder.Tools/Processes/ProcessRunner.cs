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
            UseShellExecute = false,
            CreateNoWindow = true,
        };

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
        process.OutputDataReceived += (_, e) => Append(stdout, e.Data);
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
        try
        {
            await process.WaitForExitAsync(timeoutSource.Token).ConfigureAwait(false);
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

        return new ProcessRunResult(
            timedOut ? -1 : process.ExitCode,
            stdout.ToString(),
            stderr.ToString(),
            Stopwatch.GetElapsedTime(start),
            timedOut);
    }

    private static void Append(StringBuilder builder, string? line)
    {
        if (line is not null)
        {
            builder.AppendLine(line);
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
