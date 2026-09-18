using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Romd.Persistence.Repositories;

namespace Romd.Infrastructure.Jobs;

public sealed class JobDispatchWorker(IServiceScopeFactory scopeFactory, ILogger<JobDispatchWorker> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        do
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var repository = scope.ServiceProvider.GetRequiredService<JobDispatchRepository>();
                var pending = await repository.GetAvailableAsync(25, stoppingToken);
                foreach (var jobId in pending)
                {
                    try
                    {
                        using var messageScope = scopeFactory.CreateScope();
                        await messageScope.ServiceProvider.GetRequiredService<JobDispatchService>()
                            .DispatchAsync(jobId, stoppingToken);
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { throw; }
                    catch (Exception exception)
                    {
                        logger.LogError(exception, "Job dispatch {JobId} could not be processed; lease recovery will retry", jobId);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception)
            {
                logger.LogError(exception, "Could not read pending job dispatches");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
