using ErrorOr;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NSubstitute;
using Romd.Admin.Application.Dashboard.Queries.GetCoverageStats;
using Romd.Application.Common.Cqrs;
using Romd.Contracts.Management.Models;
using Romd.Hosting.Dashboard;
using Shouldly;
using Xunit;

namespace Romd.Hosting.IntegrationTests.Hosting;

public sealed class DashboardStatsCacheTests
{
    [Fact]
    public async Task GetAsync_ConcurrentCallers_ShareCalculationAndCacheUntilExpiry()
    {
        await using var harness = new Harness();
        var first = harness.Cache.GetCoverageAsync(CancellationToken.None);
        await harness.Started.Task;
        var second = harness.Cache.GetCoverageAsync(CancellationToken.None);
        harness.Result.SetResult(Value(10));
        (await first).Value.ExpectedTitleCount.ShouldBe(10);
        (await second).Value.ExpectedTitleCount.ShouldBe(10);
        (await harness.Cache.GetCoverageAsync(CancellationToken.None)).Value.ExpectedTitleCount.ShouldBe(10);
        harness.Calls.ShouldBe(1);
        harness.Clock.Advance(TimeSpan.FromSeconds(2));
        await harness.Cache.GetCoverageAsync(CancellationToken.None);
        harness.Calls.ShouldBe(2);
    }

    [Fact]
    public async Task GetAsync_CallerCancelled_SharedScopeSurvivesUntilComputationCompletes()
    {
        await using var harness = new Harness();
        using var cancelled = new CancellationTokenSource();
        var first = harness.Cache.GetCoverageAsync(cancelled.Token);
        await harness.Started.Task;
        var second = harness.Cache.GetCoverageAsync(CancellationToken.None);
        cancelled.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(async () => await first);
        harness.Disposals.ShouldBe(0);
        harness.QueryCancellation.IsCancellationRequested.ShouldBeFalse();
        harness.Result.SetResult(Value(20));
        (await second).Value.ExpectedTitleCount.ShouldBe(20);
        harness.Disposals.ShouldBe(1);
        harness.Calls.ShouldBe(1);
    }

    [Fact]
    public async Task GetAsync_InvalidationDuringCalculation_LaterCallerWaitsForFreshCalculation()
    {
        await using var harness = new Harness();
        var old = harness.Cache.GetCoverageAsync(CancellationToken.None);
        await harness.Started.Task;
        harness.Cache.InvalidateCoverage();
        var fresh = harness.Cache.GetCoverageAsync(CancellationToken.None);
        harness.Calls.ShouldBe(1);
        harness.Result.SetResult(Value(10));
        (await old).Value.ExpectedTitleCount.ShouldBe(10);
        (await fresh).Value.ExpectedTitleCount.ShouldBe(20);
        harness.Calls.ShouldBe(2);
        (await harness.Cache.GetCoverageAsync(CancellationToken.None)).Value.ExpectedTitleCount.ShouldBe(20);
        harness.Calls.ShouldBe(2);
    }

    [Fact]
    public async Task GetAsync_InvalidationAfterSuccess_DiscardsCachedValue()
    {
        await using var harness = new Harness();
        harness.Result.SetResult(Value(10));
        await harness.Cache.GetCoverageAsync(CancellationToken.None);
        harness.Cache.InvalidateCoverage();
        (await harness.Cache.GetCoverageAsync(CancellationToken.None)).Value.ExpectedTitleCount.ShouldBe(20);
        harness.Calls.ShouldBe(2);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GetAsync_FailedCalculation_DoesNotCacheFailure(bool throws)
    {
        await using var harness = new Harness();
        if (throws) harness.Result.SetException(new InvalidOperationException("failure"));
        else harness.Result.SetResult(Error.Failure("stats.failed", "failure"));
        if (throws)
            await Should.ThrowAsync<InvalidOperationException>(async () => await harness.Cache.GetCoverageAsync(CancellationToken.None));
        else
            (await harness.Cache.GetCoverageAsync(CancellationToken.None)).IsError.ShouldBeTrue();
        (await harness.Cache.GetCoverageAsync(CancellationToken.None)).Value.ExpectedTitleCount.ShouldBe(20);
        harness.Calls.ShouldBe(2);
    }

    private static CoverageStatsDto Value(int count) => new()
    {
        ExpectedTitleCount = count,
        LocalPayloadTitleCount = 0,
        CompleteTitleCount = 0,
        PartialTitleCount = 0,
        CoverageHealthPercent = 0,
        LastDatRefreshAt = null
    };

    private sealed class Harness : IAsyncDisposable
    {
        private readonly ServiceProvider _provider;
        public readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource<ErrorOr<CoverageStatsDto>> Result = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly ManualClock Clock = new();
        public int Calls;
        public int Disposals;
        public CancellationToken QueryCancellation;
        public DashboardStatsCache Cache { get; }

        public Harness()
        {
            var services = new ServiceCollection();
            services.AddScoped<IQueryHandler<GetCoverageStatsQuery, CoverageStatsDto>>(_ => new Handler(this));
            _provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
            Cache = new DashboardStatsCache(_provider.GetRequiredService<IServiceScopeFactory>(), Clock, Substitute.For<IHostApplicationLifetime>());
        }

        public ValueTask DisposeAsync() => _provider.DisposeAsync();

        private sealed class Handler(Harness owner) : IQueryHandler<GetCoverageStatsQuery, CoverageStatsDto>, IDisposable
        {
            public Task<ErrorOr<CoverageStatsDto>> HandleAsync(GetCoverageStatsQuery query, CancellationToken ct = default)
            {
                owner.QueryCancellation = ct;
                var call = Interlocked.Increment(ref owner.Calls);
                owner.Started.TrySetResult();
                return call == 1 ? owner.Result.Task : Task.FromResult<ErrorOr<CoverageStatsDto>>(Value(20));
            }

            public void Dispose() => Interlocked.Increment(ref owner.Disposals);
        }
    }

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan elapsed) => _now += elapsed;
    }
}
