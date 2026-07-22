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
    private readonly ChangesViewModel _changes;
    private readonly ApprovalOptions _options;

    /// <summary>Creates the gate.</summary>
    public WpfApprovalGate(ChangesViewModel changes, IOptions<ApprovalOptions> options)
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

        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, _options.ApprovalTimeoutSeconds)));

        try
        {
            return await _changes.RequestApprovalAsync(change, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return ApprovalDecision.Reject(
                $"Nobody answered within {_options.ApprovalTimeoutSeconds} seconds, so the change was not written.");
        }
    }
}
