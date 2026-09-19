using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Titles;
using Romd.Admin.Application.Titles.ReadModels;
using Romd.Domain.Catalog;
using Romd.Persistence.Entities;
using Romd.Persistence.Extensions;
using Romd.Persistence.Queries;

namespace Romd.Persistence.Repositories;

public sealed class TitleRepository : ITitleRepository
{
    private readonly RomdDbContext _context;

    public TitleRepository(RomdDbContext context)
    {
        _context = context;
    }

    public Task<bool> HasTrackedAsync(IReadOnlyCollection<int>? titleIds = null, CancellationToken cancellationToken = default) =>
        titleIds is null
            ? _context.TrackedTitles.AnyAsync(cancellationToken)
            : _context.TrackedTitles.AnyAsync(t => titleIds.Contains(t.TitleId), cancellationToken);

    public async Task<Title?> GetByIdAsync(int id, CancellationToken cancellationToken = default)
    {
        var entity = await _context.Titles
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == id, cancellationToken);

        return entity?.ToDomain();
    }

    public async Task<Title?> GetByNormalizedNameAsync(int platformId, string normalizedName,
        CancellationToken cancellationToken = default)
    {
        var entity = await _context.Titles
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.PlatformId == platformId && t.NormalizedName == normalizedName,
                cancellationToken);

        return entity?.ToDomain();
    }

    public async Task<IReadOnlyList<Title>> GetByPlatformFilteredAsync(
        int platformId,
        int? libraryId,
        CancellationToken cancellationToken = default)
    {
        var entities = await _context.Titles
            .AsNoTracking()
            .Where(t => t.PlatformId == platformId)
            .ApplyLibrary(libraryId, _context.MaterializedLibraryTitles, _context.Libraries)
            .OrderBy(t => t.Name)
            .ToListAsync(cancellationToken);

        return entities.Select(e => e.ToDomain()).ToList();
    }

    public async Task<IReadOnlyList<Title>> GetWithLocalPayloadByPlatformAsync(int platformId,
        CancellationToken cancellationToken = default)
    {
        var entities = await _context.Titles
            .AsNoTracking()
            .Where(t => t.PlatformId == platformId && t.HasLocalPayload)
            .OrderBy(t => t.Name)
            .ToListAsync(cancellationToken);

        return entities.Select(e => e.ToDomain()).ToList();
    }

    public async Task<IReadOnlyList<Title>> GetWithLocalPayloadByPlatformFilteredAsync(
        int platformId,
        int? libraryId,
        CancellationToken cancellationToken = default)
    {
        var entities = await _context.Titles
            .AsNoTracking()
            .Where(t => t.PlatformId == platformId && t.HasLocalPayload)
            .ApplyLibrary(libraryId, _context.MaterializedLibraryTitles, _context.Libraries)
            .OrderBy(t => t.Name)
            .ToListAsync(cancellationToken);

        return entities.Select(e => e.ToDomain()).ToList();
    }

    public async Task<Dictionary<string, Title>> GetByNormalizedNamesAsync(
        int platformId,
        IEnumerable<string> normalizedNames,
        CancellationToken cancellationToken = default)
    {
        var nameList = normalizedNames.ToList();
        if (nameList.Count == 0)
        {
            return new Dictionary<string, Title>();
        }

        var entities = await _context.Titles
            .AsNoTracking()
            .Where(t => t.PlatformId == platformId && nameList.Contains(t.NormalizedName))
            .ToListAsync(cancellationToken);

        return entities.ToDictionary(e => e.NormalizedName, e => e.ToDomain());
    }

    public async Task<IReadOnlyList<Title>> GetPendingEnrichmentAsync(int limit,
        CancellationToken cancellationToken = default)
    {
        string pendingStatus = EnrichmentStatus.Pending.ToString();

        var entities = await _context.Titles
            .AsNoTracking()
            .Where(t => t.EnrichmentStatus == pendingStatus)
            .OrderBy(t => t.CreatedAt)
            .Take(limit)
            .ToListAsync(cancellationToken);

        return entities.Select(e => e.ToDomain()).ToList();
    }

    public async Task<IReadOnlyList<Title>> GetNeedingEnrichmentByPlatformAsync(
        int platformId,
        EnrichmentScope scope,
        CancellationToken cancellationToken = default)
    {
        string noneStatus = EnrichmentStatus.None.ToString();
        string pendingStatus = EnrichmentStatus.Pending.ToString();
        string lowConfidenceStatus = EnrichmentStatus.LowConfidence.ToString();

        var query = _context.Titles
            .AsNoTracking()
            .Where(t => t.PlatformId == platformId
                && (t.EnrichmentStatus == noneStatus
                    || t.EnrichmentStatus == pendingStatus
                    || t.EnrichmentStatus == lowConfidenceStatus));

        if (scope == EnrichmentScope.Tracked)
        {
            query = query.Where(t => _context.TrackedTitles.Any(tracked => tracked.TitleId == t.Id));
        }

        var entities = await query
            .OrderBy(t => t.Name)
            .ToListAsync(cancellationToken);

        return entities.Select(e => e.ToDomain()).ToList();
    }

    public async Task<IReadOnlyList<int>> GetIdsByPlatformAsync(
        int platformId,
        CancellationToken cancellationToken = default)
    {
        return await _context.Titles
            .AsNoTracking()
            .Where(t => t.PlatformId == platformId)
            .Select(t => t.Id)
            .ToListAsync(cancellationToken);
    }

    public async Task MarkTrackedAsync(
        IReadOnlyCollection<int> titleIds,
        CancellationToken cancellationToken = default)
    {
        var repository = new TrackedTitleRepository(_context, TimeProvider.System);
        await repository.TrackByTitleIdsAsync(titleIds, cancellationToken);
    }

    public async Task<Title?> GetByExternalIdAsync(string provider, string externalId,
        CancellationToken cancellationToken = default)
    {
        var entity = await _context.Titles
            .Include(t => t.ExternalIds)
            .Include(t => t.Media)
            .Include(t => t.ContentRatings)
            .FirstOrDefaultAsync(t => t.ExternalIds.Any(e =>
                e.Provider == provider && e.ExternalId == externalId), cancellationToken);

        return entity?.ToDomainWithCollections();
    }

    public async Task<Title?> GetWithCollectionsAsync(int id, CancellationToken cancellationToken = default)
    {
        var entity = await _context.Titles
            .Include(t => t.ExternalIds)
            .Include(t => t.Media)
            .Include(t => t.ContentRatings)
            .FirstOrDefaultAsync(t => t.Id == id, cancellationToken);

        return entity?.ToDomainWithCollections();
    }

    public async Task<Title?> GetWithMetadataLayersAsync(int id, CancellationToken cancellationToken = default)
    {
        var entity = await _context.Titles
            .Include(t => t.ExternalIds)
            .Include(t => t.Media)
            .Include(t => t.MetadataLayers)
            .Include(t => t.ContentRatings)
            .FirstOrDefaultAsync(t => t.Id == id, cancellationToken);

        return entity?.ToDomainWithCollections();
    }

    public async Task<TitleDetailData?> GetTitleDetailAsync(int id, CancellationToken cancellationToken = default)
    {
        // Fetch title with media
        var title = await _context.Titles
            .AsNoTracking()
            .Include(t => t.Media)
            .Include(t => t.ContentRatings)
            .FirstOrDefaultAsync(t => t.Id == id, cancellationToken);

        if (title is null)
        {
            return null;
        }

        var tracking = await _context.TrackedTitles
            .AsNoTracking()
            .Where(tracked => tracked.TitleId == id)
            .Select(tracked => new { tracked.PinnedCatalogReleaseId })
            .SingleOrDefaultAsync(cancellationToken);

        // Fetch all DatGames linked to this title via TitleSourceLinks
        var gameIds = _context.TitleSourceLinks
            .Where(l => l.TitleId == id)
            .Join(_context.DatGames, l => l.SourceEntryId, g => g.SourceEntryId, (l, g) => g.Id);

        var games = await _context.DatGames
            .AsNoTracking()
            .Where(g => gameIds.Contains(g.Id))
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
        if (!artwork.TryGetValue(id, out var resolvedArtwork)) return null;

        return new TitleDetailData
        {
            Id = title.Id,
            Artwork = resolvedArtwork,
            PlatformId = title.PlatformId,
            Name = title.Name,
            EnrichmentStatus = title.EnrichmentStatus,
            HasLocalPayload = title.HasLocalPayload,
            IsTracked = tracking is not null,
            PinnedCatalogReleaseId = tracking?.PinnedCatalogReleaseId,
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

    public async Task<Title> AddAsync(Title title, CancellationToken cancellationToken = default)
    {
        var entity = TitleEntity.FromDomain(title);
        _context.Titles.Add(entity);
        await _context.SaveChangesAsync(cancellationToken);
        return entity.ToDomain();
    }

    public async Task<IReadOnlyList<Title>> AddRangeAsync(IReadOnlyList<Title> titles,
        CancellationToken cancellationToken = default)
    {
        if (titles.Count == 0)
        {
            return [];
        }

        var entities = titles.Select(TitleEntity.FromDomain).ToList();
        _context.Titles.AddRange(entities);
        await _context.SaveChangesAsync(cancellationToken);

        // Return domain objects with database-assigned IDs
        return entities.Select(e => e.ToDomain()).ToList();
    }

    public async Task UpdateAsync(Title title, CancellationToken cancellationToken = default)
    {
        // Legacy immediate-save callers still exist. Own a short transaction only for those
        // callers; application-owned transactions compose with this write without nesting.
        await using var ownedTransaction = _context.Database.CurrentTransaction is null
            ? await new EfUnitOfWork(_context).BeginTransactionAsync(cancellationToken)
            : null;
        await LockSnapshotAsync(title, cancellationToken);

        var entity = TitleEntity.FromDomain(title);
        _context.Titles.Update(entity);
        PreservePersistenceOwnedState(entity);
        StageRevision(entity, title);

        if (title.ContentRatingsMaterialized)
        {
            await SyncContentRatingsAsync(title, cancellationToken);
        }

        await _context.SaveChangesAsync(cancellationToken);

        if (ownedTransaction is not null)
            await ownedTransaction.CommitAsync(cancellationToken);
    }

    public async Task UpdateStagedAsync(Title title, CancellationToken cancellationToken = default)
    {
        RequireTransaction();
        await LockSnapshotAsync(title, cancellationToken);

        // Media is authoritative on this narrow staged route: both callers load the full media
        // collection before mutating it. Delete removed/replaced rows immediately inside the
        // caller-owned transaction so a replacement with the same unique
        // (TitleId, Type, SourceId) key can be inserted by the caller's final flush.
        var retainedMediaIds = title.Media
            .Where(media => media.Id > 0)
            .Select(media => media.Id)
            .ToList();

        if (retainedMediaIds.Count == 0)
        {
            await _context.TitleMedia
                .Where(media => media.TitleId == title.Id)
                .ExecuteDeleteAsync(cancellationToken);
        }
        else
        {
            await _context.TitleMedia
                .Where(media => media.TitleId == title.Id && !retainedMediaIds.Contains(media.Id))
                .ExecuteDeleteAsync(cancellationToken);
        }

        var entity = TitleEntity.FromDomain(title);
        _context.Titles.Update(entity);
        PreservePersistenceOwnedState(entity);
        StageRevision(entity, title);

        if (title.ContentRatingsMaterialized)
        {
            await SyncContentRatingsAsync(title, cancellationToken);
        }
    }

    public async Task AppendGalleryMediaStagedAsync(TitleMedia media, CancellationToken cancellationToken = default)
    {
        RequireTransaction();
        // Serialize append/dedup against other title mutations. A retried import returns the same media.
        await _context.Titles.Where(title => title.Id == media.TitleId).ExecuteUpdateAsync(setters =>
            setters.SetProperty(title => title.Revision, Guid.NewGuid()), cancellationToken);
        var existing = await _context.TitleMedia.SingleOrDefaultAsync(item => item.TitleId == media.TitleId &&
            item.Type == media.Type.ToString() && item.SourceId == media.SourceId && item.FileId == media.FileId,
            cancellationToken);
        if (existing is not null) return;
        var entity = TitleMediaEntity.FromDomain(media);
        // Gallery collection must not implicitly replace a display selection.
        entity.IsPrimary = false;
        _context.TitleMedia.Add(entity);
    }

    public async Task AddUserMediaStagedAsync(TitleMedia media, CancellationToken cancellationToken = default)
    {
        if (_context.Database.CurrentTransaction is null || media.SourceId != "user")
            throw new InvalidOperationException("User media append requires a transaction.");
        var revision = Guid.NewGuid();
        await _context.Titles.Where(title => title.Id == media.TitleId).ExecuteUpdateAsync(setters =>
            setters.SetProperty(title => title.Revision, revision), cancellationToken);
        if (await _context.TitleMedia.AnyAsync(item => item.TitleId == media.TitleId &&
            item.SourceId == "user" && item.Type == media.Type.ToString() && item.FileId == media.FileId,
            cancellationToken)) return;
        var entity = TitleMediaEntity.FromDomain(media);
        entity.IsPrimary = !await _context.TitleMedia.AnyAsync(item => item.TitleId == media.TitleId &&
            item.Type == entity.Type && item.IsPrimary, cancellationToken);
        _context.TitleMedia.Add(entity);
    }

    public async Task UpdateMaterializedMetadataStagedAsync(
        Title title,
        CancellationToken cancellationToken = default)
    {
        RequireTransaction();
        await LockSnapshotAsync(title, cancellationToken);

        // Update the tracked row in place so persistence-only state such as CatalogState and
        // ScreenshotPrefsJson is preserved. This path deliberately does not replace collection
        // graphs: field overrides can change effective scalar/rating values and primary-media
        // selection, but cannot add, remove, or rewrite media, external IDs, or metadata layers.
        var entity = await _context.Titles
            .AsTracking()
            .SingleAsync(candidate => candidate.Id == title.Id, cancellationToken);

        StageRevision(entity, title);
        entity.Name = title.Name;
        entity.NormalizedName = title.NormalizedName;
        entity.Description = title.Description;
        entity.Publisher = title.Publisher;
        entity.Developer = title.Developer;
        entity.Genre = title.Genre;
        entity.ReleaseDate = title.ReleaseDate;
        entity.Players = title.Players;
        entity.Rating = title.Rating;
        entity.ConservativeMinimumAge = title.ConservativeMinimumAge;
        entity.EnrichmentStatus = title.EnrichmentStatus.ToString();
        entity.LastEnrichedAt = title.LastEnrichedAt;
        entity.FieldProvenanceJson = title.FieldProvenance.Count > 0
            ? JsonSerializer.Serialize(title.FieldProvenance)
            : "{}";
        entity.FieldSourceOverridesJson = title.FieldSourceOverrides.Count > 0
            ? JsonSerializer.Serialize(title.FieldSourceOverrides)
            : "{}";

        var primaryByMediaId = title.Media
            .Where(media => media.Id > 0)
            .ToDictionary(media => media.Id, media => media.IsPrimary);
        var persistedMedia = await _context.TitleMedia
            .AsTracking()
            .Where(media => media.TitleId == title.Id)
            .ToListAsync(cancellationToken);
        foreach (var media in persistedMedia)
        {
            if (primaryByMediaId.TryGetValue(media.Id, out bool isPrimary))
            {
                media.IsPrimary = isPrimary;
            }
        }

        if (title.ContentRatingsMaterialized)
        {
            await SyncContentRatingsAsync(title, cancellationToken);
        }
    }

    public async Task UpdateUserMetadataStagedAsync(Title title, CancellationToken cancellationToken = default)
    {
        RequireTransaction();
        await UpdateMaterializedMetadataStagedAsync(title, cancellationToken);

        var layer = title.MetadataLayers.SingleOrDefault(candidate => candidate.SourceId == "user");
        var persisted = await _context.TitleMetadataLayers.AsTracking()
            .SingleOrDefaultAsync(candidate => candidate.TitleId == title.Id && candidate.SourceId == "user",
                cancellationToken);
        if (layer is null)
        {
            if (persisted is not null)
                _context.TitleMetadataLayers.Remove(persisted);
        }
        else if (persisted is null)
        {
            _context.TitleMetadataLayers.Add(TitleMetadataLayerEntity.FromDomain(layer));
        }
        else
        {
            persisted.MetadataJson = layer.MetadataJson;
            persisted.SourceType = (int)layer.SourceType;
            persisted.UpdatedAt = layer.UpdatedAt;
        }
    }

    private async Task LockSnapshotAsync(Title title, CancellationToken cancellationToken)
    {
        // EF can insert a child before issuing the parent's optimistic UPDATE. Serialize on
        // the parent first so two first-time child inserts report a stale-title conflict, not
        // a child unique-key error. PostgreSQL rechecks the revision after waiting for a writer.
        int matched = await _context.Titles
            .Where(candidate => candidate.Id == title.Id && candidate.Revision == title.Revision)
            .ExecuteUpdateAsync(setters => setters.SetProperty(candidate => candidate.Revision,
                candidate => candidate.Revision), cancellationToken);
        if (matched != 1)
            throw new PersistenceConflictException(new DbUpdateConcurrencyException(
                $"Title {title.Id} changed after it was loaded."));
    }

    private void StageRevision(TitleEntity entity, Title title)
    {
        var revision = _context.Entry(entity).Property(candidate => candidate.Revision);
        // The tracked row may have been loaded after the domain snapshot. Comparing that newer
        // revision would let stale detached metadata overwrite a concurrent edit.
        revision.OriginalValue = title.Revision;
        revision.CurrentValue = Guid.NewGuid();
        revision.IsModified = true;
    }

    private void PreservePersistenceOwnedState(TitleEntity entity)
    {
        var entry = _context.Entry(entity);
        entry.Property(candidate => candidate.HasLocalPayload).IsModified = false;
        entry.Property(candidate => candidate.RetainWithoutCatalog).IsModified = false;
        entry.Property(candidate => candidate.CatalogState).IsModified = false;
        entry.Property(candidate => candidate.ScreenshotPrefsJson).IsModified = false;
        entry.Property(candidate => candidate.CreatedAt).IsModified = false;
        entry.Property(candidate => candidate.CreatedByUserId).IsModified = false;
        entry.Property(candidate => candidate.UpdatedAt).IsModified = false;
        entry.Property(candidate => candidate.UpdatedByUserId).IsModified = false;
    }

    private void RequireTransaction()
    {
        if (_context.Database.CurrentTransaction is null)
            throw new InvalidOperationException("Staged title mutations require a caller-owned transaction.");
    }

    /// <summary>
    ///     Replace-set sync of materialized rating rows keyed by (TitleId, Board).
    ///     Only invoked when the domain instance carries authoritative rating state
    ///     (<see cref="Title.ContentRatingsMaterialized" />) — instances loaded without
    ///     their rating collection must never wipe persisted rows.
    /// </summary>
    private async Task SyncContentRatingsAsync(Title title, CancellationToken cancellationToken)
    {
        var existingRows = await _context.TitleContentRatings
            .Where(r => r.TitleId == title.Id)
            .ToListAsync(cancellationToken);

        var existingByBoard = existingRows.ToDictionary(r => r.Board);
        var currentBoards = new HashSet<int>();

        foreach (var rating in title.ContentRatings)
        {
            var row = TitleContentRatingEntity.FromDomain(title.Id, rating);
            currentBoards.Add(row.Board);

            if (existingByBoard.TryGetValue(row.Board, out var existing))
            {
                row.Id = existing.Id;
                _context.TitleContentRatings.Update(row);
            }
            else
            {
                _context.TitleContentRatings.Add(row);
            }
        }

        _context.TitleContentRatings.RemoveRange(
            existingRows.Where(r => !currentBoards.Contains(r.Board)));
    }

    public async Task<IReadOnlySet<int>> GetTitleIdsWithLocalPayloadAsync(IEnumerable<int> titleIds,
        CancellationToken cancellationToken = default)
    {
        var titleIdList = titleIds.ToList();
        if (titleIdList.Count == 0)
        {
            return new HashSet<int>();
        }

        var ownedIds = await _context.Titles
            .AsNoTracking()
            .Where(title => titleIdList.Contains(title.Id) && title.HasLocalPayload)
            .Select(title => title.Id)
            .ToListAsync(cancellationToken);

        return ownedIds.ToHashSet();
    }

    public Task<bool> HasLocalPayloadAsync(int titleId, CancellationToken cancellationToken = default) =>
        _context.Titles
            .AsNoTracking()
            .AnyAsync(title => title.Id == titleId && title.HasLocalPayload, cancellationToken);

    public async Task<Title?> GetForMergeAsync(int id, CancellationToken cancellationToken = default)
    {
        var entity = await _context.Titles
            .Include(t => t.ExternalIds)
            .Include(t => t.Media)
            .Include(t => t.MetadataLayers)
            .Include(t => t.ContentRatings)
            .FirstOrDefaultAsync(t => t.Id == id, cancellationToken);

        return entity?.ToDomainWithCollections();
    }

    public Task StageArtworkMergeAsync(int sourceTitleId, int targetTitleId, CancellationToken cancellationToken = default) =>
        new Romd.Persistence.Artwork.ArtworkTitleMerger(_context).StageAsync(sourceTitleId, targetTitleId, cancellationToken);

    public async Task DeleteAsync(int id, CancellationToken cancellationToken = default)
    {
        if (await _context.TrackedTitles.AnyAsync(t => t.TitleId == id, cancellationToken))
        {
            await _context.Titles
                .Where(t => t.Id == id)
                .ExecuteUpdateAsync(
                    setters => setters.SetProperty(t => t.CatalogState, TitleCatalogState.UserOnly),
                    cancellationToken);
            return;
        }

        await _context.Titles
            .Where(t => t.Id == id)
            .ExecuteDeleteAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<int>> GetOrphanedTitleIdsAsync(
        DateTimeOffset createdBefore,
        int limit,
        CancellationToken cancellationToken = default)
    {
        return await _context.Titles
            .AsNoTracking()
            .Where(t => t.CatalogState != TitleCatalogState.UserOnly
                        && t.CreatedAt < createdBefore
                        && !_context.TitleSourceLinks.Any(l => l.TitleId == t.Id))
            .OrderBy(t => t.Id)
            .Take(limit)
            .Select(t => t.Id)
            .ToListAsync(cancellationToken);
    }

    public async Task<bool> DeleteOrRetainAsync(int id, CancellationToken cancellationToken = default)
    {
        // Every mutation below re-checks zero links at mutation time: a concurrent
        // derivation may have re-linked the title after the caller's orphan check, and a
        // re-linked title must lose nothing. That promise only holds when the statements
        // are atomic, so a caller-owned transaction is an enforced precondition
        // (repository-owned transactions are ledgered debt).
        if (_context.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException(
                "DeleteOrRetainAsync mutates releases and the title in separate statements " +
                "and requires a caller-owned transaction for atomicity.");
        }

        // Share the artwork title-lock protocol so Apply cannot commit curation
        // between the retention decision and orphan deletion.
        await _context.Titles.Where(t => t.Id == id).ExecuteUpdateAsync(
            setters => setters.SetProperty(t => t.RetainWithoutCatalog, t => t.RetainWithoutCatalog),
            cancellationToken);

        bool hasUserState = await _context.Titles
            .AsNoTracking()
            .Where(t => t.Id == id)
            .Select(t =>
                t.RetainWithoutCatalog ||
                _context.TrackedTitles.Any(tracked => tracked.TitleId == id) ||
                t.FieldSourceOverridesJson != "{}" ||
                t.FieldProvenanceJson != "{}" ||
                t.ScreenshotPrefsJson != "{}" ||
                _context.TitleExternalIds.Any(e => e.TitleId == id && e.IsConfirmed) ||
                _context.TitleMedia.Any(m => m.TitleId == id && m.IsPrimary) ||
                _context.ArtworkAssets.Any(a => a.TitleId == id) ||
                _context.ArtworkSelections.Any(s => s.TitleId == id &&
                    (s.Mode == ArtworkSelectionMode.Pinned || s.PendingRequestId != null)) ||
                _context.CollectionItems.Any(c => c.TitleId == id))
            .FirstOrDefaultAsync(cancellationToken);

        if (hasUserState)
        {
            await _context.Titles
                .Where(t => t.Id == id
                            && !_context.TitleSourceLinks.Any(l => l.TitleId == id))
                .ExecuteUpdateAsync(
                    s => s.SetProperty(t => t.CatalogState, Domain.Catalog.TitleCatalogState.UserOnly),
                    cancellationToken);
            return false;
        }

        // Release identity is fingerprint-stable and public (CatalogReleaseEntity): a
        // release another surviving source still asserts must keep its row and ID, so it is
        // re-pointed to a surviving linked title (projection recovery re-derives the true
        // representative on the next rebuild). Only releases nobody else asserts are stale
        // rows and are deleted; the Restrict FK makes hard-deleting a projected title this
        // deliberate act. MaterializedLibraryReleases rows cascade with the title.
        await _context.CatalogReleases
            .Where(r => r.CatalogTitleId == id
                        && !_context.TitleSourceLinks.Any(l => l.TitleId == id)
                        && _context.CatalogReleaseSources.Any(s =>
                            s.CatalogReleaseId == r.Id
                            && _context.TitleSourceLinks.Any(l =>
                                l.SourceEntryId == s.SourceEntryId && l.TitleId != id)))
            .ExecuteUpdateAsync(
                u => u.SetProperty(
                    r => r.CatalogTitleId,
                    r => _context.CatalogReleaseSources
                        .Where(s => s.CatalogReleaseId == r.Id)
                        .SelectMany(s => _context.TitleSourceLinks
                            .Where(l => l.SourceEntryId == s.SourceEntryId && l.TitleId != id))
                        .OrderBy(l => l.TitleId)
                        .Select(l => l.TitleId)
                        .First()),
                cancellationToken);

        await _context.CatalogReleases
            .Where(r => r.CatalogTitleId == id
                        && !_context.TitleSourceLinks.Any(l => l.TitleId == id))
            .ExecuteDeleteAsync(cancellationToken);

        return await _context.Titles
            .Where(t => t.Id == id
                        && !_context.TitleSourceLinks.Any(l => l.TitleId == id))
            .ExecuteDeleteAsync(cancellationToken) > 0;
    }

    public async Task<bool> HasGamesAsync(int id, CancellationToken cancellationToken = default) =>
        await _context.TitleSourceLinks.AnyAsync(l => l.TitleId == id, cancellationToken);

    public async Task<TitleEnrichmentStats> GetEnrichmentStatsAsync(CancellationToken cancellationToken = default)
    {
        var pendingStatus = Domain.Catalog.EnrichmentStatus.Pending.ToString();
        var completedStatus = Domain.Catalog.EnrichmentStatus.Completed.ToString();
        var notFoundStatus = Domain.Catalog.EnrichmentStatus.NotFound.ToString();
        var failedStatus = Domain.Catalog.EnrichmentStatus.Failed.ToString();
        var lowConfidenceStatus = Domain.Catalog.EnrichmentStatus.LowConfidence.ToString();

        // Enrichment progress is reported over the curated (tracked) set, mirroring the scope
        // that bulk enrichment targets by default.
        var stats = await _context.Titles
            .Where(t => _context.TrackedTitles.Any(tracked => tracked.TitleId == t.Id))
            .GroupBy(_ => 1)
            .Select(g => new TitleEnrichmentStats(
                g.Count(t => t.EnrichmentStatus == pendingStatus),
                g.Count(t => t.EnrichmentStatus == completedStatus),
                g.Count(t => t.EnrichmentStatus == notFoundStatus),
                g.Count(t => t.EnrichmentStatus == failedStatus),
                g.Count(t => t.EnrichmentStatus == lowConfidenceStatus)))
            .FirstOrDefaultAsync(cancellationToken);

        return stats ?? new TitleEnrichmentStats(0, 0, 0, 0, 0);
    }
}
