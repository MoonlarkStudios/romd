using System.Collections.ObjectModel;
using Romd.Domain.Catalog.Ratings;

namespace Romd.Domain.Catalog;

/// <summary>Owned, decoded layer values; identity fields never participate in metadata resolution.</summary>
internal sealed class TitleMetadataLayerFacts
{
    public TitleMetadataLayerFacts(string sourceId, DateTimeOffset updatedAt, TitleMetadataPayload? payload)
    {
        SourceId = sourceId;
        UpdatedAt = updatedAt;
        Description = payload?.Description;
        Publisher = payload?.Publisher;
        Developer = payload?.Developer;
        Genre = payload?.Genre;
        ReleaseDate = payload?.ReleaseDate;
        Players = payload?.Players;
        Rating = payload?.Rating;
        ContentRatings = Array.AsReadOnly((payload?.ContentRatings ?? []).Select(CaptureClaim).ToArray());
    }

    public string SourceId { get; }
    public DateTimeOffset UpdatedAt { get; }
    public string? Description { get; }
    public string? Publisher { get; }
    public string? Developer { get; }
    public string? Genre { get; }
    public DateOnly? ReleaseDate { get; }
    public int? Players { get; }
    public double? Rating { get; }
    public IReadOnlyList<ContentRatingClaim?> ContentRatings { get; }

    private static ContentRatingClaim? CaptureClaim(ContentRatingClaim? claim)
    {
        // Retained JSON can contain null descriptors or unused null claim slots. Preserve those
        // shapes; only resolution of a selected payload decides which claims need validation.
        if (claim is null || claim.Descriptors is not { } descriptors)
            return claim;
        return claim with { Descriptors = Array.AsReadOnly(descriptors.ToArray()) };
    }
}

internal sealed class TitleMetadataResolutionInput
{
    private static readonly string[] FieldNames =
    [
        nameof(Title.Description), nameof(Title.Publisher), nameof(Title.Developer), nameof(Title.Genre),
        nameof(Title.ReleaseDate), nameof(Title.Players), nameof(Title.Rating), Title.ContentRatingsFieldName
    ];

    public TitleMetadataResolutionInput(IEnumerable<TitleMetadataLayerFacts> layers, IReadOnlyList<string> globalSourcePriority,
        IReadOnlyDictionary<string, string> fieldSourceOverrides, IReadOnlyDictionary<string, string>? platformDefaults = null)
    {
        Layers = Array.AsReadOnly(layers.ToArray());
        GlobalSourcePriority = Array.AsReadOnly(globalSourcePriority.ToArray());
        FieldSourceOverrides = CaptureFieldSources(fieldSourceOverrides);
        PlatformDefaults = CaptureFieldSources(platformDefaults);
    }

    public IReadOnlyList<TitleMetadataLayerFacts> Layers { get; }
    public IReadOnlyList<string> GlobalSourcePriority { get; }
    public IReadOnlyDictionary<string, string> FieldSourceOverrides { get; }
    public IReadOnlyDictionary<string, string> PlatformDefaults { get; }

    private static IReadOnlyDictionary<string, string> CaptureFieldSources(IReadOnlyDictionary<string, string>? sources)
    {
        var captured = new Dictionary<string, string>(StringComparer.Ordinal);
        // Ask the original dictionary about canonical keys to preserve its own field-key lookup semantics.
        if (sources is not null)
            foreach (var field in FieldNames)
                if (sources.TryGetValue(field, out var source))
                    captured.Add(field, source);
        return new ReadOnlyDictionary<string, string>(captured);
    }
}
