using Moonlark.Libchdr;
using Romd.Media.Cd;
using Romd.Media.PlayStation;
using Romd.Media.Saturn;
using Shouldly;
using Xunit;

namespace Romd.Media.Tests.Cd;

public sealed class CdBootObservationTests
{
    private static readonly Ps1CdObservedFacts Ps1 = new(Ps1CdIdentifier.Profile, 0, 64, 20, 2048,
        "PSX.EXE", 24, 4096, 0x80010000, 0x80010000, 2048);
    private static readonly SaturnCdObservedFacts Saturn = new(SaturnCdIdentifier.Profile, 0, "T-00001", "JUE", 4096, new string('a', 64));

    [Fact]
    public void FromWire_AbsentOrExclusiveFamily_CapturesOnlyThatObservation()
    {
        CdBootObservation.FromWire(null, null).ShouldBe(CdBootObservation.None);
        default(CdBootObservation).ShouldBe(CdBootObservation.None);
        CdBootObservation.None.Profile.ShouldBeNull();
        var ps1 = CdBootObservation.FromWire(Ps1, null);
        ps1.Ps1.ShouldBe(Ps1);
        ps1.Saturn.ShouldBeNull();
        ps1.Profile.ShouldBe(Ps1CdIdentifier.Profile);
        var saturn = CdBootObservation.FromWire(null, Saturn);
        saturn.Saturn.ShouldBe(Saturn);
        saturn.Ps1.ShouldBeNull();
        saturn.Profile.ShouldBe(SaturnCdIdentifier.Profile);
        saturn.Fits(ChdCdTrackType.Mode1Raw, 64, 64).ShouldBeTrue();
        saturn.Fits(ChdCdTrackType.Mode2Raw, 64, 64).ShouldBeFalse();
    }

    [Fact]
    public void FromWire_ContradictoryOrMalformedFacts_RejectsBeforeAnObservationExists()
    {
        Should.Throw<InvalidDataException>(() => CdBootObservation.FromWire(Ps1, Saturn));
        Should.Throw<InvalidDataException>(() => CdBootObservation.FromWire(Ps1 with { Profile = SaturnCdIdentifier.Profile }, null));
        Should.Throw<InvalidDataException>(() => CdBootObservation.FromWire(null, Saturn with { HeaderSha256 = new string('0', 64) }));
    }
}
