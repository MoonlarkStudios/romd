using Xunit;

namespace Moonlark.Libchdr.Tests;

/// <summary>Qualifies the read-ahead ceiling: what is taken, what is refused, and that reads stay exact.</summary>
public sealed class ChdReadAheadTests
{
    private const string Compressed = "cd-cdlz";

    /// <summary>A budget of at least one hunk is taken exactly, a smaller one is refused, and every hunk still decodes exactly.</summary>
    [Theory]
    [InlineData(8_192UL, 0UL)]
    [InlineData(19_584UL, 19_584UL)]
    [InlineData(32_768UL, 32_768UL)]
    public void BudgetsOfAtLeastOneHunkEngageExactly(ulong requested, ulong taken)
    {
        byte[] logical = File.ReadAllBytes(NativeTestEnvironment.Fixture(Compressed + ".logical"));
        using ChdFile file = ChdFile.Open(NativeTestEnvironment.Fixture(Compressed + ".chd"), new ChdOpenOptions { ReadAheadBytes = requested });
        Assert.Equal(19_584U, file.Header.HunkBytes);
        Assert.Equal(taken, file.ReadAheadBytes);
        byte[] hunk = new byte[file.Header.HunkBytes];
        for (uint index = 0; index < file.Header.HunkCount; index++)
        {
            file.ReadHunk(index, hunk);
            Assert.True(logical.AsSpan((int)(index * file.Header.HunkBytes), hunk.Length).SequenceEqual(hunk), $"hunk {index}");
        }
    }

    /// <summary>libchdr allocates the whole ceiling and its window holds file bytes, so a ceiling beyond the source is clamped
    /// to the source length; a source shorter than one hunk then leaves read-ahead off.</summary>
    [Theory]
    [InlineData(Compressed, true)]
    [InlineData("dvd-zstd", false)]
    public void CeilingsBeyondTheSourceAreClampedToItsLength(string name, bool engages)
    {
        string path = NativeTestEnvironment.Fixture(name + ".chd");
        ulong length = (ulong)new FileInfo(path).Length;
        using ChdFile file = ChdFile.Open(path, new ChdOpenOptions { ReadAheadBytes = 1UL << 40 });
        Assert.Equal(engages, length >= file.Header.HunkBytes);
        Assert.Equal(engages ? length : 0UL, file.ReadAheadBytes);
    }

    /// <summary>A read-ahead allocation that fails leaves a valid file open with read-ahead off, as libchdr documents.</summary>
    [Fact]
    public void FailedReadAheadAllocationLeavesAValidFileOpen()
    {
        byte[] bytes = File.ReadAllBytes(NativeTestEnvironment.Fixture("dvd-zstd.chd"));
        var options = new ChdOpenOptions { ReadAheadBytes = ulong.MaxValue };
        Assert.True(ChdFile.TryOpen(new HugeSource(bytes), false, out ChdFile? file, out ChdError error, options), error.ToString());
        using (file)
        {
            Assert.Equal(0UL, file.ReadAheadBytes);
            file.ReadHunk(0, new byte[file.Header.HunkBytes]);
        }
    }

    /// <summary>The source length is read once, whether or not a read-ahead ceiling needs it for clamping.</summary>
    [Fact]
    public void TheSourceLengthIsReadOnceWithOrWithoutACeiling()
    {
        byte[] bytes = File.ReadAllBytes(NativeTestEnvironment.Fixture(Compressed + ".chd"));
        var plain = new LengthCountingSource(bytes);
        var clamped = new LengthCountingSource(bytes);
        using (ChdFile.Open(plain, false))
        { }
        using (ChdFile.Open(clamped, false, new ChdOpenOptions { ReadAheadBytes = 1UL << 20 }))
        { }
        Assert.Equal(1, plain.LengthReads);
        Assert.Equal(1, clamped.LengthReads);
    }

    private sealed class LengthCountingSource(byte[] bytes) : IChdDataSource
    {
        internal int LengthReads { get; private set; }

        public long Length { get { LengthReads++; return bytes.LongLength; } }

        public int Read(long offset, Span<byte> destination)
        {
            int take = (int)Math.Min(destination.Length, Math.Max(0, bytes.LongLength - offset));
            bytes.AsSpan((int)offset, take).CopyTo(destination);
            return take;
        }

        public void Dispose() { }
    }

    /// <summary>Serves a real container while claiming a length no read-ahead allocation can satisfy.</summary>
    private sealed class HugeSource(byte[] bytes) : IChdDataSource
    {
        public long Length => 1L << 62;

        public int Read(long offset, Span<byte> destination)
        {
            if (offset >= bytes.LongLength)
                return 0;
            int take = (int)Math.Min(destination.Length, bytes.LongLength - offset);
            bytes.AsSpan((int)offset, take).CopyTo(destination);
            return take;
        }

        public void Dispose() { }
    }
}
