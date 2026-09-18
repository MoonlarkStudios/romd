namespace Romd.Infrastructure.Readiness;

/// <summary>
///     Optional, lazy access to Hangfire storage for readiness checks. <c>JobStorage</c> may be
///     absent from DI (AddRomdHangfireClient is skipped in the "Testing" environment), so callers
///     must tolerate unavailability instead of requiring the service.
/// </summary>
public interface IHangfireStorageProbe
{
    /// <summary>Whether Hangfire storage is registered in this host.</summary>
    bool IsAvailable { get; }

    /// <summary>Opens and disposes a storage connection as a cheap reachability probe.</summary>
    void TouchStorage();

    /// <summary>
    ///     Newest Hangfire server heartbeat in UTC, or null when storage is unavailable or no server
    ///     has announced itself.
    /// </summary>
    DateTimeOffset? GetNewestServerHeartbeatUtc();
}
