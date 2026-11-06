using System.Buffers.Binary;
using System.Text;

namespace Romd.Media.TestSupport;

/// <summary>Generated PSP-shaped ISO bytes for parser and supervised codec tests. Contains no game data.</summary>
public static class SyntheticPspUmd
{
    private const int _sector = 2048, _pvd = 16 * _sector, _root = 24 * _sector, _game = 25 * _sector,
        _sys = 26 * _sector, _umd = 28 * _sector, _sfo = 29 * _sector, _executable = 30 * _sector;
    /// <summary>Nontrivial codec workload; the tiny parser image can finish native extraction before image observation.</summary>
    public static byte[] CodecImage(bool modern = false)
    {
        byte[] bytes = new byte[32 << 20];
        byte[] layout = Image(modern);
        layout.CopyTo(bytes, 0);
        new Random(0x505350).NextBytes(bytes.AsSpan(layout.Length));
        Both32(bytes, _pvd + 80, (uint)(bytes.Length / _sector));
        return bytes;
    }
    public static byte[] Image(bool modern = false)
    {
        byte[] bytes = new byte[64 * _sector];
        bytes[_pvd] = 1;
        "CD001"u8.CopyTo(bytes.AsSpan(_pvd + 1));
        bytes[_pvd + 6] = 1;
        bytes.AsSpan(_pvd + 8, 32).Fill(32);
        "PSP GAME"u8.CopyTo(bytes.AsSpan(_pvd + 8));
        Both32(bytes, _pvd + 80, 64);
        Both16(bytes, _pvd + 120, 1);
        Both16(bytes, _pvd + 124, 1);
        Both16(bytes, _pvd + 128, _sector);
        bytes[_pvd + 881] = modern ? (byte)2 : (byte)1;
        bytes[_pvd + _sector] = 255;
        "CD001"u8.CopyTo(bytes.AsSpan(_pvd + _sector + 1));
        bytes[_pvd + _sector + 6] = 1;
        Record(bytes, _pvd + 156, [0], 24, _sector, true);
        Self(bytes, _root, 24, 24);
        Self(bytes, _game, 25, 24);
        Self(bytes, _sys, 26, 25);
        int at = _root + 68;
        at += Record(bytes, at, "PSP_GAME"u8.ToArray(), 25, _sector, true);
        Record(bytes, at, Encoding.ASCII.GetBytes(modern ? "UMD_DATA.BIN" : "UMD_DATA.BIN;1"), 28, 32, false);
        "ULUS-12345|0001|G"u8.CopyTo(bytes.AsSpan(_umd));
        byte[] sfo = CreateSfo();
        sfo.CopyTo(bytes, _sfo);
        at = _game + 68;
        at += Record(bytes, at, Encoding.ASCII.GetBytes(modern ? "PARAM.SFO" : "PARAM.SFO;1"), 29, (uint)sfo.Length, false);
        Record(bytes, at, "SYSDIR"u8.ToArray(), 26, _sector, true);
        Record(bytes, _sys + 68, Encoding.ASCII.GetBytes(modern ? "EBOOT.BIN" : "EBOOT.BIN;1"), 30, 512, false);
        "~PSP"u8.CopyTo(bytes.AsSpan(_executable));
        return bytes;
    }

    private static byte[] CreateSfo()
    {
        string[] names = ["BOOTABLE", "CATEGORY", "DISC_ID", "DISC_VERSION"];
        byte[][] values = [[1, 0, 0, 0], "UG\0"u8.ToArray(), "ULUS12345\0"u8.ToArray(), "1.00\0"u8.ToArray()];
        int keyBytes = names.Sum(name => name.Length + 1), data = (84 + keyBytes + 3) / 4 * 4;
        byte[] bytes = new byte[data + 28];
        "\0PSF"u8.CopyTo(bytes);
        Put32(bytes, 4, 0x101);
        Put32(bytes, 8, 84);
        Put32(bytes, 12, (uint)data);
        Put32(bytes, 16, 4);
        int key = 0, value = 0;
        for (int i = 0; i < 4; i++)
        {
            int at = 20 + i * 16, maximum = (values[i].Length + 3) / 4 * 4;
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(at), (ushort)key);
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(at + 2), (ushort)(i == 0 ? 0x404 : 0x204));
            Put32(bytes, at + 4, (uint)values[i].Length);
            Put32(bytes, at + 8, (uint)maximum);
            Put32(bytes, at + 12, (uint)value);
            Encoding.ASCII.GetBytes(names[i]).CopyTo(bytes, 84 + key);
            values[i].CopyTo(bytes, data + value);
            key += names[i].Length + 1;
            value += maximum;
        }
        return bytes;
    }
    private static void Self(byte[] bytes, int at, uint self, uint parent)
    { Record(bytes, at, [0], self, _sector, true); Record(bytes, at + 34, [1], parent, _sector, true); }
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

}
