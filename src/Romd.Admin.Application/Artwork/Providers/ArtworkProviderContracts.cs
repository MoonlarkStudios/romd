using ErrorOr;
using Romd.Application.Common.Errors;
using Romd.Domain.Catalog;

namespace Romd.Admin.Application.Artwork.Providers;

public interface IArtworkProvider : IArtworkProviderBrowser, IArtworkAssetSource;

public interface IArtworkProviderBrowser
{
    ArtworkProviderCapabilities Capabilities { get; }
    Task<bool> IsAvailableAsync(CancellationToken ct = default);
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
public sealed record ProviderArtworkGame(string Id, string Name, int? Year = null);
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
    // A provider switched off or without credentials is local configuration, a conflict rather than an outage.
    public static Error NotConfigured() => Error.Conflict("Artwork.ProviderNotConfigured", "Configure this artwork provider before browsing.");
    // An upstream provider that cannot serve the request is a 503 to the caller, and one that answers invalidly a 502,
    // not a server fault.
    public static Error Unavailable() => Error.Custom(503, "Artwork.ProviderUnavailable", "The artwork provider could not complete the request.");
    public static Error InvalidResponse() => Error.Custom(502, "Artwork.ProviderInvalidResponse", "The artwork provider returned an invalid response.");
    public static Error BrowseLimitExceeded() => Error.Conflict("Artwork.ProviderBrowseLimitExceeded",
        "The artwork provider exploration limit was reached. Narrow the candidate filters and start again.");
    /// <summary>A rate-limited upstream; when it said to try again travels as <see cref="RetryAfterMetadata" />.</summary>
    public static Error RateLimited(DateTimeOffset? retryAfter = null) => Error.Custom(503,
        "Artwork.ProviderRateLimited", "The artwork provider is rate limited. Try again later.",
        RetryAfterMetadata.For(retryAfter));

    public static DateTimeOffset? GetRetryAfter(Error error) =>
        error.Code == "Artwork.ProviderRateLimited" ? RetryAfterMetadata.Of(error) : null;
    public static Error NotFound() => Error.NotFound("Artwork.ProviderAssetNotFound", "The artwork candidate is no longer available.");
}
