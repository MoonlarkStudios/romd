using System.Buffers.Binary;
using System.Text;

namespace Romd.Media.TestSupport;

/// <summary>Small ISO9660 records and a MIPS ELF boot header; no game bytes or assertion of DVD media.</summary>
public static class SyntheticPs2Iso
{
    public const int SectorBytes = 2048, VolumeBlocks = 64, PrimaryDescriptorBlock = 16,
        RootDirectoryBlock = 24, SystemCnfBlock = 25, BootExecutableBlock = 26;
    public const int RootDirectoryOffset = RootDirectoryBlock * SectorBytes,
        SystemCnfOffset = SystemCnfBlock * SectorBytes, BootExecutableOffset = BootExecutableBlock * SectorBytes;
    public const int SystemCnfRecordOffset = RootDirectoryOffset + 68, BootRecordOffset = SystemCnfRecordOffset + 46;
    public const int BootExecutableBytes = 512;
    public const uint EntryPoint = 0x00100000;
    public const string BootSerial = "SLUS_203.12", BootPath = "cdrom0:\\SLUS_203.12;1";
    public const string BootConfiguration = "BOOT2 = cdrom0:\\SLUS_203.12;1\r\nVER = 1.00\r\nVMODE = NTSC\r\n";

    /// <summary>Nontrivial codec workload; the small parser layout can finish before native image observation.</summary>
    public static byte[] CodecImage()
    {
        byte[] bytes = new byte[32 << 20];
        byte[] layout = Create();
        layout.CopyTo(bytes, 0);
        new Random(0x505332).NextBytes(bytes.AsSpan(layout.Length));
        Both32(bytes, PrimaryDescriptorBlock * SectorBytes + 80, (uint)(bytes.Length / SectorBytes));
        return bytes;
    }

    public static byte[] Create()
    {
        byte[] bytes = new byte[VolumeBlocks * SectorBytes];
        Descriptor(bytes, PrimaryDescriptorBlock, 1);
        Descriptor(bytes, PrimaryDescriptorBlock + 1, 255);
        int pvd = PrimaryDescriptorBlock * SectorBytes;
        Both32(bytes, pvd + 80, VolumeBlocks);
        Both16(bytes, pvd + 120, 1);
        Both16(bytes, pvd + 124, 1);
        Both16(bytes, pvd + 128, SectorBytes);
        Both32(bytes, pvd + 132, 10);
        U32(bytes, pvd + 140, 20);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(pvd + 148), 21);
        bytes[pvd + 881] = 1;
        // Independent little- and big-endian root path table entries.
        bytes[20 * SectorBytes] = 1;
        U32(bytes, 20 * SectorBytes + 2, RootDirectoryBlock);
        U16(bytes, 20 * SectorBytes + 6, 1);
        bytes[21 * SectorBytes] = 1;
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(21 * SectorBytes + 2), RootDirectoryBlock);
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(21 * SectorBytes + 6), 1);
        Record(bytes, pvd + 156, [0], RootDirectoryBlock, SectorBytes, 2);
        Record(bytes, RootDirectoryOffset, [0], RootDirectoryBlock, SectorBytes, 2);
        Record(bytes, RootDirectoryOffset + 34, [1], RootDirectoryBlock, SectorBytes, 2);
        Record(bytes, SystemCnfRecordOffset, "SYSTEM.CNF;1"u8.ToArray(), SystemCnfBlock, 0, 0);
        Record(bytes, BootRecordOffset, "SLUS_203.12;1"u8.ToArray(), BootExecutableBlock, BootExecutableBytes, 0);
        SetConfiguration(bytes, BootConfiguration);

        int elf = BootExecutableOffset;
        bytes[elf] = 0x7F;
        "ELF"u8.CopyTo(bytes.AsSpan(elf + 1));
        bytes[elf + 4] = 1;
        bytes[elf + 5] = 1;
        bytes[elf + 6] = 1;
        U16(bytes, elf + 16, 2);
        U16(bytes, elf + 18, 8);
        U32(bytes, elf + 20, 1);
        U32(bytes, elf + 24, EntryPoint);
        U32(bytes, elf + 28, 52);
        U16(bytes, elf + 40, 52);
        U16(bytes, elf + 42, 32);
        U16(bytes, elf + 44, 1);
        U32(bytes, elf + 52, 1);
        U32(bytes, elf + 56, 256);
        U32(bytes, elf + 60, EntryPoint);
        U32(bytes, elf + 68, 128);
        U32(bytes, elf + 72, 256);
        U32(bytes, elf + 76, 5);
        U32(bytes, elf + 80, 16);
        return bytes;
    }

    public static void SetConfiguration(byte[] bytes, string text)
    {
        byte[] configuration = Encoding.ASCII.GetBytes(text);
        if (configuration.Length > SectorBytes)
            throw new ArgumentOutOfRangeException(nameof(text));
        bytes.AsSpan(SystemCnfOffset, SectorBytes).Clear();
        configuration.CopyTo(bytes, SystemCnfOffset);
        Both32(bytes, SystemCnfRecordOffset + 10, (uint)configuration.Length);
    }

    private static void Descriptor(byte[] bytes, int block, byte type)
    {
        int offset = block * SectorBytes;
        bytes[offset] = type;
        "CD001"u8.CopyTo(bytes.AsSpan(offset + 1));
        bytes[offset + 6] = 1;
    }

    private static void Record(byte[] bytes, int offset, byte[] name, uint block, uint count, byte flags)
    {
        int length = 33 + name.Length + (name.Length % 2 == 0 ? 1 : 0);
        bytes[offset] = (byte)length;
        Both32(bytes, offset + 2, block);
        Both32(bytes, offset + 10, count);
        bytes[offset + 25] = flags;
        Both16(bytes, offset + 28, 1);
        bytes[offset + 32] = (byte)name.Length;
        name.CopyTo(bytes, offset + 33);
    }

    private static void U16(byte[] bytes, int offset, ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset), value);
    private static void U32(byte[] bytes, int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset), value);
    private static void Both16(byte[] bytes, int offset, ushort value)
    {
        U16(bytes, offset, value);
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(offset + 2), value);
    }
    private static void Both32(byte[] bytes, int offset, uint value)
    {
        U32(bytes, offset, value);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(offset + 4), value);
    }
}
