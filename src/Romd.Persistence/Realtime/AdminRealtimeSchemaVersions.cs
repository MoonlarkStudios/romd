namespace Romd.Persistence.Realtime;

/// <summary>
///     Integer payload schema versions for admin realtime events
///     (docs/decisions/admin-use-case-transaction-event-boundaries.md). The
///     version is recorded on the outbox row and delivered to clients with the
///     event. A payload schema change bumps the version for that event type
///     under the repo-atomic compatibility rule in
///     docs/decisions/admin-api-contract-policy.md.
/// </summary>
public static class AdminRealtimeSchemaVersions
{
    public const int Initial = 1;
    public const int LibraryUpdatedOpaqueIdentity = 2;

    public static int CurrentFor(string eventType) =>
        eventType == Romd.Admin.Application.Common.Realtime.AdminRealtimeEventTypes.LibraryUpdated
            ? LibraryUpdatedOpaqueIdentity
            : Initial;
}
