using NSubstitute;
using Romd.Application.Common.Readiness;
using Romd.Infrastructure.Jobs;
using Romd.Infrastructure.Readiness;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Readiness;

public sealed class HangfireStorageReadinessCheckTests
{
    [Fact]
    public async Task EvaluateAsync_VersionNotPublished_ReturnsUnready()
    {
        var probe = Substitute.For<IHangfireSchemaProbe>();
        probe.GetPublishedVersionAsync(Arg.Any<CancellationToken>()).Returns((int?)null);
        var check = new HangfireStorageReadinessCheck(probe);

        var status = await check.EvaluateAsync(CancellationToken.None);

        status.ShouldBe(ReadinessCheckStatus.Unready);
    }

    [Fact]
    public async Task EvaluateAsync_StorageReachable_ReturnsHealthy()
    {
        var probe = Substitute.For<IHangfireSchemaProbe>();
        probe.GetPublishedVersionAsync(Arg.Any<CancellationToken>())
            .Returns(HangfirePostgreSqlConfiguration.ExpectedSchemaVersion);
        var check = new HangfireStorageReadinessCheck(probe);

        var status = await check.EvaluateAsync(CancellationToken.None);

        status.ShouldBe(ReadinessCheckStatus.Healthy);
    }

    [Fact]
    public async Task EvaluateAsync_ProbeThrows_PropagatesAsCriticalFailure()
    {
        var probe = Substitute.For<IHangfireSchemaProbe>();
        probe.GetPublishedVersionAsync(Arg.Any<CancellationToken>())
            .Returns<Task<int?>>(_ => throw new InvalidOperationException("unreachable"));
        var check = new HangfireStorageReadinessCheck(probe);

        // The evaluator maps a critical check's exception to unready; the check itself propagates.
        await Should.ThrowAsync<InvalidOperationException>(() => check.EvaluateAsync(CancellationToken.None));
        check.IsCritical.ShouldBeTrue();
    }

    [Fact]
    public async Task EvaluateAsync_UnexpectedVersion_ReturnsUnready()
    {
        var probe = Substitute.For<IHangfireSchemaProbe>();
        probe.GetPublishedVersionAsync(Arg.Any<CancellationToken>())
            .Returns(HangfirePostgreSqlConfiguration.ExpectedSchemaVersion + 1);
        var check = new HangfireStorageReadinessCheck(probe);

        var status = await check.EvaluateAsync(CancellationToken.None);

        status.ShouldBe(ReadinessCheckStatus.Unready);
    }
}
