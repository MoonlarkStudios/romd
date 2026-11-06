using System.Net;
using Romd.Hosting.Security;
using Shouldly;
using Xunit;

namespace Romd.Hosting.IntegrationTests.Hosting;

public sealed class AuthenticationAttemptLimiterTests
{
    [Fact]
    public void Ip_DefaultBudget_RejectsThirtyFirstAttemptAndRecoversWithoutExtendingWindow()
    {
        var time = new ManualTimeProvider();
        using var limiter = new AuthenticationAttemptLimiter(new(), time);
        var ip = IPAddress.Parse("192.0.2.1");
        for (int i = 0; i < 30; i++)
            limiter.TryAcquireIp(ip).ShouldBeNull();
        limiter.TryAcquireIp(ip).ShouldBe(TimeSpan.FromSeconds(60));
        time.Advance(TimeSpan.FromSeconds(59));
        limiter.TryAcquireIp(ip).ShouldBe(TimeSpan.FromSeconds(1));
        time.Advance(TimeSpan.FromSeconds(1));
        limiter.TryAcquireIp(ip).ShouldBeNull();
    }

    [Fact]
    public void Account_AliasesShareBudgetWhileUnrelatedAccountsRemainAvailable()
    {
        using var limiter = new AuthenticationAttemptLimiter(new(), TimeProvider.System);
        var id = Guid.NewGuid();
        for (int i = 0; i < 10; i++)
            limiter.TryAcquireAccount(id, "user").ShouldBeNull();
        limiter.TryAcquireAccount(id, "USER@EXAMPLE.COM").ShouldNotBeNull();
        limiter.TryAcquireAccount(Guid.NewGuid(), "other").ShouldBeNull();
        for (int i = 0; i < 10; i++)
            limiter.TryAcquireAccount(null, " unknown ").ShouldBeNull();
        limiter.TryAcquireAccount(null, "UNKNOWN").ShouldNotBeNull();
    }

    [Theory]
    [InlineData("192.0.2.1", "::ffff:192.0.2.1")]
    [InlineData("2001:db8:1234:5678::1", "2001:db8:1234:5678::2")]
    public void Ip_EquivalentAddressesShareBudget(string first, string second)
    {
        using var limiter = new AuthenticationAttemptLimiter(new() { IpLimit = 1 }, TimeProvider.System);
        limiter.TryAcquireIp(IPAddress.Parse(first)).ShouldBeNull();
        limiter.TryAcquireIp(IPAddress.Parse(second)).ShouldNotBeNull();
        limiter.TryAcquireIp(IPAddress.Parse("192.0.2.2")).ShouldBeNull();
    }

    [Fact]
    public void Capacity_AttackerKeysCannotEvictLiveBudgetsAndBothMapsRecoverAfterExpiry()
    {
        var time = new ManualTimeProvider();
        using var limiter = new AuthenticationAttemptLimiter(new() { MaxIpKeys = 1, MaxAccountKeys = 1, IpLimit = 1, AccountLimit = 1 }, time);
        limiter.TryAcquireIp(IPAddress.Loopback).ShouldBeNull();
        limiter.TryAcquireAccount(null, "first").ShouldBeNull();
        for (int i = 0; i < 10000; i++)
        {
            limiter.TryAcquireIp(IPAddress.Parse("192.0.2.1")).ShouldBe(TimeSpan.FromSeconds(60));
            limiter.TryAcquireAccount(null, "new-" + i).ShouldBe(TimeSpan.FromSeconds(60));
        }
        limiter.TryAcquireIp(IPAddress.Loopback).ShouldNotBeNull();
        limiter.TryAcquireAccount(null, "first").ShouldNotBeNull();
        time.Advance(TimeSpan.FromSeconds(60));
        limiter.TryAcquireIp(IPAddress.Parse("192.0.2.1")).ShouldBeNull();
        limiter.TryAcquireAccount(null, "second").ShouldBeNull();
    }

    [Fact]
    public void ConcurrentAttempts_AdmitOnlyTheBudget()
    {
        using var limiter = new AuthenticationAttemptLimiter(new(), TimeProvider.System);
        int admitted = 0;
        Parallel.For(0, 100, _ =>
        {
            if (limiter.TryAcquireAccount(Guid.Empty, "user") is null)
                Interlocked.Increment(ref admitted);
        });
        admitted.ShouldBe(10);
    }

    [Fact]
    public void VerificationConcurrency_IsBoundedAndLeaseReleaseRecovers()
    {
        using var limiter = new AuthenticationAttemptLimiter(new(), TimeProvider.System);
        using var first = limiter.TryEnterVerification();
        using var second = limiter.TryEnterVerification();
        first.ShouldNotBeNull();
        second.ShouldNotBeNull();
        limiter.TryEnterVerification().ShouldBeNull();
        first.Dispose();
        first.Dispose();
        using var recovered = limiter.TryEnterVerification();
        recovered.ShouldNotBeNull();
        limiter.TryEnterVerification().ShouldBeNull();
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private long _timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _timestamp;
        public void Advance(TimeSpan delta) => _timestamp += delta.Ticks;
    }
}
