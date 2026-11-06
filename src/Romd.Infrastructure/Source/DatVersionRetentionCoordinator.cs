using Romd.Admin.Application.Catalog;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Libraries;
using Romd.Admin.Application.Source.Dat;

namespace Romd.Infrastructure.Source;

public sealed class DatVersionRetentionCoordinator(
    IDatRepository repository,
    IUnitOfWork unitOfWork,
    ICatalogProjectionService projection,
    ILibraryMaterializationService materialization,
    ISourceLifecycle sourceLifecycle) : IDatVersionRetentionCoordinator
{
    public async Task<int> EnforceAsync(
        int datSourceId,
        CancellationToken cancellationToken = default)
    {
        // The first replacement creates the one retained superseded version. Avoid opening a
        // no-op write transaction on that hot post-activation path; a false-to-true race
        // is harmless because the recurring sweep is the durable retention backstop.
        if (!await repository.HasSupersededVersionsBeyondRetentionAsync(
                datSourceId, cancellationToken))
        {
            return 0;
        }

        // Discovery is an intentionally safe source-wide superset and must stay outside the
        // mutation transaction. The transaction's first statement is then the repository's
        // single-statement DELETE, so it holds row locks only for the rows it removes and stays
        // correct under READ COMMITTED interleaving.
        var affectedPlatformIds =
            await repository.GetRoutedPlatformIdsBySourceIdAsync(datSourceId, cancellationToken);
        int catalogSourceId = await repository.GetCatalogSourceIdAsync(datSourceId, cancellationToken);

        await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);
        try
        {
            IReadOnlyList<int> formerlyLinkedTitleIds =
                await sourceLifecycle.GetLinkedTitleIdsAsync(catalogSourceId, cancellationToken);
            int deleted = await repository.DeleteSupersededVersionsBeyondMostRecentAsync(
                datSourceId,
                cancellationToken);

            if (deleted > 0)
            {
                await projection.RefreshCatalogSourcePayloadAsync(catalogSourceId, cancellationToken);
                foreach (int platformId in affectedPlatformIds)
                {
                    await projection.MarkPlatformDirtyAsync(
                        platformId,
                        cancellationToken,
                        formerlyLinkedTitleIds);
                    await materialization.FlagAffectedLibrariesAsync(platformId, cancellationToken);
                }
            }

            await transaction.CommitAsync(cancellationToken);
            return deleted;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }
}
