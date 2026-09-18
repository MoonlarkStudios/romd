using System.Text.Json.Serialization;

namespace Romd.Infrastructure.Realtime;

/// <summary>
///     Compatibility shape for pending version-1 outbox rows written before
///     LibraryUpdated adopted opaque public identity. The dispatcher translates
///     this shape to the current contract before SignalR delivery. Issue #187
///     owns removal once the documented minimum-upgrade and zero-pending-row
///     conditions are satisfied.
/// </summary>
internal sealed record AdminRealtimeLibraryUpdatedV1Payload(
    [property: JsonRequired] int LibraryId,
    [property: JsonRequired] string Name,
    [property: JsonRequired] bool NeedsMaterialization,
    [property: JsonRequired] int ItemCount,
    [property: JsonRequired] string ConfigurationState);
