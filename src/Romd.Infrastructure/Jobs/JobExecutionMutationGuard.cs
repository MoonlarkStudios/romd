using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Persistence;

namespace Romd.Infrastructure.Jobs;

public sealed class JobExecutionMutationGuard(
    RomdDbContext context,
    IUnitOfWork unitOfWork,
    TimeProvider timeProvider,
    JobExecutionClaimOptions claimOptions,
    ILogger<JobExecutionMutationGuard>? logger = null) : IJobExecutionMutationGuard
{
    private readonly ILogger<JobExecutionMutationGuard> _logger = logger ?? NullLogger<JobExecutionMutationGuard>.Instance;

    public async Task ExecuteAsync(
        Guid jobId,
        Guid fenceToken,
        Func<CancellationToken, Task> mutation,
        CancellationToken ct = default)
    {
        var transaction = await unitOfWork.BeginTransactionAsync(ct);
        Exception? primary = null;
        bool committed = false;
        try
        {
            var now = timeProvider.GetUtcNow();
            int owned = await context.Jobs
                .Where(job => job.Id == jobId
                    && job.ExecutionFenceToken == fenceToken
                    && job.ExecutionLeaseExpiresAtUtc > now && job.CompletedAt == null)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(job => job.ExecutionLeaseExpiresAtUtc, now + claimOptions.LeaseDuration)
                    .SetProperty(job => job.UpdatedAt, now), ct);
            if (owned != 1)
                throw new JobExecutionOwnershipLostException();

            // The conditional write above holds the claim row until commit. Reclaim therefore
            // serializes after this mutation on every relational provider instead of racing a
            // separate check with the executor's title/library write.
            await mutation(ct);
            await transaction.CommitAsync(ct);
            committed = true;
        }
        catch (Exception error)
        {
            primary = error;
            try
            {
                await transaction.RollbackAsync(CancellationToken.None);
            }
            catch (Exception cleanup)
            {
                _logger.LogWarning(cleanup, "Rollback failed after job {JobId} owned mutation failed", jobId);
            }
            throw;
        }
        finally
        {
            try
            {
                await transaction.DisposeAsync();
            }
            catch (Exception cleanup) when (primary is not null || committed)
            {
                _logger.LogWarning(cleanup, "Transaction cleanup failed after job {JobId} owned mutation settled", jobId);
            }
        }
    }
}
