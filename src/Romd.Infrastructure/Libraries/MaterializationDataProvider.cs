using Microsoft.EntityFrameworkCore;
using Romd.Admin.Application.Libraries;
using Romd.Domain.Catalog.Ratings;
using Romd.Domain.Libraries;
using Romd.Persistence.Queries;
using Romd.Persistence;

namespace Romd.Infrastructure.Libraries;

public sealed class MaterializationDataProvider(RomdDbContext context) : IMaterializationDataProvider
{
    public async Task<IReadOnlyList<TitleCandidates>> GetCandidatesAsync(
        LibraryConfiguration config,
        CancellationToken ct = default)
    {
        var titlesQuery = context.Titles.AsNoTracking().AsQueryable();

        if (config.TitleSelectionMode == LibraryTitleSelectionMode.Rules &&
            config.AllowedPlatformIds.Count > 0)
        {
            var platformIds = config.AllowedPlatformIds.ToList();
            titlesQuery = titlesQuery.Where(t => platformIds.Contains(t.PlatformId));
        }

        if (config.TitleSelectionMode == LibraryTitleSelectionMode.IncludeOnly)
        {
            var titleIds = config.IncludeTitleIds.ToList();
            titlesQuery = titlesQuery.Where(t => titleIds.Contains(t.Id));
        }

        var romFactsQuery = context.DatRoms
            .AsNoTracking()
            .GroupBy(r => r.DatGameId)
            .Select(g => new
            {
                DatGameId = g.Key,
                TotalRomCount = (int?)g.Count(),
                OwnedRomCount = (int?)g.Count(r => r.RomFileId != null)
            });

        var regionIdsQuery = context.DatGameRegions
            .AsNoTracking()
            .GroupBy(dr => dr.DatGameId)
            .Select(g => new
            {
                DatGameId = g.Key,
                RegionIdsCsv = string.Join(",", g.Select(dr => dr.RegionId))
            });

        var languageIdsQuery = context.DatGameLanguages
            .AsNoTracking()
            .GroupBy(dl => dl.DatGameId)
            .Select(g => new
            {
                DatGameId = g.Key,
                LanguageIdsCsv = string.Join(",", g.Select(dl => dl.GameLanguageId))
            });

        var candidateRows = await (
                from title in titlesQuery
                join link in context.EffectiveTitleSourceLinks().AsNoTracking()
                    on title.Id equals link.TitleId
                join game in context.DatGames.AsNoTracking()
                    on link.SourceEntryId equals game.SourceEntryId
                join romFacts in romFactsQuery
                    on game.Id equals romFacts.DatGameId into romFactsJoin
                from romFacts in romFactsJoin.DefaultIfEmpty()
                join regionIds in regionIdsQuery
                    on game.Id equals regionIds.DatGameId into regionIdsJoin
                from regionIds in regionIdsJoin.DefaultIfEmpty()
                join languageIds in languageIdsQuery
                    on game.Id equals languageIds.DatGameId into languageIdsJoin
                from languageIds in languageIdsJoin.DefaultIfEmpty()
                join releaseSource in context.CatalogReleaseSources.AsNoTracking()
                    on new { game.SourceEntryId, ProviderClaimKey = game.Id.ToString() }
                    equals new { releaseSource.SourceEntryId, releaseSource.ProviderClaimKey }
                    into releaseSourceJoin
                from releaseSource in releaseSourceJoin.DefaultIfEmpty()
                select new CandidateRow(
                    title.Id,
                    title.PlatformId,
                    title.Genre,
                    game.Id,
                    releaseSource != null ? (int?)releaseSource.CatalogReleaseId : null,
                    game.DatFileId,
                    game.Revision,
                    romFacts.OwnedRomCount > 0,
                    romFacts.TotalRomCount > 0 && romFacts.OwnedRomCount == romFacts.TotalRomCount,
                    regionIds.RegionIdsCsv,
                    languageIds.LanguageIdsCsv))
            .ToListAsync(ct);

        if (candidateRows.Count == 0)
            return [];

        var candidateTitleIds = candidateRows
            .Select(r => r.TitleId)
            .Distinct()
            .ToList();
        var ratingsByTitleId = await context.TitleContentRatings
            .AsNoTracking()
            .Where(r => candidateTitleIds.Contains(r.TitleId))
            .ToListAsync(ct);
        var ratingsLookup = ratingsByTitleId
            .GroupBy(r => r.TitleId)
            .ToDictionary(
                g => g.Key,
                g => g.Select(r => r.ToDomain()).ToList());

        return candidateRows
            .GroupBy(r => r.TitleId)
            .Select(g =>
            {
                var first = g.First();

                var candidates = g.Select(r => new GameCandidate(
                    r.DatGameId,
                    r.CatalogReleaseId,
                    r.DatFileId,
                    r.Revision,
                    r.HasOwnedRoms,
                    r.IsComplete,
                    [r.DatFileId],
                    ParseIds(r.RegionIdsCsv),
                    ParseIds(r.LanguageIdsCsv)
                )).ToList();

                return new TitleCandidates(
                    first.TitleId,
                    first.PlatformId,
                    first.Genre,
                    ratingsLookup.GetValueOrDefault(first.TitleId) ?? [],
                    candidates);
            })
            .ToList();
    }

    private static IReadOnlyList<int> ParseIds(string? idsCsv) =>
        string.IsNullOrWhiteSpace(idsCsv)
            ? []
            : idsCsv.Split(',').Select(int.Parse).ToList();

    private sealed record CandidateRow(
        int TitleId,
        int PlatformId,
        string? Genre,
        int DatGameId,
        int? CatalogReleaseId,
        int DatFileId,
        string? Revision,
        bool HasOwnedRoms,
        bool IsComplete,
        string? RegionIdsCsv,
        string? LanguageIdsCsv);
}
