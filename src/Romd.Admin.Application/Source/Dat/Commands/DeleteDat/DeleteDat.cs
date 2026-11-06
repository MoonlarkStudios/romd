using ErrorOr;
using Microsoft.Extensions.Logging;
using Romd.Admin.Application.Catalog;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Common.Realtime;
using Romd.Application.Common.Cqrs;
using Romd.Admin.Application.Libraries;
using Romd.Admin.Application.Titles;

namespace Romd.Admin.Application.Source.Dat.Commands.DeleteDat;

/// <summary>
///     Command to delete a DAT file and all associated data.
/// </summary>
public sealed record DeleteDatCommand : ICommand
{
    /// <summary>
    ///     The ID of the DAT file to delete.
    /// </summary>
    public required int DatId { get; init; }

    private DeleteDatCommand() { }

    /// <summary>
    ///     Creates a validated command.
    /// </summary>
    public static ErrorOr<DeleteDatCommand> Create(int datId)
    {
        if (datId <= 0)
        {
            return Error.Validation("Command.InvalidDatId", "DAT ID must be positive");
        }

        return new DeleteDatCommand { DatId = datId };
    }
}

/// <summary>
///     Deletes the DAT (and its source anchor when this was the source's last version),
///     marks the platform's catalog projection Dirty, flags affected
///     libraries for rematerialization, and persists realtime intents in one commit.
///     The worker catalog projection recovery dispatcher rebuilds the Dirty platform;
///     library materialization is gated until the projection is Clean again. Orphaned
///     CAS blobs are reclaimed by the recurring unreferenced-file cleanup.
/// </summary>
public sealed class DeleteDatCommandHandler : ICommandHandler<DeleteDatCommand, Deleted>
{
    private readonly ILogger<DeleteDatCommandHandler> _logger;
    private readonly IDatRepository _repository;
    private readonly ISourceLifecycle _sourceLifecycle;
    private readonly ITitleOrphanCleanup _orphanCleanup;
    private readonly ICatalogProjectionService _catalogProjection;
    private readonly ILibraryRepository _libraryRepository;
    private readonly IAdminEventOutbox _outbox;
    private readonly IUnitOfWork _unitOfWork;

    public DeleteDatCommandHandler(
        IDatRepository repository,
        ISourceLifecycle sourceLifecycle,
        ITitleOrphanCleanup orphanCleanup,
        ICatalogProjectionService catalogProjection,
        ILibraryRepository libraryRepository,
        IAdminEventOutbox outbox,
        IUnitOfWork unitOfWork,
        ILogger<DeleteDatCommandHandler> logger)
    {
        _repository = repository;
        _sourceLifecycle = sourceLifecycle;
        _orphanCleanup = orphanCleanup;
        _catalogProjection = catalogProjection;
        _libraryRepository = libraryRepository;
        _outbox = outbox;
        _unitOfWork = unitOfWork;
        _logger = logger;
    }

    public async Task<ErrorOr<Deleted>> HandleAsync(DeleteDatCommand command, CancellationToken ct = default)
    {
        string datName;
        try
        {
            var existing = await _repository.GetByIdAsync(command.DatId, ct);
            if (existing is null)
            {
                return CatalogErrors.DatNotFound();
            }

            datName = existing.Name;
            int? platformId = existing.PlatformId;

            int catalogSourceId = await _repository.GetCatalogSourceIdAsync(existing.DatSourceId, ct);

            await _unitOfWork.ExecuteInTransactionAsync<Deleted>(async token =>
            {
                // Acquire the catalog-topology fence before reading the source's affected title
                // set. A concurrent derivation/move cannot add a link between this snapshot and the
                // cascade that consumes it.
                await _repository.AcquireMutationWriteLockAsync(command.DatId, token);
                // Titles backed through this source immediately before deletion are the exact
                // orphan/rollup candidates. Cascade deletes rows, not policy, so the same set must
                // drive deletion policy and projection invalidation in this transaction.
                var orphanCandidateTitleIds =
                    await _sourceLifecycle.GetLinkedTitleIdsAsync(catalogSourceId, token);
                await _catalogProjection.RefreshCatalogSourcePayloadAsync(catalogSourceId, token);
                await _sourceLifecycle.PreserveOwnedTitleIdentitiesAsync(catalogSourceId, token);
                await _repository.DeleteAsync(command.DatId, token);
                await _repository.DeleteSourceIfOrphanedAsync(existing.DatSourceId, token);
                await _catalogProjection.RefreshCatalogSourcePayloadAsync(catalogSourceId, token);

                var orphanedTitleIds = await _sourceLifecycle.GetOrphanedTitleIdsAsync(orphanCandidateTitleIds, token);
                int titlesDeleted = 0;
                int titlesRetained = 0;
                foreach (int titleId in orphanedTitleIds)
                {
                    bool deleted = await _orphanCleanup.DeleteOrRetainAsync(titleId, token);
                    if (deleted)
                    {
                        titlesDeleted++;
                    }
                    else
                    {
                        titlesRetained++;
                    }
                }

                if (orphanedTitleIds.Count > 0)
                {
                    _logger.LogInformation(
                        "Orphan policy after DAT delete (ID: {Id}): {Deleted} title(s) deleted, {Retained} retained as UserOnly",
                        command.DatId, titlesDeleted, titlesRetained);
                }

                if (platformId.HasValue)
                {
                    await _catalogProjection.MarkPlatformDirtyAsync(platformId.Value, ct, orphanCandidateTitleIds);
                    await _libraryRepository.FlagForRematerializationByPlatformAsync(platformId.Value, token);
                }

                await EnqueueStatsChangedAsync(token);
                return Result.Deleted;
            }, _logger, ct,
                (error, acknowledged) => _logger.LogWarning(error,
                    "DAT deletion transaction cleanup failed (ID: {Id}; commit acknowledged: {CommitAcknowledged})",
                    command.DatId, acknowledged));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete DAT (ID: {Id})", command.DatId);
            return CatalogErrors.DatabaseFailed(ex.Message);
        }

        _logger.LogInformation("Deleted DAT '{Name}' (ID: {Id})", datName, command.DatId);

        return Result.Deleted;
    }

    private async Task EnqueueStatsChangedAsync(CancellationToken ct)
    {
        await _outbox.EnqueueAsync(AdminRealtimeEventTypes.StorageStatsChanged, ct);
        await _outbox.EnqueueAsync(AdminRealtimeEventTypes.CoverageStatsChanged, ct);
        await _outbox.EnqueueAsync(AdminRealtimeEventTypes.HealthStatsChanged, ct);
    }
}
