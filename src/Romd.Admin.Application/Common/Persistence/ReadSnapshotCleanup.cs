using System.Runtime.ExceptionServices;
using Microsoft.Extensions.Logging;

namespace Romd.Admin.Application.Common.Persistence;

/// <summary>Settles owned read-snapshot resources in order without replacing their acknowledged outcome or primary failure.</summary>
public static class ReadSnapshotCleanup
{
    public static async ValueTask DisposeAsync(IReadOnlyList<IAsyncDisposable> resources, bool completed,
        Exception? primary, ILogger logger)
    {
        Exception? cleanupFailure = null;
        foreach (var resource in resources)
        {
            try
            { await resource.DisposeAsync(); }
            catch (Exception cleanup)
            {
                if (primary is not null || cleanupFailure is not null)
                {
                    var cause = primary ?? cleanupFailure!;
                    if (!ReferenceEquals(cause, cleanup))
                        cause.Data["Romd.ReadSnapshot.CleanupFailure"] = cleanup;
                }
                else if (!completed)
                    cleanupFailure = cleanup;
                logger.LogWarning(cleanup, "Read snapshot cleanup failed; completion acknowledged: {Completed}", completed);
            }
        }
        if (cleanupFailure is not null)
            ExceptionDispatchInfo.Capture(cleanupFailure).Throw();
    }
}
