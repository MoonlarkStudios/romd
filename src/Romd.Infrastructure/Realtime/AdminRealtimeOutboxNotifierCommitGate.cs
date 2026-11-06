namespace Romd.Infrastructure.Realtime;

public sealed class AdminRealtimeOutboxNotifierCommitGate
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    internal async Task RunNotifierCommitAsync(Func<Task> operation, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);

        try
        {
            await operation();
        }
        finally
        {
            _gate.Release();
        }
    }
}
