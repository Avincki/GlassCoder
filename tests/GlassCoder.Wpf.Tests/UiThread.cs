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

    /// <summary>Runs <paramref name="work"/> on an STA thread and returns what it produced.</summary>
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
}
