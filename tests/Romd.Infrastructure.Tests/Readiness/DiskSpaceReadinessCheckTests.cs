using NSubstitute;
using Romd.Application.Common.Configuration;
using Romd.Application.Common.Readiness;
using Romd.Infrastructure.Readiness;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Readiness;

public sealed class DiskSpaceReadinessCheckTests
{
    [Fact]
    public async Task EvaluateAsync_FreeSpaceAboveThreshold_ReturnsHealthy()
    {
        var check = CreateCheck(new ReadinessOptions());

        var status = await check.EvaluateAsync(CancellationToken.None);

        status.ShouldBe(ReadinessCheckStatus.Healthy);
    }

    [Fact]
    public async Task EvaluateAsync_FreeSpaceBelowThreshold_ReturnsDegraded()
    {
        // The threshold is injectable via ReadinessOptions; long.MaxValue guarantees the real
        // volume is below it without touching disk state.
        var check = CreateCheck(new ReadinessOptions { MinimumFreeDiskBytes = long.MaxValue });

        var status = await check.EvaluateAsync(CancellationToken.None);

        status.ShouldBe(ReadinessCheckStatus.Degraded);
    }

    private static DiskSpaceReadinessCheck CreateCheck(ReadinessOptions options)
    {
        var romdOptions = Substitute.For<IRomdOptions>();
        romdOptions.DataDirectory.Returns(Path.GetTempPath());
        return new DiskSpaceReadinessCheck(romdOptions, options);
    }
}
