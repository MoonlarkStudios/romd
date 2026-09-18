using Microsoft.EntityFrameworkCore;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Persistence;

namespace Romd.Infrastructure.Jobs;

public sealed class JobExecutionMutationGuard(
    RomdDbContext context,
    IUnitOfWork unitOfWork,
    TimeProvider timeProvider,
    JobExecutionClaimOptions claimOptions) : IJobExecutionMutationGuard
{
    public async Task ExecuteAsync(
        Guid jobId,
        Guid fenceToken,
        Func<CancellationToken, Task> mutation,
        CancellationToken ct = default)
    {
        await using var transaction = await unitOfWork.BeginTransactionAsync(ct);
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
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }
}
