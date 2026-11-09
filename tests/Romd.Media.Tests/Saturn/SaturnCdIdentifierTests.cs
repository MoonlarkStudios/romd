using System.Buffers.Binary;
using Moonlark.Libchdr;
using Romd.Media.Cd;
using Romd.Media.PlayStation;
using Romd.Media.Saturn;
using Romd.Media.TestSupport;
using Romd.Storage;
using Shouldly;
using Xunit;

namespace Romd.Media.Tests.Saturn;

public sealed class SaturnCdIdentifierTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(150)]
    public void Inspect_ShortReadsAndStoredPregap_ObservesOnlyTheSaturnHeader(int pregap)
    {
        using var source = new Raw(SyntheticSaturnCd.Raw(pregap));
        var facts = SaturnCdIdentifier.Inspect(source, ChdCdTrackType.Mode1Raw, pregap).ShouldNotBeNull();
        facts.ProductCode.ShouldBe("T-00001");
        facts.AreaSymbols.ShouldBe("JUE");
        facts.InitialProgramBytes.ShouldBe(4096u);
        facts.FirstTrackIndex01.ShouldBe(pregap);
        SaturnCdIdentifier.Valid(facts).ShouldBeTrue();
        source.ReadBytes.ShouldBe(2352);
        Ps1CdIdentifier.Inspect(source, ChdCdTrackType.Mode1Raw, pregap, 64 + pregap).ShouldBeNull();
    }

    [Theory]
    [InlineData("signature")]
    [InlineData("sync")]
    [InlineData("mode")]
    [InlineData("product")]
    [InlineData("area")]
    [InlineData("short-ip")]
    [InlineData("long-ip")]
    [InlineData("truncated")]
    public void Inspect_MalformedOrUnsupportedHeader_DoesNotAuthorizeCompression(string mutation)
    {
        byte[] raw = SyntheticSaturnCd.Raw();
        switch (mutation)
        {
            case "signature":
                raw[16] ^= 1;
                break;
            case "sync":
                raw[1] ^= 1;
                break;
            case "mode":
                raw[15] = 2;
                break;
            case "product":
                raw.AsSpan(16 + 0x20, 10).Fill((byte)' ');
                break;
            case "area":
                raw[16 + 0x40] = (byte)'X';
                break;
            case "short-ip":
                BinaryPrimitives.WriteUInt32BigEndian(raw.AsSpan(16 + 0xe0), 255);
                break;
            case "long-ip":
                BinaryPrimitives.WriteUInt32BigEndian(raw.AsSpan(16 + 0xe0), 32769);
                break;
            case "truncated":
                raw = raw[..2352];
                break;
        }
        using var source = new Raw(raw);
        SaturnCdIdentifier.Inspect(source, ChdCdTrackType.Mode1Raw, 0).ShouldBeNull();
    }

    [Theory]
    [InlineData(ChdCdTrackType.Mode2Raw, 0)]
    [InlineData(ChdCdTrackType.Audio, 0)]
    [InlineData(ChdCdTrackType.Mode1Raw, -1)]
    [InlineData(ChdCdTrackType.Mode1Raw, 450001)]
    public void Inspect_UnsupportedGeometry_DoesNotRead(ChdCdTrackType type, long index)
    {
        using var source = new Raw(SyntheticSaturnCd.Raw());
        SaturnCdIdentifier.Inspect(source, type, index).ShouldBeNull();
        source.ReadBytes.ShouldBe(0);
    }

    [Fact]
    public void Inspect_PolyglotPs1AndSaturnHeader_RefusesAmbiguousFamily()
    {
        byte[] image = SyntheticPs1Cd.Image();
        SyntheticSaturnCd.Image().AsSpan(0, 256).CopyTo(image);
        using var source = new Raw(SyntheticPs1Cd.ToRaw(image));
        Should.Throw<InvalidDataException>(() => CdBootObservation.Inspect(source, ChdCdTrackType.Mode1Raw, 0, 64, default));
    }

    [Fact]
    public void Inspect_PreCancelled_DoesNotRead()
    {
        using var source = new Raw(SyntheticSaturnCd.Raw());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Should.Throw<OperationCanceledException>(() => SaturnCdIdentifier.Inspect(source, ChdCdTrackType.Mode1Raw, 0, cancellation.Token));
        source.ReadBytes.ShouldBe(0);
    }

    private sealed class Raw(byte[] bytes) : ISynchronousRawContent
    {
        public long Length => bytes.LongLength;
        public int ReadBytes { get; private set; }
        public int ReadAt(long offset, Span<byte> destination)
        {
            int count = (int)Math.Min(7, Math.Min(destination.Length, bytes.LongLength - offset));
            bytes.AsSpan((int)offset, count).CopyTo(destination);
            ReadBytes += count;
            return count;
        }
        public void Dispose() { }
    }
}
