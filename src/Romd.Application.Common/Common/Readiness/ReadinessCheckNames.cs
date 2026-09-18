namespace Romd.Application.Common.Readiness;

/// <summary>Stable wire names of the readiness checks.</summary>
public static class ReadinessCheckNames
{
    /// <summary>Critical: database reachable and schema current (no pending EF migrations).</summary>
    public const string Database = "database";

    public const string ApplicationSchema = "application-schema";

    /// <summary>Critical (admin host only): Hangfire storage reachable.</summary>
    public const string HangfireStorage = "hangfire-storage";

    /// <summary>Degraded (admin host only): admin realtime outbox not stalled.</summary>
    public const string Outbox = "outbox";

    /// <summary>Degraded (admin host only): Hangfire worker heartbeat fresh.</summary>
    public const string Worker = "worker";

    /// <summary>Degraded: free space on the data-directory volume above the minimum.</summary>
    public const string Disk = "disk";

    /// <summary>Degraded: content-addressable storage root directory accessible.</summary>
    public const string Storage = "storage";
}
