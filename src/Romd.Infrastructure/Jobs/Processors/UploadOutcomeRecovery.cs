using Microsoft.Extensions.DependencyInjection;
using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Domain.Jobs;

namespace Romd.Infrastructure.Jobs.Processors;

internal static class UploadOutcomeRecovery
{
    public static async Task<JobItem?> ReadAsync(
        IServiceProvider services, UploadItemContext? item, JobItemKind kind, CancellationToken ct)
    {
        if (item is null)
            return null;
        try
        {
            // Always a fresh context: a failed command transaction may have poisoned its context.
            using var scope = services.CreateScope();
            return await scope.ServiceProvider.GetRequiredService<IJobItemRepository>()
                .FindOutcomeAsync(item.JobId, kind, item.RelativePath, ct);
        }
        catch (Exception ex) when (ex is not (OperationCanceledException or JobExecutionOwnershipLostException))
        {
            throw new UploadOutcomeUncertainException("The upload outcome could not be read.", ex);
        }
    }
}
