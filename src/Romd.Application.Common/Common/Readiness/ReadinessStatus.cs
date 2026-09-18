namespace Romd.Application.Common.Readiness;

/// <summary>
///     Overall readiness of a host. <see cref="Ready" /> and <see cref="Degraded" /> map to HTTP 200;
///     <see cref="Unready" /> maps to HTTP 503.
/// </summary>
public enum ReadinessStatus
{
    Ready,
    Degraded,
    Unready
}

/// <summary>
///     Outcome of a single readiness check. A non-critical check can contribute at most
///     <see cref="Degraded" /> to the overall status.
/// </summary>
public enum ReadinessCheckStatus
{
    Healthy,
    Degraded,
    Unready
}

/// <summary>
///     Single source of the wire names for readiness statuses. The readiness endpoint's JSON body is
///     an intentionally opaque contract: lowercase names, no details.
/// </summary>
public static class ReadinessStatusNames
{
    public static string Of(ReadinessStatus status) => status switch
    {
        ReadinessStatus.Ready => "ready",
        ReadinessStatus.Degraded => "degraded",
        ReadinessStatus.Unready => "unready",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, message: null)
    };

    public static string Of(ReadinessCheckStatus status) => status switch
    {
        ReadinessCheckStatus.Healthy => "healthy",
        ReadinessCheckStatus.Degraded => "degraded",
        ReadinessCheckStatus.Unready => "unready",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, message: null)
    };
}
