using Microsoft.Extensions.Logging;
using Romd.Admin.Application.Catalog;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Common.Realtime;
using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Admin.Application.Libraries;
using Romd.Application.Common.Ids;
using Romd.Contracts.Management.Realtime;
using Romd.Domain.Libraries;
using Romd.Domain.Jobs;

namespace Romd.Infrastructure.Libraries;

public sealed class LibraryMaterializationService(
    ILibraryRepository libraryRepo,
    IMaterializationDataProvider dataProvider,
    ICatalogProjectionService catalogProjection,
    IAdminEventOutbox realtimeOutbox,
    IUnitOfWork unitOfWork,
    ILogger<LibraryMaterializationService> logger,
    IMaterializationJobRepository? jobs = null,
    TimeProvider? clock = null) : ILibraryMaterializationService
{
    public Task<MaterializationResult> MaterializeAsync(int libraryId, CancellationToken ct = default) =>
        MaterializeAsync(libraryId, null, ct);

    public async Task PublishForJobAsync(MaterializationJob job, CancellationToken ct = default)
    {
        await MaterializeAsync(job.LibraryId, job, ct);
    }

    private async Task<MaterializationResult> MaterializeAsync(int libraryId, MaterializationJob? job, CancellationToken ct)
    {
        var library = await libraryRepo.GetByIdAsync(libraryId, ct);
        if (library is null)
        {
            if (job is not null)
                throw new InvalidOperationException($"Library {libraryId} not found");
            logger.LogWarning("Library {LibraryId} not found for materialization", libraryId);
            return new MaterializationResult(0, 0, 0);
        }

        logger.LogInformation("Materializing library '{Name}' (ID: {Id})", library.Name, library.Id);

        if (job?.ReviewedRevision is { } reviewed && library.MaterializationRevision != reviewed)
            return await SettleAsync(library, job, PublicationOutcome.InputsChanged, null, MaterializationResult.Deferred(), ct);
        if (job is not null && !library.NeedsRefresh && library.HasValidConfiguration)
            return await SettleAsync(library, job, PublicationOutcome.AlreadyPublished, null, MaterializationResult.Deferred(), ct);

        if (!library.HasValidConfiguration)
        {
            logger.LogWarning(
                "Skipping materialization for library '{Name}' (ID: {Id}) because its configuration is {State}: {Error}",
                library.Name,
                library.Id,
                library.ConfigurationState,
                library.ConfigurationError);

            return await SettleAsync(library, job, PublicationOutcome.InvalidConfiguration, null, new MaterializationResult(0, 0, 0), ct);
        }

        var platformIdsNeedingRebuild = await catalogProjection.GetPlatformIdsNeedingRebuildAsync(ct);
        var blocked = platformIdsNeedingRebuild.Count != 0 &&
            (library.Configuration.TitleSelectionMode == LibraryTitleSelectionMode.IncludeOnly
                ? (await libraryRepo.GetHandpickedPlatformIdsAsync(library.Configuration.IncludeTitleIds, ct)).Any(platformIdsNeedingRebuild.Contains)
                : MaterializationCatalogGate.IsBlocked(library.Configuration, platformIdsNeedingRebuild));
        if (blocked)
        {
            logger.LogInformation(
                "Deferring materialization for library '{Name}' (ID: {Id}) — catalog projection rebuild pending " +
                "for platform(s) [{PlatformIds}]; refresh the library again once the catalog is clean",
                library.Name,
                library.Id,
                string.Join(", ", platformIdsNeedingRebuild));
            return await SettleAsync(library, job, PublicationOutcome.CatalogRebuilding, null, MaterializationResult.Deferred(), ct);
        }

        var candidates = await dataProvider.GetCandidatesAsync(library.Configuration, ct);
        var projection = MaterializedLibraryProjectionBuilder.Build(
            library.Id,
            candidates,
            library.Configuration);

        int includedReleaseCount = projection.Releases.Count(release => release.IsExposed);
        int includedTitleCount = projection.Titles.Count;
        int excludedTitleCount = candidates.Count - includedTitleCount;

        var result = await SettleAsync(library, job, PublicationOutcome.Published, projection,
            new MaterializationResult(includedReleaseCount, excludedTitleCount, candidates.Count), ct);
        if (result.Outcome == MaterializationOutcome.Materialized)
        {
            logger.LogInformation(
                "Materialized library '{Name}' (ID: {Id}) with {IncludedReleaseCount} releases included and {ExcludedTitleCount} of {TotalTitleCount} titles excluded",
                library.Name, library.Id, includedReleaseCount, excludedTitleCount, candidates.Count);
        }
        return result;
    }

    private async Task<MaterializationResult> SettleAsync(Library library, MaterializationJob? job, PublicationOutcome outcome,
        MaterializedLibraryProjection? projection, MaterializationResult result, CancellationToken ct)
    {
        if (job is null && projection is null && outcome != PublicationOutcome.InvalidConfiguration)
            return result;

        var transaction = await unitOfWork.BeginTransactionAsync(ct);
        IStagedLibraryPublication? publication = null;
        Exception? publicationFailure = null;
        bool committed = false;
        try
        {
            if (projection is not null)
            {
                // Use the revision captured before building the projection to reject intervening changes.
                publication = await libraryRepo.StageMaterializedProjectionsAndActivateAsync(
                    library.Id, projection, result.IncludedReleaseCount, library.MaterializationRevision, ct);
                if (!publication.Applied)
                {
                    logger.LogInformation(
                        "Library '{Name}' (ID: {Id}) was changed during materialization; leaving it flagged for another rebuild",
                        library.Name,
                        library.Id);
                    outcome = PublicationOutcome.InputsChanged;
                    result = MaterializationResult.Deferred();
                }

                await EnqueueLibraryUpdatedAsync(library.Id, ct);
            }
            else if (outcome == PublicationOutcome.InvalidConfiguration)
            {
                var withdrawn = await libraryRepo.StageMaterializedProjectionsAndMarkConfigurationInvalidAsync(
                    library.Id, library.ConfigurationError, library.MaterializationRevision, ct);
                if (withdrawn)
                    await EnqueueLibraryUpdatedAsync(library.Id, ct);
                else
                    outcome = PublicationOutcome.InputsChanged;
            }

            await StageJobOutcomeAsync(job, outcome, outcome == PublicationOutcome.Published ? library.MaterializationGeneration + 1 : null,
                result.TotalTitleCount, result.ExcludedTitleCount, ct);
            await unitOfWork.FlushAsync(ct);
            await transaction.CommitAsync(ct);
            committed = true;
            return result;
        }
        catch (Exception failure)
        {
            publicationFailure = failure;
            await RollbackPreservingFailureAsync(transaction, library.Id);
            throw;
        }
        finally
        {
            try
            {
                publication?.Dispose();
            }
            catch (Exception cleanupFailure) when (publicationFailure is not null || committed)
            {
                // Cleanup cannot change a committed publication or replace its original failure.
                logger.LogWarning(cleanupFailure, "Library {LibraryId} publication tracking cleanup failed", library.Id);
            }
            finally { await DisposeTransactionAsync(transaction, library.Id, publicationFailure, committed); }
        }
    }

    private async Task StageJobOutcomeAsync(MaterializationJob? job, PublicationOutcome outcome, long? generation, int total, int excluded,
        CancellationToken ct)
    {
        if (job is null)
            return;
        // The unit of work already holds the job's execution row. Stage from a fresh instance
        // so a transaction that rolls back never updates the runner's in-memory lifecycle.
        if (JobExecutionFenceScope.Current?.JobId != job.Id || jobs is null)
            throw new JobExecutionOwnershipLostException();
        var persisted = await jobs.GetByIdAsync(job.Id, ct) ?? throw new JobExecutionOwnershipLostException();
        persisted.CompletePublication(outcome, generation, total, excluded, (clock ?? TimeProvider.System).GetUtcNow());
        await jobs.UpdateStagedAsync(persisted, ct);
    }

    public async Task FlagAffectedLibrariesAsync(int? platformId, CancellationToken ct = default)
    {
        if (platformId is null)
        {
            await libraryRepo.FlagAllForRematerializationAsync(ct);
            return;
        }

        await libraryRepo.FlagForRematerializationByPlatformAsync(platformId.Value, ct);
    }

    private async Task RollbackPreservingFailureAsync(ITransaction transaction, int libraryId)
    {
        try
        { await transaction.RollbackAsync(CancellationToken.None); }
        catch (Exception cleanupFailure)
        { logger.LogWarning(cleanupFailure, "Library {LibraryId} rollback failed after publication failed", libraryId); }
    }

    private async Task DisposeTransactionAsync(ITransaction transaction, int libraryId, Exception? failure, bool committed)
    {
        try
        { await transaction.DisposeAsync(); }
        catch (Exception cleanupFailure) when (failure is not null || committed)
        { logger.LogWarning(cleanupFailure, "Library {LibraryId} transaction cleanup failed after its outcome settled", libraryId); }
    }

    private async Task EnqueueLibraryUpdatedAsync(int libraryId, CancellationToken ct)
    {
        var library = await libraryRepo.GetByIdAsync(libraryId, ct);
        if (library is null)
            return;

        await realtimeOutbox.EnqueueAsync(
            AdminRealtimeEventTypes.LibraryUpdated,
            new AdminRealtimeLibraryUpdatedPayload(
                IdCoder.Encode(library.Id),
                library.Name,
                library.NeedsMaterialization,
                library.ItemCount,
                library.ConfigurationState.ToString()),
            ct);
    }
}
