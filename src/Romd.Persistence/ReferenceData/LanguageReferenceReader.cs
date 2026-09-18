using Microsoft.EntityFrameworkCore;
using Romd.Application.Common.ReferenceCatalog;
using Romd.Admin.Application.ReferenceData;
using Romd.Contracts.Common.ReferenceCatalog;
using Romd.Domain.ReferenceData;
using Romd.Persistence.Entities;

namespace Romd.Persistence.ReferenceData;

public sealed class LanguageReferenceReader : ILanguageReferenceReader
{
    private readonly RomdDbContext db;
    public LanguageReferenceReader(RomdDbContext db) { this.db = db; }
    public async Task<IReadOnlyList<LanguageResourceDto>> ListAsync(CancellationToken ct) =>
        (await DescribeAsync(await db.GameLanguages.Where(x => x.Ownership != null).OrderBy(x => x.CanonicalKey).ToListAsync(ct), ct)).Select(x => x.Resource).ToArray();
    public async Task<ReferenceReadResult<LanguageResourceDto>?> GetAsync(string key, CancellationToken ct) =>
        await db.GameLanguages.SingleOrDefaultAsync(x => x.Ownership != null && x.CanonicalKey == key, ct) is { } row ? (await DescribeAsync([row], ct))[0] : null;
    private Task<IReadOnlyList<ReferenceReadResult<LanguageResourceDto>>> DescribeAsync(IReadOnlyList<GameLanguageEntity> rows, CancellationToken ct)
    {
        var results = rows.Select(row =>
        {
            var facts = TypedReferenceMapping.Effective(row);
            var resource = new LanguageResourceDto(row.CanonicalKey!, row.Ownership!.Value.ToString(), row.BuiltInVersion,
                facts.Name, facts.Description, facts.SortOrder, facts.Code, facts.Retired);
            return new ReferenceReadResult<LanguageResourceDto>(resource,
                TypedReferenceMapping.ETag(new { Resource = resource, Definition = TypedReferenceMapping.Definition(row) }));
        }).ToArray();
        return Task.FromResult<IReadOnlyList<ReferenceReadResult<LanguageResourceDto>>>(results);
    }
}
