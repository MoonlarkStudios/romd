using System.Buffers.Binary;
using Romd.Media.Nintendo;
using Romd.Media.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Media.Tests;

public sealed class WiiPartitionExtractorTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadRegionAsync_EncryptedReferenceData_RecoversBootFstAndExactHashExceptions(bool poison)
    {
        var fixture = SyntheticWiiIsoBuilder.Create(WiiTicketTests.SyntheticKey(41), 0, sectors: 65, poisonHashes: poison);
        var partition = fixture.Partitions[0];
        using var extractor = new WiiPartitionExtractor(partition.ExpectedTitleKey);
        var source = new MemoryContent(fixture.Iso);

        (await extractor.ReadRegionAsync(source, partition.DataOffset, 64, CancellationToken.None)).ShouldBeTrue();

        byte[] plain = new byte[64 * ReferenceDisc.SectorData], prefix = new byte[WiiPartitionExtractor.MaximumListBytes];
        extractor.CopyPayload(0, 64, plain);
        plain.ShouldBe(partition.Plain.AsSpan(0, plain.Length).ToArray());
        plain.AsSpan(0, 0x440).ToArray().ShouldBe(partition.Plain.AsSpan(0, 0x440).ToArray());
        plain.AsSpan(0x1000, 0x100).ToArray().ShouldBe(partition.Plain.AsSpan(0x1000, 0x100).ToArray());
        int bytes = extractor.WriteExceptions(0, 64, prefix);
        BinaryPrimitives.ReadUInt16BigEndian(prefix).ShouldBe((ushort)(poison ? WiiPartitionExtractor.MaximumExceptions : 0));
        bytes.ShouldBe(poison ? WiiPartitionExtractor.MaximumListBytes : 2);
        VerifyRegion(partition, 0, 64, plain, prefix.AsSpan(0, bytes));
        (await extractor.ReadRegionAsync(source, partition.DataOffset + 64L * ReferenceDisc.SectorSize, 1, CancellationToken.None)).ShouldBeTrue();
        byte[] tail = new byte[ReferenceDisc.SectorData];
        extractor.CopyPayload(0, 1, tail);
        int tailBytes = extractor.WriteExceptions(0, 1, prefix);
        VerifyRegion(partition, 64, 1, tail, prefix.AsSpan(0, tailBytes));
        source.Disposed.ShouldBeFalse();
    }

    [Fact]
    public async Task ReadRegionAsync_SubchunkExceptions_AreRelativeToTheSubchunkFirstSector()
    {
        var fixture = SyntheticWiiIsoBuilder.Create(WiiTicketTests.SyntheticKey(41), 0, sectors: 64, poisonHashes: true);
        var partition = fixture.Partitions[0];
        using var extractor = new WiiPartitionExtractor(partition.ExpectedTitleKey);
        (await extractor.ReadRegionAsync(new MemoryContent(fixture.Iso), partition.DataOffset, 64, CancellationToken.None)).ShouldBeTrue();
        byte[] prefix = new byte[WiiPartitionExtractor.MaximumListBytes];

        int bytes = extractor.WriteExceptions(7, 2, prefix);

        BinaryPrimitives.ReadUInt16BigEndian(prefix).ShouldBe((ushort)104);
        for (int at = 2; at < bytes; at += 22)
            (BinaryPrimitives.ReadUInt16BigEndian(prefix.AsSpan(at)) >> 10).ShouldBeLessThan(2);
    }

    [Fact]
    public async Task ReadRegionAsync_CancelledOrTruncatedSource_DoesNotExposePreviousRegion()
    {
        var fixture = SyntheticWiiIsoBuilder.Create(WiiTicketTests.SyntheticKey(41), 0, sectors: 1);
        var partition = fixture.Partitions[0];
        using var extractor = new WiiPartitionExtractor(partition.ExpectedTitleKey);
        using var cancelled = new CancellationTokenSource();
        (await extractor.ReadRegionAsync(new MemoryContent(fixture.Iso), partition.DataOffset, 1, CancellationToken.None)).ShouldBeTrue();
        cancelled.Cancel();

        await Should.ThrowAsync<OperationCanceledException>(() => extractor.ReadRegionAsync(new MemoryContent(fixture.Iso),
            partition.DataOffset, 1, cancelled.Token));
        Should.Throw<ArgumentOutOfRangeException>(() => extractor.CopyPayload(0, 1, new byte[ReferenceDisc.SectorData]));
        (await extractor.ReadRegionAsync(new MemoryContent([]), 0, 1, CancellationToken.None)).ShouldBeFalse();
        Should.Throw<ArgumentOutOfRangeException>(() => extractor.CopyPayload(0, 1, new byte[ReferenceDisc.SectorData]));
    }

    private static void VerifyRegion(SyntheticWiiPartition partition, int first, int sectors, byte[] plain, ReadOnlySpan<byte> prefix)
    {
        var exceptions = new List<HashOverride>();
        for (int at = 2; at < prefix.Length; at += 22)
        {
            int offset = BinaryPrimitives.ReadUInt16BigEndian(prefix[at..]);
            exceptions.Add(new HashOverride(offset >> 10, offset & 0x3FF, prefix.Slice(at + 2, 20).ToArray()));
        }
        byte[] expected = ReferenceDisc.EncryptPartition(partition.ExpectedTitleKey, plain, sectors, exceptions);
        byte[] original = ReferenceDisc.EncryptPartition(partition.ExpectedTitleKey, partition.Plain, partition.Sectors, partition.Overrides);
        expected.ShouldBe(original.AsSpan(first * ReferenceDisc.SectorSize, sectors * ReferenceDisc.SectorSize).ToArray());
    }
}
