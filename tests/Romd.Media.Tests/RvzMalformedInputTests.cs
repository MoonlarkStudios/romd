using System.Buffers.Binary;
using System.Security.Cryptography;
using Romd.Media.Rvz;
using Romd.Media.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Media.Tests;

public sealed class RvzMalformedInputTests
{
    private const int Disc = 0x48;

    [Fact]
    public async Task OpenAsync_WrongMagic_IsInvalid()
    {
        var file = Patched(Discs.GameCube().File, bytes => bytes[0] = (byte)'W');
        await Should.ThrowAsync<InvalidDataException>(() => Discs.OpenAsync(file));
    }

    [Fact]
    public async Task OpenAsync_FileHeaderHashMismatch_IsInvalid()
    {
        var file = Patched(Discs.GameCube().File, bytes => bytes[0x24] ^= 1, rehash: false);
        await Should.ThrowAsync<InvalidDataException>(() => Discs.OpenAsync(file));
    }

    [Fact]
    public async Task OpenAsync_DiscStructHashMismatch_IsInvalid()
    {
        var file = Patched(Discs.GameCube().File, bytes => bytes[Disc + 8] ^= 1, rehash: false);
        await Should.ThrowAsync<InvalidDataException>(() => Discs.OpenAsync(file));
    }

    [Fact]
    public async Task OpenAsync_PartitionTableHashMismatch_IsInvalid()
    {
        var original = Discs.Wii().File;
        var file = Patched(original, bytes => bytes[(int)Be64(bytes, Disc + 152)] ^= 1, rehash: false);
        await Should.ThrowAsync<InvalidDataException>(() => Discs.OpenAsync(file));
    }

    [Fact]
    public async Task OpenAsync_IncompatibleVersion_IsNotSupported()
    {
        var file = Patched(Discs.GameCube().File, bytes => BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(8), 0x02000000));
        await Should.ThrowAsync<NotSupportedException>(() => Discs.OpenAsync(file));
    }

    [Fact]
    public async Task OpenAsync_TruncatedFile_IsInvalid()
    {
        var file = Discs.GameCube().File;
        await Should.ThrowAsync<InvalidDataException>(() => Discs.OpenAsync(file[..^1]));
    }

    [Theory]
    [InlineData(0x9000u)]
    [InlineData(0x4000u)]
    [InlineData(0x300000u)]
    public async Task OpenAsync_InvalidChunkSize_IsInvalid(uint chunkSize)
    {
        var file = Patched(Discs.GameCube().File, bytes => BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(Disc + 12), chunkSize));
        await Should.ThrowAsync<InvalidDataException>(() => Discs.OpenAsync(file));
    }

    [Theory]
    [InlineData(1u)]
    [InlineData(9u)]
    public async Task OpenAsync_UnsupportedCompressionMethod_IsNotSupported(uint method)
    {
        var file = Patched(Discs.GameCube().File, bytes => BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(Disc + 4), method));
        await Should.ThrowAsync<NotSupportedException>(() => Discs.OpenAsync(file));
    }

    [Fact]
    public async Task OpenAsync_CompressionPropertiesDoNotMatchMethod_IsInvalid()
    {
        var file = Patched(Discs.GameCube(method: RvzMethod.Lzma).File, bytes => bytes[Disc + 212] = 0);
        await Should.ThrowAsync<InvalidDataException>(() => Discs.OpenAsync(file));
    }

    [Fact]
    public async Task OpenAsync_LogicalSizeAboveLimit_IsInvalid()
    {
        var file = Discs.GameCube().File;
        await Should.ThrowAsync<InvalidDataException>(() =>
            Discs.OpenAsync(file, options: new RvzReadOptions { MaxLogicalBytes = 0x100000 }));
    }

    [Fact]
    public async Task OpenAsync_GroupBeyondTheFile_IsInvalid()
    {
        var file = Patched(Discs.GameCube(method: RvzMethod.None).File,
            bytes => BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(GroupEntry(bytes, 1)), 0x7FFFFFFF), rehash: false);
        await Should.ThrowAsync<InvalidDataException>(() => Discs.OpenAsync(file));
    }

    [Fact]
    public async Task OpenAsync_RawAreaOverlappingItsGroups_IsInvalid()
    {
        var file = Patched(Discs.GameCube(method: RvzMethod.None).File, bytes =>
        {
            int raw = (int)Be64(bytes, Disc + 184);
            BinaryPrimitives.WriteUInt64BigEndian(bytes.AsSpan(raw + 8), Be64(bytes, raw + 8) + 0x100000);
        }, rehash: false);
        await Should.ThrowAsync<InvalidDataException>(() => Discs.OpenAsync(file));
    }

    [Fact]
    public async Task ReadAt_ExceptionCountAboveLimit_IsInvalid()
    {
        var file = Patched(Discs.Wii(method: RvzMethod.None).File,
            bytes => BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(FirstPartitionGroupData(bytes)), 0xFFFF), rehash: false);
        await using var image = await Discs.OpenAsync(file);

        await Should.ThrowAsync<InvalidDataException>(async () => await image.ReadAtAsync(PartitionStart, new byte[16]));
    }

    [Fact]
    public async Task ReadAt_ExceptionOffsetOutsideItsChunk_IsInvalid()
    {
        // The first 128 KiB chunk (four sectors) carries one exception for sector 3; move it to sector 4.
        var file = Patched(Discs.Wii(method: RvzMethod.None).File, bytes =>
        {
            int data = FirstPartitionGroupData(bytes);
            BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(data)).ShouldBe((ushort)1);
            BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(data + 2), 4 << 10);
        }, rehash: false);
        await using var image = await Discs.OpenAsync(file);

        await Should.ThrowAsync<InvalidDataException>(async () => await image.ReadAtAsync(PartitionStart, new byte[16]));
    }

    [Fact]
    public async Task ReadAt_PackedSizeMismatch_IsInvalid()
    {
        int packedGroup = -1;
        var file = Patched(Discs.GameCube(method: RvzMethod.None).File, bytes =>
        {
            int count = (int)BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(Disc + 196));
            for (int i = 0; i < count && packedGroup < 0; i++)
                if (BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(GroupEntry(bytes, i) + 8)) != 0)
                    packedGroup = i;
            packedGroup.ShouldBeGreaterThanOrEqualTo(0);
            int at = GroupEntry(bytes, packedGroup) + 8;
            BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(at), BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(at)) + 1);
        }, rehash: false);
        await using var image = await Discs.OpenAsync(file);

        await Should.ThrowAsync<InvalidDataException>(async () => await image.ReadAtAsync((long)packedGroup * 0x20000 + 0x1000, new byte[16]));
    }

    [Fact]
    public async Task ReadAt_CorruptCompressedGroup_IsInvalid()
    {
        var file = Patched(Discs.GameCube().File, bytes =>
        {
            var groups = GroupTable(bytes, out _);
            long offset = (long)BinaryPrimitives.ReadUInt32BigEndian(groups.AsSpan(12)) * 4;
            bytes[offset] ^= 0xFF; // Breaks the zstd frame magic of group 1.
        }, rehash: false);
        await using var image = await Discs.OpenAsync(file);

        await Should.ThrowAsync<InvalidDataException>(async () => await image.ReadAtAsync(0x20000, new byte[16]));
    }

    private static long PartitionStart => Discs.WiiPartitionFirstSector * 0x8000;

    private static byte[] Patched(byte[] original, Action<byte[]> edit, bool rehash = true)
    {
        var bytes = original.ToArray();
        edit(bytes);
        if (rehash)
        {
            var disc = bytes.AsSpan(Disc, 0xDC);
            uint count = BinaryPrimitives.ReadUInt32BigEndian(disc[144..]), size = BinaryPrimitives.ReadUInt32BigEndian(disc[148..]);
            SHA1.HashData(bytes.AsSpan((int)Be64(bytes, Disc + 152), (int)(count * size))).CopyTo(disc[160..]);
            SHA1.HashData(bytes.AsSpan(Disc, 0xDC)).CopyTo(bytes.AsSpan(0x10));
            SHA1.HashData(bytes.AsSpan(0, 0x34)).CopyTo(bytes.AsSpan(0x34));
        }
        return bytes;
    }

    // Only for uncompressed (method None) files, whose group table is stored as-is.
    private static int GroupEntry(byte[] bytes, int index) => (int)Be64(bytes, Disc + 200) + index * 12;

    private static int FirstPartitionGroupData(byte[] bytes)
    {
        int partitions = (int)Be64(bytes, Disc + 152);
        int group = (int)BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(partitions + 16 + 8));
        return (int)(BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(GroupEntry(bytes, group))) * 4L);
    }

    // Compressed files store the group table as one zstd frame.
    private static byte[] GroupTable(byte[] bytes, out int count)
    {
        count = (int)BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(Disc + 196));
        int offset = (int)Be64(bytes, Disc + 200), size = (int)BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(Disc + 208));
        using var decompressor = new ZstdSharp.Decompressor();
        return decompressor.Unwrap(bytes.AsSpan(offset, size)).ToArray();
    }

    private static ulong Be64(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt64BigEndian(bytes.AsSpan(offset));
}
