using ErrorOr;
using Microsoft.Extensions.Logging;
using Romd.Admin.Application.Catalog;
using Romd.Admin.Application.Catalog.Sources;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Common.Realtime;
using Romd.Admin.Application.Libraries;
using Romd.Admin.Application.Titles;
using Romd.Application.Common.Cqrs;
using Romd.Domain.Catalog;

namespace Romd.Admin.Application.Source.Dat.Commands.ApplyDatSourceLifecycle;

public sealed record ApplyDatSourceLifecycleCommand(int DatId, string Action, string ReviewToken) : ICommand<Success>;

/// <summary>Applies a reviewed source-wide lifecycle change with its retention, invalidation and durable intents.</summary>
public sealed class ApplyDatSourceLifecycleCommandHandler(IDatRepository dats, IDatSourceLifecycleStore source,
    ISourceLifecycle lifecycle, ITitleOrphanCleanup orphanCleanup, ICatalogProjectionService projection,
    ILibraryRepository libraries, IAdminEventOutbox outbox, IUnitOfWork unitOfWork,
    ILogger<ApplyDatSourceLifecycleCommandHandler> logger) : ICommandHandler<ApplyDatSourceLifecycleCommand, Success>
{
    public async Task<ErrorOr<Success>> HandleAsync(ApplyDatSourceLifecycleCommand command, CancellationToken ct = default)
    {
        if (!DatSourceLifecycleIntent.IsValid(command.Action))
            return DatSourceLifecycleIntent.InvalidAction();
        return await unitOfWork.ExecuteInTransactionAsync<Success>(async token =>
        {
            var dat = await dats.GetByIdAsync(command.DatId, token);
            if (dat is null)
                return DatSourceLifecycleIntent.Missing();
            // Include exact-document initial imports before taking their shared source/topology fences.
            foreach (var catalog in await source.ReadRelatedCatalogIdsAsync(dat.DatSourceId, token))
                if (!await source.TryLockCatalogAsync(catalog, token))
                    return DatSourceLifecycleIntent.Busy();
            if (!await source.TryLockSourceAsync(dat.DatSourceId, token))
                return DatSourceLifecycleIntent.Busy();
            await dats.AcquireMutationWriteLockAsync(command.DatId, token);
            dat = await dats.GetByIdAsync(command.DatId, token);
            if (dat is null)
                return DatSourceLifecycleIntent.Missing();
            await source.LockAffectedTitlesAsync(dat.DatSourceId, token);
            var impact = await source.ReadImpactAsync(command.DatId, dat.DatSourceId, dat.Name, command.Action, token);
            if (impact.Busy)
                return DatSourceLifecycleIntent.Busy();
            if (!string.Equals(command.ReviewToken, impact.ReviewToken, StringComparison.Ordinal))
                return DatSourceLifecycleIntent.Stale();

            var catalogSourceId = await dats.GetCatalogSourceIdAsync(dat.DatSourceId, token);
            var affected = await lifecycle.GetLinkedTitleIdsAsync(catalogSourceId, token);
            var platforms = await dats.GetRoutedPlatformIdsBySourceIdAsync(dat.DatSourceId, token);
            await projection.RefreshCatalogSourcePayloadAsync(catalogSourceId, token);
            await lifecycle.PreserveOwnedTitleIdentitiesAsync(catalogSourceId, token);
            // Binding precedes deletion/status so scheduled checks immediately observe the source's lifecycle.
            await source.BindRelatedSubscriptionsStagedAsync(dat.DatSourceId, token);
            if (command.Action == "Delete")
            {
                foreach (var version in await source.ReadVersionIdsAsync(dat.DatSourceId, token))
                    await dats.DeleteAsync(version, token);
                await dats.DeleteSourceIfOrphanedAsync(dat.DatSourceId, token);
                foreach (var id in await lifecycle.GetOrphanedTitleIdsAsync(affected, token))
                    await orphanCleanup.DeleteOrRetainAsync(id, token);
            }
            else
                await lifecycle.SetStatusAsync(catalogSourceId, Enum.Parse<CatalogSourceStatus>(command.Action), token);
            foreach (var platform in platforms)
            {
                await projection.MarkPlatformDirtyAsync(platform, token, affected);
                await libraries.FlagForRematerializationByPlatformAsync(platform, token);
            }
            await outbox.EnqueueAsync(AdminRealtimeEventTypes.CoverageStatsChanged, token);
            await outbox.EnqueueAsync(AdminRealtimeEventTypes.HealthStatsChanged, token);
            await outbox.EnqueueAsync(AdminRealtimeEventTypes.StorageStatsChanged, token);
            return Result.Success;
        }, logger, ct);
    }
}
