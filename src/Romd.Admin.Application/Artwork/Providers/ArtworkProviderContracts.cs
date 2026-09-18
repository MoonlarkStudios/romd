using ErrorOr;
using Romd.Domain.Catalog;

namespace Romd.Admin.Application.Artwork.Providers;

public interface IArtworkProvider : IArtworkProviderBrowser, IArtworkAssetSource;

public interface IArtworkProviderBrowser
{
    ArtworkProviderCapabilities Capabilities { get; }
    Task<bool> IsAvailableAsync(CancellationToken ct = default);
    Task<ErrorOr<IReadOnlyList<ProviderArtworkGame>>> SearchAsync(string query, CancellationToken ct = default);
    Task<ErrorOr<ProviderArtworkPage>> GetCandidatesAsync(string providerGameId, ProviderArtworkQuery query,
        int page = 0, CancellationToken ct = default);
}

public interface IArtworkAssetSource
{
    Task<ErrorOr<DownloadedArtworkAsset>> DownloadAsync(string providerId, string providerGameId,
        string providerAssetId, ArtworkRole role, string trustedAssetUrl, CancellationToken ct = default);
    Task<ErrorOr<DownloadedArtworkAsset>> DownloadPreviewAsync(string providerId, ArtworkRole role,
        string trustedPreviewUrl, CancellationToken ct = default);
}

public interface ISteamGridDbCredentials
{
    Task<string?> GetApiKeyAsync(CancellationToken ct = default);
}

public sealed record ArtworkProviderCapabilities(string ProviderId, string Name,
    IReadOnlyList<ArtworkRoleCapabilities> Roles, bool SupportsLanguageFilter = false, IReadOnlyList<MediaType>? MediaTypes = null);
public sealed record ArtworkRoleCapabilities(ArtworkRole Role, IReadOnlyList<string> Dimensions, IReadOnlyList<string> Styles);
public sealed record ProviderArtworkGame(string Id, string Name);
public sealed record ProviderArtworkQuery(ArtworkRole Role, string? Dimension = null, string? Style = null, MediaType? MediaType = null);
/// <summary>Internal adapter result. Public APIs must replace remote locations with controlled preview references.</summary>
public sealed record ProviderArtworkCandidate(string ProviderGameId, string ProviderAssetId, ArtworkRole Role,
    int Width, int Height, string? Style, Uri PreviewUrl, Uri AssetUrl, string? Attribution, string SourcePageUrl, MediaType? MediaType = null);
/// <summary>Provider-specific page number stays internal; public browsing uses signed opaque cursors.</summary>
public sealed record ProviderArtworkPage(IReadOnlyList<ProviderArtworkCandidate> Items, int? NextPage);
public sealed record DownloadedArtworkAsset(byte[] Bytes, string? ContentType, string? Attribution, string? SourcePageUrl);

public static class ArtworkProviderErrors
{
    public static Error InvalidRequest() => Error.Validation("Artwork.ProviderInvalidRequest", "The provider request is invalid.");
    public static Error NotConfigured() => Error.Validation("Artwork.ProviderNotConfigured", "Configure this artwork provider before browsing.");
    public static Error Unavailable() => Error.Failure("Artwork.ProviderUnavailable", "The artwork provider could not complete the request.");
    public static Error InvalidResponse() => Error.Failure("Artwork.ProviderInvalidResponse", "The artwork provider returned an invalid response.");
    public static Error RateLimited() => Error.Failure("Artwork.ProviderRateLimited", "The artwork provider is rate limited. Try again later.");
    public static Error NotFound() => Error.NotFound("Artwork.ProviderAssetNotFound", "The artwork candidate is no longer available.");
}
