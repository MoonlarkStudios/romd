using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Domain.Jobs;
using Romd.Persistence;

namespace Romd.Infrastructure.Tests.Helpers;

internal static class MaterializationJobTestExtensions
{
    // Fixture setup owns this commit. Acceptance tests exercise the staged capability directly.
    internal static async Task SeedCommittedAsync(
        this IMaterializationJobRepository repository,
        RomdDbContext context,
        MaterializationJob job,
        CancellationToken ct = default)
    {
        await using var transaction = await new EfUnitOfWork(context).BeginTransactionAsync(ct);
        await repository.AddStagedAsync(job, ct);
        await transaction.CommitAsync(ct);
    }
}
