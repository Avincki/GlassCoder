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

    /// <summary>
    /// Queues a process that was still running when its timeout arrived and had to be killed.
    /// <para>
    /// Its own outcome for <c>launch_app</c> (workplan task 71), where this is the <em>success</em>
    /// case: a desktop application that is still up after ten seconds started and did not crash.
    /// </para>
    /// </summary>
    public FakeProcessRunner EnqueueTimedOut(string standardOutput = "", string standardError = "")
    {
        _scripted.Enqueue(new ProcessRunResult(-1, standardOutput, standardError, TimeSpan.Zero, TimedOut: true));
        return this;
    }

    /// <summary>
    /// Queues a process that was stopped because <see cref="ProcessRunRequest.ReadyWhen"/> said it
    /// was ready - the third outcome, and for <c>launch_app</c> the best one: a window appeared,
    /// so there was no reason to keep waiting.
    /// </summary>
    /// <param name="elapsed">How long it took to get there, which is the point of the exercise.</param>
    public FakeProcessRunner EnqueueReady(TimeSpan elapsed = default)
    {
        _scripted.Enqueue(
            new ProcessRunResult(-1, string.Empty, string.Empty, elapsed, TimedOut: false) { ReadySignalled = true });
        return this;
    }

    /// <summary>
    /// The process id handed to <see cref="ProcessRunRequest.OnReady"/>, for a caller that wants
    /// to assert what it was told to look at.
    /// </summary>
    public int ReadyProcessId { get; set; } = 4242;

    /// <inheritdoc />
    public async Task<ProcessRunResult> RunAsync(
        ProcessRunRequest request, CancellationToken cancellationToken = default)
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

        // And a ready callback runs on exactly the result that says the process reached ready,
        // before the caller sees anything - the one gap the real runner leaves between knowing the
        // process is up and killing it. A fake that skipped this would let a caller's probe pass
        // its tests and never run against the real thing.
        if (result.ReadySignalled && request.OnReady is { } onReady)
        {
            await onReady(ReadyProcessId, cancellationToken).ConfigureAwait(false);
        }

        return result;
    }
}
