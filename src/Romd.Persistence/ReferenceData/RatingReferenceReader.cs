using Microsoft.EntityFrameworkCore;
using Romd.Application.Common.ReferenceCatalog;
using Romd.Admin.Application.ReferenceData;
using Romd.Contracts.Common.ReferenceCatalog;
using Romd.Domain.ReferenceData;
using Romd.Persistence.Entities;

namespace Romd.Persistence.ReferenceData;

public sealed class RatingReferenceReader : IRatingReferenceReader
{
    private readonly RomdDbContext db;
    public RatingReferenceReader(RomdDbContext db) { this.db = db; }
    public async Task<IReadOnlyList<RatingResourceDto>> ListAsync(CancellationToken ct) =>
        (await DescribeAsync(await db.Ratings.Where(x => true).OrderBy(x => x.Key).ToListAsync(ct), ct)).Select(x => x.Resource).ToArray();
    public async Task<ReferenceReadResult<RatingResourceDto>?> GetAsync(string key, CancellationToken ct) =>
        await db.Ratings.SingleOrDefaultAsync(x => x.Key == key, ct) is { } row ? (await DescribeAsync([row], ct))[0] : null;
    private async Task<IReadOnlyList<ReferenceReadResult<RatingResourceDto>>> DescribeAsync(IReadOnlyList<RatingEntity> rows, CancellationToken ct)
    {
        var hashes = rows.Select(x => TypedReferenceMapping.Effective(x).AssetHash).OfType<string>().Distinct().ToArray();
        var assets = await db.ReferenceAssets.Where(x => hashes.Contains(x.Hash)).ToDictionaryAsync(x => x.Hash, x => x.ContentType, ct);
        var results = rows.Select(row =>
        {
            var facts = TypedReferenceMapping.Effective(row);
            var icon = facts.AssetHash is { } hash && assets.TryGetValue(hash, out var type) ? new ReferenceAssetDto("/api/assets/" + hash, hash, type, facts.Monochrome) : null;
            var resource = new RatingResourceDto(row.Key!, row.Ownership.ToString(), row.BuiltInVersion,
                facts.Name, facts.Description, facts.BoardKey, facts.Code, facts.Designation, facts.MinimumAge, icon, facts.Retired);
            return new ReferenceReadResult<RatingResourceDto>(resource,
                TypedReferenceMapping.ETag(new { Resource = resource, Definition = TypedReferenceMapping.Definition(row) }));
        }).ToArray();
        return results;
    }
}
