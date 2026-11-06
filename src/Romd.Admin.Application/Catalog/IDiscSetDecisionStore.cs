using Romd.Domain.Catalog;

namespace Romd.Admin.Application.Catalog;

public sealed record DiscSetSnapshot(int Id, int PlatformId, Guid Revision, DiscSetDecisionFacts Facts,
    Guid? DecidedBy, DateTimeOffset? DecidedAt, string? Reason, string? SystemKey);
public sealed record DiscSetDecisionTarget(int Id, int PlatformId, Guid Revision);

/// <summary>Protected facts and purpose-specific writes inside the application's decision transaction.</summary>
public interface IDiscSetDecisionStore
{
    Task LockTopologyAsync(CancellationToken ct);
    Task<DiscSetDecisionTarget?> ReadAsync(int id, CancellationToken ct);
    DiscSetSnapshot ReadFacts(DiscSetDecisionTarget original);
    Task<string?> ReadSystemKeyAsync(int platformId, CancellationToken ct);
    Task<DiscSetClaims> ReadClaimsAsync(int platformId, int sourceId, IReadOnlyCollection<string> entryKeys, CancellationToken ct);
    Task StageDecisionAsync(DiscSetSnapshot original, DiscSetDecisionChange change, string? reason, Guid actor, DateTimeOffset decidedAt, Guid revision, CancellationToken ct);
    Task InvalidateAffectedLibrariesAsync(int id, Guid publicationRevision, DateTimeOffset now, CancellationToken ct);
}
