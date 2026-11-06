using Romd.Domain.Hashing;
using Romd.Domain.Source.Rom;
using Shouldly;
using Xunit;

namespace Romd.Domain.Tests.Source.Rom;

public sealed class RomPs2BootObservationTests
{
    [Theory]
    [InlineData(2048)]
    [InlineData(200)]
    public void Create_MeasuredOriginal_PreservesObservationAndBoundedShortRoot(int rootBytes)
    {
        var measured = Measurement();
        var boot = Boot(rootBytes: (uint)rootBytes);
        var observed = new RomPs2BootObservation(measured, boot);

        observed.Measurement.ShouldBeSameAs(measured);
        observed.Boot.ShouldBeSameAs(boot);
        observed.Matches(measured).ShouldBeTrue();
        observed.Matches(Measurement()).ShouldBeFalse(); // Same bytes/build/time, independent measurement ID.
    }

    [Theory]
    [InlineData("length")]
    [InlineData("build")]
    [InlineData("profile")]
    [InlineData("encoding")]
    [InlineData("crc")]
    public void Create_UnboundOrIncompleteFacts_Rejects(string changed)
    {
        var measured = Measurement(bytes: changed == "length" ? 133120 : 131072,
            build: changed == "build" ? "different-build" : "decoder/v1",
            profile: changed == "profile" ? "identity/v1" : RomPs2BootObservation.MeasurementProfile,
            encoding: changed == "encoding" ? RomRepresentationEncoding.Chd : RomRepresentationEncoding.Identity,
            crc: changed == "crc" ? default(Crc32) : Crc32.FromUInt32(0));

        Should.Throw<ArgumentException>(() => new RomPs2BootObservation(measured, Boot()));
    }

    private static RomVerification Measurement(long bytes = 131072, string build = "decoder/v1",
        string profile = RomPs2BootObservation.MeasurementProfile,
        RomRepresentationEncoding encoding = RomRepresentationEncoding.Identity, Crc32? crc = null) =>
        new(Sha256.Parse(new string('a', 64)), bytes, encoding, Sha256.Parse(new string('a', 64)), bytes,
            Sha1.Parse(new string('b', 40)), Md5.Parse(new string('c', 32)), crc ?? Crc32.FromUInt32(0), profile,
            build, "worker/v1", new DateTimeOffset(2026, 10, 10, 0, 0, 0, TimeSpan.Zero));

    private static RomPs2BootLayoutFacts Boot(uint rootBytes = 2048) =>
        new(131072, 64, 16, 24, rootBytes, 25, 60, 26, 512,
            "cdrom0:\\SLUS_203.12;1", "cdrom0:\\SLUS_203.12;1", "SLUS_203.12", 0x100000, 1,
            RomPs2BootLayoutFacts.ProfileVersion, "decoder/v1");
}
