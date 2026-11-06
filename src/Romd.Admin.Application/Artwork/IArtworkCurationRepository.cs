using ErrorOr;
using Romd.Application.Common.Concurrency;
using Romd.Application.Common.Pagination;
using Romd.Domain.Catalog;
using Romd.Domain.Hashing;
using Romd.Domain.Jobs;

namespace Romd.Admin.Application.Artwork;

/// <summary>
///     Every staging operation requires a caller-owned transaction. A role write compares its precondition with
///     that role's revision, and a write to the family's assets, rejections or hidden candidates compares it with
///     <see cref="ArtworkValidators.Family" />, both under the title row lock.
/// </summary>
public interface IArtworkCurationRepository
{
    Task<ErrorOr<IReadOnlyList<ArtworkCurationState>>> GetStatesAsync(int titleId, CancellationToken ct);

    /// <summary>Every role and the family validator, computed exactly as a family write compares it.</summary>
    Task<ErrorOr<ArtworkFamily>> GetFamilyAsync(int titleId, CancellationToken ct);
    Task<ErrorOr<IReadOnlyList<ArtworkAsset>>> GetGalleryAsync(int titleId, CancellationToken ct);
    Task<ErrorOr<IReadOnlyList<ArtworkAsset>>> GetRejectedAsync(int titleId, CancellationToken ct);
    /// <summary>Public saved assets in creation-time/id order, with title media restricted to this page.</summary>
    Task<ErrorOr<PagedList<SavedArtworkReadModel>>> ListAssetsAsync(int titleId, string? cursor, int limit, CancellationToken ct);
    /// <summary>Public rejected assets in id order, including rejected assets no longer eligible for delivery.</summary>
    Task<ErrorOr<PagedList<ArtworkAsset>>> ListRejectionsAsync(int titleId, string? cursor, int limit, CancellationToken ct);
    Task<ErrorOr<long>> StageRejectionAsync(int titleId, int assetId, RevisionPrecondition familyIfMatch, bool rejected, CancellationToken ct);
    Task<IReadOnlyList<string>> GetExcludedUrlsAsync(int titleId, CancellationToken ct);
    Task<ErrorOr<bool>> StageHideAsync(int titleId, string sourceUrl, RevisionPrecondition familyIfMatch, CancellationToken ct);

    Task<ErrorOr<bool>> StageDeleteAsync(int titleId, int assetId, RevisionPrecondition familyIfMatch, CancellationToken ct);

    /// <summary>
    ///     The artwork cascade of a title media delete, which runs under the title's precondition: excludes the
    ///     removed row's source URL from acquisition and, when no other media row of the title shows the same file,
    ///     rejects that file's assets and fences or returns the roles showing it. The caller removes the media row
    ///     <paramref name="mediaId" /> and owns the transaction.
    /// </summary>
    Task StageTitleMediaRemovalAsync(int titleId, int mediaId, int fileId, string? sourceUrl, CancellationToken ct);
    Task<TitleMedia?> FindMediaAsync(int titleId, int mediaId, CancellationToken ct);
    Task<ErrorOr<long>> StagePinAsync(int titleId, ArtworkRole role, int assetId, RevisionPrecondition ifMatch, CancellationToken ct, int focalX = 50, int focalY = 50);
    Task<ErrorOr<bool>> LockSelectionAsync(int titleId, ArtworkRole role, RevisionPrecondition ifMatch, CancellationToken ct);
    Task StageLocalAssetAsync(int titleId, ArtworkRole role, string sourceId, RetainedArtworkContent content, CancellationToken ct,
        string? providerGameId = null, string? providerAssetId = null);
    Task<ErrorOr<ArtworkImportJob>> StageRequestAsync(ArtworkImportRequest request, CancellationToken ct);
    Task<ErrorOr<long>> StageEmptyAsync(int titleId, ArtworkRole role, RevisionPrecondition ifMatch, CancellationToken ct);
    Task<ErrorOr<long>> StageAutomaticAsync(int titleId, ArtworkRole role, RevisionPrecondition ifMatch, CancellationToken ct);
    Task<bool> LockPendingAsync(ArtworkImportJob job, CancellationToken ct);
    Task StageAssetAsync(ArtworkImportJob job, RetainedArtworkContent content, CancellationToken ct);
    Task<int> StagePublicationAsync(ArtworkImportJob job, string contentVersion, CancellationToken ct);
    Task StageOutcomeAsync(ArtworkImportJob job, int? assetId, bool superseded, CancellationToken ct);
}

public sealed record SavedArtworkReadModel(ArtworkAsset Asset, IReadOnlyList<int> MediaIds);

public sealed record ArtworkCurationState(ArtworkRole Role, ArtworkSelectionMode Mode, long Revision,
    int? PinnedAssetId, Guid? PendingJobId);

/// <summary>
///     Created only after validating a server-issued candidate reference. A replay of <see cref="RequestId" /> answers
///     the job it already accepted before <see cref="IfMatch" /> is compared, so a retry after a lost response is
///     not refused for the revision its own request rotated.
/// </summary>
public sealed record ArtworkImportRequest(Guid RequestId, int TitleId, ArtworkRole Role,
    string ProviderId, string ProviderGameId, string ProviderAssetId, string TrustedAssetUrl, string? Attribution, Guid? ActorId,
    RevisionPrecondition IfMatch, int FocalX = 50, int FocalY = 50);

/// <summary>An artwork file the file ledger published and recorded in the caller's transaction.</summary>
public sealed record RetainedArtworkFile(int FileId, Sha256 Hash, string ContentType, int Width, int Height, string Name);

public sealed record RetainedArtworkContent(RetainedArtworkFile Original,
    IReadOnlyList<RetainedArtworkFile> Variants, string? Attribution, string? SourcePageUrl);

public static class ArtworkCurationErrors
{
    public static Error TitleNotFound() => Error.NotFound("Artwork.TitleNotFound", "Title was not found.");
    public static Error AssetNotFound() => Error.NotFound("Artwork.AssetNotFound", "The artwork asset was not found.");
    public static Error CandidateMismatch() => Error.Validation("Artwork.CandidateMismatch",
        "The candidate reference names another candidate than the route.");
    public static Error UnknownRole() => Error.NotFound("Artwork.UnknownRole", "Not an artwork role.");
    public static Error ActorRequired() => Error.Unauthorized("Artwork.ActorRequired", "A valid authenticated actor is required.");
    public static Error RequestConflict() => Error.Conflict("Artwork.RequestConflict", "This request identity was already used for a different operation.");
    public static Error SelectionChanged() => Error.Conflict("Artwork.SelectionChanged", "The artwork selection changed. Refresh before choosing again.");
    public static Error InvalidRequest() => Error.Validation("Artwork.InvalidRequest", "The artwork selection request is invalid.");
    public static Error PreconditionFailed() => Error.Custom(412, "Artwork.PreconditionFailed",
        "The artwork changed since it was read. Reload it, then choose again.");
}
