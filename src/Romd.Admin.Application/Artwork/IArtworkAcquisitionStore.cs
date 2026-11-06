using Romd.Contracts.Management.Artwork;
using Romd.Domain.Catalog;
using ErrorOr;
using Romd.Application.Common.Concurrency;

namespace Romd.Admin.Application.Artwork;

public interface IArtworkAcquisitionStore
{
    Task<ArtworkEnrichmentSettingsDto> GetSettingsAsync(CancellationToken ct);
    /// <summary>Stages a settings replacement under the caller's transaction and returns its own revision.</summary>
    Task<ErrorOr<ArtworkEnrichmentSettingsDto>> StageSettingsAsync(UpdateArtworkEnrichmentSettingsRequest request, RevisionPrecondition ifMatch, CancellationToken ct);
    Task<ArtworkAcquisitionStateDto> GetOutcomesAsync(int titleId, CancellationToken ct);
    Task RecordAsync(int titleId, ArtworkRole role, string status, string? sourceId, CancellationToken ct, Guid? runId = null);
    Task<IReadOnlyList<string>> GetProviderOrderAsync(ArtworkRole role, CancellationToken ct);
    Task<IReadOnlyList<string>> GetRejectedCandidatesAsync(int titleId, ArtworkRole role, string providerId, string gameId, CancellationToken ct);
    Task<Guid?> GetTitleRevisionAsync(int titleId, CancellationToken ct);
    Task<string?> GetProviderGameIdAsync(int titleId, string providerId, CancellationToken ct);
    // Caller holds a transaction; serializes against manual curation and other acquisitions.
    Task<bool> LockMissingAsync(int titleId, ArtworkRole role, long revision, CancellationToken ct);
}
