using ErrorOr;
using Microsoft.Extensions.Logging;

namespace Romd.Admin.Application.Common.Persistence;

public static class UnitOfWorkExtensions
{
    /// <summary>
    ///     Runs a short database mutation and commits only a successful result. The transaction
    ///     rolls back on disposal for business errors, exceptions, and cancellation. Complete external
    ///     preparation before entering; do not nest this inside another transaction. Required locked
    ///     CAS publication follows docs/architecture/backend-mutations.md. Flush explicitly inside
    ///     the operation when a read must observe staged changes before commit.
    /// </summary>
    public static async Task<ErrorOr<T>> ExecuteInTransactionAsync<T>(
        this IUnitOfWork unitOfWork,
        Func<CancellationToken, Task<ErrorOr<T>>> operation,
        ILogger logger,
        CancellationToken ct = default,
        Action<Exception, bool>? logCleanupFailure = null)
    {
        var transaction = await unitOfWork.BeginTransactionAsync(ct);
        bool committed = false;
        bool failed = false;
        try
        {
            var result = await operation(ct);
            if (result.IsError)
            {
                return result;
            }

            ct.ThrowIfCancellationRequested();
            await transaction.CommitAsync(ct);
            committed = true;
            return result;
        }
        catch
        {
            failed = true;
            throw;
        }
        finally
        {
            await transaction.DisposePreservingOutcomeAsync(committed, failed, logger, logCleanupFailure);
        }
    }

    /// <summary>
    ///     Settles an explicit owner's transaction without replacing an acknowledged commit or a
    ///     primary failure. An uncommitted result with neither still surfaces cleanup failures.
    /// </summary>
    internal static async ValueTask DisposePreservingOutcomeAsync(
        this ITransaction transaction,
        bool committed,
        bool failed,
        ILogger logger,
        Action<Exception, bool>? logCleanupFailure = null)
    {
        try
        {
            await transaction.DisposeAsync();
        }
        catch (Exception ex) when (committed || failed)
        {
            if (logCleanupFailure is not null)
                logCleanupFailure(ex, committed);
            else if (committed)
                logger.LogWarning(ex, "Database mutation committed but transaction cleanup failed");
            else
                logger.LogWarning(ex, "Database mutation failed and transaction cleanup also failed");
        }
    }
}
