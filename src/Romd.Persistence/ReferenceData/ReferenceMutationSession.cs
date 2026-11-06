using System.Data;
using ErrorOr;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;
using Romd.Admin.Application.ReferenceData;

namespace Romd.Persistence.ReferenceData;

public sealed class ReferenceMutationSession : IReferenceMutationSession
{
    private readonly RomdDbContext db;
    private readonly ILogger<ReferenceMutationSession>? logger;

    public ReferenceMutationSession(RomdDbContext db, ILogger<ReferenceMutationSession>? logger = null)
    {
        this.db = db;
        this.logger = logger;
    }

    public Task FlushAsync(CancellationToken ct) => db.SaveChangesAsync(ct);

    public async Task<ErrorOr<T>> RunAsync<T>(Func<CancellationToken, Task<ErrorOr<T>>> operation, CancellationToken ct, bool protectDependencies = false)
    {
        var tx = await db.Database.BeginTransactionAsync(protectDependencies ? IsolationLevel.Serializable : IsolationLevel.ReadCommitted, ct);
        bool commitAcknowledged = false;
        Exception? primary = null;
        try
        {
            await ReferenceCatalogService.LockAsync(db, ct);
            var result = await operation(ct);
            if (result.IsError)
            {
                try
                { await tx.RollbackAsync(CancellationToken.None); }
                finally { db.ChangeTracker.Clear(); }
                return result;
            }
            await ReferenceCatalogPublisher.PublishAsync(db, null, ct);
            await tx.CommitAsync(ct);
            commitAcknowledged = true;
            return result;
        }
        catch (Exception error) when (error is PostgresException { SqlState: "40001" or "23503" or "23505" }
            || error is DbUpdateException { InnerException: PostgresException { SqlState: "40001" or "23503" or "23505" } })
        {
            primary = error;
            try
            { await tx.RollbackAsync(CancellationToken.None); }
            catch (Exception cleanup)
            {
                error.Data["Romd.ReferenceMutation.RollbackFailure"] = cleanup;
                logger?.LogWarning(cleanup, "Reference mutation rollback failed after the original persistence conflict");
            }
            finally { db.ChangeTracker.Clear(); }
            return Error.Conflict("ReferenceResource.Conflict", "A dependency or identity changed concurrently. Reload before retrying.");
        }
        catch (Exception error)
        {
            primary = error;
            try
            { await tx.RollbackAsync(CancellationToken.None); }
            catch (Exception cleanup)
            {
                error.Data["Romd.ReferenceMutation.RollbackFailure"] = cleanup;
                logger?.LogWarning(cleanup, "Reference mutation rollback failed after the original mutation failure");
            }
            finally { db.ChangeTracker.Clear(); }
            throw;
        }
        finally { await TransactionCleanup.DisposeAsync(tx, commitAcknowledged, primary, logger); }
    }
}
