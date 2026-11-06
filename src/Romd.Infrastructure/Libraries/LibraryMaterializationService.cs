using Microsoft.Extensions.Logging;
using Romd.Admin.Application.Catalog;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Common.Realtime;
using Romd.Admin.Application.Libraries;
using Romd.Application.Common.Ids;
using Romd.Contracts.Management.Realtime;
using Romd.Domain.Libraries;

namespace Romd.Infrastructure.Libraries;

public sealed class LibraryMaterializationService(
    ILibraryRepository libraryRepo,
    IMaterializationDataProvider dataProvider,
    ICatalogProjectionService catalogProjection,
    IAdminEventOutbox realtimeOutbox,
    IUnitOfWork unitOfWork,
    ILogger<LibraryMaterializationService> logger) : ILibraryMaterializationService
{
    public async Task<MaterializationResult> MaterializeAsync(int libraryId, CancellationToken ct = default)
    {
        var library = await libraryRepo.GetByIdAsync(libraryId, ct);
        if (library is null)
        {
            logger.LogWarning("Library {LibraryId} not found for materialization", libraryId);
            return new MaterializationResult(0, 0, 0);
        }

        // Capture the revision before reading candidates so intervening changes reject this rebuild.
        var materializationToken = library.MaterializationRevision;
        logger.LogInformation("Materializing library '{Name}' (ID: {Id})", library.Name, library.Id);

        if (!library.HasValidConfiguration)
        {
            logger.LogWarning(
                "Skipping materialization for library '{Name}' (ID: {Id}) because its configuration is {State}: {Error}",
                library.Name,
                library.Id,
                library.ConfigurationState,
                library.ConfigurationError);

            var transaction = await unitOfWork.BeginTransactionAsync(ct);
            Exception? failure = null;
            bool committed = false;
            try
            {
                await libraryRepo.StageMaterializedProjectionsAndMarkConfigurationInvalidAsync(
                    library.Id,
                    library.ConfigurationError,
                    materializationToken,
                    ct);
                await EnqueueLibraryUpdatedAsync(library.Id, ct);
                await unitOfWork.FlushAsync(ct);
                await transaction.CommitAsync(ct);
                committed = true;
            }
            catch (Exception error)
            {
                failure = error;
                await RollbackPreservingFailureAsync(transaction, library.Id);
                throw;
            }
            finally { await DisposeTransactionAsync(transaction, library.Id, failure, committed); }

            return new MaterializationResult(0, 0, 0);
        }

        var platformIdsNeedingRebuild = await catalogProjection.GetPlatformIdsNeedingRebuildAsync(ct);
        if (MaterializationCatalogGate.IsBlocked(library.Configuration, platformIdsNeedingRebuild))
        {
            logger.LogInformation(
                "Deferring materialization for library '{Name}' (ID: {Id}) — catalog projection rebuild pending " +
                "for platform(s) [{PlatformIds}]; refresh the library again once the catalog is clean",
                library.Name,
                library.Id,
                string.Join(", ", platformIdsNeedingRebuild));
            return MaterializationResult.Deferred();
        }

        var candidates = await dataProvider.GetCandidatesAsync(library.Configuration, ct);
        var projection = MaterializedLibraryProjectionBuilder.Build(
            library.Id,
            candidates,
            library.Configuration);

        int includedReleaseCount = projection.Releases.Count(release => release.IsExposed);
        int includedTitleCount = projection.Titles.Count;
        int excludedTitleCount = candidates.Count - includedTitleCount;

        bool activated;
        {
            var transaction = await unitOfWork.BeginTransactionAsync(ct);
            IStagedLibraryPublication? publication = null;
            Exception? publicationFailure = null;
            bool committed = false;
            try
            {
                publication = await libraryRepo.StageMaterializedProjectionsAndActivateAsync(
                    library.Id,
                    projection,
                    includedReleaseCount,
                    materializationToken,
                    ct);
                activated = publication.Applied;
                if (!activated)
                {
                    logger.LogInformation(
                        "Library '{Name}' (ID: {Id}) was changed during materialization; leaving it flagged for another rebuild",
                        library.Name,
                        library.Id);
                }

                await EnqueueLibraryUpdatedAsync(library.Id, ct);
                await unitOfWork.FlushAsync(ct);
                await transaction.CommitAsync(ct);
                committed = true;
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

        if (!activated)
            return MaterializationResult.Deferred();

        logger.LogInformation(
            "Materialized library '{Name}': {IncludedReleaseCount} releases included; {ExcludedTitleCount} titles excluded from {TotalTitleCount} total titles",
            library.Name,
            includedReleaseCount,
            excludedTitleCount,
            candidates.Count);

        return new MaterializationResult(includedReleaseCount, excludedTitleCount, candidates.Count);
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
