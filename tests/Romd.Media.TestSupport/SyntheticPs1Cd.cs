using System.Buffers.Binary;
using System.Text;

namespace Romd.Media.TestSupport;

/// <summary>Generated PS-X EXE/ISO records in raw CD frames. No copyrighted game data.</summary>
public static class SyntheticPs1Cd
{
    private const int Sector = 2048, Pvd = 16 * Sector, Root = 24 * Sector, Cnf = 25 * Sector, Exe = 26 * Sector;
    public static byte[] Image()
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
    /// <summary>Full generated codec workload; the small parser layout and boot records stay unchanged.</summary>
    public static byte[] CodecImage()
    {
        byte[] parser = Image();
        byte[] iso = new byte[8 << 20];
        parser.CopyTo(iso, 0);
        new Random(0x4344).NextBytes(iso.AsSpan(parser.Length));
        Both32(iso.AsSpan(Pvd + 80), iso.Length / Sector);
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
    public static byte[] ToRaw(byte[] iso, bool mode2 = false, int pregap = 0)
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
}
