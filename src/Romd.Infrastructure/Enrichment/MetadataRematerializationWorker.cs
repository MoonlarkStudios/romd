using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Romd.Admin.Application.Titles.Enrichment;

namespace Romd.Infrastructure.Enrichment;

/// <summary>
/// Reconciles durable intents on the single worker host. A crash before deletion repeats
/// idempotent rematerialization; no request is acknowledged before its work completes.
/// </summary>
public sealed class MetadataRematerializationWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly TimeProvider _clock;
    private readonly ILogger<MetadataRematerializationWorker> _logger;

    public MetadataRematerializationWorker(
        IServiceScopeFactory scopes,
        TimeProvider clock,
        ILogger<MetadataRematerializationWorker> logger)
    {
        _scopes = scopes;
        _clock = clock;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        do
        {
            try { await ProcessPendingAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception) { _logger.LogError(exception, "Could not reconcile metadata rematerialization requests"); }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    public async Task ProcessPendingAsync(CancellationToken ct = default)
    {
        using var readScope = _scopes.CreateScope();
        var queue = readScope.ServiceProvider.GetRequiredService<IMetadataRematerializationQueue>();
        var pending = await queue.GetPendingAsync(_clock.GetUtcNow(), 25, ct);

        foreach (var request in pending)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (request.TitleId is not { } titleId)
                {
                    await queue.ExpandAsync(request, _clock.GetUtcNow(), ct);
                    continue;
                }

                using var workScope = _scopes.CreateScope();
                var service = workScope.ServiceProvider.GetRequiredService<IRematerializationService>();
                await service.RematerializeTitleAsync(titleId, ct);

                await queue.AcknowledgeAsync(request.Id, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Metadata request {RequestId} failed on attempt {Attempt}; retained for retry",
                    request.Id, request.Attempts + 1);
                var retryAt = _clock.GetUtcNow().AddSeconds(30);
                await queue.RetryAsync(request.Id, retryAt, ct);
            }
        }
    }

}
