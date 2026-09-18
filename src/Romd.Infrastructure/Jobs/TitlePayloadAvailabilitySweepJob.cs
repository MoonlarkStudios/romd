using Hangfire;
using Romd.Application.Common.Jobs;
using Romd.Admin.Application.Catalog;
using Romd.Admin.Application.Common.Persistence;

namespace Romd.Infrastructure.Jobs;

/// <summary>
///     Repair backstop for historical or missed-path availability drift. One Hangfire invocation
///     owns the complete keyset scan while every bounded page commits independently. The
///     storage-backed Hangfire mutex rejects a later recurring root while this run is active on
///     any worker; disabling Hangfire retries prevents a rejected root from becoming a second
///     sweep. If the owner fails after a page commits, a later recurring root safely starts at the
///     beginning and the idempotent audit converges the remaining pages.
/// </summary>
public sealed class TitlePayloadAvailabilitySweepJob(
    ITitlePayloadAvailabilityProjection projection,
    IUnitOfWork unitOfWork)
{
    internal const int BatchSize = 5_000;

    [AutomaticRetry(Attempts = 0)]
    [DisableConcurrentExecution(timeoutInSeconds: 0)]
    [Queue(JobQueues.Default)]
    public async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        int? afterTitleId = null;

        do
        {
            await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);
            var result = await projection.AuditBatchAsync(
                afterTitleId,
                BatchSize,
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            afterTitleId = result.NextTitleId;
        }
        while (afterTitleId is not null);
    }
}
