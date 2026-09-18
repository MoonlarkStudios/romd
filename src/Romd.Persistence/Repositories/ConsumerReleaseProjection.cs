using Microsoft.EntityFrameworkCore;
using Romd.Consumer.Application.Browse;
using Romd.Consumer.Application.Browse.ReadModels;
using Romd.Domain.Libraries;

namespace Romd.Persistence.Repositories;

internal static class ConsumerReleaseProjection
{
    private static readonly string ValidConfigurationState = LibraryConfigurationState.Valid.ToString();

    public static async Task<IReadOnlyDictionary<int, IReadOnlyList<ConsumerReleaseSelectionCandidate>>> LoadByTitleIdsAsync(
        RomdDbContext context,
        int libraryId,
        IReadOnlyCollection<int> titleIds,
        CancellationToken ct = default)
    {
        if (titleIds.Count == 0)
        {
            return new Dictionary<int, IReadOnlyList<ConsumerReleaseSelectionCandidate>>();
        }

        var rows = await (
                from release in context.MaterializedLibraryReleases.AsNoTracking()
                where release.LibraryId == libraryId &&
                      release.IsExposed &&
                      release.IsOwned &&
                      release.CatalogReleaseId != null &&
                      titleIds.Contains(release.TitleId) &&
                      context.Libraries.Any(library =>
                          library.Id == release.LibraryId &&
                          library.ConfigurationState == ValidConfigurationState)
                join catalogRelease in context.CatalogReleases.AsNoTracking()
                    on release.CatalogReleaseId equals catalogRelease.Id
                orderby release.TitleId, catalogRelease.Name, catalogRelease.Id
                select new ReleaseRow(
                    release.TitleId,
                    catalogRelease.Id,
                    catalogRelease.Name,
                    catalogRelease.Revision,
                    catalogRelease.Region,
                    catalogRelease.Language,
                    catalogRelease.SizeBytes,
                    release.IsComplete,
                    release.IsPlayable))
            .ToListAsync(ct);

        var releaseIds = rows
            .Select(row => row.Id)
            .Distinct()
            .ToList();

        var regionsByReleaseId = await LoadRegionsByReleaseIdAsync(context, releaseIds, ct);
        var languagesByReleaseId = await LoadLanguagesByReleaseIdAsync(context, releaseIds, ct);

        return rows
            .GroupBy(row => row.TitleId)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<ConsumerReleaseSelectionCandidate>)group
                    .Select(row => ToReleaseSelectionCandidate(row, regionsByReleaseId, languagesByReleaseId))
                    .ToList());
    }

    private static async Task<IReadOnlyDictionary<int, ReleaseTaxonomyData>> LoadRegionsByReleaseIdAsync(
        RomdDbContext context,
        IReadOnlyCollection<int> releaseIds,
        CancellationToken ct)
    {
        if (releaseIds.Count == 0)
        {
            return new Dictionary<int, ReleaseTaxonomyData>();
        }

        var rows = await (
                from releaseRegion in context.CatalogReleaseRegions.AsNoTracking()
                where releaseIds.Contains(releaseRegion.CatalogReleaseId)
                join region in context.Regions.AsNoTracking()
                    on releaseRegion.RegionId equals region.Id
                orderby region.SortOrder, region.Name
                select new { ReleaseId = releaseRegion.CatalogReleaseId, RegionId = region.Id, region.Name })
            .ToListAsync(ct);

        return rows
            .GroupBy(row => row.ReleaseId)
            .ToDictionary(
                group => group.Key,
                group => new ReleaseTaxonomyData(
                    group.Select(row => row.RegionId).Distinct().ToList(),
                    group.Select(row => row.Name).Distinct().ToList()));
    }

    private static async Task<IReadOnlyDictionary<int, ReleaseTaxonomyData>> LoadLanguagesByReleaseIdAsync(
        RomdDbContext context,
        IReadOnlyCollection<int> releaseIds,
        CancellationToken ct)
    {
        if (releaseIds.Count == 0)
        {
            return new Dictionary<int, ReleaseTaxonomyData>();
        }

        var rows = await (
                from releaseLanguage in context.CatalogReleaseLanguages.AsNoTracking()
                where releaseIds.Contains(releaseLanguage.CatalogReleaseId)
                join language in context.GameLanguages.AsNoTracking()
                    on releaseLanguage.GameLanguageId equals language.Id
                orderby language.SortOrder, language.Name
                select new { ReleaseId = releaseLanguage.CatalogReleaseId, LanguageId = language.Id, language.Name })
            .ToListAsync(ct);

        return rows
            .GroupBy(row => row.ReleaseId)
            .ToDictionary(
                group => group.Key,
                group => new ReleaseTaxonomyData(
                    group.Select(row => row.LanguageId).Distinct().ToList(),
                    group.Select(row => row.Name).Distinct().ToList()));
    }

    private static ConsumerReleaseSelectionCandidate ToReleaseSelectionCandidate(
        ReleaseRow row,
        IReadOnlyDictionary<int, ReleaseTaxonomyData> regionsByReleaseId,
        IReadOnlyDictionary<int, ReleaseTaxonomyData> languagesByReleaseId)
    {
        var regions = GetTaxonomy(regionsByReleaseId, row.Id, row.Region);
        var languages = GetTaxonomy(languagesByReleaseId, row.Id, row.Language);

        return new ConsumerReleaseSelectionCandidate(
            new ConsumerReleaseData
            {
                Id = row.Id,
                Name = row.Name,
                Revision = row.Revision,
                Regions = regions.Names,
                Languages = languages.Names,
                SizeBytes = row.SizeBytes,
                IsComplete = row.IsComplete
            },
            row.IsPlayable,
            regions.Ids,
            languages.Ids);
    }

    private static ReleaseTaxonomyData GetTaxonomy(
        IReadOnlyDictionary<int, ReleaseTaxonomyData> taxonomyByReleaseId,
        int releaseId,
        string? fallback) =>
        taxonomyByReleaseId.TryGetValue(releaseId, out var data) && data.Names.Count > 0
            ? data
            : new ReleaseTaxonomyData([], string.IsNullOrWhiteSpace(fallback) ? [] : [fallback]);

    private sealed record ReleaseTaxonomyData(
        IReadOnlyList<int> Ids,
        IReadOnlyList<string> Names);

    private sealed record ReleaseRow(
        int TitleId,
        int Id,
        string Name,
        string? Revision,
        string? Region,
        string? Language,
        long SizeBytes,
        bool IsComplete,
        bool IsPlayable);
}
