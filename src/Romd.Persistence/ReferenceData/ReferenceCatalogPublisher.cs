using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Romd.Contracts.Common.ReferenceCatalog;
using Romd.Persistence.Entities;

namespace Romd.Persistence.ReferenceData;

/// <summary>Projects authoritative typed facts into the current snapshot and compatibility columns.</summary>
internal static class ReferenceCatalogPublisher
{
    // The caller owns the catalog lock and database transaction.
    internal static async Task<ReferenceCatalogDto> PublishAsync(RomdDbContext db, int? builtInVersion, CancellationToken ct)
    {
        var state = await db.ReferenceCatalogState.AsTracking().SingleOrDefaultAsync(ct);
        var assets = await db.ReferenceAssets.ToDictionaryAsync(x => x.Hash, x => x.ContentType, ct);
        ReferenceAssetDto? Icon(string? hash, bool monochrome) => hash is not null && assets.TryGetValue(hash, out var type)
            ? new("/api/assets/" + hash, hash, type, monochrome) : null;
        var companyRows = await db.Companies.AsTracking().OrderBy(x => x.Key).ToListAsync(ct);
        foreach (var row in companyRows) row.Name = TypedReferenceMapping.Effective(row).Name;
        var companies = companyRows.Select(x => (x.Key, Facts: TypedReferenceMapping.Effective(x))).ToArray();
        var companyNames = companies.ToDictionary(x => x.Key, x => x.Facts.Name, StringComparer.Ordinal);
        var relationships = await db.SystemCompanies.OrderBy(x => x.CompanyKey).ToListAsync(ct);
        var manufacturers = relationships.GroupBy(x => x.PlatformId).ToDictionary(x => x.Key,
            x => x.Select(c => new CompanySummaryDto(c.CompanyKey, companyNames[c.CompanyKey])).ToArray());
        var systemRows = await db.Platforms.AsTracking().Where(x => x.Ownership != null).OrderBy(x => x.CanonicalKey).ToListAsync(ct);
        var systems = systemRows.Select(row =>
        {
            var facts = TypedReferenceMapping.Effective(row);
            var makers = manufacturers.GetValueOrDefault(row.Id) ?? [];
            row.Name = facts.Name;
            row.Manufacturer = SystemCompanyReader.Names(makers);
            return new ReferenceSystemDto(row.CanonicalKey!, facts.Name, facts.CompactLabel, facts.Description,
                Icon(facts.AssetHash, facts.Monochrome), makers.Select(x => x.Key).ToArray(), facts.Retired, makers);
        }).ToArray();
        var regionRows = await db.Regions.AsTracking().Where(x => x.Ownership != null).OrderBy(x => x.CanonicalKey).ToListAsync(ct);
        var regions = regionRows.Select(row =>
        {
            var facts = TypedReferenceMapping.Effective(row);
            row.Name = facts.Name; row.SortOrder = facts.SortOrder;
            return new ReferenceTaxonomyDto(row.CanonicalKey!, facts.Name, facts.Description, facts.SortOrder, null, facts.Retired);
        }).ToArray();
        var languageRows = await db.GameLanguages.AsTracking().Where(x => x.Ownership != null).OrderBy(x => x.CanonicalKey).ToListAsync(ct);
        var languages = languageRows.Select(row =>
        {
            var facts = TypedReferenceMapping.Effective(row);
            row.Name = facts.Name; row.SortOrder = facts.SortOrder;
            return new ReferenceTaxonomyDto(row.CanonicalKey!, facts.Name, facts.Description, facts.SortOrder, facts.Code, facts.Retired);
        }).ToArray();
        var boards = (await db.RatingBoards.OrderBy(x => x.Key).ToListAsync(ct)).Select(row =>
        {
            var facts = TypedReferenceMapping.Effective(row);
            return new ReferenceRatingBoardDto(row.Key, facts.Name, facts.Description, facts.Retired);
        }).ToArray();
        var ratings = (await db.Ratings.OrderBy(x => x.Key).ToListAsync(ct)).Select(row =>
        {
            var facts = TypedReferenceMapping.Effective(row);
            return new ReferenceRatingDto(facts.BoardKey, facts.Code, facts.Name, facts.Description,
                Icon(facts.AssetHash, facts.Monochrome), facts.Designation, facts.MinimumAge, facts.Retired);
        }).ToArray();
        var snapshot = new ReferenceCatalogDto
        {
            BuiltInVersion = builtInVersion ?? state?.BuiltInVersion ?? 1, Revision = "",
            Systems = systems, Companies = companies.Select(x => new ReferenceNamedDto(x.Key, x.Facts.Name, x.Facts.Description, x.Facts.Retired)).ToArray(),
            Regions = regions, Languages = languages, RatingBoards = boards, Ratings = ratings
        };
        snapshot = snapshot with { Revision = ReferenceCatalogService.Hash(JsonSerializer.SerializeToUtf8Bytes(snapshot, ReferenceCatalogService.Json)) };
        var json = JsonSerializer.Serialize(snapshot, ReferenceCatalogService.Json);
        if (state is null) db.ReferenceCatalogState.Add(new ReferenceCatalogStateEntity { Revision = snapshot.Revision, BuiltInVersion = snapshot.BuiltInVersion, Json = json });
        else { state.Revision = snapshot.Revision; state.BuiltInVersion = snapshot.BuiltInVersion; state.Json = json; }
        await ReferenceArtworkOwnershipIndex.RebuildAsync(db, ct);
        await db.SaveChangesAsync(ct);
        return snapshot;
    }
}
