using System.Collections.ObjectModel;

namespace Romd.Domain.Catalog;

/// <summary>Actual publication instants; imported metadata layers retain separate recency evidence.</summary>
public sealed class TitleAbsorptionTimes
{
    public TitleAbsorptionTimes(DateTimeOffset createdAt, IEnumerable<KeyValuePair<string, DateTimeOffset>> metadataLayerUpdatedAt)
    {
        CreatedAt = createdAt;
        var captured = new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase);
        foreach (var (source, timestamp) in metadataLayerUpdatedAt)
            captured.TryAdd(source, timestamp);
        MetadataLayerUpdatedAt = new ReadOnlyDictionary<string, DateTimeOffset>(captured);
    }

    public DateTimeOffset CreatedAt { get; }
    public IReadOnlyDictionary<string, DateTimeOffset> MetadataLayerUpdatedAt { get; }
}
