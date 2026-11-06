using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Romd.Admin.Application.Configuration;
using Romd.Admin.Application.Titles;
using Romd.Admin.Application.Titles.ReadModels;
using Romd.Domain.Catalog;
using Romd.Persistence.Repositories;

namespace Romd.Persistence.Queries;

/// <summary>
///     Title detail projection. Its raw DAT joins are the current read shape; #185 owns moving it onto
///     canonical catalog facts.
/// </summary>
public sealed partial class TitleDetailReader : ITitleDetailReader
{
    private readonly RomdDbContext _context;

    private readonly EnrichmentOptions _options;

    public TitleDetailReader(RomdDbContext context, IOptions<EnrichmentOptions>? options = null)
    {
        _context = context;
        _options = options?.Value ?? new EnrichmentOptions();
    }

    public Task<TitleDetailData?> GetDetailAsync(int titleId, CancellationToken cancellationToken = default) => GetDetailCoreAsync(titleId, false, cancellationToken);

    private async Task<TitleDetailData?> GetDetailCoreAsync(int titleId, bool publicRead, CancellationToken cancellationToken)
    {
        int id = titleId;
        var titleQuery = _context.Titles
            .AsNoTracking()
            .Include(t => t.ContentRatings)
            .AsQueryable();
        if (!publicRead)
            titleQuery = titleQuery.Include(t => t.Media);
        var title = await titleQuery.FirstOrDefaultAsync(t => t.Id == id, cancellationToken);

        if (title is null)
        {
            return null;
        }

        var tracking = await _context.TrackedTitles
            .AsNoTracking()
            .Where(tracked => tracked.TitleId == id)
            .Select(tracked => new { tracked.PinnedCatalogReleaseId })
            .SingleOrDefaultAsync(cancellationToken);

        var candidates = await _context.CatalogReleases.Where(release => release.CatalogTitleId == id)
            .Select(release => new CatalogReleasePreferenceCandidate(release.Id, release.Name, release.Region,
                release.Revision, _context.CatalogReleaseFiles.Any(file => file.CatalogReleaseId == release.Id
                    && file.Status != null && (file.Status.ToLower() == "baddump" || file.Status.ToLower() == "nodump"))))
            .ToListAsync(cancellationToken);
        var preferred = new CatalogReleasePreferencePolicy(_options.RegionPriority)
            .SelectDesired(candidates, tracking?.PinnedCatalogReleaseId);

        // Fetch all DatGames linked to this title via TitleSourceLinks
        var gameIds = _context.TitleSourceLinks
            .Where(l => l.TitleId == id)
            .Join(_context.DatGames, l => l.SourceEntryId, g => g.SourceEntryId, (l, g) => g.Id);

        var games = await _context.DatGames
            .AsNoTracking()
            .Where(g => !publicRead && gameIds.Contains(g.Id))
            .Include(g => g.Roms)
            .OrderBy(g => g.Name)
            .ToListAsync(cancellationToken);

        // Collapse source games that resolve to the same canonical release (e.g. identical content
        // declared by two DATs) into one displayed release. Each collapsed release keeps the
        // representative game as its id (so the admin Move action still targets a concrete DatGame)
        // and links its contributing DAT entries as sources. Unprojected games stay their own row.
        // Keyed by (entry, asserting claim): a release-source row is the selected asserting
        // payload claim, so a version that shares the entry but was not selected (e.g. a pending
        // replacement with different hashes) must not inherit the release.
        var loadedSourceEntryIds = games.Select(g => g.SourceEntryId).ToList();
        var catalogReleaseByAssertingSource = await _context.CatalogReleaseSources
            .AsNoTracking()
            .Where(source => loadedSourceEntryIds.Contains(source.SourceEntryId))
            .ToDictionaryAsync(
                source => (source.SourceEntryId, source.ProviderClaimKey),
                source => source.CatalogReleaseId,
                cancellationToken);

        var datFileIds = games.Select(g => g.DatFileId).Distinct().ToList();
        var datNamesById = await _context.DatFiles
            .AsNoTracking()
            .Where(datFile => datFileIds.Contains(datFile.Id))
            .ToDictionaryAsync(datFile => datFile.Id, datFile => datFile.Name, cancellationToken);

        var releaseGroups = games
            .GroupBy(g => catalogReleaseByAssertingSource.TryGetValue(
                    (g.SourceEntryId, g.Id.ToString(CultureInfo.InvariantCulture)),
                    out int releaseId)
                ? releaseId
                : -g.Id)
            .Select(group => (
                CatalogReleaseId: group.Key > 0 ? (int?)group.Key : null,
                Members: group.OrderBy(g => g.Id).ToList()))
            .OrderBy(group => group.Members[0].Name)
            .ToList();

        // Resolve stored file facts for each media file from CAS (media -> Files by FileId).
        var mediaFileIds = title.Media.Select(m => m.FileId).Distinct().ToList();
        var mediaFilesByFileId = await _context.Files
            .AsNoTracking()
            .Where(f => mediaFileIds.Contains(f.Id))
            .Select(f => new { f.Id, f.Size, f.SizeOnDisk, f.IsCompressed })
            .ToDictionaryAsync(f => f.Id, f => (f.Size, f.SizeOnDisk, f.IsCompressed), cancellationToken);

        // Deserialize FieldProvenance if present
        Dictionary<string, string>? fieldProvenance = null;
        if (!string.IsNullOrWhiteSpace(title.FieldProvenanceJson) && title.FieldProvenanceJson != "{}")
        {
            fieldProvenance = JsonSerializer.Deserialize<Dictionary<string, string>>(title.FieldProvenanceJson);
        }

        var artwork = await new ArtworkReader(_context).ResolveAsync([id], cancellationToken);
        if (!artwork.TryGetValue(id, out var resolvedArtwork))
            return null;

        return new TitleDetailData
        {
            Id = title.Id,
            Revision = title.Revision,
            Artwork = resolvedArtwork,
            PlatformId = title.PlatformId,
            Name = title.Name,
            EnrichmentStatus = title.EnrichmentStatus,
            HasLocalPayload = title.HasLocalPayload,
            IsTracked = tracking is not null,
            PinnedCatalogReleaseId = tracking?.PinnedCatalogReleaseId,
            PreferredCatalogReleaseId = preferred?.CatalogReleaseId,
            PreferredReleaseName = preferred?.Name,
            ReleaseRegionPriority = _options.RegionPriority.ToArray(),
            Description = title.Description,
            Publisher = title.Publisher,
            Developer = title.Developer,
            Genre = title.Genre,
            ReleaseDate = title.ReleaseDate,
            Players = title.Players,
            Rating = title.Rating,
            ContentRatings = title.ContentRatings
                .OrderBy(r => r.Board)
                .Select(r => new TitleContentRatingData
                {
                    Board = r.Board,
                    Code = r.Code,
                    Designation = r.Designation,
                    MinimumAge = r.MinimumAge,
                    SourceId = r.SourceId,
                    ExternalRatingId = r.ExternalRatingId,
                    Descriptors = string.IsNullOrWhiteSpace(r.DescriptorsJson) || r.DescriptorsJson == "[]"
                        ? []
                        : JsonSerializer.Deserialize<List<string>>(r.DescriptorsJson) ?? [],
                    Synopsis = r.Synopsis
                })
                .ToList(),
            CreatedAt = title.CreatedAt,
            LastEnrichedAt = title.LastEnrichedAt,
            FieldProvenance = fieldProvenance,
            Media =
                title.Media.Select(m => new TitleMediaData
                {
                    Id = m.Id,
                    Type = m.Type,
                    FileId = m.FileId,
                    SourceId = m.SourceId,
                    Attribution = m.Attribution,
                    SourcePageUrl = m.SourcePageUrl,
                    IsPrimary = m.IsPrimary,
                    SizeBytes = mediaFilesByFileId.TryGetValue(m.FileId, out var mf) ? mf.Size : null,
                    SizeOnDiskBytes = mediaFilesByFileId.TryGetValue(m.FileId, out var mf2) ? mf2.SizeOnDisk : null,
                    IsCompressed = mediaFilesByFileId.TryGetValue(m.FileId, out var mf3) ? mf3.IsCompressed : null
                }).ToList(),
            Releases = releaseGroups.Select(group =>
            {
                var members = group.Members;
                var representative = members[0];
                return new TitleReleaseData
                {
                    CatalogReleaseId = group.CatalogReleaseId,
                    Id = representative.Id,
                    DatFileId = representative.DatFileId,
                    Name = representative.Name,
                    Description = representative.Description,
                    Year = representative.Year,
                    Manufacturer = representative.Manufacturer,
                    Region = representative.Region,
                    Language = representative.Language,
                    Revision = representative.Revision,
                    Files = representative.Roms.Select(r => new TitleFileData
                    {
                        Id = r.Id,
                        Name = r.Name,
                        Size = r.Size,
                        Sha1 = r.Sha1 != null ? r.Sha1.ToString() : null,
                        Md5 = r.Md5 != null ? r.Md5.ToString() : null,
                        Crc = r.Crc != null ? r.Crc.ToString() : null,
                        Status = r.Status,
                        RomFileId = r.RomFileId
                    }).ToList(),
                    Sources = members.Select(m => new TitleReleaseSourceData
                    {
                        DatGameId = m.Id,
                        DatFileId = m.DatFileId,
                        DatName = datNamesById.GetValueOrDefault(m.DatFileId, string.Empty),
                        GameName = m.Name
                    }).ToList()
                };
            }).ToList()
        };
    }
}
