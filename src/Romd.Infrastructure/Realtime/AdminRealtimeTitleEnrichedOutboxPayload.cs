namespace Romd.Infrastructure.Realtime;

/// <summary>
///     Durable internal payload. The SignalR adapter maps these database keys to
///     <see cref="Romd.Contracts.Management.Realtime.AdminRealtimeTitleEnrichedPayload" />
///     before delivery across the public admin boundary.
/// </summary>
public sealed record AdminRealtimeTitleEnrichedOutboxPayload(
    int TitleId,
    int PlatformId,
    int? CoverMediaId);
