namespace Romd.Domain.Catalog;

/// <summary>
///     Represents a media asset (cover, screenshot, etc.) for a title.
///     Media files are stored locally with optional source URL for refresh.
///     Multiple media of the same type can exist from different sources;
///     IsPrimary indicates which one is currently displayed.
/// </summary>
public sealed class TitleMedia
{
    private TitleMedia(
        int id,
        int titleId,
        MediaType type,
        int fileId,
        string sourceId,
        string contentType,
        bool isPrimary,
        string? sourceUrl,
        DateTimeOffset createdAt,
        string? attribution,
        string? sourcePageUrl,
        MediaPlacement placement,
        string? providerAssetId)
    {
        Id = id;
        TitleId = titleId;
        Type = type;
        FileId = fileId;
        SourceId = sourceId;
        ContentType = contentType;
        IsPrimary = isPrimary;
        SourceUrl = sourceUrl;
        CreatedAt = createdAt;
        Attribution = attribution;
        SourcePageUrl = sourcePageUrl;
        CatalogReleaseId = placement.CatalogReleaseId;
        Region = placement.Region;
        Language = placement.Language;
        IsPreferred = placement.IsPreferred;
        ProviderAssetId = providerAssetId;
    }

    public int Id { get; private set; }
    public int TitleId { get; private set; }

    /// <summary>
    ///     Type of media (Cover, Screenshot, Banner, Logo).
    /// </summary>
    public MediaType Type { get; private set; }

    /// <summary>
    ///     FileId for the stored media blob.
    /// </summary>
    public int FileId { get; private set; }

    /// <summary>
    ///     Identifier for the source: "user", "igdb", "screenscraper", etc.
    /// </summary>
    public string SourceId { get; private set; }

    /// <summary>
    ///     MIME content type for serving (e.g., "image/jpeg", "image/png").
    ///     Required for CAS storage where files don't have extensions.
    /// </summary>
    public string ContentType { get; private set; }

    /// <summary>
    ///     Indicates if this is the currently active/displayed media for this Type.
    ///     For primary media types (Cover, Logo, Banner), exactly one should be primary.
    ///     For supplementary media (Screenshots), multiple can be shown.
    /// </summary>
    public bool IsPrimary { get; private set; }

    /// <summary>
    ///     Original URL from provider for potential refresh.
    ///     Null for user uploads.
    /// </summary>
    public string? SourceUrl { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }
    public string? Attribution { get; }
    public string? SourcePageUrl { get; }

    /// <summary>The canonical catalog release this media belongs to; null for title-level media.</summary>
    public int? CatalogReleaseId { get; private set; }

    /// <summary>The region the item was published for, when known.</summary>
    public string? Region { get; private set; }

    /// <summary>The item's language, when known.</summary>
    public string? Language { get; private set; }

    /// <summary>
    ///     The packaging scan shown wherever one image of its type and release is displayed. At most one item per
    ///     title, type and release is preferred; alternatives are retained.
    /// </summary>
    public bool IsPreferred { get; private set; }

    /// <summary>The provider's own identifier for the collected asset, when it came from a provider.</summary>
    public string? ProviderAssetId { get; }

    /// <summary>Uploads and manually collected provider media; enrichment-owned rows keep their provider type.</summary>
    public bool IsCurated => SourceId == "user" || SourceId.StartsWith("gallery:", StringComparison.Ordinal);

    public MediaPlacement Placement => new(CatalogReleaseId, Region, Language, IsPreferred);

    /// <summary>
    ///     Creates a new media entry. IsPrimary defaults to false;
    ///     caller should invoke RecalculatePrimaryMedia on the Title after adding.
    /// </summary>
    public static TitleMedia CreateNew(
        int titleId,
        MediaType type,
        int fileId,
        string sourceId,
        string contentType,
        DateTimeOffset createdAt,
        string? sourceUrl = null,
        string? attribution = null,
        string? sourcePageUrl = null,
        MediaPlacement? placement = null,
        string? providerAssetId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentType);
        placement ??= MediaPlacement.TitleLevel;
        if (placement.IsPreferred && !MediaTypes.IsPackaging(type))
            throw new ArgumentException("Only packaging media can be preferred.", nameof(placement));

        return new TitleMedia(
            id: 0,
            titleId: titleId,
            type: type,
            fileId: fileId,
            sourceId: sourceId,
            contentType: contentType,
            isPrimary: false,
            sourceUrl: sourceUrl,
            createdAt: createdAt,
            attribution: attribution,
            sourcePageUrl: sourcePageUrl,
            placement: placement,
            providerAssetId: providerAssetId);
    }

    /// <summary>
    ///     Rehydrates a media entry from persistence. Trusts that data is valid.
    /// </summary>
    internal static TitleMedia Rehydrate(
        int id,
        int titleId,
        MediaType type,
        int fileId,
        string sourceId,
        string contentType,
        bool isPrimary,
        string? sourceUrl,
        DateTimeOffset createdAt,
        string? attribution = null,
        string? sourcePageUrl = null,
        MediaPlacement? placement = null,
        string? providerAssetId = null)
    {
        return new TitleMedia(id, titleId, type, fileId, sourceId, contentType, isPrimary, sourceUrl, createdAt,
            attribution, sourcePageUrl, placement ?? MediaPlacement.TitleLevel, providerAssetId);
    }

    /// <summary>Retains all media facts while giving one title its own mutable child.</summary>
    internal TitleMedia CopyForTitle(int titleId)
    {
        if (TitleId != titleId)
            throw new ArgumentException("Media must belong to the title retaining it.", nameof(titleId));
        return new TitleMedia(Id, TitleId, Type, FileId, SourceId, ContentType, IsPrimary, SourceUrl, CreatedAt,
            Attribution, SourcePageUrl, Placement, ProviderAssetId);
    }

    /// <summary>A new row for another title with the same facts, its classification and no display flags.</summary>
    internal TitleMedia CopyToTitle(int titleId, DateTimeOffset createdAt) => new(0, titleId, Type, FileId, SourceId,
        ContentType, false, SourceUrl, createdAt, Attribution, SourcePageUrl, Placement with { IsPreferred = false },
        ProviderAssetId);

    internal void Reclassify(MediaType type, MediaPlacement placement)
    {
        Type = type;
        CatalogReleaseId = placement.CatalogReleaseId;
        Region = placement.Region;
        Language = placement.Language;
        IsPreferred = placement.IsPreferred;
    }

    internal void ClearPreference() => IsPreferred = false;

    /// <summary>
    ///     Sets whether this media is the primary for its type.
    /// </summary>
    internal void SetPrimary(bool isPrimary)
    {
        IsPrimary = isPrimary;
    }

}

/// <summary>
///     Where a media item belongs: a canonical release or the title, with the region and language printed on it.
///     Region and language are trimmed display facts bounded to <see cref="MaxTextLength" /> characters.
/// </summary>
public sealed record MediaPlacement(int? CatalogReleaseId, string? Region, string? Language, bool IsPreferred)
{
    public const int MaxTextLength = 100;

    public static MediaPlacement TitleLevel { get; } = new(null, null, null, false);

    /// <summary>Normalizes text and rejects values persistence cannot hold. Returns null for invalid input.</summary>
    public static MediaPlacement? TryCreate(int? catalogReleaseId, string? region, string? language, bool isPreferred)
    {
        if (catalogReleaseId is <= 0)
            return null;
        var normalizedRegion = Normalize(region);
        var normalizedLanguage = Normalize(language);
        if (normalizedRegion?.Length > MaxTextLength || normalizedLanguage?.Length > MaxTextLength ||
            HasControl(normalizedRegion) || HasControl(normalizedLanguage))
            return null;
        return new MediaPlacement(catalogReleaseId, normalizedRegion, normalizedLanguage, isPreferred);
    }

    private static string? Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static bool HasControl(string? value) => value is not null && value.Any(char.IsControl);
}
