using System.Text.Json;

namespace Romd.Domain.Catalog;

/// <summary>
///     Stores partial metadata from a specific source as JSON.
///     Each title can have multiple layers from different sources (user, igdb, screenscraper, etc.).
/// </summary>
public sealed class TitleMetadataLayer
{
    private TitleMetadataLayer(
        int id,
        int titleId,
        string sourceId,
        MetadataSourceType sourceType,
        string metadataJson,
        DateTimeOffset updatedAt)
    {
        Id = id;
        TitleId = titleId;
        SourceId = sourceId;
        SourceType = sourceType;
        MetadataJson = metadataJson;
        UpdatedAt = updatedAt;
    }

    /// <summary>
    ///     Database-generated ID.
    /// </summary>
    public int Id { get; private set; }

    /// <summary>
    ///     The owning Title ID.
    /// </summary>
    public int TitleId { get; private set; }

    /// <summary>
    ///     Identifier for the source: "user", "igdb", "screenscraper", etc.
    /// </summary>
    public string SourceId { get; private set; }

    /// <summary>
    ///     The type/category of this source.
    /// </summary>
    public MetadataSourceType SourceType { get; private set; }

    /// <summary>
    ///     JSON blob containing the metadata fields from this source.
    /// </summary>
    public string MetadataJson { get; private set; }

    /// <summary>
    ///     When this layer was last updated.
    /// </summary>
    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>
    ///     Creates a new metadata layer for a title.
    /// </summary>
    public static TitleMetadataLayer CreateNew(
        int titleId,
        string sourceId,
        MetadataSourceType sourceType,
        TitleMetadataPayload payload)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceId);

        var json = JsonSerializer.Serialize(payload, MetadataJsonOptions.Default);

        return new TitleMetadataLayer(
            id: 0,
            titleId: titleId,
            sourceId: sourceId,
            sourceType: sourceType,
            metadataJson: json,
            updatedAt: DateTimeOffset.UtcNow);
    }

    /// <summary>
    ///     Rehydrates a layer from persistence.
    /// </summary>
    internal static TitleMetadataLayer Rehydrate(
        int id,
        int titleId,
        string sourceId,
        MetadataSourceType sourceType,
        string metadataJson,
        DateTimeOffset updatedAt)
    {
        return new TitleMetadataLayer(id, titleId, sourceId, sourceType, metadataJson, updatedAt);
    }

    /// <summary>
    ///     Updates the metadata in this layer.
    /// </summary>
    public void UpdateData(TitleMetadataPayload payload)
    {
        MetadataJson = JsonSerializer.Serialize(payload, MetadataJsonOptions.Default);
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    /// <summary>
    ///     Deserializes the stored JSON to a payload object.
    /// </summary>
    public TitleMetadataPayload? GetPayload()
    {
        if (string.IsNullOrWhiteSpace(MetadataJson) || MetadataJson == "{}")
        {
            return null;
        }

        return JsonSerializer.Deserialize<TitleMetadataPayload>(MetadataJson, MetadataJsonOptions.Default);
    }
}
