using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Romd.Admin.Application.Common.Persistence;

public static class ReadSnapshotExtensions
{
    /// <summary>Completes one read-only snapshot after the callback returns, including a missing-row result.</summary>
    public static async Task<T> ExecuteReadSnapshotAsync<T>(this IReadSnapshotTransactionFactory snapshots,
        Func<CancellationToken, Task<T>> read, ILogger? logger = null, CancellationToken ct = default)
    {
        var snapshot = await snapshots.BeginAsync(ct);
        bool completed = false;
        Exception? primary = null;
        try
        {
            var result = await read(ct);
            await snapshot.CompleteAsync(ct);
            completed = true;
            return result;
        }
        catch (Exception error) { primary = error; throw; }
        finally
        {
            await ReadSnapshotCleanup.DisposeAsync([snapshot], completed, primary, logger ?? NullLogger.Instance);
        }
    }
}
