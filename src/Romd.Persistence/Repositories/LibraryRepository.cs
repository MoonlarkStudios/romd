using Microsoft.EntityFrameworkCore;
using Romd.Consumer.Application.Libraries;
using Romd.Admin.Application.Libraries;
using Romd.Domain.Libraries;
using Romd.Persistence.Entities;
using Romd.Persistence.Queries;

namespace Romd.Persistence.Repositories;

public sealed class LibraryRepository(RomdDbContext context) : ILibraryRepository, IConsumerLibraryContextRepository
{
    private static readonly string ValidConfigurationState = LibraryConfigurationState.Valid.ToString();
    private readonly LiveConsumerLibraryQuery _liveConsumerLibrary = new(context);

    public async Task<Library?> GetByIdAsync(int id, CancellationToken ct = default)
    {
        var entity = await context.Libraries
            .AsNoTracking()
            .FirstOrDefaultAsync(l => l.Id == id, ct);

        return entity?.ToDomain();
    }

    public async Task<Library?> GetByNameAsync(string name, CancellationToken ct = default)
    {
        var entity = await context.Libraries
            .AsNoTracking()
            .FirstOrDefaultAsync(l => l.Name == name, ct);

        return entity?.ToDomain();
    }

    public async Task<Library?> GetDefaultAsync(CancellationToken ct = default)
    {
        var entity = await context.Libraries
            .AsNoTracking()
            .FirstOrDefaultAsync(l => l.IsDefault, ct);

        return entity?.ToDomain();
    }

    public async Task<IReadOnlyList<Library>> GetAllAsync(CancellationToken ct = default)
    {
        var entities = await context.Libraries
            .AsNoTracking()
            .OrderBy(l => l.Name)
            .ToListAsync(ct);

        return entities.Select(e => e.ToDomain()).ToList();
    }

    public Task AddStagedAsync(Library library, CancellationToken ct = default)
    {
        context.Libraries.Add(LibraryEntity.FromDomain(library));
        return Task.CompletedTask;
    }

    public async Task UpdateAsync(Library library, CancellationToken ct = default)
    {
        if (library.IsDefault)
        {
            var revision = Guid.NewGuid();
            await using var transaction = await context.Database.BeginTransactionAsync(ct);
            await context.Libraries
                .Where(l => l.IsDefault && l.Id != library.Id)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(l => l.IsDefault, false)
                    .SetProperty(l => l.MaterializationRevision, revision)
                    .SetProperty(l => l.UpdatedAt, DateTimeOffset.UtcNow), ct);

            await ApplyMutableFieldsAsync(library, ct);
            await context.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return;
        }

        await ApplyMutableFieldsAsync(library, ct);
        await context.SaveChangesAsync(ct);
    }

    public Task UpdateStagedAsync(Library library, CancellationToken ct = default) =>
        ApplyMutableFieldsAsync(library, ct);

    public async Task<bool> DeleteAsync(int id, CancellationToken ct = default)
    {
        int deleted = await context.Libraries
            .Where(l => l.Id == id)
            .ExecuteDeleteAsync(ct);

        return deleted > 0;
    }

    public async Task SetDefaultAsync(int libraryId, CancellationToken ct = default)
    {
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        var now = DateTimeOffset.UtcNow;
        var revision = Guid.NewGuid();

        await context.Libraries
            .Where(l => l.IsDefault && l.Id != libraryId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(l => l.IsDefault, false)
                .SetProperty(l => l.MaterializationRevision, revision)
                .SetProperty(l => l.UpdatedAt, now), ct);

        await context.Libraries
            .Where(l => l.Id == libraryId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(l => l.IsDefault, true)
                .SetProperty(l => l.MaterializationRevision, revision)
                .SetProperty(l => l.UpdatedAt, now), ct);

        await transaction.CommitAsync(ct);
    }

    public async Task ClearDefaultAsync(CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var revision = Guid.NewGuid();

        await context.Libraries
            .Where(l => l.IsDefault)
            .ExecuteUpdateAsync(s => s
                .SetProperty(l => l.IsDefault, false)
                .SetProperty(l => l.MaterializationRevision, revision)
                .SetProperty(l => l.UpdatedAt, now), ct);
    }

    public async Task<string?> ValidateConfigurationReferencesAsync(
        LibraryConfiguration configuration,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var validationError = LibraryConfigurationValidator.Validate(configuration);
        if (validationError is not null)
        {
            return validationError;
        }

        if (await FindMissingIdAsync(
                context.Platforms.AsNoTracking().Select(platform => platform.Id),
                configuration.AllowedPlatformIds,
                ct) is int missingPlatformId)
        {
            return $"Allowed platform ID {missingPlatformId} does not exist.";
        }

        if (await FindMissingIdAsync(
                context.DatFiles.AsNoTracking().Select(datFile => datFile.Id),
                configuration.ExcludedDatIds,
                ct) is int missingDatId)
        {
            return $"Excluded DAT ID {missingDatId} does not exist.";
        }

        if (await FindMissingIdAsync(
                context.Titles.AsNoTracking().Select(title => title.Id),
                configuration.IncludeTitleIds,
                ct) is int missingIncludedTitleId)
        {
            return $"Included title ID {missingIncludedTitleId} does not exist.";
        }

        if (await FindMissingIdAsync(
                context.Titles.AsNoTracking().Select(title => title.Id),
                configuration.ExcludeTitleIds,
                ct) is int missingExcludedTitleId)
        {
            return $"Excluded title ID {missingExcludedTitleId} does not exist.";
        }

        return null;
    }

    public async Task MarkConfigurationInvalidAsync(int libraryId, string? error, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var revision = Guid.NewGuid();
        await context.Libraries
            .Where(l => l.Id == libraryId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(l => l.ConfigurationState, LibraryConfigurationState.Invalid.ToString())
                .SetProperty(l => l.ConfigurationError, error ?? "Library configuration is invalid.")
                .SetProperty(l => l.NeedsMaterialization, false)
                .SetProperty(l => l.ItemCount, 0)
                .SetProperty(l => l.MaterializationRevision, revision)
                .SetProperty(l => l.UpdatedAt, now), ct);
    }

    public async Task<IReadOnlyList<Library>> GetNeedingMaterializationAsync(CancellationToken ct = default)
    {
        var entities = await context.Libraries
            .AsNoTracking()
            .Where(l => l.NeedsMaterialization && l.ConfigurationState == ValidConfigurationState)
            .ToListAsync(ct);

        return entities.Select(e => e.ToDomain()).ToList();
    }

    public async Task<Library?> FlagForRematerializationAsync(int libraryId, CancellationToken ct = default)
    {
        if (context.Database.CurrentTransaction is null)
            throw new InvalidOperationException("Library invalidation requires a caller-owned transaction.");

        var now = DateTimeOffset.UtcNow;
        var revision = Guid.NewGuid();
        int updated = await context.Libraries.Where(library => library.Id == libraryId)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(library => library.NeedsMaterialization, true)
                .SetProperty(library => library.MaterializationRevision, revision)
                .SetProperty(library => library.UpdatedAt, now), ct);
        return updated == 0 ? null : await GetByIdAsync(libraryId, ct);
    }

    public async Task FlagAllForRematerializationAsync(CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var revision = Guid.NewGuid();
        await context.Libraries
            .ExecuteUpdateAsync(s => s
                .SetProperty(l => l.NeedsMaterialization, true)
                .SetProperty(l => l.MaterializationRevision, revision)
                .SetProperty(l => l.UpdatedAt, now), ct);
    }

    public async Task FlagForRematerializationByPlatformAsync(int platformId, CancellationToken ct = default)
    {
        // Load libraries and filter in C# (configs are JSON — can't reliably filter in SQL).
        // Library count is small (< 50 typically), so this is a single lightweight read + single batch write.
        var candidates = await context.Libraries
            .AsNoTracking()
            .Select(l => new { l.Id, l.ConfigurationJson, l.ConfigurationState, l.ConfigurationError })
            .ToListAsync(ct);

        var affectedIds = candidates
            .Where(l =>
            {
                var parsed = LibraryEntity.DeserializeConfig(
                    l.ConfigurationJson,
                    l.ConfigurationState,
                    l.ConfigurationError);

                return parsed.State == LibraryConfigurationState.Valid &&
                       (parsed.Configuration.AllowedPlatformIds.Count == 0 ||
                        parsed.Configuration.AllowedPlatformIds.Contains(platformId));
            })
            .Select(l => l.Id)
            .ToList();

        if (affectedIds.Count > 0)
        {
            var now = DateTimeOffset.UtcNow;
            var revision = Guid.NewGuid();
            await context.Libraries
                .Where(l => affectedIds.Contains(l.Id))
                .ExecuteUpdateAsync(s => s
                    .SetProperty(l => l.NeedsMaterialization, true)
                    .SetProperty(l => l.MaterializationRevision, revision)
                    .SetProperty(l => l.UpdatedAt, now), ct);
        }
    }

    public async Task<bool> TryReplaceMaterializedProjectionsAndActivateAsync(
        int libraryId,
        MaterializedLibraryProjection projection,
        int itemCount,
        Guid materializationToken,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(projection);

        var previouslyTrackedEntities = context.ChangeTracker.Entries()
            .Select(entry => entry.Entity)
            .ToHashSet(ReferenceEqualityComparer.Instance);
        bool autoDetectChangesEnabled = context.ChangeTracker.AutoDetectChangesEnabled;
        await using var ownedTransaction = context.Database.CurrentTransaction is null
            ? await context.Database.BeginTransactionAsync(ct)
            : null;
        context.ChangeTracker.AutoDetectChangesEnabled = false;
        try
        {
            var now = DateTimeOffset.UtcNow;
            var revision = Guid.NewGuid();
            int activated = await context.Libraries
                .Where(l => l.Id == libraryId && l.MaterializationRevision == materializationToken)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(l => l.NeedsMaterialization, false)
                    .SetProperty(l => l.LastMaterializedAt, now)
                    .SetProperty(l => l.ItemCount, itemCount)
                    .SetProperty(l => l.MaterializationGeneration, l => l.MaterializationGeneration + 1)
                    .SetProperty(l => l.MaterializationRevision, revision)
                    .SetProperty(l => l.UpdatedAt, now), ct);
            if (activated == 0)
            {
                if (ownedTransaction is not null)
                {
                    await ownedTransaction.RollbackAsync(ct);
                }

                return false;
            }

            var existingTitles = await context.MaterializedLibraryTitles
                .AsTracking()
                .Where(m => m.LibraryId == libraryId)
                .ToListAsync(ct);

            var existingReleases = await context.MaterializedLibraryReleases
                .AsTracking()
                .Where(m => m.LibraryId == libraryId)
                .ToListAsync(ct);

            SyncMaterializedTitles(existingTitles, projection.Titles);
            SyncMaterializedReleases(existingReleases, projection.Releases);

            context.ChangeTracker.DetectChanges();
            await context.SaveChangesAsync(ct);
            if (ownedTransaction is not null)
            {
                await ownedTransaction.CommitAsync(ct);
            }

            return true;
        }
        finally
        {
            foreach (var entry in context.ChangeTracker.Entries()
                         .Where(entry => !previouslyTrackedEntities.Contains(entry.Entity)
                                         && entry.Entity is MaterializedLibraryTitleEntity
                                             or MaterializedLibraryReleaseEntity))
            {
                entry.State = EntityState.Detached;
            }

            context.ChangeTracker.AutoDetectChangesEnabled = autoDetectChangesEnabled;
        }
    }

    private void SyncMaterializedTitles(
        IReadOnlyList<MaterializedLibraryTitleEntity> existingTitles,
        IReadOnlyList<MaterializedLibraryTitle> projectedTitles)
    {
        var projectedByTitleId = projectedTitles.ToDictionary(title => title.TitleId);
        var existingTitleIds = existingTitles.Select(title => title.TitleId).ToHashSet();

        foreach (var existing in existingTitles)
        {
            if (projectedByTitleId.TryGetValue(existing.TitleId, out var projected))
            {
                UpdateMaterializedTitle(existing, projected);
                continue;
            }

            context.MaterializedLibraryTitles.Remove(existing);
        }

        foreach (var projected in projectedTitles.Where(title => !existingTitleIds.Contains(title.TitleId)))
        {
            context.MaterializedLibraryTitles.Add(MaterializedLibraryTitleEntity.FromDomain(projected));
        }
    }

    private void SyncMaterializedReleases(
        IReadOnlyList<MaterializedLibraryReleaseEntity> existingReleases,
        IReadOnlyList<MaterializedLibraryRelease> projectedReleases)
    {
        var projectedByDatGameId = projectedReleases.ToDictionary(release => release.DatGameId);
        var existingDatGameIds = existingReleases.Select(release => release.DatGameId).ToHashSet();

        foreach (var existing in existingReleases)
        {
            if (projectedByDatGameId.TryGetValue(existing.DatGameId, out var projected))
            {
                UpdateMaterializedRelease(existing, projected);
                continue;
            }

            context.MaterializedLibraryReleases.Remove(existing);
        }

        foreach (var projected in projectedReleases.Where(release => !existingDatGameIds.Contains(release.DatGameId)))
        {
            context.MaterializedLibraryReleases.Add(MaterializedLibraryReleaseEntity.FromDomain(projected));
        }
    }

    private static void UpdateMaterializedTitle(
        MaterializedLibraryTitleEntity existing,
        MaterializedLibraryTitle projected)
    {
        string availability = projected.Availability.ToString();

        if (existing.Genre != projected.Genre)
            existing.Genre = projected.Genre;
        if (existing.IsVisible != projected.IsVisible)
            existing.IsVisible = projected.IsVisible;
        if (existing.IsOwned != projected.IsOwned)
            existing.IsOwned = projected.IsOwned;
        if (existing.IsPlayable != projected.IsPlayable)
            existing.IsPlayable = projected.IsPlayable;
        if (existing.EligibleReleaseCount != projected.EligibleReleaseCount)
            existing.EligibleReleaseCount = projected.EligibleReleaseCount;
        if (existing.PlayableReleaseCount != projected.PlayableReleaseCount)
            existing.PlayableReleaseCount = projected.PlayableReleaseCount;
        if (existing.ExposedReleaseCount != projected.ExposedReleaseCount)
            existing.ExposedReleaseCount = projected.ExposedReleaseCount;
        if (existing.Availability != availability)
            existing.Availability = availability;
    }

    private static void UpdateMaterializedRelease(
        MaterializedLibraryReleaseEntity existing,
        MaterializedLibraryRelease projected)
    {
        if (existing.CatalogReleaseId != projected.CatalogReleaseId)
            existing.CatalogReleaseId = projected.CatalogReleaseId;
        if (existing.IsEligible != projected.IsEligible)
            existing.IsEligible = projected.IsEligible;
        if (existing.IsComplete != projected.IsComplete)
            existing.IsComplete = projected.IsComplete;
        if (existing.IsOwned != projected.IsOwned)
            existing.IsOwned = projected.IsOwned;
        if (existing.IsPlayable != projected.IsPlayable)
            existing.IsPlayable = projected.IsPlayable;
        if (existing.IsBlocked != projected.IsBlocked)
            existing.IsBlocked = projected.IsBlocked;
        if (existing.BlockReason != projected.BlockReason)
            existing.BlockReason = projected.BlockReason;
        if (existing.IsExposed != projected.IsExposed)
            existing.IsExposed = projected.IsExposed;
        if (existing.ExposureReason != projected.ExposureReason)
            existing.ExposureReason = projected.ExposureReason;
    }

    private async Task ApplyMutableFieldsAsync(Library library, CancellationToken ct)
    {
        var entity = await context.Libraries
            .AsTracking()
            .SingleAsync(candidate => candidate.Id == library.Id, ct);
        var updated = LibraryEntity.FromDomain(library);

        entity.Name = updated.Name;
        entity.ConfigurationJson = updated.ConfigurationJson;
        entity.ConfigurationState = updated.ConfigurationState;
        entity.ConfigurationError = updated.ConfigurationError;
        entity.IsDefault = updated.IsDefault;
        entity.NeedsMaterialization = updated.NeedsMaterialization;
        // Projection state is store-owned and changes only with an atomic projection operation.
        // A stale aggregate must not rewind a committed generation, timestamp, or item count.
        entity.UpdatedAt = updated.UpdatedAt;
        entity.MaterializationRevision = Guid.NewGuid();
    }

    public async Task<bool> TryReplaceMaterializedProjectionsAndMarkConfigurationInvalidAsync(
        int libraryId,
        string? error,
        Guid materializationToken,
        CancellationToken ct = default)
    {
        bool autoDetectChangesEnabled = context.ChangeTracker.AutoDetectChangesEnabled;
        await using var ownedTransaction = context.Database.CurrentTransaction is null
            ? await context.Database.BeginTransactionAsync(ct)
            : null;
        context.ChangeTracker.AutoDetectChangesEnabled = false;
        try
        {
            var now = DateTimeOffset.UtcNow;
            var revision = Guid.NewGuid();
            int invalidated = await context.Libraries
                .Where(l => l.Id == libraryId && l.MaterializationRevision == materializationToken)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(l => l.ConfigurationState, LibraryConfigurationState.Invalid.ToString())
                    .SetProperty(l => l.ConfigurationError, error ?? "Library configuration is invalid.")
                    .SetProperty(l => l.NeedsMaterialization, false)
                    .SetProperty(l => l.ItemCount, 0)
                    .SetProperty(l => l.MaterializationRevision, revision)
                    .SetProperty(l => l.UpdatedAt, now), ct);

            if (invalidated == 0)
            {
                if (ownedTransaction is not null)
                {
                    await ownedTransaction.RollbackAsync(ct);
                }

                return false;
            }

            await context.MaterializedLibraryReleases
                .Where(m => m.LibraryId == libraryId)
                .ExecuteDeleteAsync(ct);

            await context.MaterializedLibraryTitles
                .Where(m => m.LibraryId == libraryId)
                .ExecuteDeleteAsync(ct);

            if (ownedTransaction is not null)
            {
                await ownedTransaction.CommitAsync(ct);
            }

            return true;
        }
        finally
        {
            context.ChangeTracker.AutoDetectChangesEnabled = autoDetectChangesEnabled;
        }
    }

    public async Task<LibraryContentCounts> GetContentCountsAsync(int libraryId, CancellationToken ct = default)
    {
        int ownedTitleCount = await MaterializedTitlesForValidLibrary(libraryId)
            .Where(m => m.IsOwned)
            .CountAsync(ct);

        int availableTitleCount = await MaterializedTitlesForValidLibrary(libraryId)
            .Where(m => m.ExposedReleaseCount > 0)
            .CountAsync(ct);

        int collectionCount = await context.CollectionItems
            .Where(i => context.LibraryCollections.Any(a => a.LibraryId == libraryId && a.CollectionId == i.CollectionId))
            .Join(
                MaterializedTitlesForValidLibrary(libraryId).Where(m => m.IsOwned),
                ci => ci.TitleId,
                title => title.TitleId,
                (ci, _) => ci.CollectionId)
            .Distinct()
            .CountAsync(ct);

        return new LibraryContentCounts(ownedTitleCount, availableTitleCount, collectionCount);
    }

    public Task<ConsumerLibraryReadResult<ConsumerLibraryContextReadModel>> GetCurrentContextAsync(
        ConsumerLibraryScope scope,
        CancellationToken ct = default) =>
        _liveConsumerLibrary.ReadAsync<ConsumerLibraryContextReadModel>(
            scope,
            async (libraryId, token) =>
                new ConsumerLibraryProjectionResult<ConsumerLibraryContextReadModel>.Found(
                    await GetCurrentContextForLibraryAsync(libraryId, token)),
            ct);

    private async Task<ConsumerLibraryContextReadModel> GetCurrentContextForLibraryAsync(
        int libraryId,
        CancellationToken ct)
    {
        var library = await context.Libraries
            .AsNoTracking()
            .Where(candidate => candidate.Id == libraryId)
            .Select(candidate => new { candidate.Name })
            .SingleAsync(ct);

        // Library validity is already confirmed above, so scope facets directly by LibraryId and
        // aggregate in SQL rather than streaming every owned title to the app to group in memory.
        var ownedTitles = context.MaterializedLibraryTitles
            .AsNoTracking()
            .Where(title => title.LibraryId == libraryId && title.IsOwned);

        var platformFacetCounts = await ownedTitles
            .GroupBy(title => title.PlatformId)
            .Select(group => new PlatformFacetCount(group.Key, group.Count()))
            .ToListAsync(ct);
        int ownedTitleCount = platformFacetCounts.Sum(facet => facet.Count);

        var platformIds = platformFacetCounts.Select(facet => facet.PlatformId).ToList();
        var systemSummaries = await new SystemSummaryReader(context).ReadAsync(platformIds, ct);

        var platforms = platformFacetCounts
            .Select(facet => new ConsumerLibraryPlatformFacetReadModel(
                systemSummaries[facet.PlatformId].Key,
                systemSummaries[facet.PlatformId].Name,
                facet.Count))
            .OrderByDescending(facet => facet.Count)
            .ThenBy(facet => facet.PlatformName)
            .ToList();

        var genreFacetCounts = await ownedTitles
            .Where(title => title.Genre != null)
            .GroupBy(title => title.Genre!)
            .Select(group => new { Genre = group.Key, Count = group.Count() })
            .ToListAsync(ct);
        var genres = genreFacetCounts
            .Select(facet => new ConsumerLibraryGenreFacetReadModel(facet.Genre, facet.Count))
            .OrderByDescending(facet => facet.Count)
            .ThenBy(facet => facet.Genre)
            .ToList();

        int availableTitleCount = await context.MaterializedLibraryTitles
            .AsNoTracking()
            .CountAsync(title => title.LibraryId == libraryId && title.ExposedReleaseCount > 0, ct);

        var collectionFacets = await context.CollectionItems
            .AsNoTracking()
            .Where(i => context.LibraryCollections.Any(a => a.LibraryId == libraryId && a.CollectionId == i.CollectionId))
            .Join(
                context.MaterializedLibraryTitles
                    .AsNoTracking()
                    .Where(title => title.LibraryId == libraryId && title.IsOwned),
                item => item.TitleId,
                title => title.TitleId,
                (item, _) => item.CollectionId)
            .GroupBy(collectionId => collectionId)
            .Select(group => new { CollectionId = group.Key, MatchingCount = group.Count() })
            .Join(
                context.Collections.AsNoTracking(),
                facet => facet.CollectionId,
                collection => collection.Id,
                (facet, collection) => new
                {
                    facet.CollectionId,
                    CollectionName = collection.Name,
                    facet.MatchingCount,
                    IsFeatured = context.LibraryCollections.Any(a => a.LibraryId == libraryId && a.CollectionId == facet.CollectionId && a.IsFeatured),
                    SortOrder = context.LibraryCollections.Where(a => a.LibraryId == libraryId && a.CollectionId == facet.CollectionId).Select(a => a.SortOrder).First()
                })
            .OrderBy(facet => facet.SortOrder)
            .ThenBy(facet => facet.CollectionName)
            .ToListAsync(ct);
        var collections = collectionFacets
            .Where(facet => facet.IsFeatured)
            .Select(facet => new ConsumerLibraryCollectionFacetReadModel(
                facet.CollectionId,
                facet.CollectionName,
                facet.MatchingCount))
            .ToList();

        return new ConsumerLibraryContextReadModel(
            library.Name,
            new ConsumerLibraryContentCountsReadModel(
                ownedTitleCount,
                availableTitleCount,
                collectionFacets.Count),
            platforms,
            genres,
            collections);
    }

    public async Task<IReadOnlyList<PlatformFacet>> GetPlatformFacetsAsync(
        int libraryId,
        int minimumItems = 1,
        CancellationToken ct = default)
    {
        var facets = await MaterializedTitlesForValidLibrary(libraryId)
            .Where(m => m.IsOwned)
            .GroupBy(m => m.PlatformId)
            .Select(g => new { PlatformId = g.Key, Count = g.Count() })
            .Where(f => f.Count >= minimumItems)
            .ToListAsync(ct);

        // Lookup platform names for the result set
        var platformIds = facets.Select(f => f.PlatformId).ToList();
        var platforms = await context.Platforms
            .Where(p => platformIds.Contains(p.Id))
            .Select(p => new { p.Id, p.Name })
            .ToDictionaryAsync(p => p.Id, p => p.Name, ct);

        return facets
            .Select(f => new PlatformFacet(f.PlatformId, platforms.GetValueOrDefault(f.PlatformId, "Unknown"), f.Count))
            .OrderByDescending(f => f.Count)
            .ThenBy(f => f.PlatformName)
            .ToList();
    }

    public async Task<IReadOnlyList<GenreFacet>> GetGenreFacetsAsync(
        int libraryId,
        int minimumItems = 1,
        CancellationToken ct = default)
    {
        var facets = await MaterializedTitlesForValidLibrary(libraryId)
            .Where(m => m.IsOwned && m.Genre != null)
            .GroupBy(m => m.Genre!)
            .Select(g => new { Genre = g.Key, Count = g.Count() })
            .Where(f => f.Count >= minimumItems)
            .OrderByDescending(f => f.Count)
            .ThenBy(f => f.Genre)
            .ToListAsync(ct);

        return facets
            .Select(f => new GenreFacet(f.Genre, f.Count))
            .ToList();
    }

    public async Task<IReadOnlyList<CollectionFacet>> GetCollectionFacetsAsync(
        int libraryId,
        int minimumItems = 1,
        CancellationToken ct = default)
    {
        var facets = await context.CollectionItems
            .Where(i => context.LibraryCollections.Any(a => a.LibraryId == libraryId && a.CollectionId == i.CollectionId))
            .Join(
                MaterializedTitlesForValidLibrary(libraryId).Where(m => m.IsOwned),
                ci => ci.TitleId,
                title => title.TitleId,
                (ci, _) => ci.CollectionId)
            .GroupBy(collectionId => collectionId)
            .Select(g => new { CollectionId = g.Key, MatchingCount = g.Count() })
            .Where(f => f.MatchingCount >= minimumItems)
            .ToListAsync(ct);

        // Lookup collection names and total counts
        var collectionIds = facets.Select(f => f.CollectionId).ToList();
        var collections = await context.Collections
            .Where(c => collectionIds.Contains(c.Id))
            .Select(c => new { c.Id, c.Name })
            .ToDictionaryAsync(c => c.Id, c => c.Name, ct);

        var totalCounts = await context.CollectionItems
            .Where(ci => collectionIds.Contains(ci.CollectionId))
            .GroupBy(ci => ci.CollectionId)
            .Select(g => new { CollectionId = g.Key, TotalCount = g.Count() })
            .ToDictionaryAsync(c => c.CollectionId, c => c.TotalCount, ct);

        return facets
            .Select(f => new CollectionFacet(
                f.CollectionId,
                collections.GetValueOrDefault(f.CollectionId, "Unknown"),
                f.MatchingCount,
                totalCounts.GetValueOrDefault(f.CollectionId, 0)))
            .OrderByDescending(f => f.MatchingCount)
            .ThenBy(f => f.CollectionName)
            .ToList();
    }

    public async Task<IReadOnlyList<LibraryTitleReleaseDiagnostics>> GetTitleReleaseDiagnosticsAsync(
        int libraryId,
        int titleId,
        CancellationToken ct = default)
    {
        var rows = await context.MaterializedLibraryReleases
            .AsNoTracking()
            .Where(release => release.LibraryId == libraryId && release.TitleId == titleId)
            .Join(
                context.DatGames.AsNoTracking(),
                release => release.DatGameId,
                game => game.Id,
                (release, game) => new
                {
                    ReleaseId = release.DatGameId,
                    release.DatFileId,
                    game.Name,
                    release.IsEligible,
                    release.IsBlocked,
                    release.BlockReason,
                    release.IsExposed,
                    release.ExposureReason
                })
            .OrderByDescending(release => release.IsExposed)
            .ThenBy(release => release.Name)
            .ThenBy(release => release.ReleaseId)
            .ToListAsync(ct);

        return rows
            .Select(release => new LibraryTitleReleaseDiagnostics(
                release.ReleaseId,
                release.DatFileId,
                release.Name,
                release.IsEligible,
                release.IsBlocked,
                release.BlockReason,
                release.IsExposed,
                release.ExposureReason))
            .ToList();
    }

    private static async Task<int?> FindMissingIdAsync(
        IQueryable<int> existingIdsQuery,
        IReadOnlyList<int> requestedIds,
        CancellationToken ct)
    {
        var distinctIds = requestedIds.Distinct().ToList();
        if (distinctIds.Count == 0)
        {
            return null;
        }

        var existingIds = await existingIdsQuery
            .Where(id => distinctIds.Contains(id))
            .ToListAsync(ct);
        var existingSet = existingIds.ToHashSet();

        return distinctIds
            .Where(id => !existingSet.Contains(id))
            .Order()
            .Cast<int?>()
            .FirstOrDefault();
    }

    private IQueryable<MaterializedLibraryTitleEntity> MaterializedTitlesForValidLibrary(int libraryId) =>
        context.MaterializedLibraryTitles.Where(title =>
            title.LibraryId == libraryId &&
            context.Libraries.Any(library =>
                library.Id == title.LibraryId &&
                library.ConfigurationState == ValidConfigurationState));

    private readonly record struct PlatformFacetCount(int PlatformId, int Count);
}
