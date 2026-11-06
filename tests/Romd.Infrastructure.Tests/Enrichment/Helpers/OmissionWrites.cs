using ErrorOr;
using Romd.Application.Common.Concurrency;
using Romd.Persistence;
using Romd.Persistence.Enrichment;

namespace Romd.Infrastructure.Tests.Enrichment.Helpers;

/// <summary>Writes an omission decision in its own transaction, committed on success, as the omission command does.</summary>
internal static class OmissionWrites
{
    public static async Task<ErrorOr<Updated>> SetCommittedAsync(this EnrichmentWorkbench workbench, RomdDbContext db,
        int titleId, string field, bool accepted, RevisionPrecondition ifMatch)
    {
        await using var transaction = await db.Database.BeginTransactionAsync();
        var written = await workbench.SetOmissionAsync(titleId, field, accepted, ifMatch, default);
        if (!written.IsError)
        {
            await db.SaveChangesAsync();
            await transaction.CommitAsync();
        }
        return written;
    }
}
