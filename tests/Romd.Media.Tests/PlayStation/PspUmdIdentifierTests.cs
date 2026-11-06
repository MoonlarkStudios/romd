using System.Buffers.Binary;
using System.Text;
using Romd.Media.PlayStation;
using Romd.Media.TestSupport;
using Romd.Storage;
using Shouldly;
using Xunit;

namespace Romd.Media.Tests.PlayStation;

public sealed class PspUmdIdentifierTests
{
    private const int _sector = 2048, _pvd = 16 * _sector, _root = 24 * _sector, _game = 25 * _sector,
        _sys = 26 * _sector, _umd = 28 * _sector, _sfo = 29 * _sector, _executable = 30 * _sector;
    // SYSDIR's "." and ".." (34 bytes each) and the 44-byte EBOOT.BIN;1 record precede this free slot.
    private const int _extra = _sys + 68 + 44;

    [Fact]
    public async Task InspectAsync_CompleteUmdLayout_ObservesMetadataWithoutReadingExecutableBodyOrDisposingSource()
    {
        var source = new Content(Image());
        var facts = (await PspUmdIdentifier.InspectAsync(source, source.Length)).ShouldNotBeNull();
        facts.ShouldBe(new PspUmdObservedFacts(source.Length, 64, "ULUS12345", "1.00"));
        source.MaximumReadEnd.ShouldBe(_executable + 4);
        source.RequestedBytes.ShouldBeLessThan(12 * _sector);
        source.Disposed.ShouldBeFalse();
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task InspectAsync_PspUnversionedFilenames_RecognizesBothObservedFileStructureVersions(byte version)
    {
        byte[] bytes = Image();
        bytes[_pvd + 881] = version;
        foreach (int at in new[] { _root + 68 + 42, _game + 68, _sys + 68 })
            bytes[at + 32] -= 2;
        (await PspUmdIdentifier.InspectAsync(new Content(bytes), bytes.Length)).ShouldNotBeNull();
    }

    [Fact]
    public async Task InspectAsync_VersionedAndUnversionedAlias_RejectsAmbiguousRequiredFile()
    {
        byte[] bytes = Image();
        Record(bytes, _root + 68 + 42 + 48, "UMD_DATA.BIN"u8.ToArray(), 28, 32, false);
        (await PspUmdIdentifier.InspectAsync(new Content(bytes), bytes.Length)).ShouldBeNull();
    }

    [Theory]
    [InlineData("hidden")]
    [InlineData("associated")]
    [InlineData("multi-extent")]
    [InlineData("extended-attributes")]
    [InlineData("interleave")]
    [InlineData("volume-sequence")]
    [InlineData("endian")]
    [InlineData("outside-volume")]
    [InlineData("latin1-name")]
    [InlineData("separator-name")]
    public async Task InspectAsync_UnrelatedNonConformingEntry_StillRecognizesTheLayout(string change)
    {
        byte[] bytes = Image();
        int at = _extra;
        Record(bytes, at, "EXTRA.BIN;1"u8.ToArray(), 31, 16, false);
        switch (change)
        {
            case "hidden":
                bytes[at + 25] = 1;
                break;
            case "associated":
                bytes[at + 25] = 4;
                break;
            case "multi-extent":
                bytes[at + 25] = 128;
                break;
            case "extended-attributes":
                bytes[at + 1] = 1;
                break;
            case "interleave":
                bytes[at + 26] = 1;
                bytes[at + 27] = 1;
                break;
            case "volume-sequence":
                Both16(bytes, at + 28, 2);
                break;
            case "endian":
                bytes[at + 6] ^= 1;
                break;
            case "outside-volume":
                Both32(bytes, at + 2, 1000);
                break;
            case "latin1-name":
                bytes[at + 33] = 0xc9;
                break;
            case "separator-name":
                bytes[at + 33] = (byte)'/';
                break;
        }
        (await PspUmdIdentifier.InspectAsync(new Content(bytes), bytes.Length))
            .ShouldBe(new PspUmdObservedFacts(bytes.Length, 64, "ULUS12345", "1.00"));
    }

    [Fact]
    public async Task InspectAsync_MultiExtentExecutable_RejectsTheAmbiguousRequiredFile()
    {
        byte[] bytes = Image();
        // A multi-extent file repeats its name; only its final extent clears the multi-extent flag.
        int length = Record(bytes, _sys + 68, "EBOOT.BIN;1"u8.ToArray(), 31, _sector, false);
        bytes[_sys + 68 + 25] = 128;
        Record(bytes, _sys + 68 + length, "EBOOT.BIN;1"u8.ToArray(), 30, 512, false);
        (await PspUmdIdentifier.InspectAsync(new Content(bytes), bytes.Length)).ShouldBeNull();
    }

    [Fact]
    public async Task InspectAsync_ShortPositionalReads_ProducesTheSameObservation()
    {
        var source = new Content(Image()) { Chunk = 7 };
        (await PspUmdIdentifier.InspectAsync(source, source.Length)).ShouldBe(new PspUmdObservedFacts(source.Length, 64, "ULUS12345", "1.00"));
        source.Disposed.ShouldBeFalse();
    }

    [Theory]
    [InlineData("system")]
    [InlineData("volume-endian")]
    [InlineData("volume-outside")]
    [InlineData("sector-size")]
    [InlineData("file-structure-version")]
    [InlineData("duplicate-pvd")]
    [InlineData("no-terminator")]
    [InlineData("root-endian")]
    [InlineData("root-outside")]
    [InlineData("root-file")]
    [InlineData("missing-game")]
    [InlineData("duplicate-game")]
    [InlineData("multi-extent")]
    [InlineData("hidden-executable")]
    [InlineData("latin1-executable")]
    [InlineData("sfo-outside")]
    [InlineData("sfo-overlap")]
    [InlineData("sfo-magic")]
    [InlineData("sfo-version")]
    [InlineData("sfo-count")]
    [InlineData("sfo-key-offset")]
    [InlineData("sfo-length")]
    [InlineData("sfo-alias")]
    [InlineData("category-video")]
    [InlineData("not-bootable")]
    [InlineData("umd-id")]
    [InlineData("executable")]
    [InlineData("truncated-executable")]
    public async Task InspectAsync_UnsupportedOrContradictoryLayout_DoesNotClaimPsp(string change)
    {
        byte[] bytes = Image();
        int data = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(_sfo + 12));
        switch (change)
        {
            case "system":
                bytes[_pvd + 8] ^= 1;
                break;
            case "volume-endian":
                bytes[_pvd + 84] ^= 1;
                break;
            case "volume-outside":
                Both32(bytes, _pvd + 80, 65);
                break;
            case "sector-size":
                Both16(bytes, _pvd + 128, 2352);
                break;
            case "file-structure-version":
                bytes[_pvd + 881] = 3;
                break;
            case "duplicate-pvd":
                bytes.AsSpan(_pvd, _sector).CopyTo(bytes.AsSpan(_pvd + _sector));
                break;
            case "no-terminator":
                bytes[_pvd + _sector] = 2;
                break;
            case "root-endian":
                bytes[_pvd + 156 + 6] ^= 1;
                break;
            case "root-outside":
                Both32(bytes, _pvd + 158, uint.MaxValue);
                break;
            case "root-file":
                bytes[_pvd + 181] = 0;
                break;
            case "missing-game":
                bytes[_root + 68 + 33] = (byte)'X';
                break;
            case "duplicate-game":
                Record(bytes, _root + 68 + 42 + 48, "PSP_GAME"u8.ToArray(), 25, _sector, true);
                break;
            case "multi-extent":
                bytes[_game + 68 + 25] = 128;
                break;
            case "hidden-executable":
                bytes[_sys + 68 + 25] = 1;
                break;
            case "latin1-executable":
                bytes[_sys + 68 + 33] = 0xc5;
                break;
            case "sfo-outside":
                Both32(bytes, _game + 68 + 2, 64);
                break;
            case "sfo-overlap":
                Both32(bytes, _game + 68 + 2, 25);
                break;
            case "sfo-magic":
                bytes[_sfo] = 1;
                break;
            case "sfo-version":
                bytes[_sfo + 4] = 2;
                break;
            case "sfo-count":
                Put32(bytes, _sfo + 16, uint.MaxValue);
                break;
            case "sfo-key-offset":
                Put32(bytes, _sfo + 8, 0);
                break;
            case "sfo-length":
                Put32(bytes, _sfo + 20 + 4, uint.MaxValue);
                break;
            case "sfo-alias":
                Put32(bytes, _sfo + 20 + 16 + 12, 0);
                break;
            case "category-video":
                bytes[_sfo + data + 4] = (byte)'V';
                break;
            case "not-bootable":
                bytes[_sfo + data] = 0;
                break;
            case "umd-id":
                bytes[_umd] = (byte)'X';
                break;
            case "executable":
                bytes[_executable] = 0;
                break;
            case "truncated-executable":
                Both32(bytes, _sys + 68 + 10, 3);
                break;
        }
        (await PspUmdIdentifier.InspectAsync(new Content(bytes), bytes.Length)).ShouldBeNull();
    }

    [Fact]
    public async Task InspectAsync_AdmissionLimit_RejectsBeforeReading()
    {
        var source = new Content(Image());
        (await PspUmdIdentifier.InspectAsync(source, source.Length - 1)).ShouldBeNull();
        source.RequestedBytes.ShouldBe(0);
    }

    [Fact]
    public async Task InspectAsync_Cancellation_IsPropagated()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(() => PspUmdIdentifier.InspectAsync(new Content(Image()), 1 << 20, cancellation.Token));
    }

    private static byte[] Image() => SyntheticPspUmd.Image();

    private static int Record(byte[] bytes, int at, byte[] name, uint block, uint count, bool directory)
    {
        int length = (33 + name.Length + 1) / 2 * 2;
        bytes[at] = (byte)length;
        Both32(bytes, at + 2, block);
        Both32(bytes, at + 10, count);
        bytes[at + 25] = directory ? (byte)2 : (byte)0;
        Both16(bytes, at + 28, 1);
        bytes[at + 32] = (byte)name.Length;
        name.CopyTo(bytes, at + 33);
        return length;
    }
    private static void Put32(byte[] bytes, int at, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(at), value);
    private static void Both32(byte[] bytes, int at, uint value)
    { Put32(bytes, at, value); BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(at + 4), value); }
    private static void Both16(byte[] bytes, int at, ushort value)
    { BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(at), value); BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(at + 2), value); }

    private sealed class Content(byte[] bytes) : IRandomAccessContent
    {
        public long Length => bytes.LongLength;
        public ContentReadIntegrity Integrity => ContentReadIntegrity.None;
        public int Chunk { get; init; } = int.MaxValue;
        public long MaximumReadEnd { get; private set; }
        public long RequestedBytes { get; private set; }
        public bool Disposed { get; private set; }
        public ValueTask<int> ReadAtAsync(long offset, Memory<byte> destination, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            int take = (int)Math.Min(Math.Min(destination.Length, Chunk), Math.Max(0, Length - offset));
            bytes.AsMemory((int)offset, take).CopyTo(destination);
            MaximumReadEnd = Math.Max(MaximumReadEnd, offset + take);
            RequestedBytes += take;
            return ValueTask.FromResult(take);
        }
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }
}
