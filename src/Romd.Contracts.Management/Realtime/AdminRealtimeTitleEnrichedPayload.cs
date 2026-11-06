namespace Romd.Contracts.Management.Realtime;

public sealed record AdminRealtimeTitleEnrichedPayload(
    string TitleId,
    string SystemKey,
    string? CoverMediaId);
