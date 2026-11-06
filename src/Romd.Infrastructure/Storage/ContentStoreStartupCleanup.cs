using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Romd.Storage;

namespace Romd.Infrastructure.Storage;

/// <summary>
///     Removes abandoned content staging files once after the worker starts. Staging runs outside
///     the root mutation lease, so the store skips files that a live writer in any host still holds.
/// </summary>
public sealed class ContentStoreStartupCleanup(
    IContentAddressableStore store,
    ILogger<ContentStoreStartupCleanup> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            int removed = await store.CleanupTemporaryFilesAsync(stoppingToken);
            if (removed > 0)
                logger.LogInformation("Removed {Count} abandoned content staging files.", removed);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Content staging cleanup failed; it runs again at the next worker start.");
        }
    }
}
