namespace Romd.Application.Common.Readiness;

/// <summary>
///     One dependency probe evaluated by the readiness endpoint. Implementations must be cheap,
///     retry-free, and honor the cancellation token: the evaluator enforces a per-check budget and
///     treats a timeout or exception as a failure of this check.
/// </summary>
public interface IReadinessCheck
{
    /// <summary>Stable wire name of the check (see <see cref="ReadinessCheckNames" />).</summary>
    string Name { get; }

    /// <summary>
    ///     A failed or timed-out critical check makes the host unready (503); a failed non-critical
    ///     check only degrades it (200).
    /// </summary>
    bool IsCritical { get; }

    Task<ReadinessCheckStatus> EvaluateAsync(CancellationToken cancellationToken);
}
