using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Romd.Application.Common.Readiness;
using Romd.Infrastructure.Readiness;
using Romd.Infrastructure.Tests.Helpers;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Readiness;

public sealed class WorkerHeartbeatReadinessCheckTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 20, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task EvaluateAsync_FreshHeartbeat_ReturnsHealthy()
    {
        var check = CreateCheck(newestHeartbeatUtc: Now.AddMinutes(-1));

        var status = await check.EvaluateAsync(CancellationToken.None);

        status.ShouldBe(ReadinessCheckStatus.Healthy);
    }

    [Fact]
    public async Task EvaluateAsync_StaleHeartbeat_ReturnsDegraded()
    {
        var check = CreateCheck(newestHeartbeatUtc: Now.AddMinutes(-10));

        var status = await check.EvaluateAsync(CancellationToken.None);

        status.ShouldBe(ReadinessCheckStatus.Degraded);
    }

    [Fact]
    public async Task EvaluateAsync_NoServerHeartbeat_ReturnsDegraded()
    {
        var check = CreateCheck(newestHeartbeatUtc: null);

        var status = await check.EvaluateAsync(CancellationToken.None);

        status.ShouldBe(ReadinessCheckStatus.Degraded);
    }

    [Fact]
    public async Task EvaluateAsync_StorageProbeThrows_ReturnsDegraded()
    {
        var probe = Substitute.For<IHangfireStorageProbe>();
        probe.GetNewestServerHeartbeatUtc().Throws(new InvalidOperationException("storage unavailable"));
        var check = new WorkerHeartbeatReadinessCheck(probe, new ManualTimeProvider(Now), new ReadinessOptions());

        var status = await check.EvaluateAsync(CancellationToken.None);

        status.ShouldBe(ReadinessCheckStatus.Degraded);
    }

    private static WorkerHeartbeatReadinessCheck CreateCheck(DateTimeOffset? newestHeartbeatUtc)
    {
        var probe = Substitute.For<IHangfireStorageProbe>();
        probe.GetNewestServerHeartbeatUtc().Returns(newestHeartbeatUtc);
        return new WorkerHeartbeatReadinessCheck(probe, new ManualTimeProvider(Now), new ReadinessOptions());
    }
}
