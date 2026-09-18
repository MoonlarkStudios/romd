using Microsoft.EntityFrameworkCore;
using Romd.Application.Common.ReferenceCatalog;
using Romd.Consumer.Application.Browse.ReadModels;
using Romd.Persistence.ReferenceData;

namespace Romd.Persistence.Queries;

/// <summary>Resolves effective rating facts for a page without per-title queries or a client catalog join.</summary>
public sealed class ContentRatingSummaryReader(RomdDbContext context)
{
    public async Task<IReadOnlyDictionary<string, ConsumerContentRatingData>> ReadAsync(
        IEnumerable<ConsumerContentRatingData> ratings, CancellationToken ct)
    {
        var identities = ratings.DistinctBy(Key).ToArray();
        if (identities.Length == 0) return new Dictionary<string, ConsumerContentRatingData>();
        var keys = identities.Select(Key).ToArray();
        var boards = identities.Select(x => x.Board.ToString()).Distinct().ToArray();
        var rows = await context.Ratings.Where(x => keys.Contains(x.Key)).ToListAsync(ct);
        var boardRows = await context.RatingBoards.Where(x => boards.Contains(x.Key)).ToListAsync(ct);
        var definitions = rows.ToDictionary(x => x.Key, TypedReferenceMapping.Effective);
        var names = boardRows.ToDictionary(x => x.Key, x => TypedReferenceMapping.Effective(x).Name);
        var hashes = definitions.Values.Select(x => x.AssetHash).Where(x => x != null).Distinct().ToArray();
        var assets = await context.ReferenceAssets.Where(x => hashes.Contains(x.Hash))
            .Select(x => new { x.Hash, x.ContentType }).ToDictionaryAsync(x => x.Hash, x => x.ContentType, ct);
        return identities.ToDictionary(Key, rating =>
        {
            var definition = definitions.GetValueOrDefault(Key(rating));
            var boardName = names.GetValueOrDefault(rating.Board.ToString()) ?? rating.Board.ToString();
            var fallback = rating.Code.StartsWith(boardName, StringComparison.OrdinalIgnoreCase)
                ? rating.Code : $"{boardName} {rating.Code}";
            var icon = definition?.AssetHash is { } hash && assets.TryGetValue(hash, out var type)
                ? new ReferenceAssetData($"/api/assets/{hash}", hash, type, definition.Monochrome) : null;
            return rating with { BoardName = boardName, Name = definition?.Name ?? fallback, Description = definition?.Description, Icon = icon };
        });
    }

    public static string Key(ConsumerContentRatingData rating) => $"{rating.Board}:{rating.Code}";
}
