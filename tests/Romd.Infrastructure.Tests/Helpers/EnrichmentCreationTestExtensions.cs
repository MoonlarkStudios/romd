using Microsoft.EntityFrameworkCore;
using Romd.Admin.Application.Titles.Enrichment;
using Romd.Domain.Jobs;
using Romd.Persistence;
using Romd.Persistence.Entities;

namespace Romd.Infrastructure.Tests.Helpers;

internal static class EnrichmentCreationTestExtensions
{
    // Fixture setup owns the commit; acceptance/atomicity tests exercise the staged ports directly.
    internal static async Task CommitCreationAsync(this IEnrichmentJobAcceptance acceptance, RomdDbContext context,
        EnrichmentJob job, CancellationToken ct = default)
    {
        await using var transaction = await new EfUnitOfWork(context).BeginTransactionAsync(ct);
        await acceptance.AddStagedAsync(job, ct);
        await transaction.CommitAsync(ct);
        // Preserve the prior single-job fixture lifetime without clearing unrelated tracked rows.
        context.ChangeTracker.Entries<EnrichmentJobEntity>().Single(entry => entry.Entity.Id == job.Id).State = EntityState.Detached;
    }

    internal static async Task CommitCreationAsync(this IBulkEnrichmentJobAcceptance acceptance, RomdDbContext context,
        BulkEnrichmentJob job, CancellationToken ct = default)
    {
        await using var transaction = await new EfUnitOfWork(context).BeginTransactionAsync(ct);
        await acceptance.AddStagedAsync(job, ct);
        await transaction.CommitAsync(ct);
    }
}
