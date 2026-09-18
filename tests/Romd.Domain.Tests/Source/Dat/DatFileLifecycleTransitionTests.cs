using Romd.Domain.Source.Dat;
using Shouldly;
using Xunit;

namespace Romd.Domain.Tests.Source.Dat;

public sealed class DatFileLifecycleTransitionTests
{
    [Fact]
    public void CreateNew_Always_IsActiveAtBirthWithoutSupersededAt()
    {
        var datFile = DatFile.CreateNew("DAT", "DAT", DatType.NoIntro, "test.dat", fileId: 1);

        datFile.Lifecycle.ShouldBe(DatFileLifecycle.Active);
        datFile.SupersededAt.ShouldBeNull();
    }

    [Fact]
    public void Activate_PendingActivation_BecomesActive()
    {
        var datFile = RehydrateWithLifecycle(DatFileLifecycle.PendingActivation);

        datFile.Activate();

        datFile.Lifecycle.ShouldBe(DatFileLifecycle.Active);
        datFile.SupersededAt.ShouldBeNull();
    }

    [Theory]
    [InlineData(DatFileLifecycle.Active)]
    [InlineData(DatFileLifecycle.Superseded)]
    public void Activate_NotPendingActivation_Throws(DatFileLifecycle lifecycle)
    {
        var datFile = RehydrateWithLifecycle(lifecycle);

        var exception = Should.Throw<InvalidOperationException>(datFile.Activate);

        exception.Message.ShouldContain(nameof(DatFileLifecycle.PendingActivation));
        datFile.Lifecycle.ShouldBe(lifecycle);
    }

    [Fact]
    public void Supersede_Active_BecomesSupersededAndRecordsTimestamp()
    {
        var datFile = RehydrateWithLifecycle(DatFileLifecycle.Active);
        var now = new DateTimeOffset(2026, 8, 19, 12, 0, 0, TimeSpan.Zero);

        datFile.Supersede(now);

        datFile.Lifecycle.ShouldBe(DatFileLifecycle.Superseded);
        datFile.SupersededAt.ShouldBe(now);
    }

    [Theory]
    [InlineData(DatFileLifecycle.PendingActivation)]
    [InlineData(DatFileLifecycle.Superseded)]
    public void Supersede_NotActive_Throws(DatFileLifecycle lifecycle)
    {
        var datFile = RehydrateWithLifecycle(lifecycle);

        var exception = Should.Throw<InvalidOperationException>(
            () => datFile.Supersede(DateTimeOffset.UtcNow));

        exception.Message.ShouldContain(nameof(DatFileLifecycle.Active));
        datFile.Lifecycle.ShouldBe(lifecycle);
        datFile.SupersededAt.ShouldBeNull();
    }

    private static DatFile RehydrateWithLifecycle(DatFileLifecycle lifecycle) =>
        DatFile.Rehydrate(
            1,
            "DAT",
            "DAT",
            null,
            null,
            null,
            DatType.NoIntro,
            null,
            "test.dat",
            1,
            DateTimeOffset.UtcNow,
            null,
            0,
            0,
            0,
            datSourceId: 7,
            lifecycle,
            supersededAt: null);
}
