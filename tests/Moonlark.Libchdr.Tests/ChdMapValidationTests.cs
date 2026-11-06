using System.Buffers.Binary;
using Xunit;
using static Moonlark.Libchdr.Tests.CraftedChd;

namespace Moonlark.Libchdr.Tests;

/// <summary>Qualifies map and codec validation against inputs the pinned libchdr would mishandle.</summary>
public sealed class ChdMapValidationTests
{
    private const int MaxDepth = 16;
    private const ulong MiniValue = 0x0102030405060708;
    private const ulong ReadAheadBytes = 64 * 1024;

    /// <summary>Loads the digest-verified native test asset.</summary>
    public ChdMapValidationTests() => _ = NativeTestEnvironment.Root;

    /// <summary>Qualifies crafted controls opening and reading every hunk, including legal self-references and legacy zlib,
    /// with output independent of the destination's prior contents.</summary>
    [Theory]
    [InlineData("v5-stored")]
    [InlineData("v5-self-backward")]
    [InlineData("v5-self-forward")]
    [InlineData("v5-self-chain-at-limit")]
    [InlineData("v4-stored")]
    [InlineData("v4-self-backward")]
    [InlineData("v4-self-chain-at-limit")]
    [InlineData("v4-zlib")]
    [InlineData("v4-zlib-plus")]
    [InlineData("v2-stored")]
    public void CraftedControlsReadEveryHunkThroughItsReferences(string name)
    {
        (byte[] bytes, int[] sources) = Control(name);
        Assert.True(ChdFile.TryOpen(new MemoryStream(bytes, false), false, out ChdFile? file, out ChdError error), error.ToString());
        using (file)
        {
            Assert.Equal((uint)sources.Length, file.Header.HunkCount);
            byte[] hunk = new byte[HunkBytes];
            for (uint index = 0; index < file.Header.HunkCount; index++)
            {
                foreach (byte prefill in (ReadOnlySpan<byte>)[0x00, 0xFF])
                {
                    Array.Fill(hunk, prefill);
                    file.ReadHunk(index, hunk);
                    Assert.True(hunk.AsSpan().IndexOfAnyExcept(Fill(sources[index])) < 0, $"hunk {index} after prefill {prefill:x2}");
                }
            }
        }
    }

    /// <summary>A MINI entry repeats its eight big-endian bytes through any hunk of at least eight bytes, writing nothing beyond it.</summary>
    [Theory]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(HunkBytes)]
    public void LegacyMiniEntriesRepeatTheirEightBytes(int hunkBytes)
    {
        Assert.True(ChdFile.TryOpen(new MemoryStream(V4Sized(hunkBytes, 0, LegacyEntry.Mini(MiniValue)), false), false,
            out ChdFile? file, out ChdError error), error.ToString());
        using (file)
        {
            byte[] memory = Enumerable.Repeat((byte)0xCC, hunkBytes + 8).ToArray();
            file.ReadHunk(0, memory.AsSpan(0, hunkBytes));
            Span<byte> value = stackalloc byte[8];
            BinaryPrimitives.WriteUInt64BigEndian(value, MiniValue);
            for (int offset = 0; offset < hunkBytes; offset++)
                Assert.Equal(value[offset % 8], memory[offset]);
            Assert.True(memory.AsSpan(hunkBytes).IndexOfAnyExcept((byte)0xCC) < 0, "MINI wrote past the hunk");
        }
    }

    /// <summary>Qualifies entries that libchdr would report as read without writing, write past the destination, or route to a
    /// codec slot with no codec.</summary>
    [Theory]
    [InlineData("v5-type-14", ChdError.InvalidData)]
    [InlineData("v5-type-15", ChdError.InvalidData)]
    [InlineData("v5-empty-codec-slot", ChdError.InvalidData)]
    [InlineData("v5-self-out-of-range", ChdError.InvalidData)]
    [InlineData("v5-self-target-beyond-8-bits", ChdError.InvalidData)]
    [InlineData("v5-self-chain-too-deep", ChdError.InvalidData)]
    [InlineData("v5-parent", ChdError.InvalidData)]
    [InlineData("v5-stored-hunk-beyond-24-bits", ChdError.InvalidData)]
    [InlineData("v5-stored-hunk-beyond-24-bits-by-4096", ChdError.InvalidData)]
    [InlineData("v4-type-0", ChdError.InvalidData)]
    [InlineData("v4-type-6", ChdError.InvalidData)]
    [InlineData("v4-type-15", ChdError.InvalidData)]
    [InlineData("v4-type-15-in-second-chunk", ChdError.InvalidData)]
    [InlineData("v4-compressed-without-codec", ChdError.InvalidData)]
    [InlineData("v4-self-out-of-range", ChdError.InvalidData)]
    [InlineData("v4-self-target-above-32-bits", ChdError.InvalidData)]
    [InlineData("v4-self-chain-too-deep", ChdError.InvalidData)]
    [InlineData("v4-mini-short-hunk", ChdError.InvalidData)]
    [InlineData("v4-mini-hunk-of-7", ChdError.InvalidData)]
    [InlineData("v2-compressed-without-codec", ChdError.InvalidData)]
    public void RejectsUnreadableEntriesBeforeAnyHunkIsRead(string name, ChdError expected) => AssertRejected(name, expected);

    /// <summary>Qualifies the reference cycles and parent entries that crash or hang native reads.</summary>
    [Theory]
    [InlineData("v5-self-cycle", ChdError.InvalidData)]
    [InlineData("v5-self-two-cycle", ChdError.InvalidData)]
    [InlineData("v4-self-cycle", ChdError.InvalidData)]
    [InlineData("v4-parent", ChdError.InvalidData)]
    public void RejectsCyclesAndParentEntriesBeforeAnyHunkIsRead(string name, ChdError expected) => AssertRejected(name, expected);

    /// <summary>Legacy headers may name only v1–v4 codecs, as MAME requires; the A/V codec is unsupported in either version.</summary>
    [Theory]
    [InlineData("v4-av-codec", ChdError.UnsupportedFormat)]
    [InlineData("v5-av-codec", ChdError.UnsupportedFormat)]
    [InlineData("v5-av-codec-in-slot-1", ChdError.UnsupportedFormat)]
    [InlineData("v4-zlib-fourcc", ChdError.InvalidData)]
    [InlineData("v4-lzma-fourcc", ChdError.InvalidData)]
    public void RejectsCodecsLibchdrDecodesUnsafely(string name, ChdError expected) => AssertRejected(name, expected);

    /// <summary>A v1–v4 entry whose stored bytes end past the source is rejected as libchdr's own open check rejects it,
    /// with or without read-ahead. That check and the read-ahead window checks add the offset and length without a wrap
    /// guard, so an entry whose end wraps past 2^64 would otherwise open and copy from before the window.</summary>
    [Theory]
    [InlineData("v4-stored-offset-wraps")]
    [InlineData("v4-zlib-offset-wraps")]
    [InlineData("v4-stored-straddles-eof")]
    [InlineData("v4-stored-beyond-eof")]
    [InlineData("v4-stored-end-at-long-max")]
    [InlineData("v4-zlib-beyond-eof")]
    public void RejectsLegacyEntriesEndingPastTheSource(string name)
    {
        AssertRejected(name, ChdError.InvalidFile);
        ChdException failure = Assert.Throws<ChdException>(() =>
            ChdFile.Open(new MemoryStream(Malformed(name), false), false, new ChdOpenOptions { ReadAheadBytes = ReadAheadBytes }));
        Assert.Equal(ChdError.InvalidFile, failure.Error);
    }

    /// <summary>A rejection with leaveOpen preserves the caller's stream.</summary>
    [Fact]
    public void RejectionWithLeaveOpenPreservesTheStream()
    {
        using var stream = new MemoryStream(Malformed("v5-type-14"), false);
        Assert.False(ChdFile.TryOpen(stream, true, out ChdFile? file, out ChdError error));
        Assert.Null(file);
        Assert.Equal(ChdError.InvalidData, error);
        Assert.True(stream.CanRead);
    }

    private static void AssertRejected(string name, ChdError expected)
    {
        var source = new CountingStream(Malformed(name));
        Assert.False(ChdFile.TryOpen(source, false, out ChdFile? file, out ChdError error));
        Assert.Null(file);
        Assert.Equal(expected, error);
        Assert.Equal(1, source.Disposals);
        ChdException failure = Assert.Throws<ChdException>(() => ChdFile.Open(new MemoryStream(Malformed(name), false), false));
        Assert.Equal(expected, failure.Error);
    }

    private static (byte[] Bytes, int[] Sources) Control(string name) => name switch
    {
        "v5-stored" => (V5(V5Entry.Stored, V5Entry.Stored), [0, 1]),
        "v5-self-backward" => (V5(V5Entry.Stored, V5Entry.Self(0)), [0, 0]),
        "v5-self-forward" => (V5(V5Entry.Self(1), V5Entry.Stored), [1, 1]),
        "v5-self-chain-at-limit" => (V5(V5Chain(MaxDepth)), new int[MaxDepth + 1]),
        "v4-stored" => (V4(0, LegacyEntry.Stored, LegacyEntry.Stored), [0, 1]),
        "v4-self-backward" => (V4(0, LegacyEntry.Stored, LegacyEntry.Self(0)), [0, 0]),
        "v4-self-chain-at-limit" => (V4(0, LegacyChain(MaxDepth)), new int[MaxDepth + 1]),
        "v4-zlib" => (V4(1, LegacyEntry.Deflated, LegacyEntry.Deflated), [0, 1]),
        "v4-zlib-plus" => (V4(2, LegacyEntry.Deflated), [0]),
        "v2-stored" => (V2(0, true, true), [0, 1]),
        _ => throw new ArgumentOutOfRangeException(nameof(name)),
    };

    private static byte[] Malformed(string name) => name switch
    {
        "v5-type-14" => V5(V5Entry.Stored, new(14)),
        "v5-type-15" => V5(V5Entry.Stored, new(15)),
        "v5-empty-codec-slot" => V5(V5Entry.Stored, new(1)),
        "v5-self-out-of-range" => V5(V5Entry.Stored, V5Entry.Self(9)),
        "v5-self-target-beyond-8-bits" => V5(V5Entry.Stored, V5Entry.Self(0xFFFFFF)),
        "v5-self-chain-too-deep" => V5(V5Chain(MaxDepth + 1)),
        "v5-parent" => V5(V5Entry.Stored, new(V5Parent)),
        "v5-stored-hunk-beyond-24-bits" => V5Sized(1 << 24, "zlib", V5Entry.Stored),
        "v5-stored-hunk-beyond-24-bits-by-4096" => V5Sized((1 << 24) + 4096, "zlib", V5Entry.Stored),
        "v5-av-codec-in-slot-1" => WithCodecSlot(V5(V5Entry.Stored), 1, "avhu"),
        "v5-self-cycle" => V5(V5Entry.Stored, V5Entry.Self(1)),
        "v5-self-two-cycle" => V5(V5Entry.Stored, V5Entry.Self(2), V5Entry.Self(1)),
        "v5-av-codec" => V5Sized(HunkBytes, "avhu", V5Entry.Stored),
        "v4-type-0" => V4(0, LegacyEntry.Stored, new(0x00)),
        "v4-type-6" => V4(0, LegacyEntry.Stored, new(0x06)),
        "v4-type-15" => V4(0, LegacyEntry.Stored, new(0x0F)),
        "v4-type-15-in-second-chunk" => V4(0, [.. Enumerable.Range(0, 300).Select(index => index == 290 ? new LegacyEntry(0x0F) : LegacyEntry.Mini(MiniValue))]),
        "v4-compressed-without-codec" => V4(0, LegacyEntry.Stored, new(0x10 | LegacyCompressed)),
        "v4-self-out-of-range" => V4(0, LegacyEntry.Stored, LegacyEntry.Self(5)),
        "v4-self-target-above-32-bits" => V4(0, LegacyEntry.Stored, LegacyEntry.Self(1UL << 32)),
        "v4-self-chain-too-deep" => V4(0, LegacyChain(MaxDepth + 1)),
        "v4-self-cycle" => V4(0, LegacyEntry.Stored, LegacyEntry.Self(1)),
        "v4-parent" => V4(0, LegacyEntry.Stored, new(LegacyParent)),
        "v4-mini-short-hunk" => V4Sized(4, 0, LegacyEntry.Mini(MiniValue)),
        "v4-mini-hunk-of-7" => V4Sized(7, 0, LegacyEntry.Mini(MiniValue)),
        "v4-av-codec" => V4(3, LegacyEntry.Stored),
        "v4-zlib-fourcc" => V4(0x7A6C6962, LegacyEntry.Stored),
        "v4-lzma-fourcc" => V4(0x6C7A6D61, LegacyEntry.Stored),
        "v2-compressed-without-codec" => V2(0, true, false),
        "v4-stored-offset-wraps" => WithV4Offset(V4(0, LegacyEntry.Stored, LegacyEntry.Stored), 1, ulong.MaxValue),
        "v4-zlib-offset-wraps" => WithV4Offset(V4(1, LegacyEntry.Deflated, LegacyEntry.Deflated), 1, ulong.MaxValue - 8),
        "v4-stored-straddles-eof" => WithV4Offset(V4(0, LegacyEntry.Stored, LegacyEntry.Stored), 1, StoredV4Bytes(2) - HunkBytes / 2),
        "v4-stored-beyond-eof" => WithV4Offset(V4(0, LegacyEntry.Stored, LegacyEntry.Stored), 1, StoredV4Bytes(2) + HunkBytes),
        "v4-stored-end-at-long-max" => WithV4Offset(V4(0, LegacyEntry.Stored, LegacyEntry.Stored), 1, long.MaxValue - HunkBytes),
        "v4-zlib-beyond-eof" => WithV4Offset(V4(1, LegacyEntry.Deflated, LegacyEntry.Deflated), 1, 1UL << 40),
        _ => throw new ArgumentOutOfRangeException(nameof(name)),
    };

    /// <summary>The byte length of a v4 file of <paramref name="entries"/> stored hunks.</summary>
    private static ulong StoredV4Bytes(int entries) => (ulong)V4(0, [.. Enumerable.Repeat(LegacyEntry.Stored, entries)]).Length;

    /// <summary>Names <paramref name="codec"/> in a v5 header codec slot after slot 0.</summary>
    private static byte[] WithCodecSlot(byte[] v5, int slot, string codec)
    {
        System.Text.Encoding.ASCII.GetBytes(codec).CopyTo(v5.AsSpan(16 + slot * 4, 4));
        return v5;
    }

    /// <summary>Hunk 0 is stored and hunk n references hunk n - 1, so the last hunk sits <paramref name="depth"/> hops away.</summary>
    private static V5Entry[] V5Chain(int depth) =>
        [V5Entry.Stored, .. Enumerable.Range(0, depth).Select(target => V5Entry.Self((ulong)target))];

    /// <inheritdoc cref="V5Chain"/>
    private static LegacyEntry[] LegacyChain(int depth) =>
        [LegacyEntry.Stored, .. Enumerable.Range(0, depth).Select(target => LegacyEntry.Self((ulong)target))];

    private sealed class CountingStream(byte[] bytes) : MemoryStream(bytes, false)
    {
        internal int Disposals { get; private set; }
        protected override void Dispose(bool disposing) { if (disposing) Disposals++; base.Dispose(disposing); }
    }
}
