using System;
using System.Threading;
using System.Threading.Tasks;
using GlassCoder.Core.Agent;

namespace GlassCoder.Wpf.Services;

/// <summary>
/// Routes the loop's "may this ceiling grow?" question to whichever surface is showing the run
/// (workplan: operator limit extension).
/// <para>
/// The loop is transient and the shell is a singleton, so the shell cannot be a constructor
/// dependency of the loop's gate - the same seam shape as <c>Func&lt;ChangesViewModel&gt;</c>
/// on the approval gate. The shell assigns <see cref="Handler"/> once at startup; a question
/// asked before that (a headless run, a test) is declined, which is exactly the no-gate
/// behaviour.
/// </para>
/// </summary>
public sealed class LimitExtensionGate : ILimitExtensionGate
{
    /// <summary>The shell's answer, assigned when the window is composed.</summary>
    public Func<RunLimitReached, CancellationToken, Task<bool>>? Handler { get; set; }

    /// <inheritdoc />
    public Task<bool> RequestExtensionAsync(RunLimitReached limit, CancellationToken cancellationToken) =>
        Handler is { } handler ? handler(limit, cancellationToken) : Task.FromResult(false);
}
