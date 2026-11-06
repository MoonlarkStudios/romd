namespace Romd.Infrastructure.Artwork.SteamGridDb;

/// <summary>Bounds provider network work across scopes; leases cover streamed bodies and redirects.</summary>
public sealed class SteamGridDbRequestGate
{
    internal static SteamGridDbRequestGate Shared { get; } = new();
    private readonly SemaphoreSlim _requests;

    public SteamGridDbRequestGate(int concurrency = 4)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(concurrency);
        _requests = new SemaphoreSlim(concurrency, concurrency);
    }

    public async Task<IDisposable> EnterAsync(CancellationToken ct)
    {
        await _requests.WaitAsync(ct);
        return new Lease(_requests);
    }

    private sealed class Lease(SemaphoreSlim requests) : IDisposable
    {
        private SemaphoreSlim? _requests = requests;
        public void Dispose() => Interlocked.Exchange(ref _requests, null)?.Release();
    }
}
