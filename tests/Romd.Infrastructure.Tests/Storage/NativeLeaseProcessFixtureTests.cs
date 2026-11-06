using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Storage;

public sealed class NativeLeaseProcessFixtureTests
{
    [Fact]
    public async Task RunAsync_ChildFailure_ReportsDiagnosticToCallingTest()
    {
        using var fixture = new NativeLeaseProcessFixture();

        var failure = await Assert.ThrowsAsync<ShouldAssertException>(() => fixture.RunAsync("unknown-lease-test"));

        failure.Message.ShouldContain("Unknown lease test: unknown-lease-test");
    }
}
