using ErrorOr;
using Romd.Domain.Catalog;

namespace Romd.Admin.Application.Artwork.Providers;

public interface IArtworkBrowsingService
{
    Task<IReadOnlyList<ArtworkBrowsingCapabilities>> GetCapabilitiesAsync(CancellationToken ct = default);
    Task<ErrorOr<IReadOnlyList<ProviderArtworkGame>>> SearchAsync(int titleId, Guid adminUserId,
        string providerId, string query, CancellationToken ct = default);
    Task<ErrorOr<ArtworkCandidatePage>> BrowseAsync(int titleId, Guid adminUserId, string providerId,
        string gameId, ProviderArtworkQuery query, string? cursor = null, CancellationToken ct = default);
    Task<ErrorOr<TrustedArtworkCandidate>> ValidateCandidateAsync(int titleId, Guid adminUserId,
        string candidateReference, CancellationToken ct = default);
    Task<ErrorOr<ArtworkPreviewImage>> PreviewAsync(int titleId, Guid adminUserId,
        string candidateReference, CancellationToken ct = default);
}

public sealed record ArtworkCandidatePreview(string Reference, string ProviderAssetId, int Width, int Height,
    string? Style, string? Attribution, ArtworkRole Role, string SourcePageUrl, MediaType? MediaType = null, bool IsSaved = false);
public sealed record ArtworkBrowsingCapabilities(ArtworkProviderCapabilities Provider, bool IsAvailable);
public sealed record ArtworkCandidatePage(IReadOnlyList<ArtworkCandidatePreview> Items, string? NextCursor);
public sealed record TrustedArtworkCandidate(string ProviderId, string GameId, string AssetId, ArtworkRole Role,
    string TrustedAssetUrl, string TrustedPreviewUrl, string? Attribution, string SourcePageUrl, MediaType? MediaType = null);
public sealed record ArtworkPreviewImage(byte[] Bytes, string ContentType);
