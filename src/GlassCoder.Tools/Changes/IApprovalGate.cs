using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GlassCoder.Tools.Changes;

/// <summary>A human's answer to a proposed change.</summary>
/// <param name="Approved">Whether the change may be written.</param>
/// <param name="Reason">Why it was refused, when it was.</param>
public sealed record ApprovalDecision(bool Approved, string? Reason = null)
{
    /// <summary>An approval.</summary>
    public static ApprovalDecision Approve() => new(true);

    /// <summary>A refusal.</summary>
    public static ApprovalDecision Reject(string reason) => new(false, reason);
}

/// <summary>Approval settings (CLAUDE.md §10, workplan tasks 28, 41).</summary>
public sealed class ApprovalOptions
{
    /// <summary>Configuration section these options bind from.</summary>
    public const string SectionName = "GlassCoder:Approval";

    /// <summary>
    /// Whether a human must approve each write. Off by default because the harness's normal
    /// mode is headless measurement; the WPF app turns it on to put a person in the loop.
    /// </summary>
    public bool RequireApprovalForWrites { get; set; }

    /// <summary>
    /// Whether a human must approve each push. On by default, unlike writes: a write can be
    /// reverted from the change log, a push has left the machine.
    /// </summary>
    public bool RequireApprovalForPush { get; set; } = true;

    /// <summary>
    /// How long to wait for an answer before treating silence as refusal. Refusal, not
    /// approval: an unattended prompt must never become a write.
    /// </summary>
    public int ApprovalTimeoutSeconds { get; set; } = 300;
}

/// <summary>
/// An agent action that is not a file diff - a push, later a pull request (workplan task 41).
/// The title and detail lines are all a reviewer sees, so they must carry everything the
/// decision needs.
/// </summary>
/// <param name="Tool">Tool asking, for example <c>git_push</c>.</param>
/// <param name="Title">One line naming the action, for example "Push 2 commit(s) on main to origin".</param>
/// <param name="Detail">Supporting lines - the outgoing commits, the branch, the remote.</param>
public sealed record AgentAction(string Tool, string Title, IReadOnlyList<string> Detail);

/// <summary>
/// The permission prompt, as a guardrail before write (CLAUDE.md §7, §10; workplan task 28).
/// </summary>
public interface IApprovalGate
{
    /// <summary>Whether this gate actually asks anyone.</summary>
    bool IsInteractive { get; }

    /// <summary>Asks whether a change may be written.</summary>
    Task<ApprovalDecision> RequestAsync(CodeChange change, CancellationToken cancellationToken = default);

    /// <summary>
    /// Asks whether an outward-facing action may proceed. Governed by
    /// <see cref="ApprovalOptions.RequireApprovalForPush"/> while push is the only such action.
    /// </summary>
    Task<ApprovalDecision> RequestActionAsync(AgentAction action, CancellationToken cancellationToken = default);
}

/// <summary>
/// The default gate: approves everything, unless configuration says a human must be asked - in
/// which case it refuses, because there is nobody to ask.
/// </summary>
/// <remarks>
/// Failing closed matters here. If approval is required and no interactive gate was registered,
/// approving anyway would turn a safety setting into a no-op precisely when someone believed it
/// was protecting them.
/// </remarks>
public sealed class AutoApprovalGate : IApprovalGate
{
    private readonly ApprovalOptions _options;
    private readonly ILogger<AutoApprovalGate> _logger;

    /// <summary>Creates the gate.</summary>
    public AutoApprovalGate(IOptions<ApprovalOptions> options, ILogger<AutoApprovalGate>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        _options = options.Value;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<AutoApprovalGate>.Instance;
    }

    /// <inheritdoc />
    public bool IsInteractive => false;

    /// <inheritdoc />
    public Task<ApprovalDecision> RequestAsync(CodeChange change, CancellationToken cancellationToken = default)
    {
        if (!_options.RequireApprovalForWrites)
        {
            return Task.FromResult(ApprovalDecision.Approve());
        }

        _logger.LogWarning(
            "Approval is required for writes but no interactive approver is registered; refusing the change to {Path}",
            change?.Path);

        return Task.FromResult(ApprovalDecision.Reject(
            "Approval is required for writes, but this host has no way to ask anyone. " +
            "Run the desktop app, or set GlassCoder:Approval:RequireApprovalForWrites to false."));
    }

    /// <inheritdoc />
    public Task<ApprovalDecision> RequestActionAsync(AgentAction action, CancellationToken cancellationToken = default)
    {
        if (!_options.RequireApprovalForPush)
        {
            return Task.FromResult(ApprovalDecision.Approve());
        }

        _logger.LogWarning(
            "Approval is required for {Tool} but no interactive approver is registered; refusing the action",
            action?.Tool);

        return Task.FromResult(ApprovalDecision.Reject(
            "Approval is required for this action, but this host has no way to ask anyone. " +
            "Run the desktop app, or set GlassCoder:Approval:RequireApprovalForPush to false."));
    }
}
