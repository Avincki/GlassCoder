namespace GlassCoder.Core.Agent;

/// <summary>
/// Asks whoever is watching the run whether a tripped step or token ceiling may grow by one
/// more allotment of its configured size.
/// <para>
/// A limit exists to stop a run nobody is watching; a run somebody <em>is</em> watching can be
/// three steps from done when it trips, and the only options used to be re-running from zero
/// or raising the configured limit for every future run. The gate pauses the loop on the
/// question instead. The console host registers no gate and stops exactly as before; the WPF
/// shell answers with a banner, because the operator watching a nearly-finished run is the one
/// person who knows whether another allotment is money well spent.
/// </para>
/// </summary>
public interface ILimitExtensionGate
{
    /// <summary>
    /// True to raise the tripped ceiling by one more allotment and continue; false to stop
    /// with the ordinary limit outcome. Asked again each time the extended ceiling trips.
    /// </summary>
    Task<bool> RequestExtensionAsync(RunLimitReached limit, CancellationToken cancellationToken);
}

/// <summary>The limit that tripped, in the unit of whichever ceiling it was.</summary>
/// <param name="Reason">Which limit: <see cref="AgentStopReason.StepLimit"/> or <see cref="AgentStopReason.TokenLimit"/>.</param>
/// <param name="Used">What the run has spent, in the tripped limit's unit.</param>
/// <param name="Ceiling">The current ceiling, extensions included.</param>
/// <param name="Allotment">The configured limit - what one extension adds.</param>
public sealed record RunLimitReached(AgentStopReason Reason, long Used, long Ceiling, long Allotment);
