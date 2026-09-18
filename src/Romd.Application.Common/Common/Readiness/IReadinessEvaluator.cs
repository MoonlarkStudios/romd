namespace Romd.Application.Common.Readiness;

/// <summary>
///     Evaluates the host's registered readiness checks. Implementations are single-flight and cache
///     the result briefly so a probe storm cannot multiply dependency hits.
/// </summary>
public interface IReadinessEvaluator
{
    Task<ReadinessReport> EvaluateAsync(CancellationToken cancellationToken);
}
