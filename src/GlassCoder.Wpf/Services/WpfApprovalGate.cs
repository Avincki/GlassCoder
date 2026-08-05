using System;
using System.Threading;
using System.Threading.Tasks;
using GlassCoder.Tools.Changes;
using GlassCoder.Wpf.ViewModels;
using Microsoft.Extensions.Options;

namespace GlassCoder.Wpf.Services;

/// <summary>
/// The interactive permission prompt (CLAUDE.md §10, workplan task 28).
/// <para>
/// Surfaced through the change view rather than a modal dialog, so the reviewer decides while
/// looking at the actual diff. A timeout counts as refusal: an unattended prompt must never
/// become a write.
/// </para>
/// </summary>
public sealed class WpfApprovalGate : IApprovalGate
{
    private readonly Func<ChangesViewModel> _changes;
    private readonly ApprovalOptions _options;

    /// <summary>
    /// Creates the gate.
    /// </summary>
    /// <param name="changes">
    /// The change view, resolved when a decision is actually needed rather than at construction.
    /// <para>
    /// The lateness is load-bearing: with the git tools enabled the graph is a cycle -
    /// <c>ChangesViewModel</c> takes <c>GitTool</c> to decide whether to show its git controls,
    /// <c>GitTool</c> takes this gate so a push still asks a human, and this gate needs the view
    /// to ask with. Taking the view eagerly closed that loop, and because the view model is
    /// registered through a factory the container cannot see the cycle to report it - it recursed
    /// until its stack guard handed the work to a thread pool thread, which then blocked on the
    /// singleton lock the UI thread was already holding. The window never appeared and nothing
    /// was logged. Asking for the view only when a change or an action is waiting breaks the
    /// loop at the one edge that is genuinely late anyway.
    /// </para>
    /// </param>
    /// <param name="options">Approval settings.</param>
    public WpfApprovalGate(Func<ChangesViewModel> changes, IOptions<ApprovalOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);

        _changes = changes;
        _options = options.Value;
    }

    /// <inheritdoc />
    public bool IsInteractive => true;

    /// <inheritdoc />
    public async Task<ApprovalDecision> RequestAsync(CodeChange change, CancellationToken cancellationToken = default)
    {
        if (!_options.RequireApprovalForWrites)
        {
            return ApprovalDecision.Approve();
        }

        try
        {
            return await AskAsync(t => _changes().RequestApprovalAsync(change, t), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return ApprovalDecision.Reject(
                $"Nobody answered within {_options.ApprovalTimeoutSeconds} seconds, so the change was not written.");
        }
    }

    /// <inheritdoc />
    public async Task<ApprovalDecision> RequestActionAsync(AgentAction action, CancellationToken cancellationToken = default)
    {
        if (!_options.RequireApprovalForPush)
        {
            return ApprovalDecision.Approve();
        }

        try
        {
            return await AskAsync(t => _changes().RequestApprovalAsync(action, t), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return ApprovalDecision.Reject(
                $"Nobody answered within {_options.ApprovalTimeoutSeconds} seconds, so the action was not run.");
        }
    }

    private async Task<ApprovalDecision> AskAsync(
        Func<CancellationToken, Task<ApprovalDecision>> request,
        CancellationToken cancellationToken)
    {
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, _options.ApprovalTimeoutSeconds)));
        return await request(timeout.Token).ConfigureAwait(false);
    }
}
