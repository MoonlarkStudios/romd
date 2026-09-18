using Microsoft.EntityFrameworkCore;
using Romd.Application.Common.ReferenceCatalog;
using Romd.Admin.Application.ReferenceData;
using Romd.Contracts.Common.ReferenceCatalog;
using Romd.Domain.ReferenceData;
using Romd.Persistence.Entities;

namespace Romd.Persistence.ReferenceData;

public sealed class RatingBoardReferenceReader : IRatingBoardReferenceReader
{
    private readonly RomdDbContext db;
    public RatingBoardReferenceReader(RomdDbContext db) { this.db = db; }
    public async Task<IReadOnlyList<RatingBoardResourceDto>> ListAsync(CancellationToken ct) =>
        (await DescribeAsync(await db.RatingBoards.Where(x => true).OrderBy(x => x.Key).ToListAsync(ct), ct)).Select(x => x.Resource).ToArray();
    public async Task<ReferenceReadResult<RatingBoardResourceDto>?> GetAsync(string key, CancellationToken ct) =>
        await db.RatingBoards.SingleOrDefaultAsync(x => x.Key == key, ct) is { } row ? (await DescribeAsync([row], ct))[0] : null;
    private Task<IReadOnlyList<ReferenceReadResult<RatingBoardResourceDto>>> DescribeAsync(IReadOnlyList<RatingBoardEntity> rows, CancellationToken ct)
    {
        var results = rows.Select(row =>
        {
            var facts = TypedReferenceMapping.Effective(row);
            var resource = new RatingBoardResourceDto(row.Key!, row.Ownership.ToString(), row.BuiltInVersion,
                facts.Name, facts.Description, facts.Retired);
            return new ReferenceReadResult<RatingBoardResourceDto>(resource,
                TypedReferenceMapping.ETag(new { Resource = resource, Definition = TypedReferenceMapping.Definition(row) }));
        }).ToArray();
        return Task.FromResult<IReadOnlyList<ReferenceReadResult<RatingBoardResourceDto>>>(results);
    }
}
