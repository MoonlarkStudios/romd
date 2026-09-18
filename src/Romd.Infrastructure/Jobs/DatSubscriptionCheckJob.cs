using Hangfire;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Admin.Application.Source.Dat;

namespace Romd.Infrastructure.Jobs;

/// <summary>Checks due subscriptions only. Candidates always await explicit review and activation.</summary>
public sealed class DatSubscriptionCheckJob(IServiceScopeFactory scopes, ILogger<DatSubscriptionCheckJob> logger)
{
    [AutomaticRetry(Attempts = 0)]
    [Queue(JobQueues.Default)]
    public async Task ExecuteAsync(CancellationToken ct)
    {
        IReadOnlyList<int> due;
        await using (var scope = scopes.CreateAsyncScope())
            due = await scope.ServiceProvider.GetRequiredService<IDatCatalogEnrollmentService>().GetDueCheckIdsAsync(ct);
        foreach (var id in due)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
                deadline.CancelAfter(TimeSpan.FromMinutes(5));
                var result = await scope.ServiceProvider.GetRequiredService<IDatCatalogEnrollmentService>().CheckScheduledAsync(id, deadline.Token);
                if (result.IsError)
                {
                    logger.LogInformation("Subscription {SubscriptionId} check did not run: {Code}", id, result.FirstError.Code);
                    if (result.FirstError.Code is not ("DatEnrollment.Busy" or "DatEnrollment.NotFound"))
                    {
                        await using var failureScope = scopes.CreateAsyncScope();
                        await failureScope.ServiceProvider.GetRequiredService<IDatCatalogEnrollmentService>().RecordScheduledFailureAsync(id, ct);
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Subscription {SubscriptionId} check failed; preserving installed catalog", id);
                try
                {
                    await using var failureScope = scopes.CreateAsyncScope();
                    await failureScope.ServiceProvider.GetRequiredService<IDatCatalogEnrollmentService>().RecordScheduledFailureAsync(id, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception failure) { logger.LogError(failure, "Could not record subscription {SubscriptionId} check failure", id); }
            }
        }
    }
}
