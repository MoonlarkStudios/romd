using ErrorOr;
using Microsoft.EntityFrameworkCore;
using Romd.Admin.Application.Taxonomy.Queries.GetMergeImpact;
using Romd.Contracts.Management.Taxonomy;
namespace Romd.Persistence.Queries;
public sealed class TaxonomyMergeImpactReader(RomdDbContext db) : ITaxonomyMergeImpactReader
{
    public async Task<ErrorOr<TaxonomyMergeImpactDto>> ReadAsync(GetMergeImpactQuery query, CancellationToken ct)
    {
        if (query.SourceId == query.TargetId) return Error.Validation("Taxonomy.SameTarget", "Choose a different target.");
        if (query.Kind == "regions")
        {
            if (await db.Regions.CountAsync(item => item.Id == query.SourceId || item.Id == query.TargetId, ct) != 2) return Missing();
            return new TaxonomyMergeImpactDto(await db.RegionAliases.CountAsync(item => item.RegionId == query.SourceId, ct),
                await db.DatGameRegions.CountAsync(item => item.RegionId == query.SourceId, ct),
                await db.CatalogReleaseRegions.CountAsync(item => item.RegionId == query.SourceId, ct));
        }
        if (query.Kind == "languages")
        {
            if (await db.GameLanguages.CountAsync(item => item.Id == query.SourceId || item.Id == query.TargetId, ct) != 2) return Missing();
            return new TaxonomyMergeImpactDto(await db.GameLanguageAliases.CountAsync(item => item.GameLanguageId == query.SourceId, ct),
                await db.DatGameLanguages.CountAsync(item => item.GameLanguageId == query.SourceId, ct),
                await db.CatalogReleaseLanguages.CountAsync(item => item.GameLanguageId == query.SourceId, ct));
        }
        return Missing();
    }
    private static Error Missing() => Error.NotFound("Taxonomy.NotFound", "Reference value not found.");
}
