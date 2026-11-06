using System.Collections.Immutable;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Romd.Admin.Application.Catalog;
using Romd.Domain.Catalog;
using Romd.Domain.Hashing;
using Romd.Domain.Source.Dat;
using Romd.Persistence.Queries;
using Romd.Persistence;

namespace Romd.Infrastructure.Source;

/// <summary>
///     DAT provider for the catalog-owned source snapshot boundary. All provider payload and
///     source joins stay here; the projection service consumes only immutable catalog facts.
///     This reader intentionally uses the shared scoped context so its queries participate in
///     the rebuild transaction and database snapshot opened by <see cref="CatalogProjectionService"/>.
/// </summary>
public sealed class DatCatalogSourceSnapshotProvider(RomdDbContext context) : ICatalogSourceSnapshotProvider
{
    public async Task<CatalogSourceSnapshot> ReadPlatformAsync(
        int platformId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var effectiveEntries = context.EffectiveSourceEntries().AsNoTracking();
        var eligibleClaims =
            from claim in context.DatGames.AsNoTracking()
            join version in context.DatFiles.AsNoTracking() on claim.DatFileId equals version.Id
            join providerSource in context.DatSources.AsNoTracking() on version.DatSourceId equals providerSource.Id
            join entry in effectiveEntries on claim.SourceEntryId equals entry.Id
            join source in context.CatalogSources.AsNoTracking() on entry.CatalogSourceId equals source.Id
            where version.PlatformId == platformId
                  && !claim.IsBios
                  && providerSource.CatalogSourceId == entry.CatalogSourceId
            select new
            {
                ClaimId = claim.Id,
                claim.SourceEntryId,
                ClaimPrecedence = version.Lifecycle == nameof(DatFileLifecycle.Active)
                    ? 0
                    : version.Lifecycle == nameof(DatFileLifecycle.PendingActivation)
                        ? 1
                        : 2,
                SourceKind = source.Kind,
                claim.Name,
                claim.Region,
                claim.Language,
                claim.Revision
            };

        var claims = await (
                from claim in eligibleClaims
                join link in context.TitleSourceLinks.AsNoTracking()
                    on claim.SourceEntryId equals link.SourceEntryId into links
                from link in links.DefaultIfEmpty()
                select new ClaimRow(
                    claim.ClaimId,
                    claim.SourceEntryId,
                    claim.ClaimPrecedence,
                    claim.SourceKind,
                    claim.Name,
                    claim.Region,
                    claim.Language,
                    claim.Revision,
                    (int?)link.TitleId))
            .ToListAsync(cancellationToken);

        if (claims.Count == 0)
        {
            return CatalogSourceSnapshot.Empty;
        }

        var roms = await (
                from rom in context.DatRoms.AsNoTracking()
                join claim in eligibleClaims on rom.DatGameId equals claim.ClaimId
                select new RequirementRow(
                    claim.ClaimId,
                    claim.SourceEntryId,
                    rom.Id,
                    CatalogSourceRequirementKind.Rom,
                    rom.Name,
                    rom.Size,
                    rom.Crc,
                    rom.Md5,
                    rom.Sha1,
                    rom.Status,
                    rom.RomFileId != null))
            .ToListAsync(cancellationToken);

        var disks = await (
                from disk in context.DatDisks.AsNoTracking()
                join claim in eligibleClaims on disk.DatGameId equals claim.ClaimId
                select new RequirementRow(
                    claim.ClaimId,
                    claim.SourceEntryId,
                    disk.Id,
                    CatalogSourceRequirementKind.Disk,
                    disk.Name,
                    0,
                    null,
                    disk.Md5,
                    disk.Sha1,
                    disk.Status,
                    false))
            .ToListAsync(cancellationToken);

        var regionRows = await (
                from region in context.DatGameRegions.AsNoTracking()
                join claim in eligibleClaims on region.DatGameId equals claim.ClaimId
                select new TaxonomyRow(claim.ClaimId, region.RegionId))
            .ToListAsync(cancellationToken);

        var languageRows = await (
                from language in context.DatGameLanguages.AsNoTracking()
                join claim in eligibleClaims on language.DatGameId equals claim.ClaimId
                select new TaxonomyRow(claim.ClaimId, language.GameLanguageId))
            .ToListAsync(cancellationToken);

        var requirementsByClaim = roms
            .Concat(disks)
            .GroupBy(row => row.ClaimId)
            .ToDictionary(
                group => group.Key,
                group => group
                    .Select(ToSnapshot)
                    .ToImmutableArray());
        var regionsByClaim = ToTaxonomyLookup(regionRows);
        var languagesByClaim = ToTaxonomyLookup(languageRows);
        var hasLocalPayloadByEntry = roms
            .GroupBy(row => row.SourceEntryId)
            .ToDictionary(group => group.Key, group => group.Any(row => row.HasLocalPayload));

        return new CatalogSourceSnapshot(claims
            .GroupBy(row => row.SourceEntryId)
            .Select(group => new CatalogSourceEntrySnapshot(
                group.Key,
                Enum.Parse<CatalogSourceKind>(group.First().SourceKind, ignoreCase: false),
                group.Select(row => row.AssertedTitleId).Distinct().Single(),
                hasLocalPayloadByEntry.GetValueOrDefault(group.Key),
                group.Select(row => new CatalogSourceClaimSnapshot(
                        row.ClaimId.ToString(CultureInfo.InvariantCulture),
                        row.ClaimPrecedence,
                        row.ClaimId,
                        row.Name,
                        row.Region,
                        row.Language,
                        row.Revision,
                        requirementsByClaim.GetValueOrDefault(
                            row.ClaimId,
                            ImmutableArray<CatalogSourceRequirementSnapshot>.Empty),
                        regionsByClaim.GetValueOrDefault(row.ClaimId, ImmutableArray<int>.Empty),
                        languagesByClaim.GetValueOrDefault(row.ClaimId, ImmutableArray<int>.Empty)))
                    .ToImmutableArray()))
            .ToImmutableArray());
    }

    private static CatalogSourceRequirementSnapshot ToSnapshot(RequirementRow row) =>
        new(
            row.ProvenanceId.ToString(CultureInfo.InvariantCulture),
            row.ProvenanceId,
            row.Kind,
            row.Name,
            row.Size,
            row.Crc,
            row.Md5,
            row.Sha1,
            row.Status);

    private static Dictionary<int, ImmutableArray<int>> ToTaxonomyLookup(IEnumerable<TaxonomyRow> rows) =>
        rows.GroupBy(row => row.ClaimId)
            .ToDictionary(
                group => group.Key,
                group => group.Select(row => row.Value).Distinct().ToImmutableArray());

    private sealed record ClaimRow(
        int ClaimId,
        int SourceEntryId,
        int ClaimPrecedence,
        string SourceKind,
        string Name,
        string? Region,
        string? Language,
        string? Revision,
        int? AssertedTitleId);

    private sealed record RequirementRow(
        int ClaimId,
        int SourceEntryId,
        int ProvenanceId,
        CatalogSourceRequirementKind Kind,
        string Name,
        long Size,
        Crc32? Crc,
        Md5? Md5,
        Sha1? Sha1,
        string? Status,
        bool HasLocalPayload);

    private sealed record TaxonomyRow(int ClaimId, int Value);
}
