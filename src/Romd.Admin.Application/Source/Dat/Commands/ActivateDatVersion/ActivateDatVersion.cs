using ErrorOr;
using Microsoft.Extensions.Logging;
using Romd.Admin.Application.Catalog;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Common.Realtime;
using Romd.Admin.Application.Libraries;
using Romd.Application.Common.Cqrs;
using Romd.Domain.Source.Dat;

namespace Romd.Admin.Application.Source.Dat.Commands.ActivateDatVersion;

/// <summary>
///     Command to activate a pending replacement DAT version, superseding the source's
///     current version. This is the single atomic transition point of the replace saga.
/// </summary>
public sealed record ActivateDatVersionCommand : ICommand<DatFile>
{
    /// <summary>
    ///     The ID of the PendingActivation DAT version to activate.
    /// </summary>
    public required int NewDatId { get; init; }

    /// <summary>The immutable DAT version reviewed before this replacement was accepted.</summary>
    public int? ExpectedActiveDatId { get; init; }

    private ActivateDatVersionCommand() { }

    /// <summary>
    ///     Creates a validated command.
    /// </summary>
    public static ErrorOr<ActivateDatVersionCommand> Create(int newDatId, int? expectedActiveDatId = null)
    {
        if (newDatId <= 0 || expectedActiveDatId is <= 0)
        {
            return Error.Validation("Command.InvalidDatId", "DAT ID must be positive");
        }

        return new ActivateDatVersionCommand { NewDatId = newDatId, ExpectedActiveDatId = expectedActiveDatId };
    }
}

/// <summary>
///     Activates the pending version and supersedes the source's current version in ONE
///     commit: lifecycle transitions, N=1 superseded-version retention, deletion of the prior
///     version's parsed graph, Dirty projection marking, library rematerialization flags, and
///     stats outbox events. The partial unique Active index makes double-activation impossible at
///     the database; a redelivery that observes an already-active version converges as a
///     no-op, and a loser converges or reports an opaque failure. Downstream recovery is the
///     worker catalog projection recovery dispatcher plus the materialization gate.
/// </summary>
public sealed class ActivateDatVersionCommandHandler : ICommandHandler<ActivateDatVersionCommand, DatFile>
{
    private readonly IDatRepository _repository;
    private readonly ICatalogProjectionService _catalogProjection;
    private readonly ILibraryRepository _libraryRepository;
    private readonly IAdminEventOutbox _outbox;
    private readonly IUnitOfWork _unitOfWork;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ActivateDatVersionCommandHandler> _logger;
    private readonly ISourceLifecycle _sourceLifecycle;

    public ActivateDatVersionCommandHandler(
        IDatRepository repository,
        ICatalogProjectionService catalogProjection,
        ILibraryRepository libraryRepository,
        IAdminEventOutbox outbox,
        IUnitOfWork unitOfWork,
        TimeProvider timeProvider,
        ILogger<ActivateDatVersionCommandHandler> logger,
        ISourceLifecycle sourceLifecycle)
    {
        _repository = repository;
        _catalogProjection = catalogProjection;
        _libraryRepository = libraryRepository;
        _outbox = outbox;
        _unitOfWork = unitOfWork;
        _timeProvider = timeProvider;
        _logger = logger;
        _sourceLifecycle = sourceLifecycle;
    }

    public async Task<ErrorOr<DatFile>> HandleAsync(
        ActivateDatVersionCommand command,
        CancellationToken ct = default)
    {
        DatFile? newVersion = null;
        DatFile? oldVersion = null;
        bool commitSucceeded = false;
        try
        {
            newVersion = await _repository.GetByIdAsync(command.NewDatId, ct);
            if (newVersion is null)
            {
                return CatalogErrors.DatNotFound(command.NewDatId);
            }

            if (newVersion.Lifecycle != DatFileLifecycle.PendingActivation)
            {
                // Redelivery after a committed activation: converge without re-running it.
                return newVersion.Lifecycle == DatFileLifecycle.Active
                    ? newVersion
                    : Error.Conflict("DatReview.Stale", "This candidate is no longer awaiting activation.");
            }

            await using var transaction = await _unitOfWork.BeginTransactionAsync(ct);
            await _repository.AcquireMutationWriteLockAsync(newVersion.Id, ct);
            newVersion = await _repository.GetByIdAsync(command.NewDatId, ct);
            if (newVersion is null) return CatalogErrors.DatNotFound(command.NewDatId);
            if (newVersion.Lifecycle == DatFileLifecycle.Active) return newVersion;
            if (newVersion.Lifecycle != DatFileLifecycle.PendingActivation)
                return Error.Conflict("DatReview.Stale", "This candidate is no longer awaiting activation.");
            oldVersion = await _repository.GetActiveBySourceIdAsync(newVersion.DatSourceId, ct);
            if (command.ExpectedActiveDatId is int expected && oldVersion?.Id != expected)
                return Error.Conflict("DatReview.Stale", "The active catalog changed. Preview the update again before applying.");
            // Retention is source-wide, so its claim pruning can affect historical platforms
            // beyond the new/current pair. All routed versions are a lifecycle-independent
            // superset of the retention candidates. Read under the topology fence so the
            // baseline and the affected platforms stay stable through activation.
            var routedSourcePlatformIds = await _repository.GetRoutedPlatformIdsBySourceIdAsync(
                newVersion.DatSourceId,
                ct);
            int catalogSourceId = await _repository.GetCatalogSourceIdAsync(newVersion.DatSourceId, ct);

            IReadOnlyList<int> formerlyLinkedTitleIds =
                await _sourceLifecycle.GetLinkedTitleIdsAsync(catalogSourceId, ct);

            // IX_DatFiles_Active_SourceId is checked per statement, so the old version's
            // Superseded update must flush before the new version's Active update. EF batches
            // same-table updates in primary-key order, and the pending version's id is always
            // greater than the old Active's (inserted later), so this ordering holds; if it
            // ever broke, the commit would fail loudly at the index — never two Actives.
            newVersion.Activate();
            await _repository.UpdateLifecycleStagedAsync(newVersion, ct);

            if (oldVersion is not null)
            {
                oldVersion.Supersede(_timeProvider.GetUtcNow());
                await _repository.UpdateLifecycleStagedAsync(oldVersion, ct);
                await _repository.DeleteGamesByDatFileIdAsync(oldVersion.Id, ct);
            }

            // Persist the lifecycle flips before the retention DELETE selects its newest
            // Superseded winner. Both remain inside this transaction, so any retention failure
            // rolls activation, graph deletion, invalidation, and outbox effects back together.
            await _unitOfWork.FlushAsync(ct);
            int deletedVersionCount = await _repository.DeleteSupersededVersionsBeyondMostRecentAsync(
                newVersion.DatSourceId,
                ct);
            await _catalogProjection.RefreshCatalogSourcePayloadAsync(catalogSourceId, ct);

            foreach (int platformId in AffectedPlatformIds(
                         newVersion,
                         oldVersion,
                         routedSourcePlatformIds,
                         deletedVersionCount > 0))
            {
                await _catalogProjection.MarkPlatformDirtyAsync(platformId, ct, formerlyLinkedTitleIds);
                await _libraryRepository.FlagForRematerializationByPlatformAsync(platformId, ct);
            }

            await EnqueueStatsChangedAsync(ct);
            await transaction.CommitAsync(ct);
            commitSucceeded = true;
        }
        catch (Exception ex) when (commitSucceeded)
        {
            _logger.LogWarning(
                ex,
                "DAT activation committed but transaction cleanup failed (ID: {Id})",
                command.NewDatId);
            return newVersion!;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            var converged = await TryGetConvergedAsync(command.NewDatId);
            if (converged is not null)
            {
                _logger.LogWarning(
                    ex,
                    "DAT activation lost a concurrent race but converged on active version {Id}",
                    converged.Id);
                return converged;
            }

            _logger.LogError(ex, "Failed to activate DAT version (ID: {Id})", command.NewDatId);
            return CatalogErrors.DatabaseFailed(ex.Message);
        }

        _logger.LogInformation(
            "Activated DAT version {NewId} for source {SourceId}, superseding version {OldId}",
            newVersion.Id,
            newVersion.DatSourceId,
            oldVersion?.Id);

        return newVersion;
    }

    private static IEnumerable<int> AffectedPlatformIds(
        DatFile newVersion,
        DatFile? oldVersion,
        IReadOnlyList<int> routedSourcePlatformIds,
        bool retentionPrunedClaimGraph) =>
        new[] { newVersion.PlatformId, oldVersion?.PlatformId }
            .Where(id => id.HasValue)
            .Select(id => id!.Value)
            .Concat(retentionPrunedClaimGraph ? routedSourcePlatformIds : Array.Empty<int>())
            .Distinct();

    private async Task<DatFile?> TryGetConvergedAsync(int newDatId)
    {
        try
        {
            var version = await _repository.GetByIdAsync(newDatId, CancellationToken.None);
            return version is { Lifecycle: DatFileLifecycle.Active } ? version : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private async Task EnqueueStatsChangedAsync(CancellationToken ct)
    {
        await _outbox.EnqueueAsync(AdminRealtimeEventTypes.StorageStatsChanged, ct);
        await _outbox.EnqueueAsync(AdminRealtimeEventTypes.CoverageStatsChanged, ct);
        await _outbox.EnqueueAsync(AdminRealtimeEventTypes.HealthStatsChanged, ct);
    }
}
