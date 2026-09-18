using ErrorOr;
using Romd.Domain.Catalog;
using Romd.Domain.Hashing;
using Romd.Domain.Jobs;

namespace Romd.Admin.Application.Artwork;

/// <summary>Every staging operation requires a caller-owned transaction.</summary>
public interface IArtworkCurationRepository
{
    Task<ErrorOr<IReadOnlyList<ArtworkCurationState>>> GetStatesAsync(int titleId, CancellationToken ct);
    Task<ErrorOr<IReadOnlyList<ArtworkAsset>>> GetGalleryAsync(int titleId, CancellationToken ct);
    Task<TitleMedia?> FindMediaAsync(int titleId, int mediaId, CancellationToken ct);
    Task<ErrorOr<long>> StagePinAsync(int titleId, ArtworkRole role, int assetId, long expectedRevision, CancellationToken ct, int focalX = 50, int focalY = 50);
    Task<ErrorOr<bool>> LockSelectionAsync(int titleId, ArtworkRole role, long expectedRevision, CancellationToken ct);
    Task StageLocalAssetAsync(int titleId, ArtworkRole role, string sourceId, RetainedArtworkContent content, Guid actorId, CancellationToken ct,
        string? providerGameId = null, string? providerAssetId = null);
    Task<ErrorOr<ArtworkImportJob>> StageRequestAsync(ArtworkImportRequest request, CancellationToken ct);
    Task<ErrorOr<long>> StageAutomaticAsync(int titleId, ArtworkRole role, CancellationToken ct);
    Task<bool> LockPendingAsync(ArtworkImportJob job, CancellationToken ct);
    Task StageAssetAsync(ArtworkImportJob job, RetainedArtworkContent content, CancellationToken ct);
    Task<int> StagePublicationAsync(ArtworkImportJob job, string contentVersion, CancellationToken ct);
    Task StageOutcomeAsync(ArtworkImportJob job, int? assetId, bool superseded, CancellationToken ct);
}

public sealed record ArtworkCurationState(ArtworkRole Role, ArtworkSelectionMode Mode, long Revision,
    int? PinnedAssetId, Guid? PendingJobId);

/// <summary>Created only after validating a server-issued candidate reference.</summary>
public sealed record ArtworkImportRequest(Guid RequestId, int TitleId, ArtworkRole Role,
    string ProviderId, string ProviderGameId, string ProviderAssetId, string TrustedAssetUrl, string? Attribution, Guid? ActorId,
    int FocalX = 50, int FocalY = 50, long? ExpectedRevision = null);

public sealed record RetainedArtworkFile(Sha256 Hash, long Size, long SizeOnDisk, bool IsCompressed,
    string ContentType, int Width, int Height, string Name);

public sealed record RetainedArtworkContent(RetainedArtworkFile Original,
    IReadOnlyList<RetainedArtworkFile> Variants, string? Attribution, string? SourcePageUrl);

public static class ArtworkCurationErrors
{
    public static Error TitleNotFound() => Error.NotFound("Artwork.TitleNotFound", "Title was not found.");
    public static Error RequestConflict() => Error.Conflict("Artwork.RequestConflict", "This request identity was already used for a different operation.");
    public static Error SelectionChanged() => Error.Conflict("Artwork.SelectionChanged", "The artwork selection changed. Refresh before choosing again.");
    public static Error InvalidRequest() => Error.Validation("Artwork.InvalidRequest", "The artwork selection request is invalid.");
}
