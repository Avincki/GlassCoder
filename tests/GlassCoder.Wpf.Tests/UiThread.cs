using System.Diagnostics;
using System.Windows.Threading;

namespace GlassCoder.Wpf.Tests;

/// <summary>
/// Runs work on a fresh STA thread that owns a dispatcher - the apartment and the thread affinity
/// the real UI thread has - and gives up after a budget rather than waiting forever.
/// <para>
/// The budget is the point of this class. The defect these tests exist for was a dependency cycle
/// the container could not see, because the view model at the top of it is registered through a
/// factory and a factory is opaque to cycle detection. Such a cycle does not throw: the container
/// recurses, its stack guard hands the work to a thread pool thread, and that thread blocks on the
/// singleton lock the resolving thread is still holding. Nothing fails and nothing is logged. A
/// test that simply called <c>GetRequiredService</c> would therefore hang the whole run instead of
/// failing one case, which is the difference between a regression test and a trap.
/// </para>
/// </summary>
internal static class UiThread
{
    /// <summary>
    /// How long the composition root gets before it is declared deadlocked. Generous on purpose -
    /// this measures liveness, not speed, and a slow first resolve on a cold machine must not read
    /// as a cycle. The whole graph resolves in well under a second when it is healthy, so the
    /// margin here is two orders of magnitude and a failure means what it says.
    /// </summary>
    public static readonly TimeSpan Budget = TimeSpan.FromSeconds(30);

    private static readonly Lock ApplicationGate = new();
    private static Dispatcher? _applicationDispatcher;

    /// <summary>
    /// Starts the one thread allowed to own the <see cref="System.Windows.Application"/>, if it is
    /// not already running, and returns its dispatcher.
    /// <para>
    /// Unlike <see cref="Run{T}"/> this thread is long-lived and pumps a real dispatcher loop,
    /// because the application and every window shown against it have to stay on one thread for
    /// the lifetime of the host. See <see cref="TestApplication"/> for why.
    /// </para>
    /// </summary>
    public static Dispatcher EnsureApplicationThread()
    {
        lock (ApplicationGate)
        {
            if (_applicationDispatcher is { } running)
            {
                return running;
            }

            TaskCompletionSource<Dispatcher> ready = new(TaskCreationOptions.RunContinuationsAsynchronously);

            Thread thread = new(() =>
            {
                try
                {
                    TestApplication.CreateOnThisThread();
                    ready.TrySetResult(Dispatcher.CurrentDispatcher);
                }
                catch (Exception ex)
                {
                    ready.TrySetException(ex);
                    return;
                }

                // A loop rather than a bare dispatcher: work handed over between calls has to run
                // without anybody pumping by hand. Inside a delegate nothing changes - a nested
                // post still waits for Pump, exactly as it did on a throwaway thread.
                Dispatcher.Run();
            })
            {
                IsBackground = true,
                Name = "glasscoder-ui-application",
            };

            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();

            if (!ready.Task.Wait(Budget))
            {
                throw new TimeoutException(
                    $"The application thread did not start within {Budget.TotalSeconds:F0} seconds.");
            }

            _applicationDispatcher = ready.Task.GetAwaiter().GetResult();
            return _applicationDispatcher;
        }
    }

    /// <summary>
    /// Runs <paramref name="work"/> on the thread that owns the application, and returns what it
    /// produced. Use this instead of <see cref="Run{T}"/> for anything that shows a window: a
    /// window resolves the brushes in <c>App.xaml</c>, and those belong to that thread alone.
    /// </summary>
    /// <param name="work">The work, handed the dispatcher belonging to the application thread.</param>
    /// <exception cref="TimeoutException">The work did not finish inside <see cref="Budget"/>.</exception>
    public static T RunOnApplicationThread<T>(Func<Dispatcher, T> work)
    {
        Dispatcher dispatcher = EnsureApplicationThread();

        if (dispatcher.CheckAccess())
        {
            return work(dispatcher);
        }

        DispatcherOperation<T> operation = dispatcher.InvokeAsync(() => work(dispatcher));

        if (!operation.Task.Wait(Budget))
        {
            throw new TimeoutException(
                $"Nothing came back from the application thread within {Budget.TotalSeconds:F0} seconds.");
        }

        return operation.Task.GetAwaiter().GetResult();
    }

    /// <summary>Runs <paramref name="work"/> on a fresh STA thread and returns what it produced.</summary>
    /// <param name="work">The work, handed the dispatcher belonging to its own thread.</param>
    /// <exception cref="TimeoutException">The work did not finish inside <see cref="Budget"/>.</exception>
    public static T Run<T>(Func<Dispatcher, T> work)
    {
        TaskCompletionSource<T> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        Thread thread = new(() =>
        {
            try
            {
                completion.TrySetResult(work(Dispatcher.CurrentDispatcher));
            }
            catch (Exception ex)
            {
                completion.TrySetException(ex);
            }
        })
        {
            // Background, so a thread that never returns cannot outlive the run: a deadlocked
            // resolve has to fail one test, not leave the test host hanging at exit.
            IsBackground = true,
            Name = "glasscoder-ui-test",
        };

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        if (!completion.Task.Wait(Budget))
        {
            throw new TimeoutException(
                $"Nothing came back within {Budget.TotalSeconds:F0} seconds. The usual cause is a " +
                "dependency cycle in the composition root: the container recurses through a factory " +
                "registration until it deadlocks on its own singleton lock, which is a hang rather " +
                "than an exception.");
        }

        return completion.Task.GetAwaiter().GetResult();
    }

    /// <summary>
    /// Runs queued dispatcher work until <paramref name="until"/> holds, and reports whether it
    /// did. Call it on the dispatcher's own thread - inside the delegate handed to
    /// <see cref="Run{T}"/>.
    /// <para>
    /// A dispatcher created by <see cref="Dispatcher.CurrentDispatcher"/> has a queue but no
    /// loop, so anything posted to it sits there until something pumps. That is fine for the
    /// composition tests, which post nothing; it is the whole difficulty for anything driven by
    /// a file-system watcher, whose events arrive on a thread-pool thread and are posted here.
    /// </para>
    /// </summary>
    /// <param name="dispatcher">The dispatcher belonging to the calling thread.</param>
    /// <param name="until">The condition being waited for, evaluated between pumps.</param>
    /// <param name="budget">How long to keep pumping. Defaults to five seconds.</param>
    public static bool Pump(Dispatcher dispatcher, Func<bool> until, TimeSpan? budget = null)
    {
        TimeSpan limit = budget ?? TimeSpan.FromSeconds(5);
        Stopwatch clock = Stopwatch.StartNew();

        while (!until())
        {
            if (clock.Elapsed > limit)
            {
                return false;
            }

            // Drain everything queued at Background or above: posting the frame's own exit at
            // Background means it runs after the work already in front of it.
            DispatcherFrame frame = new();
            dispatcher.BeginInvoke(DispatcherPriority.Background, () => frame.Continue = false);
            Dispatcher.PushFrame(frame);

            // The watcher has not necessarily posted anything yet. Yield rather than spin.
            Thread.Sleep(15);
        }

        return true;
    }
}
