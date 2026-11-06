using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace Romd.Hosting.Security;

/// <summary>
/// Ephemeral, process-local attempt budgets shared by interactive and device sign-in. Full maps
/// reject new keys until expiry; live entries are never evicted to make room for attacker keys.
/// </summary>
public sealed class AuthenticationAttemptLimiter : IDisposable
{
    private readonly object _sync = new();
    private readonly Dictionary<string, Bucket> _ips = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Bucket> _accounts = new(StringComparer.Ordinal);
    private readonly PriorityQueue<(Dictionary<string, Bucket> Map, string Key), long> _expirations = new();
    private readonly AuthenticationAttemptOptions _options;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _verificationSlots;
    private readonly long _windowTicks;

    public AuthenticationAttemptLimiter(AuthenticationAttemptOptions options, TimeProvider time)
    {
        options.Validate();
        _options = options;
        _time = time;
        _windowTicks = checked((long)(time.TimestampFrequency * (double)options.WindowSeconds));
        _verificationSlots = new SemaphoreSlim(options.MaxConcurrentVerifications);
    }

    // RemoteIpAddress is set by the trusted-proxy middleware. Never read user-supplied headers here.
    public TimeSpan? TryAcquireIp(IPAddress? address)
    {
        if (address?.IsIPv4MappedToIPv6 == true)
            address = address.MapToIPv4();
        string key = "unknown";
        if (address is not null)
        {
            byte[] bytes = address.GetAddressBytes();
            // IPv6 privacy addresses within one /64 share a budget.
            key = Convert.ToHexString(address.AddressFamily == AddressFamily.InterNetworkV6 ? bytes.AsSpan(0, 8) : bytes);
        }
        return TryAcquire(_ips, key, _options.IpLimit, _options.MaxIpKeys);
    }

    public TimeSpan? TryAcquireAccount(Guid? userId, string login)
    {
        string key = userId?.ToString("N")
            ?? "unknown:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(login.Trim().ToUpperInvariant())));
        return TryAcquire(_accounts, key, _options.AccountLimit, _options.MaxAccountKeys);
    }

    public IDisposable? TryEnterVerification() => _verificationSlots.Wait(0) ? new VerificationLease(_verificationSlots) : null;

    private TimeSpan? TryAcquire(Dictionary<string, Bucket> map, string key, int limit, int capacity)
    {
        lock (_sync)
        {
            long now = _time.GetTimestamp();
            while (_expirations.TryPeek(out var expired, out long expiry) && expiry <= now)
            {
                _expirations.Dequeue();
                expired.Map.Remove(expired.Key);
            }

            if (!map.TryGetValue(key, out var bucket))
            {
                if (map.Count >= capacity)
                    return _time.GetElapsedTime(now, map.Values.Min(value => value.ExpiresAt));
                bucket = new Bucket(now + _windowTicks);
                map.Add(key, bucket);
                _expirations.Enqueue((map, key), bucket.ExpiresAt);
            }
            if (bucket.Attempts >= limit)
                return _time.GetElapsedTime(now, bucket.ExpiresAt);
            bucket.Attempts++;
            return null;
        }
    }

    public void Dispose() => _verificationSlots.Dispose();

    private sealed class Bucket(long expiresAt)
    {
        public long ExpiresAt { get; } = expiresAt;
        public int Attempts { get; set; }
    }

    private sealed class VerificationLease(SemaphoreSlim slots) : IDisposable
    {
        private int _released;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
                slots.Release();
        }
    }
}
