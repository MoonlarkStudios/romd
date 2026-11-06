using System.Buffers.Binary;
using System.Text;
using Moonlark.Libchdr;
using Romd.Media.PlayStation;
using Romd.Storage;
using Shouldly;
using Xunit;

namespace Romd.Media.Tests.PlayStation;

public sealed class Ps1CdIdentifierTests
{
    private const int Sector = 2048, Pvd = 16 * Sector, Root = 24 * Sector, Cnf = 25 * Sector, Exe = 26 * Sector;

    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 0)]
    [InlineData(true, 150)]
    public void Inspect_RawDataTrackAndStoredFirstPregap_ReturnsBoundedPs1Facts(bool mode2, int pregap)
    {
        var raw = new Raw(ToRaw(Image(), mode2, pregap)) { MaximumRead = 7 };
        var facts = Ps1CdIdentifier.Inspect(raw, mode2 ? ChdCdTrackType.Mode2Raw : ChdCdTrackType.Mode1Raw, pregap, 64 + pregap).ShouldNotBeNull();
        facts.Profile.ShouldBe(Ps1CdIdentifier.Profile);
        facts.FirstTrackIndex01.ShouldBe(pregap);
        facts.BootPath.ShouldBe("SLUS_000.01;1");
        facts.BootBlock.ShouldBe(26u);
        facts.LoadBytes.ShouldBe(2048u);
        Ps1CdIdentifier.Valid(facts).ShouldBeTrue();
        raw.Disposed.ShouldBeFalse();
        raw.RequestedBytes.ShouldBeLessThan(1 << 20);
    }

    [Theory]
    [InlineData("BOOT=cdrom:\\SLUS_000.01;1\r\n\u001a", true)]
    [InlineData("BOOT=cdrom:\\SLUS_000.01;1\r\n\u001a\0\0", true)]
    [InlineData("BOOT=cdrom:\\SLUS_000.01;1\u001a\r\n", false)]
    [InlineData("BOOT=cdrom:\\SLUS_000.01;1\r\n\u001aBOOT2=x", false)]
    public void Inspect_DosEndOfFile_IsAllowedOnlyAsATrailingTerminator(string config, bool admitted)
    {
        byte[] iso = Image();
        Config(iso, config);
        (Ps1CdIdentifier.Inspect(new Raw(ToRaw(iso)), ChdCdTrackType.Mode1Raw, 0, 64) is not null).ShouldBe(admitted);
    }

    [Fact]
    public void Inspect_MixedDiscVolumeMayIncludeAudio_ButBootReadsStayInsideDataBin()
    {
        byte[] iso = Image();
        Both32(iso.AsSpan(Pvd + 80), 500);
        Config(iso, "BOOT=cdrom:SLUS_000.01;1\r\n\u001a");
        var raw = new Raw(ToRaw(iso));
        Ps1CdIdentifier.Inspect(raw, ChdCdTrackType.Mode1Raw, 0, 64).ShouldBeNull();
        Ps1CdIdentifier.Inspect(raw, ChdCdTrackType.Mode1Raw, 0, 500).ShouldNotBeNull().VolumeBlocks.ShouldBe(500u);
        Ps1CdIdentifier.Inspect(raw, ChdCdTrackType.Mode1Raw, 0, 499).ShouldBeNull();
        Both32(iso.AsSpan(Root + 68 + 46 + 2), 100);
        Ps1CdIdentifier.Inspect(new Raw(ToRaw(iso)), ChdCdTrackType.Mode1Raw, 0, 500).ShouldBeNull();
    }

    [Fact]
    public void Inspect_BootExtentSpillsIntoAudio_RefusesEvenWhenItsHeaderFitsTheFirstDataBin()
    {
        byte[] iso = Image();
        Both32(iso.AsSpan(Pvd + 80), 500);
        Both32(iso.AsSpan(Root + 68 + 46 + 10), 100 * Sector);
        Ps1CdIdentifier.Inspect(new Raw(ToRaw(iso)), ChdCdTrackType.Mode1Raw, 0, 500).ShouldBeNull();
    }

    [Theory]
    [InlineData(-1, 64)]
    [InlineData(450001, 450001)]
    [InlineData(0, 63)]
    [InlineData(0, 450001)]
    public void Inspect_UnboundedOrContradictoryGeometry_DoesNotReadTheBorrowedTrack(long index01, long discFrames)
    {
        var source = new Raw(ToRaw(Image()));
        Ps1CdIdentifier.Inspect(source, ChdCdTrackType.Mode1Raw, index01, discFrames).ShouldBeNull();
        source.RequestedBytes.ShouldBe(0);
        source.Disposed.ShouldBeFalse();
    }

    [Fact]
    public void Inspect_NoSystemCnf_RecognizesTheDefaultPsxExeOnly()
    {
        byte[] iso = Image();
        iso.AsSpan(Root + 68, Sector - 68).Clear();
        Record(iso.AsSpan(Root + 68), "PSX.EXE;1", 26, 4096, 0);
        Ps1CdIdentifier.Inspect(new Raw(ToRaw(iso)), ChdCdTrackType.Mode1Raw, 0, 64).ShouldNotBeNull().BootPath.ShouldBe("PSX.EXE;1");
    }

    [Theory]
    [InlineData("ps2-boot2")]
    [InlineData("elf")]
    [InlineData("wrong-system")]
    [InlineData("endian-volume")]
    [InlineData("volume-overrun")]
    [InlineData("root-overrun")]
    [InlineData("nested-boot")]
    [InlineData("traversal")]
    [InlineData("duplicate-boot")]
    [InlineData("duplicate-directory-name")]
    [InlineData("program-truncated")]
    [InlineData("load-overrun")]
    [InlineData("unaligned-pc")]
    [InlineData("root-overlap")]
    public void Inspect_UnsupportedOrContradictoryBoot_DoesNotAdmit(string mutation)
    {
        byte[] iso = Image();
        switch (mutation)
        {
            case "ps2-boot2":
                Config(iso, "BOOT2=cdrom0:\\SLUS_000.01;1\n");
                break;
            case "elf":
                "\u007fELF"u8.CopyTo(iso.AsSpan(Exe));
                break;
            case "wrong-system":
                iso[Pvd + 8] = (byte)'X';
                break;
            case "endian-volume":
                iso[Pvd + 84] ^= 1;
                break;
            case "volume-overrun":
                Both32(iso.AsSpan(Pvd + 80), 1000);
                break;
            case "root-overrun":
                Both32(iso.AsSpan(Pvd + 156 + 10), 300 << 10);
                break;
            case "nested-boot":
                Config(iso, "BOOT=cdrom:\\DIR\\SLUS_000.01;1\n");
                break;
            case "traversal":
                Config(iso, "BOOT=cdrom:\\..\\SLUS_000.01;1\n");
                break;
            case "duplicate-boot":
                Config(iso, "BOOT=cdrom:\\SLUS_000.01;1\nBOOT=cdrom:\\SLUS_000.01;1\n");
                break;
            case "duplicate-directory-name":
                Record(iso.AsSpan(Root + 68 + 46 + 46), "SLUS_000.01;1", 29, 4096, 0);
                break;
            case "program-truncated":
                BinaryPrimitives.WriteUInt32LittleEndian(iso.AsSpan(Exe + 0x1c), 4096);
                break;
            case "load-overrun":
                BinaryPrimitives.WriteUInt32LittleEndian(iso.AsSpan(Exe + 0x18), 0x801ffff0);
                break;
            case "unaligned-pc":
                BinaryPrimitives.WriteUInt32LittleEndian(iso.AsSpan(Exe + 0x10), 0x80010001);
                break;
            case "root-overlap":
                Both32(iso.AsSpan(Root + 68 + 46 + 2), 24);
                break;
        }
        Ps1CdIdentifier.Inspect(new Raw(ToRaw(iso)), ChdCdTrackType.Mode1Raw, 0, 64).ShouldBeNull();
    }

    [Theory]
    [InlineData("mode")]
    [InlineData("sync")]
    [InlineData("form2")]
    [InlineData("subheader")]
    public void Inspect_DamagedOrUnsupportedRawSector_Rejects(string mutation)
    {
        byte[] raw = ToRaw(Image(), true);
        int at = 16 * 2352;
        raw[at + (mutation switch { "mode" => 15, "sync" => 1, "form2" => 18, _ => 20 })] ^= mutation == "form2" ? (byte)0x20 : (byte)1;
        Ps1CdIdentifier.Inspect(new Raw(raw), ChdCdTrackType.Mode2Raw, 0, 64).ShouldBeNull();
    }

    [Fact]
    public void Inspect_PreCancelled_DoesNotReadOrDisposeTheBorrowedSource()
    {
        var source = new Raw(ToRaw(Image()));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Should.Throw<OperationCanceledException>(() => Ps1CdIdentifier.Inspect(source, ChdCdTrackType.Mode1Raw, 0, 64, cancellation.Token));
        source.RequestedBytes.ShouldBe(0);
        source.Disposed.ShouldBeFalse();
    }
    private static byte[] Image()
    {
        byte[] iso = new byte[64 * Sector];
        iso[Pvd] = 1;
        "CD001"u8.CopyTo(iso.AsSpan(Pvd + 1));
        iso[Pvd + 6] = 1;
        iso.AsSpan(Pvd + 8, 32).Fill((byte)' ');
        "PLAYSTATION"u8.CopyTo(iso.AsSpan(Pvd + 8));
        Both32(iso.AsSpan(Pvd + 80), 64);
        Both16(iso.AsSpan(Pvd + 120), 1);
        Both16(iso.AsSpan(Pvd + 124), 1);
        Both16(iso.AsSpan(Pvd + 128), Sector);
        iso[17 * Sector] = 255;
        "CD001"u8.CopyTo(iso.AsSpan(17 * Sector + 1));
        iso[17 * Sector + 6] = 1;
        Record(iso.AsSpan(Pvd + 156), "\0", 24, Sector, 2);
        Record(iso.AsSpan(Root), "\0", 24, Sector, 2);
        Record(iso.AsSpan(Root + 34), "\u0001", 24, Sector, 2);
        Record(iso.AsSpan(Root + 68 + 46), "SLUS_000.01;1", 26, 4096, 0);
        Config(iso, "BOOT=cdrom:\\SLUS_000.01;1\r\nVMODE=NTSC\r\n");
        "PS-X EXE"u8.CopyTo(iso.AsSpan(Exe));
        BinaryPrimitives.WriteUInt32LittleEndian(iso.AsSpan(Exe + 0x10), 0x80010000);
        BinaryPrimitives.WriteUInt32LittleEndian(iso.AsSpan(Exe + 0x18), 0x80010000);
        BinaryPrimitives.WriteUInt32LittleEndian(iso.AsSpan(Exe + 0x1c), 2048);
        return iso;
    }
    private static void Config(byte[] iso, string text)
    { iso.AsSpan(Cnf, Sector).Clear(); Encoding.ASCII.GetBytes(text).CopyTo(iso, Cnf); Record(iso.AsSpan(Root + 68), "SYSTEM.CNF;1", 25, text.Length, 0); }
    private static void Record(Span<byte> bytes, string name, int block, int count, byte flags)
    {
        int size = 33 + name.Length + (name.Length % 2 == 0 ? 1 : 0);
        bytes[..size].Clear();
        bytes[0] = (byte)size;
        Both32(bytes[2..], block);
        Both32(bytes[10..], count);
        bytes[25] = flags;
        Both16(bytes[28..], 1);
        bytes[32] = (byte)name.Length;
        Encoding.ASCII.GetBytes(name).CopyTo(bytes[33..]);
    }
    private static void Both32(Span<byte> bytes, int value) { BinaryPrimitives.WriteUInt32LittleEndian(bytes, (uint)value); BinaryPrimitives.WriteUInt32BigEndian(bytes[4..], (uint)value); }
    private static void Both16(Span<byte> bytes, int value) { BinaryPrimitives.WriteUInt16LittleEndian(bytes, (ushort)value); BinaryPrimitives.WriteUInt16BigEndian(bytes[2..], (ushort)value); }
    private static byte[] ToRaw(byte[] iso, bool mode2 = false, int pregap = 0)
    {
        byte[] raw = new byte[(iso.Length / Sector + pregap) * 2352];
        for (int i = 0; i < iso.Length / Sector; i++)
        {
            int at = (i + pregap) * 2352;
            raw.AsSpan(at + 1, 10).Fill(0xff);
            raw[at + 15] = mode2 ? (byte)2 : (byte)1;
            iso.AsSpan(i * Sector, Sector).CopyTo(raw.AsSpan(at + (mode2 ? 24 : 16)));
        }
        return raw;
    }
    private sealed class Raw(byte[] bytes) : ISynchronousRawContent
    {
        public long Length => bytes.Length;
        public int MaximumRead { get; init; } = int.MaxValue;
        public long RequestedBytes { get; private set; }
        public bool Disposed { get; private set; }
        public int ReadAt(long offset, Span<byte> destination)
        {
            int count = (int)Math.Min(Math.Min(destination.Length, MaximumRead), Math.Max(0, bytes.Length - offset));
            bytes.AsSpan((int)Math.Min(offset, bytes.Length), count).CopyTo(destination);
            RequestedBytes += count;
            return count;
        }
        public void Dispose() => Disposed = true;
    }
}
