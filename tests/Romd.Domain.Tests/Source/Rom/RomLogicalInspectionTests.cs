using Romd.Domain.Hashing;
using Romd.Domain.Source.Rom;
using Shouldly;
using Xunit;

namespace Romd.Domain.Tests.Source.Rom;

public sealed class RomLogicalInspectionTests
{
    private static RomLogicalIdentity Identity(long size) => new(size, Sha256.Parse(new string('a', 64)), Sha1.Parse(new string('b', 40)), Md5.Parse(new string('c', 32)), Crc32.FromUInt32(0));

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(32)]
    [InlineData(100)]
    public void Inspection_ExactPrefix_CopiesTheActualBytes(int length)
    {
        var prefix = Enumerable.Range(0, Math.Min(length, 32)).Select(value => (byte)value).ToArray();
        var captured = new RomLogicalInspection(Guid.NewGuid(), Identity(length), "inspector/profile", "inspector/build", DateTimeOffset.UnixEpoch, prefix);
        if (prefix.Length > 0)
            prefix[0] = 255;
        captured.Prefix.ToArray().ShouldBe(Enumerable.Range(0, Math.Min(length, 32)).Select(value => (byte)value).ToArray());
        captured.Layout.ShouldBeNull();
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(3, 2)]
    [InlineData(100, 31)]
    [InlineData(100, 33)]
    public void Inspection_MissingOrExcessPrefix_Refuses(int length, int prefix) =>
        Should.Throw<ArgumentException>(() => new RomLogicalInspection(Guid.NewGuid(), Identity(length), "profile", "build", DateTimeOffset.UnixEpoch, new byte[prefix]));

    [Fact]
    public void Inspection_LayoutDescribingAnotherIdentity_Refuses()
    {
        var psp = new RomPspLayoutFacts(36864, 18, "ULUS12345", "1.00");
        new RomLogicalInspection(Guid.NewGuid(), Identity(36864), "profile", "build", DateTimeOffset.UnixEpoch, new byte[32], psp)
            .Layout.ShouldBe(psp);
        Should.Throw<ArgumentException>(() =>
            new RomLogicalInspection(Guid.NewGuid(), Identity(38912), "profile", "build", DateTimeOffset.UnixEpoch, new byte[32], psp));
        Should.Throw<ArgumentException>(() => new RomLogicalInspection(Guid.NewGuid(), Identity(31), "profile", "build",
            DateTimeOffset.UnixEpoch, new byte[31], new RomNintendoLayoutFacts(0, 0, 0)));
    }

    [Fact]
    public void Exclusive_SeparatelyReportedFamilies_ReturnsTheOneObservedAndRefusesSeveral()
    {
        var nintendo = new RomNintendoLayoutFacts(0, 0, 0);
        var psp = new RomPspLayoutFacts(36864, 18, "ULUS12345", "1.00");
        RomLayoutObservation.Exclusive(null, null).ShouldBeNull();
        RomLayoutObservation.Exclusive(null, psp, null).ShouldBe(psp);
        Should.Throw<ArgumentException>(() => RomLayoutObservation.Exclusive(nintendo, null, psp));
    }

    [Fact]
    public void LayoutFamily_DeclaredOutsideTheDomain_IsRefusedThroughTheRecordCopyConstructor()
    {
        var psp = new RomPspLayoutFacts(36864, 18, "ULUS12345", "1.00");
        var boot = new RomPs2BootLayoutFacts(131072, 64, 16, 24, 864, 25, 80, 26, 256, @"cdrom0:\SLUS_123.45;1", @"cdrom0:\SLUS_123.45;1",
            "SLUS_123.45", 0x100008, 1, RomPs2BootLayoutFacts.ProfileVersion, "inspector");

        Should.Throw<InvalidOperationException>(() => new ForeignLayout(psp));
        // Domain families keep record copying.
        (psp with { }).ShouldBe(psp);
        (boot with { }).ShouldBe(boot);
        (new RomNintendoLayoutFacts(1, 1, 0) with { }).ShouldBe(new RomNintendoLayoutFacts(1, 1, 0));
    }

    private sealed record ForeignLayout : RomLayoutObservation
    {
        public ForeignLayout(RomLayoutObservation original) : base(original) { }
        public override bool Describes(RomLogicalIdentity identity) => true;
    }

    [Fact]
    public void PartitionCounts_ContradictoryOrOverflowing_Refuses()
    {
        Should.Throw<ArgumentException>(() => new RomNintendoLayoutFacts(0, 1, 0));
        Should.Throw<ArgumentException>(() => new RomNintendoLayoutFacts(64, int.MaxValue, int.MaxValue));
        Should.Throw<ArgumentException>(() => new RomNintendoLayoutFacts(65, 64, 1));
        new RomNintendoLayoutFacts(2, 1, 1).Partitions.ShouldBe(2);
    }
}
