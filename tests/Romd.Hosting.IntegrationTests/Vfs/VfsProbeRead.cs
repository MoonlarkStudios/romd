using System.Runtime.ExceptionServices;

namespace Romd.Hosting.IntegrationTests.Vfs;

internal static class VfsProbeRead
{
    public static async Task<T> ObserveAsync<T>(Func<CancellationToken, Task<T>> read,
        Func<CancellationToken, Task> observe, Action restore, CancellationToken ct)
    {
        using var readCancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Task<T> pending = read(readCancellation.Token);
        Exception? failure = null;
        try
        { await observe(ct); }
        catch (Exception error) { failure = error; }
        try
        { restore(); }
        catch (Exception error) { failure ??= error; }
        if (failure is not null)
        {
            try
            { await readCancellation.CancelAsync(); }
            catch (Exception error) { failure ??= error; }
        }
        T result = default!;
        try
        { result = await pending; }
        catch (Exception error) { failure ??= error; }
        if (failure is not null)
            ExceptionDispatchInfo.Capture(failure).Throw();
        return result;
    }
}
