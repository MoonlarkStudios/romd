using ErrorOr;
using Microsoft.Extensions.Logging;

namespace Romd.Admin.Application.Common.Persistence;

public static class UnitOfWorkExtensions
{
    /// <summary>
    ///     Runs a short database mutation and commits only a successful result. The transaction
    ///     rolls back on disposal for business errors, exceptions, and cancellation. Do not put
    ///     external I/O in the operation or nest it inside another transaction. Flush explicitly
    ///     inside the operation when a read must observe staged changes before commit.
    /// </summary>
    public static async Task<ErrorOr<T>> ExecuteInTransactionAsync<T>(
        this IUnitOfWork unitOfWork,
        Func<CancellationToken, Task<ErrorOr<T>>> operation,
        ILogger logger,
        CancellationToken ct = default)
    {
        ErrorOr<T> result = default;
        bool committed = false;
        try
        {
            await using var transaction = await unitOfWork.BeginTransactionAsync(ct);
            result = await operation(ct);
            if (result.IsError)
            {
                return result;
            }

            ct.ThrowIfCancellationRequested();
            await transaction.CommitAsync(ct);
            committed = true;
        }
        catch (Exception ex) when (committed)
        {
            // A cleanup failure must not turn a durable mutation into an apparent failure.
            logger.LogWarning(ex, "Database mutation committed but transaction cleanup failed");
        }

        return result;
    }
}
