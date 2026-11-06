using System.Security.Cryptography;
using System.Text;

namespace Romd.Infrastructure.Enrichment;

/// <summary>
/// Shares Twitch authentication across scoped IGDB clients. One credential set is retained,
/// matching the server's single IGDB configuration; rotation never reuses another set's token.
/// The refresh callback is awaited without retaining its scoped HTTP client after completion.
/// </summary>
public sealed class IgdbAccessTokenCache : IDisposable
{
    private readonly TimeProvider _clock;
    private readonly SemaphoreSlim _refresh = new(1, 1);
    private AccessToken? _entry;

    public IgdbAccessTokenCache(TimeProvider clock)
    {
        _clock = clock;
    }

    internal async Task<AccessToken> GetAsync(string clientId, string clientSecret,
        Func<CancellationToken, Task<(string Token, int LifetimeSeconds)>> refresh,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        // The length prefix keeps credential pairs unambiguous; the cache retains no client secret.
        string credentials = Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes($"{clientId.Length}:{clientId}{clientSecret}")));
        var entry = Volatile.Read(ref _entry);
        if (IsFresh(entry, credentials))
            return entry!;

        await _refresh.WaitAsync(ct);
        try
        {
            ct.ThrowIfCancellationRequested();
            entry = _entry;
            if (IsFresh(entry, credentials))
                return entry!;

            // Failed, expired or rotated credentials must not fall back to the previous token.
            Volatile.Write(ref _entry, null);
            var fetched = await refresh(ct);
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(fetched.Token) || fetched.LifetimeSeconds <= 0)
                throw new InvalidOperationException("Twitch returned an invalid authentication response.");

            // Keep the normal one-minute margin, but let short-lived tokens remain usable.
            var lifetime = TimeSpan.FromSeconds(fetched.LifetimeSeconds);
            var margin = TimeSpan.FromSeconds(Math.Min(60, fetched.LifetimeSeconds * 0.1));
            entry = new AccessToken(credentials, fetched.Token, _clock.GetUtcNow() + lifetime - margin);
            Volatile.Write(ref _entry, entry);
            return entry;
        }
        finally
        {
            _refresh.Release();
        }
    }

    internal void Invalidate(AccessToken rejected) => Interlocked.CompareExchange(ref _entry, null, rejected);

    private bool IsFresh(AccessToken? entry, string credentials) =>
        entry is not null && entry.Credentials == credentials && _clock.GetUtcNow() < entry.RefreshAfter;

    public void Dispose() => _refresh.Dispose();

    // A class deliberately avoids record-generated ToString output containing the bearer token.
    internal sealed class AccessToken(string credentials, string token, DateTimeOffset refreshAfter)
    {
        internal string Credentials { get; } = credentials;
        internal string Token { get; } = token;
        internal DateTimeOffset RefreshAfter { get; } = refreshAfter;
    }
}
