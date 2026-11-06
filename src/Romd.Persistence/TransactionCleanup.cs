using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;

namespace Romd.Persistence;

/// <summary>Releases a provider transaction without replacing a settled outcome or its original recovery signal.</summary>
internal static class TransactionCleanup
{
    public static async Task DisposeAsync(IDbContextTransaction transaction, bool commitAcknowledged, Exception? primary,
        ILogger? logger = null)
    {
        try
        {
            await transaction.DisposeAsync();
        }
        catch (Exception cleanup) when (commitAcknowledged || primary is not null)
        {
            if (primary is not null)
                primary.Data["Romd.Transaction.CleanupFailure"] = cleanup;
            logger?.LogWarning(cleanup, "Provider transaction cleanup failed; commit acknowledged: {CommitAcknowledged}", commitAcknowledged);
        }
    }
}
