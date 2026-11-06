using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Titles;
using Romd.Domain.Catalog;
using Romd.Persistence.Entities;

namespace Romd.Persistence.Repositories;

/// <summary>
///     Title aggregate adapter. One scoped instance serves the aggregate, row-lock, media, merge, and
///     creation roles; every snapshot write verifies and locks the snapshot revision first.
/// </summary>
public sealed class TitleRepository
    : ITitleAggregateReader, ITitleMetadataStore, ITitleRowLock, ITitleMediaStore, ITitleMergeStore,
        ITitleCreationStore
{
    private readonly RomdDbContext _context;

    public TitleRepository(RomdDbContext context)
    {
        _context = context;
    }

    public async Task<Title?> GetAggregateAsync(int titleId, CancellationToken cancellationToken = default)
    {
        // Load sibling collections separately: one joined query multiplies media, layers,
        // identities and ratings. A concurrent revision change invalidates the whole read,
        // so READ COMMITTED callers never receive children from a different title revision.
        // Reuse any caller transaction without changing its isolation or acquiring write locks.
        for (int attempt = 0; attempt < 3; attempt++)
        {
            var entity = await _context.Titles
                .AsNoTracking()
                .AsSplitQuery()
                .Include(t => t.ExternalIds)
                .Include(t => t.Media)
                .Include(t => t.MetadataLayers)
                .Include(t => t.ContentRatings)
                .FirstOrDefaultAsync(t => t.Id == titleId, cancellationToken);

            if (entity is null)
                return null;

            var revision = await _context.Titles
                .Where(title => title.Id == titleId)
                .Select(title => (Guid?)title.Revision)
                .SingleOrDefaultAsync(cancellationToken);
            if (revision == entity.Revision)
                return entity.ToDomainWithCollections();
        }

        throw new PersistenceConflictException(new DbUpdateConcurrencyException(
            $"Title {titleId} kept changing while its aggregate was loaded."));
    }

    public Task<bool> ExistsWithNormalizedNameAsync(int platformId, string normalizedName,
        CancellationToken cancellationToken = default) =>
        _context.Titles
            .AsNoTracking()
            .AnyAsync(t => t.PlatformId == platformId && t.NormalizedName == normalizedName, cancellationToken);

    public async Task<IReadOnlyDictionary<string, int>> FindIdsByNormalizedNamesAsync(
        int platformId,
        IReadOnlyCollection<string> normalizedNames,
        CancellationToken cancellationToken = default)
    {
        if (normalizedNames.Count == 0)
        {
            return new Dictionary<string, int>();
        }

        var nameList = normalizedNames.ToList();
        return await _context.Titles
            .AsNoTracking()
            .Where(t => t.PlatformId == platformId && nameList.Contains(t.NormalizedName))
            .ToDictionaryAsync(t => t.NormalizedName, t => t.Id, cancellationToken);
    }

    public Task<IStagedTitleCreation> AddStagedAsync(Title title, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (title.Id != 0)
            throw new ArgumentException("Title creation requires a new title without an identity.", nameof(title));
        RequireTransaction();
        var entity = TitleEntity.FromDomain(title);
        _context.Titles.Add(entity);
        return Task.FromResult<IStagedTitleCreation>(new StagedTitleCreation(entity));
    }

    private sealed class StagedTitleCreation(TitleEntity entity) : IStagedTitleCreation
    {
        public int ReadIdAfterFlush() => entity.Id > 0
            ? entity.Id
            : throw new InvalidOperationException("Flush staged title creation before reading its identity.");
    }

    public Task<IStagedTitleCreationBatch> AddBatchStagedAsync(IReadOnlyList<Title> titles,
        CancellationToken cancellationToken = default)
    {
        if (titles.Count == 0)
        {
            return Task.FromResult<IStagedTitleCreationBatch>(new EmptyTitleBatch());
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (titles.Any(title => title.Id != 0))
            throw new ArgumentException("Title batch creation requires new titles without identities.", nameof(titles));
        var entities = titles.Select(TitleEntity.FromDomain).ToList();
        _context.Titles.AddRange(entities);
        return Task.FromResult<IStagedTitleCreationBatch>(new StagedTitleBatch(_context,
            _context.Database.CurrentTransaction?.TransactionId, entities));
    }

    private sealed class EmptyTitleBatch : IStagedTitleCreationBatch
    {
        public bool RequiresFlush => false;
        public IReadOnlyList<int> ReadIdsAfterFlush() => [];
    }

    private sealed class StagedTitleBatch(RomdDbContext context, Guid? transactionId, IReadOnlyList<TitleEntity> entities)
        : IStagedTitleCreationBatch
    {
        public bool RequiresFlush
        {
            get
            {
                if (context.Database.CurrentTransaction?.TransactionId != transactionId
                    || entities.Any(entity => context.Entry(entity).State is EntityState.Detached or EntityState.Deleted))
                    throw new InvalidOperationException("Title batch identities belong to their original transaction and tracked staging.");
                return entities.Any(entity => context.Entry(entity).State == EntityState.Added);
            }
        }
        public IReadOnlyList<int> ReadIdsAfterFlush()
        {
            if (RequiresFlush || entities.Any(entity => entity.Id <= 0))
                throw new InvalidOperationException("Flush staged title creation before reading batch identities.");
            return entities.Select(entity => entity.Id).ToArray();
        }
    }

    public Task<bool> LockAsync(int titleId, CancellationToken cancellationToken = default) =>
        TitleRowLock.AcquireAsync(_context, titleId, cancellationToken);

    public async Task<Guid?> LockRevisionAsync(int titleId, CancellationToken cancellationToken = default)
    {
        RequireTransaction();
        return await TitleRowLock.AcquireAsync(_context, titleId, cancellationToken)
            ? await _context.Titles.Where(title => title.Id == titleId).Select(title => title.Revision)
                .SingleAsync(cancellationToken)
            : null;
    }

    public async Task<StagedMediaAppend> AppendGalleryMediaStagedAsync(TitleMedia media, CancellationToken cancellationToken = default)
    {
        RequireTransaction();
        // Serialize append/dedup against other title mutations. A repeated import writes nothing and keeps the
        // current revision.
        await TitleRowLock.AcquireAsync(_context, media.TitleId, cancellationToken);
        if (await _context.TitleMedia.AnyAsync(item => item.TitleId == media.TitleId &&
                item.Type == media.Type.ToString() && item.SourceId == media.SourceId && item.FileId == media.FileId,
                cancellationToken))
            return new StagedMediaAppend(await _context.Titles.Where(title => title.Id == media.TitleId)
                .Select(title => title.Revision).SingleAsync(cancellationToken), Added: false);
        var revision = Guid.NewGuid();
        await _context.Titles.Where(title => title.Id == media.TitleId).ExecuteUpdateAsync(setters =>
            setters.SetProperty(title => title.Revision, revision), cancellationToken);
        var entity = TitleMediaEntity.FromDomain(media);
        // Gallery collection must not implicitly replace a display selection.
        entity.IsPrimary = false;
        _context.TitleMedia.Add(entity);
        return new StagedMediaAppend(revision, Added: true);
    }

    public async Task<StagedMediaAppend> AddUserMediaStagedAsync(TitleMedia media, CancellationToken cancellationToken = default)
    {
        if (_context.Database.CurrentTransaction is null || media.SourceId != "user")
            throw new InvalidOperationException("User media append requires a transaction.");
        // The non-rotating row lock serializes the duplicate check with every other writer of this title, so two
        // identical uploads cannot both miss it.
        await TitleRowLock.AcquireAsync(_context, media.TitleId, cancellationToken);
        // A repeat of the same content and type is the no-op the caller answers with 200 under the current ETag (a
        // stale one is already a 412): it writes nothing and keeps the current revision.
        if (await _context.TitleMedia.AnyAsync(item => item.TitleId == media.TitleId &&
            item.SourceId == "user" && item.Type == media.Type.ToString() && item.FileId == media.FileId,
            cancellationToken))
            return new StagedMediaAppend(await _context.Titles.Where(title => title.Id == media.TitleId)
                .Select(title => title.Revision).SingleAsync(cancellationToken), Added: false);
        var revision = Guid.NewGuid();
        await _context.Titles.Where(title => title.Id == media.TitleId).ExecuteUpdateAsync(setters =>
            setters.SetProperty(title => title.Revision, revision), cancellationToken);
        var entity = TitleMediaEntity.FromDomain(media);
        entity.IsPrimary = !await _context.TitleMedia.AnyAsync(item => item.TitleId == media.TitleId &&
            item.Type == entity.Type && item.IsPrimary, cancellationToken);
        _context.TitleMedia.Add(entity);
        return new StagedMediaAppend(revision, Added: true);
    }

    public async Task UpdateMaterializedMetadataStagedAsync(
        Title title,
        CancellationToken cancellationToken = default)
    {
        // Update the tracked row in place so persistence-only state such as CatalogState and
        // ScreenshotPrefsJson is preserved. This path deliberately does not replace collection
        // graphs: field overrides can change effective scalar/rating values and primary-media
        // selection, but cannot add, remove, or rewrite media, external IDs, or metadata layers.
        var entity = await TrackSnapshotAsync(title, cancellationToken);
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

    public async Task RemoveMediaStagedAsync(Title title, int mediaId, CancellationToken cancellationToken = default)
    {
        if (title.GetMediaById(mediaId) is not null)
            throw new ArgumentException($"Remove media {mediaId} from title {title.Id} before staging its deletion.",
                nameof(mediaId));
        await TrackSnapshotAsync(title, cancellationToken);
        var persisted = await _context.TitleMedia
            .AsTracking()
            .Where(media => media.TitleId == title.Id)
            .ToListAsync(cancellationToken);
        // The revision lock guarantees the row still exists; a missing row means the snapshot is stale.
        var removed = persisted.SingleOrDefault(media => media.Id == mediaId) ?? throw StaleChildren(title.Id);
        _context.TitleMedia.Remove(removed);

        // Primary recalculation may touch every media type, so persist each remaining row's flag.
        var primaryByMediaId = title.Media
            .Where(media => media.Id > 0)
            .ToDictionary(media => media.Id, media => media.IsPrimary);
        foreach (var media in persisted.Where(media => media.Id != mediaId))
        {
            if (primaryByMediaId.TryGetValue(media.Id, out bool isPrimary))
            {
                media.IsPrimary = isPrimary;
            }
        }
    }

    public async Task UpdatePrimaryMediaStagedAsync(Title title, MediaType type,
        CancellationToken cancellationToken = default)
    {
        await TrackSnapshotAsync(title, cancellationToken);
        var primaryIds = title.GetMediaByType(type)
            .Where(media => media.IsPrimary && media.Id > 0)
            .Select(media => media.Id)
            .ToHashSet();
        string typeName = type.ToString();
        var persisted = await _context.TitleMedia
            .AsTracking()
            .Where(media => media.TitleId == title.Id && media.Type == typeName)
            .ToListAsync(cancellationToken);
        foreach (var media in persisted)
        {
            media.IsPrimary = primaryIds.Contains(media.Id);
        }
    }

    public async Task ApplyEnrichmentStagedAsync(Title title, CancellationToken cancellationToken = default)
    {
        await UpdateMaterializedMetadataStagedAsync(title, cancellationToken);
        await UpsertExternalIdsAsync(title.Id, title.ExternalIds, cancellationToken);
        await UpsertMetadataLayersAsync(title.Id, title.MetadataLayers, cancellationToken);
        // Media rows are append-only on this route; existing rows changed only their primary flag,
        // which the materialized write above already staged.
        foreach (var media in title.Media.Where(media => media.Id == 0))
        {
            var entity = TitleMediaEntity.FromDomain(media);
            entity.TitleId = title.Id;
            _context.TitleMedia.Add(entity);
        }
    }

    public async Task ApplyMergeStagedAsync(Title target, Title source, CancellationToken cancellationToken = default)
    {
        RequireTransaction();
        // The absorbed source state must still be current. Lock in ID order, as artwork merge does,
        // so opposing merges cannot deadlock on the two title rows.
        foreach (var snapshot in new[] { source, target }.OrderBy(snapshot => snapshot.Id))
        {
            await LockSnapshotAsync(snapshot, cancellationToken);
        }

        await ApplyEnrichmentStagedAsync(target, cancellationToken);
    }

    public async Task<IReadOnlyList<string>> GetUnlinkedProvidersAsync(int titleId,
        CancellationToken cancellationToken = default) =>
        await _context.Set<TitleProviderMatchStateEntity>()
            .Where(state => state.TitleId == titleId && state.SuppressAutomaticMatch)
            .Select(state => state.ProviderId)
            .ToListAsync(cancellationToken);

    private async Task UpsertExternalIdsAsync(int titleId, IEnumerable<TitleExternalId> links,
        CancellationToken cancellationToken)
    {
        var persisted = await _context.TitleExternalIds
            .AsTracking()
            .Where(link => link.TitleId == titleId)
            .ToListAsync(cancellationToken);
        foreach (var link in links)
        {
            var row = link.Id > 0
                ? persisted.SingleOrDefault(candidate => candidate.Id == link.Id) ?? throw StaleChildren(titleId)
                : persisted.FirstOrDefault(candidate =>
                    candidate.Provider.Equals(link.Provider, StringComparison.OrdinalIgnoreCase));
            if (row is null)
            {
                row = TitleExternalIdEntity.FromDomain(link);
                row.TitleId = titleId;
                _context.TitleExternalIds.Add(row);
                continue;
            }

            row.ExternalId = link.ExternalId;
            row.MatchConfidence = link.MatchConfidence;
            row.IsConfirmed = link.IsConfirmed;
        }
    }

    private async Task UpsertMetadataLayersAsync(int titleId, IEnumerable<TitleMetadataLayer> layers,
        CancellationToken cancellationToken)
    {
        var persisted = await _context.TitleMetadataLayers
            .AsTracking()
            .Where(layer => layer.TitleId == titleId)
            .ToListAsync(cancellationToken);
        foreach (var layer in layers)
        {
            var row = layer.Id > 0
                ? persisted.SingleOrDefault(candidate => candidate.Id == layer.Id) ?? throw StaleChildren(titleId)
                : persisted.FirstOrDefault(candidate =>
                    candidate.SourceId.Equals(layer.SourceId, StringComparison.OrdinalIgnoreCase));
            if (row is null)
            {
                row = TitleMetadataLayerEntity.FromDomain(layer);
                row.TitleId = titleId;
                _context.TitleMetadataLayers.Add(row);
                continue;
            }

            row.MetadataJson = layer.MetadataJson;
            row.SourceType = (int)layer.SourceType;
        }
    }

    private static PersistenceConflictException StaleChildren(int titleId) =>
        new(new DbUpdateConcurrencyException($"Title {titleId} children changed after it was loaded."));

    /// <summary>
    ///     Verifies and locks the snapshot revision, then tracks the title row with the revision
    ///     rotation staged. Every staged title write starts here.
    /// </summary>
    private async Task<TitleEntity> TrackSnapshotAsync(Title title, CancellationToken cancellationToken)
    {
        RequireTransaction();
        await LockSnapshotAsync(title, cancellationToken);
        var entity = await _context.Titles
            .AsTracking()
            .SingleAsync(candidate => candidate.Id == title.Id, cancellationToken);
        StageRevision(entity, title);
        return entity;
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


    public async Task StageArtworkMergeAsync(int sourceTitleId, int targetTitleId, CancellationToken cancellationToken = default)
    {
        await new Romd.Persistence.Artwork.ArtworkTitleMerger(_context).StageAsync(sourceTitleId, targetTitleId, cancellationToken);
        // Curator acceptance follows the surviving identity, just like pinned artwork.
        // A populated target field still wins; acceptance matters only if the field is absent later.
        var accepted = await _context.Set<EnrichmentOmissionEntity>().Where(x => x.TitleId == targetTitleId)
            .Select(x => x.Field).ToListAsync(cancellationToken);
        var source = await _context.Set<EnrichmentOmissionEntity>().Where(x => x.TitleId == sourceTitleId).ToListAsync(cancellationToken);
        foreach (var omission in source.Where(x => !accepted.Contains(x.Field)))
            _context.Add(new EnrichmentOmissionEntity { TitleId = targetTitleId, Field = omission.Field, AcceptedAt = omission.AcceptedAt });
    }

    public async Task DeleteAbsorbedSourceAsync(int sourceTitleId, CancellationToken cancellationToken = default)
    {
        if (await _context.TrackedTitles.AnyAsync(t => t.TitleId == sourceTitleId, cancellationToken))
        {
            await _context.Titles
                .Where(t => t.Id == sourceTitleId)
                .ExecuteUpdateAsync(
                    setters => setters.SetProperty(t => t.CatalogState, TitleCatalogState.UserOnly),
                    cancellationToken);
            return;
        }

        await _context.Titles
            .Where(t => t.Id == sourceTitleId)
            .ExecuteDeleteAsync(cancellationToken);
    }
}
