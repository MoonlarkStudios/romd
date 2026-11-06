using Microsoft.EntityFrameworkCore;
using Romd.Admin.Application.Libraries;
using Romd.Domain.Catalog.Ratings;
using Romd.Domain.Libraries;
using Romd.Persistence.Queries;

namespace Romd.Persistence.Libraries;

public sealed class MaterializationDataProvider(RomdDbContext context) : IMaterializationDataProvider
{
    public async Task<IReadOnlyList<TitleCandidates>> GetCandidatesAsync(
        LibraryConfiguration config,
        CancellationToken ct = default)
    {
        var titlesQuery = context.Titles.AsNoTracking().AsQueryable();

        if (!config.IncludeUntrackedTitles)
        {
            // TitleId is unique in TrackedTitles. An explicit early join keeps this selective
            // relation inside PostgreSQL's join search before the child aggregate joins.
            titlesQuery = from tracked in context.TrackedTitles
                          join title in titlesQuery on tracked.TitleId equals title.Id
                          select title;
        }

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

        // Scope all child aggregates before grouping, so a small tracked library does not
        // aggregate ROM/region/language facts for the entire catalog.
        var candidateGameIds =
            from title in titlesQuery
            join link in context.EffectiveTitleSourceLinks() on title.Id equals link.TitleId
            join game in context.DatGames on link.SourceEntryId equals game.SourceEntryId
            select game.Id;

        var roms = context.DatRoms.AsNoTracking();
        var regions = context.DatGameRegions.AsNoTracking();
        var languages = context.DatGameLanguages.AsNoTracking();
        if (!config.IncludeUntrackedTitles ||
            config.TitleSelectionMode == LibraryTitleSelectionMode.IncludeOnly ||
            config.AllowedPlatformIds.Count > 0)
        {
            roms = roms.Where(r => candidateGameIds.Contains(r.DatGameId));
            regions = regions.Where(r => candidateGameIds.Contains(r.DatGameId));
            languages = languages.Where(l => candidateGameIds.Contains(l.DatGameId));
        }
        // An unrestricted opt-in library benefits from whole-table aggregation; do not
        // repeat the complete catalog join in each aggregate when there is no title scope.
        var romFactsQuery = roms.GroupBy(r => r.DatGameId)
            .Select(g => new
            {
                DatGameId = g.Key,
                TotalRomCount = (int?)g.Count(),
                OwnedRomCount = (int?)g.Count(r => r.RomFileId != null)
            });

        var regionIdsQuery = regions.GroupBy(dr => dr.DatGameId)
            .Select(g => new
            {
                DatGameId = g.Key,
                RegionIdsCsv = string.Join(",", g.Select(dr => dr.RegionId))
            });

        var languageIdsQuery = languages.GroupBy(dl => dl.DatGameId)
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
                    languageIds.LanguageIdsCsv,
                    context.TrackedTitles.Any(tracked => tracked.TitleId == title.Id)))
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
                    candidates,
                    first.IsTracked);
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
        string? LanguageIdsCsv,
        bool IsTracked);
}
