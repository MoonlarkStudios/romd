using Romd.Application.Common.ReferenceCatalog;
using ErrorOr;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Romd.Admin.Application.Catalog;
using Romd.Application.Common.Cqrs;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Configuration;
using Romd.Admin.Application.Libraries;
using Romd.Admin.Application.Source.Dat;
using Romd.Admin.Application.Titles.Enrichment;
using Romd.Admin.Application.Titles.ReadModels;
using Romd.Contracts.Management.Models;

namespace Romd.Admin.Application.Titles.Commands.MergeTitles;

/// <summary>
///     Command to merge two titles. All games from source move to target,
///     metadata layers and media are merged, then source is deleted.
/// </summary>
public sealed record MergeTitlesCommand(
    int SourceTitleId,
    int TargetTitleId
) : ICommand<TitleDetail>;

/// <summary>
///     Handler for <see cref="MergeTitlesCommand" />.
///     Merges source title into target, moving all games, merging metadata and media.
/// </summary>
public sealed class MergeTitlesCommandHandler : ICommandHandler<MergeTitlesCommand, TitleDetail>
{
    private readonly IReferenceCatalogService _referenceCatalog;
    private readonly ITitleSourceAssignmentStore _titleSourceAssignments;
    private readonly EnrichmentOptions _enrichmentOptions;
    private readonly ILibraryRepository _libraryRepository;
    private readonly ICatalogProjectionService _catalogProjection;
    private readonly ILogger<MergeTitlesCommandHandler> _logger;
    private readonly IRematerializationScheduler _rematerializationScheduler;
    private readonly ITitleRepository _titleRepository;
    private readonly ITrackedTitleRepository _trackedTitleRepository;
    private readonly IUnitOfWork _unitOfWork;

    public MergeTitlesCommandHandler(IReferenceCatalogService referenceCatalog,
        ITitleRepository titleRepository,
        ITrackedTitleRepository trackedTitleRepository,
        ITitleSourceAssignmentStore titleSourceAssignments,
        IUnitOfWork unitOfWork,
        IRematerializationScheduler rematerializationScheduler,
        ILibraryRepository libraryRepository,
        ICatalogProjectionService catalogProjection,
        IOptions<EnrichmentOptions> enrichmentOptions,
        ILogger<MergeTitlesCommandHandler> logger)
    {
        _referenceCatalog = referenceCatalog;
        _titleRepository = titleRepository;
        _trackedTitleRepository = trackedTitleRepository;
        _titleSourceAssignments = titleSourceAssignments;
        _unitOfWork = unitOfWork;
        _rematerializationScheduler = rematerializationScheduler;
        _libraryRepository = libraryRepository;
        _catalogProjection = catalogProjection;
        _enrichmentOptions = enrichmentOptions.Value;
        _logger = logger;
    }

    public async Task<ErrorOr<TitleDetail>> HandleAsync(MergeTitlesCommand command, CancellationToken ct = default)
    {
        var systemKeys = await _referenceCatalog.GetSystemKeysAsync(ct);
        // 1. Validate not merging into self
        if (command.SourceTitleId == command.TargetTitleId)
        {
            return CatalogErrors.MergeIntoSelf();
        }

        // 2. Load both titles with all collections
        var source = await _titleRepository.GetForMergeAsync(command.SourceTitleId, ct);
        if (source is null)
        {
            return CatalogErrors.TitleNotFound(command.SourceTitleId);
        }

        var target = await _titleRepository.GetForMergeAsync(command.TargetTitleId, ct);
        if (target is null)
        {
            return CatalogErrors.TitleNotFound(command.TargetTitleId);
        }

        // 3. Validate same platform
        if (source.PlatformId != target.PlatformId)
        {
            return CatalogErrors.PlatformMismatch(source.PlatformId, target.PlatformId);
        }

        bool commitSucceeded = false;
        IReadOnlyList<int> sourceEntryIds;
        try
        {
            await using var transaction = await _unitOfWork.BeginTransactionAsync(ct);

            // 4. Move all games from source to target
            sourceEntryIds = await _titleSourceAssignments.GetSourceEntryIdsByTitleAsync(command.SourceTitleId, ct);
            if (sourceEntryIds.Count > 0)
            {
                var assignments = sourceEntryIds
                    .Select(sourceEntryId => new TitleSourceAssignment(sourceEntryId, command.TargetTitleId))
                    .ToList();
                await _titleSourceAssignments.UpsertAssignmentsAsync(assignments, ct);
            }

            // 5. Absorb source into target (metadata, media, external IDs)
            var sourcePriority = _enrichmentOptions.GetSourcePriorityOrder();
            target.Absorb(source, sourcePriority);
            target.InheritEnrichmentStatus(source.EnrichmentStatus, source.LastEnrichedAt);

            // Retain artwork before deleting the source. The target update flushes staged selections.
            await _titleRepository.StageArtworkMergeAsync(command.SourceTitleId, command.TargetTitleId, ct);

            // 6. Save target
            await _titleRepository.UpdateAsync(target, ct);

            // 7. Consolidate tracked intent before deleting the source. Pins cannot cross title
            // boundaries, so a source-only pin is cleared while the target remains tracked.
            await _trackedTitleRepository.ConsolidateAsync(command.SourceTitleId, command.TargetTitleId, ct);

            // Delete source (TitleSourceLinks already point to target, cascade won't affect them).
            // A merge is a deliberate consolidation: the source is hard-deleted and its curated state
            // was absorbed into the target above, so this stays a hard delete (not a UserOnly retain).
            await _titleRepository.DeleteAsync(command.SourceTitleId, ct);

            if (sourceEntryIds.Count > 0)
            {
                // Keep the mutation, invalidation marker, and affected-library flag atomic. A
                // committed Dirty platform is recoverable even if request-lifetime acceleration
                // below never runs.
                await _catalogProjection.MarkPlatformDirtyAsync(
                    target.PlatformId,
                    ct,
                    [command.SourceTitleId, command.TargetTitleId]);
                await _libraryRepository.FlagForRematerializationByPlatformAsync(target.PlatformId, ct);
            }

            await _rematerializationScheduler.EnqueueTitleAsync(target.Id, ct);
            await transaction.CommitAsync(ct);
            commitSucceeded = true;

            // Best-effort acceleration only: durable Dirty state is the recovery contract.
            if (sourceEntryIds.Count > 0)
            {
                await TryRebuildCatalogAsync(target.PlatformId, ct);
            }

            _logger.LogInformation(
                "Merged title '{SourceName}' (ID: {SourceId}) into '{TargetName}' (ID: {TargetId}). {GameCount} games moved.",
                source.Name, source.Id, target.Name, target.Id, sourceEntryIds.Count);

            // The mutation is already durable. A caller cancellation after commit must not turn
            // the response into a false failure, so the final reload is non-cancelable.
            return await LoadCommittedDetailAsync(command.TargetTitleId, target.Id);
        }
        catch (Exception ex) when (commitSucceeded)
        {
            _logger.LogWarning(
                ex,
                "Title merge committed but transaction cleanup failed for source {SourceId} and target {TargetId}",
                command.SourceTitleId,
                command.TargetTitleId);
            return await LoadCommittedDetailAsync(command.TargetTitleId, target.Id);
        }
    }

    private async Task<ErrorOr<TitleDetail>> LoadCommittedDetailAsync(int requestedTargetId, int targetId)
    {
        var detailData = await _titleRepository.GetTitleDetailAsync(targetId, CancellationToken.None);
        return detailData is null
            ? CatalogErrors.TitleNotFound(requestedTargetId)
            : TitleDetailMapper.ToContract(detailData, await _referenceCatalog.GetSystemKeysAsync(CancellationToken.None));
    }

    private async Task TryRebuildCatalogAsync(int platformId, CancellationToken ct)
    {
        try
        {
            await _catalogProjection.RebuildPlatformAsync(platformId, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Title merge committed but catalog rebuild acceleration failed for platform {PlatformId}; recovery will retry",
                platformId);
        }
    }
}
