using Romd.Contracts.Management.Artwork;
using Romd.Domain.Catalog;

namespace Romd.Admin.Application.Artwork;

public interface IArtworkAcquisitionStore
{
    Task<ArtworkEnrichmentSettingsDto> GetSettingsAsync(CancellationToken ct);
    Task<bool> UpdateSettingsAsync(UpdateArtworkEnrichmentSettingsRequest request, CancellationToken ct);
    Task<ArtworkAcquisitionStateDto> GetOutcomesAsync(int titleId, CancellationToken ct);
    Task RecordAsync(int titleId, ArtworkRole role, string status, string? sourceId, CancellationToken ct);
    Task<string?> GetProviderGameIdAsync(int titleId, string providerId, CancellationToken ct);
    // Caller holds a transaction; serializes against manual curation and other acquisitions.
    Task<bool> LockMissingAsync(int titleId, ArtworkRole role, long revision, CancellationToken ct);
}
