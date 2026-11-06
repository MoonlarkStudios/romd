using Romd.Application.Common.ReferenceCatalog;
using ErrorOr;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Romd.Admin.Application.Catalog;
using Romd.Application.Common.Concurrency;
using Romd.Application.Common.Cqrs;
using Romd.Application.Common.Systems;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Configuration;
using Romd.Admin.Application.Libraries;
using Romd.Admin.Application.Source.Dat;
using Romd.Admin.Application.Titles.Enrichment;
using Romd.Admin.Application.Titles.ReadModels;
using Romd.Contracts.Management.Models;
using Romd.Domain.Catalog;

namespace Romd.Admin.Application.Titles.Commands.MergeTitles;

/// <summary>
///     Command to merge two titles. All games from source move to target,
///     metadata layers and media are merged, then source is deleted. <paramref name="TargetIfMatch" /> guards
///     the target; <paramref name="SourceRevision" /> is the source's batch revision.
/// </summary>
public sealed record MergeTitlesCommand(
    int SourceTitleId,
    int TargetTitleId,
    RevisionPrecondition TargetIfMatch,
    string SourceRevision
) : ICommand<TitleDetail>;

/// <summary>
///     Handler for <see cref="MergeTitlesCommand" />.
///     Merges source title into target, moving all games, merging metadata and media.
/// </summary>
public sealed class MergeTitlesCommandHandler : ICommandHandler<MergeTitlesCommand, TitleDetail>
{
    private readonly TimeProvider _clock;
    private readonly IReferenceCatalogService _referenceCatalog;
    private readonly ITitleSourceAssignmentStore _titleSourceAssignments;
    private readonly EnrichmentOptions _enrichmentOptions;
    private readonly ILibraryRepository _libraryRepository;
    private readonly ICatalogProjectionService _catalogProjection;
    private readonly ILogger<MergeTitlesCommandHandler> _logger;
    private readonly IRematerializationScheduler _rematerializationScheduler;
    private readonly ITitleAggregateReader _titleAggregates;
    private readonly ITitleMergeStore _mergeStore;
    private readonly ITitleDetailReader _titleDetails;
    private readonly ITrackedTitleRepository _trackedTitleRepository;
    private readonly IUnitOfWork _unitOfWork;

    public MergeTitlesCommandHandler(IReferenceCatalogService referenceCatalog,
        ITitleAggregateReader titleAggregates,
        ITitleMergeStore mergeStore,
        ITitleDetailReader titleDetails,
        ITrackedTitleRepository trackedTitleRepository,
        ITitleSourceAssignmentStore titleSourceAssignments,
        IUnitOfWork unitOfWork,
        IRematerializationScheduler rematerializationScheduler,
        ILibraryRepository libraryRepository,
        ICatalogProjectionService catalogProjection,
        IOptions<EnrichmentOptions> enrichmentOptions,
        ILogger<MergeTitlesCommandHandler> logger,
        TimeProvider clock)
    {
        _referenceCatalog = referenceCatalog;
        _titleAggregates = titleAggregates;
        _mergeStore = mergeStore;
        _titleDetails = titleDetails;
        _trackedTitleRepository = trackedTitleRepository;
        _titleSourceAssignments = titleSourceAssignments;
        _unitOfWork = unitOfWork;
        _rematerializationScheduler = rematerializationScheduler;
        _libraryRepository = libraryRepository;
        _catalogProjection = catalogProjection;
        _enrichmentOptions = enrichmentOptions.Value;
        _logger = logger;
        _clock = clock;
    }

    public async Task<ErrorOr<TitleDetail>> HandleAsync(MergeTitlesCommand command, CancellationToken ct = default)
    {
        var systemKeys = await _referenceCatalog.GetSystemKeysAsync(ct);
        // 1. Validate not merging into self
        if (command.SourceTitleId == command.TargetTitleId)
        {
            return CatalogErrors.MergeIntoSelf();
        }

        // 2. Load both titles before deciding whether either supplied revision is current.
        Domain.Catalog.Title? source;
        Domain.Catalog.Title? target;
        try
        {
            source = await _titleAggregates.GetAggregateAsync(command.SourceTitleId, ct);
            if (source is null)
                return CatalogErrors.TitleNotFound();
            target = await _titleAggregates.GetAggregateAsync(command.TargetTitleId, ct);
            if (target is null)
                return CatalogErrors.TitleNotFound();
        }
        catch (PersistenceConflictException)
        {
            ct.ThrowIfCancellationRequested();
            return CatalogErrors.TitleReadConflict();
        }

        // The target is the precondition's resource (412); the source is a batch revision (409).
        if (!command.TargetIfMatch.IsSatisfiedBy(target.Revision))
        {
            return CatalogErrors.TitlePreconditionFailed();
        }

        if (!RevisionPrecondition.For(command.SourceRevision).IsSatisfiedBy(source.Revision))
        {
            return CatalogErrors.MergeSourceChanged();
        }

        // 3. Validate same platform
        if (source.PlatformId != target.PlatformId)
        {
            return CatalogErrors.PlatformMismatch();
        }

        // 4. Absorb source into target (metadata, media, external IDs). A provider the curator unlinked on the
        // target stays unlinked, as automatic identity publication never links a suppressed provider either.
        var loadedTargetRevision = target.Revision;
        var unlinkedProviders = await _mergeStore.GetUnlinkedProvidersAsync(target.Id, ct);
        var createdAt = _clock.GetUtcNow();
        var layerTimes = new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase);
        // Unknown-provider precedence uses layer recency. Keep actual separate readings, including real ties.
        foreach (var layer in source.MetadataLayers)
            if (!layerTimes.ContainsKey(layer.SourceId))
                layerTimes.Add(layer.SourceId, _clock.GetUtcNow());
        target.Absorb(source, _enrichmentOptions.GetSourcePriorityOrder(),
            unlinkedProviders, new TitleAbsorptionTimes(createdAt, layerTimes));
        target.InheritEnrichmentStatus(source.EnrichmentStatus, source.LastEnrichedAt);

        ErrorOr<MergeOutcome> merged;
        try
        {
            merged = await MergeAsync(command, target, source, systemKeys, ct);
        }
        catch (PersistenceConflictException)
        {
            // One of the two snapshots changed after the checks above; report the one that did.
            Domain.Catalog.Title? current;
            try
            {
                current = await _titleAggregates.GetAggregateAsync(command.TargetTitleId, ct);
            }
            catch (PersistenceConflictException)
            {
                ct.ThrowIfCancellationRequested();
                return CatalogErrors.TitleReadConflict();
            }
            return current?.Revision == loadedTargetRevision
                ? CatalogErrors.MergeSourceChanged()
                : CatalogErrors.TitlePreconditionFailed();
        }

        if (merged.IsError)
        {
            return merged.Errors;
        }

        _logger.LogInformation(
            "Merged title '{SourceName}' (ID: {SourceId}) into '{TargetName}' (ID: {TargetId}). {GameCount} games moved.",
            source.Name, source.Id, target.Name, target.Id, merged.Value.MovedSourceEntries);

        // Best-effort acceleration only: durable Dirty state is the recovery contract. A rebuild
        // regroups releases, so refresh the committed response when it succeeds.
        return merged.Value.MovedSourceEntries > 0 && await TryRebuildCatalogAsync(target.PlatformId, ct)
            ? await RefreshCommittedDetailAsync(target.Id, merged.Value.Detail, systemKeys)
            : merged.Value.Detail;
    }

    private Task<ErrorOr<MergeOutcome>> MergeAsync(MergeTitlesCommand command, Domain.Catalog.Title target,
        Domain.Catalog.Title source, SystemKeys systemKeys, CancellationToken ct) =>
        _unitOfWork.ExecuteInTransactionAsync<MergeOutcome>(async token =>
        {
            // The staged write rejects either snapshot if it changed after loading, before
            // anything else is staged.
            await _mergeStore.ApplyMergeStagedAsync(target, source, token);

            // 5. Move all games from source to target
            var sourceEntryIds = await _titleSourceAssignments.GetSourceEntryIdsByTitleAsync(command.SourceTitleId, token);
            if (sourceEntryIds.Count > 0)
            {
                var assignments = sourceEntryIds
                    .Select(sourceEntryId => new TitleSourceAssignment(sourceEntryId, command.TargetTitleId))
                    .ToList();
                await _titleSourceAssignments.UpsertAssignmentsAsync(assignments, token);
            }

            // Retain artwork before deleting the source, then flush staged assignments, target
            // children and selections so the source deletion cannot cascade over them.
            await _mergeStore.StageArtworkMergeAsync(command.SourceTitleId, command.TargetTitleId, token);
            await _unitOfWork.FlushAsync(token);

            // 6. Consolidate tracked intent before deleting the source. Pins cannot cross title
            // boundaries, so a source-only pin is cleared while the target remains tracked.
            await _trackedTitleRepository.ConsolidateAsync(command.SourceTitleId, command.TargetTitleId, token);

            // Delete source (TitleSourceLinks already point to target, cascade won't affect them).
            // A merge is a deliberate consolidation: the source is hard-deleted and its curated state
            // was absorbed into the target above, so this stays a hard delete (not a UserOnly retain).
            await _mergeStore.DeleteAbsorbedSourceAsync(command.SourceTitleId, token);

            if (sourceEntryIds.Count > 0)
            {
                // Keep the mutation, invalidation marker, and affected-library flag atomic. A
                // committed Dirty platform is recoverable even if request-lifetime acceleration
                // below never runs.
                await _catalogProjection.MarkPlatformDirtyAsync(
                    target.PlatformId,
                    token,
                    [command.SourceTitleId, command.TargetTitleId]);
                await _libraryRepository.FlagForRematerializationByPlatformAsync(target.PlatformId, token);
            }

            await _rematerializationScheduler.EnqueueTitleAsync(target.Id, token);

            // Read the response before commit: a failed read rolls the merge back rather than
            // reporting a durable merge as failed.
            await _unitOfWork.FlushAsync(token);
            var detailData = await _titleDetails.GetDetailAsync(target.Id, token);
            return detailData is null
                ? CatalogErrors.TitleNotFound()
                : new MergeOutcome(TitleDetailMapper.ToContract(detailData, systemKeys), sourceEntryIds.Count);
        }, _logger, ct);

    /// <summary>
    ///     The merge is already durable. Caller cancellation must not turn the response into a false
    ///     failure, so the refresh is non-cancelable and falls back to the pre-commit read. A refresh at
    ///     another revision includes a writer that committed after the merge, so it falls back too: the
    ///     response and its ETag are the merge's own.
    /// </summary>
    private async Task<TitleDetail> RefreshCommittedDetailAsync(int targetId, TitleDetail committed,
        SystemKeys systemKeys)
    {
        try
        {
            var detailData = await _titleDetails.GetDetailAsync(targetId, CancellationToken.None);
            return detailData is not null && detailData.Revision.ToString() == committed.Revision
                ? TitleDetailMapper.ToContract(detailData, systemKeys)
                : committed;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Title merge committed but response refresh failed for target {TargetId}", targetId);
            return committed;
        }
    }

    private async Task<bool> TryRebuildCatalogAsync(int platformId, CancellationToken ct)
    {
        try
        {
            return await _catalogProjection.RebuildPlatformAsync(platformId, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Title merge committed but catalog rebuild acceleration failed for platform {PlatformId}; recovery will retry",
                platformId);
            return false;
        }
    }

    private sealed record MergeOutcome(TitleDetail Detail, int MovedSourceEntries);
}
