using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Romd.Hosting.Vfs;
using Shouldly;
using Xunit;

namespace Romd.Hosting.IntegrationTests.Vfs;

public sealed class ConsumerVfsAdmissionTests
{
    [Fact]
    public async Task EnterAsync_CompetingConnectionsAndAccounts_TakeTurnsWithoutHeadOfLineBlocking()
    {
        var admission = Create();
        var user = Guid.NewGuid();
        var deck = Guid.NewGuid();
        var laptop = Guid.NewGuid();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var first = await admission.EnterAsync(user, deck, deadline.Token);
        var deckWait = admission.EnterAsync(user, deck, deadline.Token);
        var laptopWait = admission.EnterAsync(user, laptop, deadline.Token);
        var otherWait = admission.EnterAsync(Guid.NewGuid(), Guid.NewGuid(), deadline.Token);
        first.ShouldNotBeNull();
        first.Dispose();
        using var other = await otherWait.WaitAsync(deadline.Token);
        deckWait.IsCompleted.ShouldBeFalse();
        laptopWait.IsCompleted.ShouldBeFalse();
        other.ShouldNotBeNull();
        other.Dispose();
        using var next = await laptopWait.WaitAsync(deadline.Token);
        deckWait.IsCompleted.ShouldBeFalse();
        next.ShouldNotBeNull();
        next.Dispose();
        using var last = await deckWait.WaitAsync(deadline.Token);
        last.ShouldNotBeNull();
    }

    [Fact]
    public async Task EnterAsync_CancelledWaiter_FreesQueueCapacityAndDoesNotLeakSlot()
    {
        var admission = Create();
        using var first = await admission.EnterAsync(Guid.NewGuid(), null, default);
        using var cancellation = new CancellationTokenSource();
        var queued = admission.EnterAsync(Guid.NewGuid(), null, cancellation.Token);
        cancellation.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(() => queued);
        first!.Dispose();
        using var next = await admission.EnterAsync(Guid.NewGuid(), null, default);
        next.ShouldNotBeNull();
    }

    [Fact]
    public async Task EnterAsync_FullQueueOrTimeout_ReturnsNoLeaseAndRecovers()
    {
        var options = new ConsumerVfsOptions { MaximumConcurrentRequests = 1, MaximumQueuedRequests = 1, MaximumQueueWaitMilliseconds = 100 };
        var admission = new ConsumerVfsAdmission(options);
        using var first = await admission.EnterAsync(Guid.NewGuid(), null, default);
        var queued = admission.EnterAsync(Guid.NewGuid(), null, default);
        (await admission.EnterAsync(Guid.NewGuid(), null, default)).ShouldBeNull();
        (await queued).ShouldBeNull();
        first!.Dispose();
        using var next = await admission.EnterAsync(Guid.NewGuid(), null, default);
        next.ShouldNotBeNull();
    }

    [Fact]
    public async Task EnterAsync_ConnectionAtLimit_OtherConnectionUsesFreeSlot()
    {
        var admission = new ConsumerVfsAdmission(new() { MaximumConcurrentRequests = 2, MaximumConcurrentRequestsPerConnection = 1, MaximumQueuedRequests = 1 });
        var user = Guid.NewGuid();
        var connection = Guid.NewGuid();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var first = await admission.EnterAsync(user, connection, cancellation.Token);
        var queued = admission.EnterAsync(user, connection, cancellation.Token);
        using var second = await admission.EnterAsync(user, Guid.NewGuid(), cancellation.Token);
        second.ShouldNotBeNull();
        queued.IsCompleted.ShouldBeFalse();
        first!.Dispose();
        using var released = await queued;
        released.ShouldNotBeNull();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task EnterAsync_AccountAndConnectionQueueBounds_LeaveRoomForOtherAccounts(bool accountBound)
    {
        var admission = new ConsumerVfsAdmission(new()
        {
            MaximumConcurrentRequests = 1,
            MaximumQueuedRequestsPerUser = accountBound ? 1 : 10,
            MaximumQueuedRequestsPerConnection = 1,
            MaximumQueueWaitMilliseconds = 5000
        });
        var user = Guid.NewGuid();
        var connection = Guid.NewGuid();
        using var first = await admission.EnterAsync(user, connection, default);
        using var cancellation = new CancellationTokenSource();
        var queued = admission.EnterAsync(user, connection, cancellation.Token);
        (await admission.EnterAsync(user, accountBound ? Guid.NewGuid() : connection, default)).ShouldBeNull();
        var other = admission.EnterAsync(Guid.NewGuid(), Guid.NewGuid(), cancellation.Token);
        admission.QueuedRequestCount.ShouldBe(2);
        cancellation.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(() => queued);
        await Should.ThrowAsync<OperationCanceledException>(() => other);
        admission.QueuedRequestCount.ShouldBe(0);
    }

    [Fact]
    public async Task EnterAsync_CancellationRacesRelease_AllCapacityIsRecovered()
    {
        var admission = Create();
        for (int i = 0; i < 100; i++)
        {
            using var first = await admission.EnterAsync(Guid.NewGuid(), null, default);
            using var cancellation = new CancellationTokenSource();
            var pending = admission.EnterAsync(Guid.NewGuid(), null, cancellation.Token);
            await Task.WhenAll(Task.Run(() => first!.Dispose()), Task.Run(() => cancellation.Cancel()));
            try
            { (await pending)?.Dispose(); }
            catch (OperationCanceledException) { }
            admission.QueuedRequestCount.ShouldBe(0);
            admission.TryEnter(Guid.Empty).ShouldBeTrue();
            admission.Exit(Guid.Empty);
        }
    }

    [Theory]
    [InlineData("MaximumConcurrentRequestsPerConnection", 0)]
    [InlineData("MaximumConcurrentRequestsPerConnection", 129)]
    [InlineData("MaximumQueuedRequests", 0)]
    [InlineData("MaximumQueuedRequests", 4097)]
    [InlineData("MaximumQueuedRequestsPerUser", 0)]
    [InlineData("MaximumQueuedRequestsPerUser", 4097)]
    [InlineData("MaximumQueuedRequestsPerConnection", 0)]
    [InlineData("MaximumQueuedRequestsPerConnection", 4097)]
    [InlineData("MaximumQueueWaitMilliseconds", 0)]
    [InlineData("MaximumQueueWaitMilliseconds", 60001)]
    public void Registration_InvalidQueueSettings_FailsStartup(string setting, int value)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [$"Romd:Vfs:{setting}"] = value.ToString(System.Globalization.CultureInfo.InvariantCulture)
        }).Build();
        Should.Throw<InvalidOperationException>(() => new ServiceCollection().AddRomdConsumerVfs(configuration));
    }

    private static ConsumerVfsAdmission Create() => new(new()
    {
        MaximumConcurrentRequests = 1,
        MaximumQueueWaitMilliseconds = 5000
    });
}
