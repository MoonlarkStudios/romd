using Romd.Domain.Hashing;
using Romd.Domain.Source.Rom;
using Shouldly;
using Xunit;

namespace Romd.Domain.Tests.Source.Rom;

public sealed class RomContainerFactsTests
{
    private static readonly Sha1 Raw = Sha1.Parse(new string('a', 40));
    private static readonly Sha1 Overall = Sha1.Parse(new string('b', 40));

    [Fact]
    public void Chd_OrderedMetadata_ProducesExactWireFingerprintAndCapturesInputs()
    {
        byte[] payload = [0];
        uint[] codecs = [0, 0, 0, 0];
        var metadata = new[] { new RomChdMetadataEntry(0x44564420, 1, payload) };
        var facts = new RomContainerFacts.Chd(5, 4096, 4096, 2048, 1, codecs, false, Raw, Overall, metadata);
        payload[0] = 1;
        codecs[0] = 1;
        metadata[0] = new RomChdMetadataEntry(0, 0, [1]);
        facts.MetadataSha256.ShouldBe(RomChdDvdMetadataFacts.SingletonFingerprint);
        facts.Metadata[0].Payload.ToArray().ShouldBe(new byte[] { 0 });
        facts.Codecs[0].ShouldBe(0u);
    }

    [Fact]
    public void Chd_CdGeometry_DoesNotInventDvdMetadataOrKind()
    {
        var facts = new RomContainerFacts.Chd(5, 36 * 2448, 4 * 2448, 2448, 9, [0, 0, 0, 0], false, Raw, Overall,
            [new RomChdMetadataEntry(0x43485432, 1, "TRACK:1 TYPE:MODE2_RAW"u8)]);
        facts.UnitBytes.ShouldBe(2448u);
        facts.Metadata[0].Tag.ShouldBe(0x43485432u);
    }

    [Theory]
    [InlineData(4096, 4096, 2448, 1, false)]
    [InlineData(4096, 4096, 2048, 2, false)]
    [InlineData(4096, 4096, 2048, 1, true)]
    public void Chd_ContradictoryGeometry_Rejects(long logical, uint hunk, uint unit, uint hunks, bool parent) =>
        Should.Throw<ArgumentException>(() => new RomContainerFacts.Chd(5, logical, hunk, unit, hunks, [0, 0, 0, 0], parent, Raw, Overall, []));

    [Fact]
    public void Rvz_CapturedNoncanonicalSettings_RetainsFacts()
    {
        var facts = new RomContainerFacts.Rvz(0x00030000, 1, 3, 7, 32768, false, 4096);
        facts.Compression.ShouldBe(3u);
        facts.CompressionLevel.ShouldBe(7);
        facts.BlockBytes.ShouldBe(32768);
    }

    [Theory]
    [InlineData(100, 1)]
    [InlineData(3, 262144)]
    public void Chd_MetadataCollectionExceedsBounds_Rejects(int count, int payloadBytes)
    {
        var entries = Enumerable.Range(0, count).Select(index => new RomChdMetadataEntry((uint)index, 1, new byte[payloadBytes])).ToArray();
        Should.Throw<ArgumentException>(() => new RomContainerFacts.Chd(5, 4096, 4096, 2048, 1, [0, 0, 0, 0], false, Raw, Overall, entries));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(262145)]
    public void Metadata_PayloadExceedsBounds_Rejects(int bytes) =>
        Should.Throw<ArgumentException>(() => new RomChdMetadataEntry(0, 0, new byte[bytes]));

    [Fact]
    public void Chd_NinetyNineTrackMetadataEntries_FitsRegisteredCdLayouts()
    {
        var entries = Enumerable.Range(1, 99).Select(number => new RomChdMetadataEntry(0x43485432, 1,
            System.Text.Encoding.ASCII.GetBytes($"TRACK:{number} TYPE:MODE2_RAW"))).ToArray();
        new RomContainerFacts.Chd(5, 99 * 4 * 2448, 4 * 2448, 2448, 99, [0, 0, 0, 0], false, Raw, Overall, entries)
            .Metadata.Count.ShouldBe(99);
    }
}
