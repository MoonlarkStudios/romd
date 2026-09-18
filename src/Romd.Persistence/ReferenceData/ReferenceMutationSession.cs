using System.Data;
using ErrorOr;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Romd.Admin.Application.ReferenceData;

namespace Romd.Persistence.ReferenceData;

public sealed class ReferenceMutationSession : IReferenceMutationSession
{
    private readonly RomdDbContext db;

    public ReferenceMutationSession(RomdDbContext db)
    {
        this.db = db;
    }

    public Task FlushAsync(CancellationToken ct) => db.SaveChangesAsync(ct);

    public async Task<ErrorOr<T>> RunAsync<T>(Func<CancellationToken, Task<ErrorOr<T>>> operation, CancellationToken ct, bool protectDependencies = false)
    {
        await using var tx = await db.Database.BeginTransactionAsync(protectDependencies ? IsolationLevel.Serializable : IsolationLevel.ReadCommitted, ct);
        try
        {
            await ReferenceCatalogService.LockAsync(db, ct);
            var result = await operation(ct);
            if (result.IsError)
            { await tx.RollbackAsync(ct); db.ChangeTracker.Clear(); return result; }
            await ReferenceCatalogPublisher.PublishAsync(db, null, ct);
            await tx.CommitAsync(ct);
            return result;
        }
        catch (Exception error) when (error is PostgresException { SqlState: "40001" or "23503" or "23505" }
            || error is DbUpdateException { InnerException: PostgresException { SqlState: "40001" or "23503" or "23505" } })
        {
            await tx.RollbackAsync(ct);
            db.ChangeTracker.Clear();
            return Error.Conflict("ReferenceResource.Conflict", "A dependency or identity changed concurrently. Reload before retrying.");
        }
        catch
        {
            await tx.RollbackAsync(CancellationToken.None);
            db.ChangeTracker.Clear();
            throw;
        }

    }
}
