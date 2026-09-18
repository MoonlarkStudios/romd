namespace Romd.Application.Common.Readiness;

/// <summary>Per-check readiness outcome; deliberately name + status only, no details.</summary>
public sealed record ReadinessCheckResult(string Name, ReadinessCheckStatus Status);

/// <summary>Aggregated readiness outcome for one evaluation of all registered checks.</summary>
public sealed record ReadinessReport(ReadinessStatus Status, IReadOnlyList<ReadinessCheckResult> Checks);
