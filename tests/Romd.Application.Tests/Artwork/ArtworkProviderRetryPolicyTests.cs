using Romd.Admin.Application.Artwork.Providers;
using Shouldly;
using Xunit;

namespace Romd.Application.Tests.Artwork;

public sealed class ArtworkProviderRetryPolicyTests
{
    [Theory]
    [InlineData(null, 60)]
    [InlineData(-60, 1)]
    [InlineData(0, 1)]
    [InlineData(10, 10)]
    [InlineData(7200, 3600)]
    public void GetDeferredUntil_RateLimitHint_UsesBoundedDeadline(int? hintSeconds, int expectedSeconds)
    {
        var now = new DateTimeOffset(2026, 9, 22, 12, 0, 0, TimeSpan.Zero);
        var error = ArtworkProviderErrors.RateLimited(hintSeconds is { } seconds ? now.AddSeconds(seconds) : null);

        ArtworkProviderRetryPolicy.GetDeferredUntil([error], now).ShouldBe(now.AddSeconds(expectedSeconds));
    }

    [Fact]
    public void GetDeferredUntil_MultipleRateLimits_RespectsLatestDeadline()
    {
        var now = new DateTimeOffset(2026, 9, 22, 12, 0, 0, TimeSpan.Zero);

        ArtworkProviderRetryPolicy.GetDeferredUntil(
            [ArtworkProviderErrors.RateLimited(now.AddSeconds(5)), ArtworkProviderErrors.RateLimited(now.AddSeconds(15))], now)
            .ShouldBe(now.AddSeconds(15));
    }

    [Fact]
    public void GetDeferredUntil_NotThrottled_DoesNotDefer() =>
        ArtworkProviderRetryPolicy.GetDeferredUntil([ArtworkProviderErrors.Unavailable()], DateTimeOffset.UtcNow).ShouldBeNull();
}
