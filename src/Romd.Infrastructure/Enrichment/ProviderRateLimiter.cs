using System.Collections.Concurrent;
using System.Threading.RateLimiting;
using Microsoft.Extensions.Options;
using Romd.Admin.Application.Configuration;
using Romd.Admin.Application.Titles.Enrichment;

namespace Romd.Infrastructure.Enrichment;

/// <summary>
///     Singleton rate limiter using a SlidingWindowRateLimiter per provider.
///     Sits above existing Polly retry/circuit-breaker policies.
/// </summary>
public sealed class ProviderRateLimiter : IProviderRateLimiter, IDisposable
{
    private readonly ConcurrentDictionary<string, RateLimiter> _limiters = new(StringComparer.OrdinalIgnoreCase);
    private readonly EnrichmentOptions _options;

    public ProviderRateLimiter(IOptions<EnrichmentOptions> options)
    {
        _options = options.Value;
    }

    public async Task AcquireAsync(string providerId, CancellationToken cancellationToken = default)
    {
        var limiter = _limiters.GetOrAdd(providerId, CreateLimiter);

        using var lease = await limiter.AcquireAsync(1, cancellationToken);
        if (!lease.IsAcquired)
        {
            throw new InvalidOperationException($"Rate limit exceeded for provider '{providerId}'");
        }
    }

    private RateLimiter CreateLimiter(string providerId)
    {
        var rateLimit = _options.GetProviderRateLimit(providerId);

        return new SlidingWindowRateLimiter(new SlidingWindowRateLimiterOptions
        {
            PermitLimit = rateLimit,
            Window = TimeSpan.FromSeconds(1),
            SegmentsPerWindow = 4,
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
            QueueLimit = rateLimit * 10
        });
    }

    public void Dispose()
    {
        foreach (var limiter in _limiters.Values)
        {
            limiter.Dispose();
        }

        _limiters.Clear();
    }
}
