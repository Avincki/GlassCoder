using GlassCoder.Tools.Processes;

namespace GlassCoder.TestSupport;

/// <summary>
/// A scripted <see cref="IProcessRunner"/> (workplan task 8). Unit tests must never launch a
/// real compiler: builds are slow, machine-dependent, and arbitrary code execution.
/// </summary>
public sealed class FakeProcessRunner : IProcessRunner
{
    private readonly Queue<ProcessRunResult> _scripted = new();

    /// <summary>Result returned when the script is empty.</summary>
    public ProcessRunResult Default { get; set; } =
        new(0, string.Empty, string.Empty, TimeSpan.Zero, TimedOut: false);

    /// <summary>Every request that was run, in order.</summary>
    public List<ProcessRunRequest> Requests { get; } = [];

    /// <summary>Queues one result to be returned by the next call.</summary>
    public FakeProcessRunner Enqueue(int exitCode, string standardOutput = "", string standardError = "")
    {
        _scripted.Enqueue(new ProcessRunResult(exitCode, standardOutput, standardError, TimeSpan.Zero, TimedOut: false));
        return this;
    }

    /// <inheritdoc />
    public Task<ProcessRunResult> RunAsync(ProcessRunRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Requests.Add(request);

        ProcessRunResult result = _scripted.Count > 0 ? _scripted.Dequeue() : Default;

        // A watching caller sees the output a line at a time before it sees the result, because
        // that is what the real runner does - it hands each line to OnOutputLine from the reader
        // thread and returns the buffer at exit. A fake that skipped this would let a streaming
        // caller pass its tests and find nothing to read against the real thing.
        if (request.OnOutputLine is { } watcher)
        {
            foreach (string line in result.StandardOutput.Split('\n'))
            {
                watcher(line.TrimEnd('\r'));
            }
        }

        return Task.FromResult(result);
    }
}
