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
        string? sourcePageUrl)
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
        string? sourcePageUrl = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentType);

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
            sourcePageUrl: sourcePageUrl);
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
        string? sourcePageUrl = null)
    {
        return new TitleMedia(id, titleId, type, fileId, sourceId, contentType, isPrimary, sourceUrl, createdAt, attribution, sourcePageUrl);
    }

    /// <summary>Retains all media facts while giving one title its own mutable child.</summary>
    internal TitleMedia CopyForTitle(int titleId)
    {
        if (TitleId != titleId)
            throw new ArgumentException("Media must belong to the title retaining it.", nameof(titleId));
        return new TitleMedia(Id, TitleId, Type, FileId, SourceId, ContentType, IsPrimary, SourceUrl, CreatedAt, Attribution, SourcePageUrl);
    }

    /// <summary>
    ///     Sets whether this media is the primary for its type.
    /// </summary>
    internal void SetPrimary(bool isPrimary)
    {
        IsPrimary = isPrimary;
    }

}
