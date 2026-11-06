using Romd.Domain.Catalog;

namespace Romd.Admin.Application.Catalog;

/// <summary>Proposal inputs and staged writes inside the catalog rebuild worker's transaction.</summary>
public interface IDiscSetProposalStore
{
    Task<IReadOnlyList<DiscSetSourceClaim>> ReadClaimsAsync(int platformId, CancellationToken ct);
    /// <summary>Stages proposal replacement/tombstones without flushing; the rebuild owner flushes inside its savepoint.</summary>
    Task RefreshStagedAsync(int platformId, IReadOnlyList<DiscSetProposal> desired, CancellationToken ct);
}
