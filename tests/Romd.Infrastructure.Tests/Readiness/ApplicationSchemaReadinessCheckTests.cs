using NSubstitute;
using Romd.Application.Common.Readiness;
using Romd.Infrastructure.Readiness;
using Romd.Persistence;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Readiness;

/// <summary>API hosts stay unready until the worker publishes exactly the schema this build expects.</summary>
public sealed class ApplicationSchemaReadinessCheckTests
{
    [Theory]
    [InlineData(null)]
    [InlineData(71)]
    [InlineData(72)]
    [InlineData(73)]
    [InlineData(75)]
    public async Task EvaluateAsync_UnpublishedPreviousOrNewerSchema_FailsClosed(int? published)
    {
        var check = new ApplicationSchemaReadinessCheck(Probe(published));

        (await check.EvaluateAsync(CancellationToken.None)).ShouldBe(ReadinessCheckStatus.Unready);
        check.IsCritical.ShouldBeTrue();
    }

    [Fact]
    public async Task EvaluateAsync_WorkerPublishedFreshMediaBaseline_ReturnsHealthy()
    {
        PostgreSqlConfiguration.ExpectedSchemaVersion.ShouldBe(74);
        var check = new ApplicationSchemaReadinessCheck(Probe(74));

        (await check.EvaluateAsync(CancellationToken.None)).ShouldBe(ReadinessCheckStatus.Healthy);
    }

    private static IRomdSchemaProbe Probe(int? published)
    {
        var probe = Substitute.For<IRomdSchemaProbe>();
        probe.GetPublishedVersionAsync(Arg.Any<CancellationToken>()).Returns(published);
        return probe;
    }
}
