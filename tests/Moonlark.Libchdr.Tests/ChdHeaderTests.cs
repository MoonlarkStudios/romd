using System.Buffers.Binary;
using Xunit;

namespace Moonlark.Libchdr.Tests;

/// <summary>Verifies slot order, forward-compatible codec values and index bounds.</summary>
public sealed class ChdHeaderTests
{
    /// <summary>All four slots retain their numeric identities, including an unknown future codec.</summary>
    [Fact]
    public void CodecSlotsPreserveOrderAndUnknownValues()
    {
        var future = (ChdCodec)0xdeadbeef;
        var header = new ChdHeader(5, 4096, 4096, 2048, 1,
            ChdCodec.Lzma, ChdCodec.Zstd, future, ChdCodec.None, default, default, default, false);
        Assert.Equal(ChdCodec.Lzma, header.GetCodec(0));
        Assert.Equal(ChdCodec.Zstd, header.GetCodec(1));
        Assert.Equal(future, header.GetCodec(2));
        Assert.Equal(ChdCodec.None, header.GetCodec(3));
        foreach (int slot in new[] { -1, 4, int.MinValue, int.MaxValue })
        {
            var exception = Assert.Throws<ArgumentOutOfRangeException>(() => header.GetCodec(slot));
            Assert.Equal("slot", exception.ParamName);
            Assert.Equal(slot, exception.ActualValue);
        }
    }

    /// <summary>v1–v4 legacy compression values are reported as the v5 codecs MAME maps them to; A/V files do not open.</summary>
    [Theory]
    [InlineData(4, 0U, ChdCodec.None)]
    [InlineData(4, 1U, ChdCodec.Zlib)]
    [InlineData(4, 2U, ChdCodec.Zlib)]
    [InlineData(2, 1U, ChdCodec.Zlib)]
    [InlineData(2, 2U, ChdCodec.Zlib)]
    public void LegacyCompressionReportsItsV5Codec(int version, uint legacy, ChdCodec expected)
    {
        using ChdFile file = OpenCrafted(version == 4 ? CraftedChd.V4(legacy, CraftedChd.LegacyEntry.Stored) : CraftedChd.V2(legacy, true));
        Assert.Equal(expected, file.Header.GetCodec(0));
        for (int slot = 1; slot < 4; slot++)
            Assert.Equal(ChdCodec.None, file.Header.GetCodec(slot));
    }

    /// <summary>A header naming a parent cannot be opened, because no parent can be supplied, so HasParent is false on every open file.</summary>
    [Theory]
    [InlineData(5)]
    [InlineData(4)]
    public void ParentRequiringFilesFailToOpenWithRequiresParent(int version)
    {
        byte[] bytes = version == 5 ? CraftedChd.V5(CraftedChd.V5Entry.Stored) : CraftedChd.V4(0, CraftedChd.LegacyEntry.Stored);
        if (version == 5)
            bytes.AsSpan(V5ParentSha1, ChdSha1.ByteLength).Fill(0x5A);
        else
        {
            BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(16), 1);
            bytes.AsSpan(V4ParentSha1, ChdSha1.ByteLength).Fill(0x5A);
        }
        _ = NativeTestEnvironment.Root;
        Assert.False(ChdFile.TryOpen(new MemoryStream(bytes, false), false, out ChdFile? file, out ChdError error));
        Assert.Null(file);
        Assert.Equal(ChdError.RequiresParent, error);
    }

    /// <summary>A v1–v4 parent hash counts only with the parent flag, as MAME reads it, so an open file reports none.</summary>
    [Fact]
    public void LegacyParentHashWithoutTheFlagIsNotReported()
    {
        byte[] bytes = CraftedChd.V4(0, CraftedChd.LegacyEntry.Stored);
        bytes.AsSpan(V4ParentSha1, ChdSha1.ByteLength).Fill(0x5A);
        using ChdFile file = OpenCrafted(bytes);
        Assert.False(file.Header.HasParent);
        Assert.True(file.Header.ParentSha1.IsEmpty);
    }

    private const int V5ParentSha1 = 104;
    private const int V4ParentSha1 = 68;

    private static ChdFile OpenCrafted(byte[] bytes)
    {
        _ = NativeTestEnvironment.Root;
        Assert.True(ChdFile.TryOpen(new MemoryStream(bytes, false), false, out ChdFile? file, out ChdError error), error.ToString());
        return file;
    }
}
