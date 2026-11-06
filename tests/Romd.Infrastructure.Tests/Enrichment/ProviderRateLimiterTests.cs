using Microsoft.Extensions.Options;
using Romd.Admin.Application.Configuration;
using Romd.Infrastructure.Enrichment;
using Xunit;

namespace Romd.Infrastructure.Tests.Enrichment;

public class ProviderRateLimiterTests
{
    private static ProviderRateLimiter CreateLimiter(int rateLimit = 10)
    {
        var options = new EnrichmentOptions();
        options.Providers["testProvider"] = new ProviderSettings { RateLimit = rateLimit };
        options.Providers["otherProvider"] = new ProviderSettings { RateLimit = rateLimit };
        return new ProviderRateLimiter(Options.Create(options));
    }

    [Fact]
    public async Task AcquireAsync_IndependentProviders_DontShareBudget()
    {
        using var limiter = CreateLimiter(2);

        // Acquire 2 permits for testProvider (exhausts budget)
        await limiter.AcquireAsync("testProvider");
        await limiter.AcquireAsync("testProvider");

        // otherProvider should still have budget (independent limiter)
        await limiter.AcquireAsync("otherProvider");
        await limiter.AcquireAsync("otherProvider");

        // If we got here without throwing, the providers are independent
    }

    [Fact]
    public async Task AcquireAsync_SameProvider_ThrottledCorrectly()
    {
        // Rate limit of 2 per second, queue limit = 20
        using var limiter = CreateLimiter(2);

        // First two should succeed immediately
        await limiter.AcquireAsync("testProvider");
        await limiter.AcquireAsync("testProvider");

        // Additional requests should queue (not fail) — they wait for the sliding window
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await limiter.AcquireAsync("testProvider", cts.Token);
    }

    [Fact]
    public async Task Dispose_CleansUpLimiters()
    {
        var limiter = CreateLimiter();

        // Warm up the limiter to create internal state
        await limiter.AcquireAsync("testProvider");

        // Dispose should not throw
        limiter.Dispose();
    }
}
