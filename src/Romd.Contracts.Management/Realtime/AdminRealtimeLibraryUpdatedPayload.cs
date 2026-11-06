using System.Text.Json.Serialization;

namespace Romd.Contracts.Management.Realtime;

public sealed record AdminRealtimeLibraryUpdatedPayload(
    [property: JsonRequired] string LibraryId,
    [property: JsonRequired] string Name,
    [property: JsonRequired] bool NeedsMaterialization,
    [property: JsonRequired] int ItemCount,
    [property: JsonRequired] string ConfigurationState);
