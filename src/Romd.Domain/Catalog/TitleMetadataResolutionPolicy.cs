using System.Collections.ObjectModel;
using Romd.Domain.Catalog.Ratings;

namespace Romd.Domain.Catalog;

internal sealed class TitleMetadataResolution
{
    public TitleMetadataResolution(string? description, string? publisher, string? developer, string? genre,
        DateOnly? releaseDate, int? players, double? rating, IDictionary<string, string> provenance,
        IEnumerable<ContentRating> contentRatings)
    {
        Description = description;
        Publisher = publisher;
        Developer = developer;
        Genre = genre;
        ReleaseDate = releaseDate;
        Players = players;
        Rating = rating;
        FieldProvenance = new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(provenance, StringComparer.Ordinal));
        ContentRatings = Array.AsReadOnly(contentRatings.Select(value => value.Descriptors is { } descriptors
            ? value with { Descriptors = Array.AsReadOnly(descriptors.ToArray()) }
            : value).ToArray());
        ConservativeMinimumAge = ContentRatings.Where(value => value.Designation == RatingDesignation.Rated)
            .Max(value => value.MinimumAge);
    }

    public string? Description { get; }
    public string? Publisher { get; }
    public string? Developer { get; }
    public string? Genre { get; }
    public DateOnly? ReleaseDate { get; }
    public int? Players { get; }
    public double? Rating { get; }
    public IReadOnlyDictionary<string, string> FieldProvenance { get; }
    public IReadOnlyList<ContentRating> ContentRatings { get; }
    public int? ConservativeMinimumAge { get; }
}

/// <summary>Resolves each field and rating board without changing the aggregate or reading ambient state.</summary>
internal static class TitleMetadataResolutionPolicy
{
    public static TitleMetadataResolution Resolve(TitleMetadataResolutionInput input)
    {
        var user = input.Layers.FirstOrDefault(layer => IsUser(layer.SourceId));
        var automated = input.Layers.Where(layer => !IsUser(layer.SourceId))
            .OrderBy(layer => Priority(layer.SourceId)).ThenByDescending(layer => layer.UpdatedAt).ToArray();
        var layersBySource = new Dictionary<string, TitleMetadataLayerFacts>(StringComparer.OrdinalIgnoreCase);
        if (user is not null)
            layersBySource["user"] = user;
        // Preserve ordered last-payload behavior when a retained snapshot contains duplicate source IDs.
        foreach (var layer in automated)
            layersBySource[layer.SourceId] = layer;
        var provenance = new Dictionary<string, string>(StringComparer.Ordinal);
        var description = ResolveField(nameof(Title.Description), layer => layer.Description);
        var publisher = ResolveField(nameof(Title.Publisher), layer => layer.Publisher);
        var developer = ResolveField(nameof(Title.Developer), layer => layer.Developer);
        var genre = ResolveField(nameof(Title.Genre), layer => layer.Genre);
        var releaseDate = ResolveField(nameof(Title.ReleaseDate), layer => layer.ReleaseDate);
        var players = ResolveField(nameof(Title.Players), layer => layer.Players);
        var rating = ResolveField(nameof(Title.Rating), layer => layer.Rating);
        var contentRatings = new List<ContentRating>();
        foreach (var source in SourcesFor(Title.ContentRatingsFieldName))
        {
            if (!layersBySource.TryGetValue(source, out var layer))
                continue;
            foreach (var claim in layer.ContentRatings)
            {
                ArgumentNullException.ThrowIfNull(claim);
                if (contentRatings.Any(value => value.Board == claim.Board))
                    continue;
                if (RatingBoardCatalog.TryResolve(claim, source) is { } resolved)
                    contentRatings.Add(resolved);
            }
        }
        return new(description, publisher, developer, genre, releaseDate, players, rating, provenance, contentRatings);

        T ResolveField<T>(string field, Func<TitleMetadataLayerFacts, T> extract)
        {
            foreach (var source in SourcesFor(field))
            {
                if (!layersBySource.TryGetValue(source, out var layer))
                    continue;
                var value = extract(layer);
                if (value is null)
                    continue;
                provenance[field] = source;
                return value;
            }
            return default!;
        }

        IEnumerable<string> SourcesFor(string field)
        {
            if (input.FieldSourceOverrides.TryGetValue(field, out var overridden))
                yield return overridden;
            if (user is not null)
                yield return "user";
            if (input.PlatformDefaults.TryGetValue(field, out var platform))
                yield return platform;
            foreach (var layer in automated)
                yield return layer.SourceId;
        }

        int Priority(string source)
        {
            for (var i = 0; i < input.GlobalSourcePriority.Count; i++)
                if (input.GlobalSourcePriority[i].Equals(source, StringComparison.OrdinalIgnoreCase))
                    return i;
            return int.MaxValue;
        }
    }

    private static bool IsUser(string source) => source.Equals("user", StringComparison.OrdinalIgnoreCase);
}
