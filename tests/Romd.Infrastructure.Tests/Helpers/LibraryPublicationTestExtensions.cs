using Romd.Domain.Libraries;
using Romd.Persistence;
using Romd.Persistence.Repositories;

namespace Romd.Infrastructure.Tests.Helpers;

internal static class LibraryPublicationTestExtensions
{
    // Fixture callers own a complete publication. Boundary/lifecycle tests call the staged port directly.
    internal static async Task<bool> PublishProjectionAsync(
        this LibraryRepository repository,
        RomdDbContext context,
        int libraryId,
        MaterializedLibraryProjection projection,
        int itemCount,
        Guid materializationToken,
        CancellationToken ct = default)
    {
        await using var transaction = await new EfUnitOfWork(context).BeginTransactionAsync(ct);
        using var publication = await repository.StageMaterializedProjectionsAndActivateAsync(
            libraryId, projection, itemCount, materializationToken, ct);
        try
        {
            await transaction.CommitAsync(ct);
            return publication.Applied;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    internal static async Task<bool> WithdrawProjectionAsync(
        this LibraryRepository repository,
        RomdDbContext context,
        int libraryId,
        string? error,
        Guid materializationToken,
        CancellationToken ct = default)
    {
        await using var transaction = await new EfUnitOfWork(context).BeginTransactionAsync(ct);
        bool applied = await repository.StageMaterializedProjectionsAndMarkConfigurationInvalidAsync(
            libraryId, error, materializationToken, ct);
        await transaction.CommitAsync(ct);
        return applied;
    }
}
