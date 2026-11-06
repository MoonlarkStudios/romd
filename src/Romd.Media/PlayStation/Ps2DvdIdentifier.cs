using System.Buffers.Binary;
using System.Text;
using Romd.Storage;

namespace Romd.Media.PlayStation;

public enum Ps2DvdIdentificationStatus { Identified, NotIdentified, Malformed, LimitExceeded }

/// <summary>Bounded observed filesystem/boot facts; no source hash, container integrity or game identity.</summary>
public sealed record Ps2DvdObservedFacts(long LogicalBytes, uint VolumeBlocks, uint PrimaryDescriptorBlock,
    uint RootDirectoryBlock, uint RootDirectoryBytes, uint SystemCnfBlock, uint SystemCnfBytes,
    uint BootExecutableBlock, uint BootExecutableBytes, string ObservedBoot2Value, string BootPath,
    string BootSerial, uint EntryPoint, ushort ProgramHeaderCount);

public sealed record Ps2DvdIdentificationResult(Ps2DvdIdentificationStatus Status, Ps2DvdObservedFacts? Facts = null);

/// <summary>Recognizes a bounded single-volume ISO9660 root boot path with a MIPS ELF executable.</summary>
/// <remarks>Borrowed logical boot-layout facts only. The caller must establish DVD media independently; a small PS2 ISO can be CD.
/// Measured PS2 compatibility permits short declared directories and empty load headers with noncongruent offsets;
/// this recognizes boot layout, not ISO9660 or ELF ABI conformance. Nested paths remain unrecognized; no execution.</remarks>
public static class Ps2DvdIdentifier
{
    public const string ProfileVersion = "ps2-iso-root-elf/v1";
    public const long MaximumLogicalBytes = 10L << 30;
    public const int SectorBytes = 2048, MaximumDescriptorBlocks = 64,
        MaximumRootDirectoryBytes = 256 << 10, MaximumSystemCnfBytes = 16 << 10;
    private const int MaximumProgramHeaders = 128, MaximumSectionHeaders = 4096;

    public static async Task<Ps2DvdIdentificationResult> InspectAsync(IRandomAccessContent source,
        long maxLogicalBytes, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxLogicalBytes);
        ct.ThrowIfCancellationRequested();
        long length = source.Length;
        if (length > Math.Min(maxLogicalBytes, MaximumLogicalBytes))
            return Result(Ps2DvdIdentificationStatus.LimitExceeded);
        if (length < 0 || length % SectorBytes != 0)
            return Result(Ps2DvdIdentificationStatus.Malformed);
        Ps2DvdIdentificationResult result;
        try
        { result = await InspectCoreAsync(source, length, ct); }
        catch (EndOfStreamException) { result = Result(Ps2DvdIdentificationStatus.Malformed); }
        ct.ThrowIfCancellationRequested();
        return source.Length == length ? result : Result(Ps2DvdIdentificationStatus.Malformed);
    }

    private static async Task<Ps2DvdIdentificationResult> InspectCoreAsync(IRandomAccessContent source,
        long length, CancellationToken ct)
    {
        if (length < 17L * SectorBytes)
            return Result(Ps2DvdIdentificationStatus.NotIdentified);
        byte[] header = new byte[32];
        await FillAsync(source, 0, header, ct);
        if (BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(0x1C)) == 0xC2339F3D ||
            BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(0x18)) == 0x5D1C9EA3)
            return Result(Ps2DvdIdentificationStatus.NotIdentified);

        byte[] descriptor = new byte[SectorBytes];
        byte[]? primary = null;
        uint primaryBlock = 0, descriptorEnd = 0;
        for (uint index = 0; index < MaximumDescriptorBlocks; index++)
        {
            uint block = 16 + index;
            if (!Inside(block, SectorBytes, (ulong)length))
                return Result(primary is null ? Ps2DvdIdentificationStatus.NotIdentified : Ps2DvdIdentificationStatus.Malformed);
            await FillAsync(source, (long)block * SectorBytes, descriptor, ct);
            if (!descriptor.AsSpan(1, 5).SequenceEqual("CD001"u8) || descriptor[6] != 1)
                return Result(primary is null ? Ps2DvdIdentificationStatus.NotIdentified : Ps2DvdIdentificationStatus.Malformed);
            if (descriptor[0] == 1)
            {
                if (primary is not null)
                    return Result(Ps2DvdIdentificationStatus.Malformed);
                primary = (byte[])descriptor.Clone();
                primaryBlock = block;
            }
            else if (descriptor[0] == 255)
            { descriptorEnd = block + 1; break; }
            else if (descriptor[0] is not (0 or 2 or 3))
                return Result(Ps2DvdIdentificationStatus.Malformed);
        }
        if (descriptorEnd == 0)
            return Result(Ps2DvdIdentificationStatus.LimitExceeded);
        if (primary is null)
            return Result(Ps2DvdIdentificationStatus.NotIdentified);
        if (!Both32(primary, 80, out uint volumeBlocks) || !Both16(primary, 120, out ushort setSize) ||
            !Both16(primary, 124, out ushort sequence) || !Both16(primary, 128, out ushort blockBytes) ||
            !Both32(primary, 132, out uint pathBytes) || setSize != 1 || sequence != 1 ||
            blockBytes != SectorBytes || volumeBlocks < descriptorEnd ||
            (ulong)volumeBlocks * SectorBytes > (ulong)length || primary[881] != 1 || pathBytes < 10)
            return Result(Ps2DvdIdentificationStatus.Malformed);
        ulong volumeBytes = (ulong)volumeBlocks * SectorBytes;
        uint littlePath = BinaryPrimitives.ReadUInt32LittleEndian(primary.AsSpan(140));
        uint bigPath = BinaryPrimitives.ReadUInt32BigEndian(primary.AsSpan(148));
        uint optionalLittlePath = BinaryPrimitives.ReadUInt32LittleEndian(primary.AsSpan(144));
        uint optionalBigPath = BinaryPrimitives.ReadUInt32BigEndian(primary.AsSpan(152));
        if (!DataExtent(littlePath, pathBytes, descriptorEnd, volumeBytes) ||
            !DataExtent(bigPath, pathBytes, descriptorEnd, volumeBytes) ||
            optionalLittlePath != 0 && !DataExtent(optionalLittlePath, pathBytes, descriptorEnd, volumeBytes) ||
            optionalBigPath != 0 && !DataExtent(optionalBigPath, pathBytes, descriptorEnd, volumeBytes) ||
            !TryRecord(primary.AsSpan(156), volumeBytes, descriptorEnd, out var root) ||
            root!.Name != "\0" || root.Flags != 2 || root.Bytes == 0)
            return Result(Ps2DvdIdentificationStatus.Malformed);
        if (root.Bytes > MaximumRootDirectoryBytes)
            return Result(Ps2DvdIdentificationStatus.LimitExceeded);

        byte[] directory = new byte[checked((int)root.Bytes)];
        await FillAsync(source, (long)root.Block * SectorBytes, directory, ct);
        var records = new Dictionary<string, DirectoryRecord>(StringComparer.OrdinalIgnoreCase);
        for (int at = 0; at < directory.Length;)
        {
            ct.ThrowIfCancellationRequested();
            int remainingSector = Math.Min(SectorBytes - at % SectorBytes, directory.Length - at);
            if (directory[at] == 0)
            {
                if (directory.AsSpan(at, remainingSector).IndexOfAnyExcept((byte)0) >= 0)
                    return Result(Ps2DvdIdentificationStatus.Malformed);
                at += remainingSector;
                continue;
            }
            int recordBytes = directory[at];
            if (recordBytes > remainingSector || !TryRecord(directory.AsSpan(at, recordBytes), volumeBytes,
                    descriptorEnd, out var record) || !records.TryAdd(record!.Name, record))
                return Result(Ps2DvdIdentificationStatus.Malformed);
            at += recordBytes;
        }
        if (!records.TryGetValue("\0", out var self) || !records.TryGetValue("\u0001", out var parent) ||
            self.Flags != 2 || parent.Flags != 2 || self.Block != root.Block || parent.Block != root.Block ||
            self.Bytes != root.Bytes || parent.Bytes != root.Bytes)
            return Result(Ps2DvdIdentificationStatus.Malformed);
        var cnfs = records.Values.Where(record => record.Name.Split(';')[0].Equals("SYSTEM.CNF", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (cnfs.Length == 0)
            return Result(Ps2DvdIdentificationStatus.NotIdentified);
        if (cnfs.Length != 1 || (cnfs[0].Flags & ~1) != 0 || cnfs[0].Bytes == 0)
            return Result(Ps2DvdIdentificationStatus.Malformed);
        var cnf = cnfs[0];
        if (!cnf.Name.Equals("SYSTEM.CNF;1", StringComparison.OrdinalIgnoreCase) &&
            !cnf.Name.Equals("SYSTEM.CNF", StringComparison.OrdinalIgnoreCase))
            return Result(Ps2DvdIdentificationStatus.NotIdentified);
        if (cnf.Bytes > MaximumSystemCnfBytes)
            return Result(Ps2DvdIdentificationStatus.LimitExceeded);
        if (Overlap(root, cnf))
            return Result(Ps2DvdIdentificationStatus.Malformed);
        byte[] configuration = new byte[checked((int)cnf.Bytes)];
        await FillAsync(source, (long)cnf.Block * SectorBytes, configuration, ct);
        var configStatus = ParseBoot2(configuration, out string? observed, out string? serial);
        if (configStatus != Ps2DvdIdentificationStatus.Identified)
            return Result(configStatus);
        var boots = records.Values.Where(record => record.Name.Equals(serial, StringComparison.OrdinalIgnoreCase) ||
            record.Name.Equals(serial + ";1", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (boots.Length != 1 || (boots[0].Flags & ~1) != 0 || boots[0].Bytes < 52 ||
            Overlap(boots[0], root) || Overlap(boots[0], cnf))
            return Result(Ps2DvdIdentificationStatus.Malformed);
        var boot = boots[0];
        var elf = await InspectElfAsync(source, boot, ct);
        if (elf.Status != Ps2DvdIdentificationStatus.Identified)
            return Result(elf.Status);
        return new(Ps2DvdIdentificationStatus.Identified, new(length, volumeBlocks, primaryBlock, root.Block,
            root.Bytes, cnf.Block, cnf.Bytes, boot.Block, boot.Bytes, observed!, "cdrom0:\\" + serial + ";1",
            serial!, elf.EntryPoint, elf.Headers));
    }

    private static Ps2DvdIdentificationStatus ParseBoot2(byte[] bytes, out string? observed, out string? serial)
    {
        observed = serial = null;
        int length = bytes.Length;
        while (length > 0 && bytes[length - 1] == 0)
            length--;
        foreach (byte value in bytes.AsSpan(0, length))
            if (value is not (9 or 10 or 13) && (value < 32 || value > 126))
                return Ps2DvdIdentificationStatus.Malformed;
        foreach (string rawLine in Encoding.ASCII.GetString(bytes, 0, length).Split(['\r', '\n']))
        {
            string line = rawLine.Trim();
            if (line.Length == 0 || line[0] is '#' or ';')
                continue;
            int equals = line.IndexOf('=');
            if (equals < 1)
                return Ps2DvdIdentificationStatus.Malformed;
            if (!line[..equals].Trim().Equals("BOOT2", StringComparison.OrdinalIgnoreCase))
                continue;
            if (observed is not null)
                return Ps2DvdIdentificationStatus.Malformed;
            observed = line[(equals + 1)..].Trim();
        }
        if (observed is null)
            return Ps2DvdIdentificationStatus.NotIdentified;
        if (observed.Length > 128 || observed.Contains("..", StringComparison.Ordinal) || observed.Contains('/'))
            return Ps2DvdIdentificationStatus.Malformed;
        if (!observed.StartsWith("cdrom0:\\", StringComparison.OrdinalIgnoreCase))
            return Ps2DvdIdentificationStatus.NotIdentified;
        string name = observed[8..];
        if (name.Contains('\\'))
            return Ps2DvdIdentificationStatus.NotIdentified;
        if (name.Length != 13 || !name.EndsWith(";1", StringComparison.Ordinal) || name[4] != '_' || name[8] != '.')
            return Ps2DvdIdentificationStatus.NotIdentified;
        for (int index = 0; index < 4; index++)
            if (!char.IsAsciiLetter(name[index]))
                return Ps2DvdIdentificationStatus.NotIdentified;
        foreach (int index in new[] { 5, 6, 7, 9, 10 })
            if (!char.IsAsciiDigit(name[index]))
                return Ps2DvdIdentificationStatus.NotIdentified;
        serial = name[..11].ToUpperInvariant();
        return Ps2DvdIdentificationStatus.Identified;
    }

    private static async Task<(Ps2DvdIdentificationStatus Status, uint EntryPoint, ushort Headers)> InspectElfAsync(
        IRandomAccessContent source, DirectoryRecord boot, CancellationToken ct)
    {
        byte[] elf = new byte[52];
        await FillAsync(source, (long)boot.Block * SectorBytes, elf, ct);
        if (elf[0] != 0x7F || elf[1] != 'E' || elf[2] != 'L' || elf[3] != 'F' ||
            elf[4] != 1 || elf[5] != 1 || elf[6] != 1 || U16(elf, 16) != 2 || U16(elf, 18) != 8)
            return (Ps2DvdIdentificationStatus.NotIdentified, 0, 0);
        uint entry = U32(elf, 24), programOffset = U32(elf, 28), sectionOffset = U32(elf, 32);
        ushort headers = U16(elf, 44), sections = U16(elf, 48), stringSection = U16(elf, 50);
        if (headers > MaximumProgramHeaders || sections > MaximumSectionHeaders)
            return (Ps2DvdIdentificationStatus.LimitExceeded, 0, 0);
        if (U32(elf, 20) != 1 || U16(elf, 40) != 52 || U16(elf, 42) != 32 || headers == 0 ||
            entry == 0 || entry % 4 != 0 || programOffset < 52 || (ulong)programOffset + (ulong)headers * 32 > boot.Bytes ||
            (sections == 0 ? sectionOffset != 0 || stringSection != 0 || U16(elf, 46) is not (0 or 40) :
                U16(elf, 46) != 40 || sectionOffset < 52 || (ulong)sectionOffset + (ulong)sections * 40 > boot.Bytes || stringSection >= sections))
            return (Ps2DvdIdentificationStatus.Malformed, 0, 0);
        byte[] programs = new byte[headers * 32];
        await FillAsync(source, (long)boot.Block * SectorBytes + programOffset, programs, ct);
        bool executableEntry = false;
        for (int at = 0; at < programs.Length; at += 32)
        {
            ct.ThrowIfCancellationRequested();
            uint type = U32(programs, at), offset = U32(programs, at + 4), address = U32(programs, at + 8),
                fileBytes = U32(programs, at + 16), memoryBytes = U32(programs, at + 20),
                flags = U32(programs, at + 24), alignment = U32(programs, at + 28);
            if ((ulong)offset + fileBytes > boot.Bytes)
                return (Ps2DvdIdentificationStatus.Malformed, 0, 0);
            if (type != 1)
                continue;
            // A measured PS2 compatibility case: both sizes zero contributes no mapping or executable entry.
            // Retain file bounds above, known flags and alignment form; do not assert ABI congruence for this empty case.
            if (fileBytes == 0 && memoryBytes == 0)
            {
                if ((flags & ~7U) != 0 || alignment > 1 && (alignment & (alignment - 1)) != 0)
                    return (Ps2DvdIdentificationStatus.Malformed, 0, 0);
                continue;
            }
            if (memoryBytes == 0 || fileBytes > memoryBytes || (ulong)address + memoryBytes > (1UL << 32) ||
                (flags & ~7U) != 0 || alignment > 1 && ((alignment & (alignment - 1)) != 0 || address % alignment != offset % alignment))
                return (Ps2DvdIdentificationStatus.Malformed, 0, 0);
            if ((flags & 1) != 0 && entry >= address && (ulong)entry < (ulong)address + fileBytes)
                executableEntry = true;
        }
        return executableEntry ? (Ps2DvdIdentificationStatus.Identified, entry, headers) : (Ps2DvdIdentificationStatus.Malformed, 0, 0);
    }

    private sealed record DirectoryRecord(string Name, uint Block, uint Bytes, byte Flags);

    private static bool TryRecord(ReadOnlySpan<byte> bytes, ulong volumeBytes, uint descriptorEnd,
        out DirectoryRecord? record)
    {
        record = null;
        if (bytes.Length < 34 || bytes[0] < 34 || bytes[0] > bytes.Length || bytes[1] != 0 ||
            bytes[26] != 0 || bytes[27] != 0 || (bytes[25] & 0xFC) != 0 ||
            !Both32(bytes, 2, out uint block) || !Both32(bytes, 10, out uint count) ||
            !Both16(bytes, 28, out ushort sequence) || sequence != 1 ||
            !Inside(block, count, volumeBytes) || count != 0 && block < descriptorEnd)
            return false;
        int nameBytes = bytes[32], minimum = 33 + nameBytes + (nameBytes % 2 == 0 ? 1 : 0);
        if (nameBytes == 0 || minimum > bytes[0] || nameBytes % 2 == 0 && bytes[33 + nameBytes] != 0)
            return false;
        ReadOnlySpan<byte> name = bytes.Slice(33, nameBytes);
        if (!(nameBytes == 1 && name[0] is 0 or 1))
            foreach (byte value in name)
                if (value < 32 || value > 126 || value is (byte)'/' or (byte)'\\' or (byte)':')
                    return false;
        record = new(Encoding.ASCII.GetString(name), block, count, bytes[25]);
        return true;
    }

    private static bool Both32(ReadOnlySpan<byte> bytes, int at, out uint value)
    {
        value = BinaryPrimitives.ReadUInt32LittleEndian(bytes[at..]);
        return value == BinaryPrimitives.ReadUInt32BigEndian(bytes[(at + 4)..]);
    }

    private static bool Both16(ReadOnlySpan<byte> bytes, int at, out ushort value)
    {
        value = BinaryPrimitives.ReadUInt16LittleEndian(bytes[at..]);
        return value == BinaryPrimitives.ReadUInt16BigEndian(bytes[(at + 2)..]);
    }

    private static bool Inside(uint block, uint bytes, ulong length) => (ulong)block * SectorBytes + bytes <= length;
    private static bool DataExtent(uint block, uint bytes, uint first, ulong length) => block >= first && Inside(block, bytes, length);
    private static bool Overlap(DirectoryRecord a, DirectoryRecord b) =>
        (ulong)a.Block * SectorBytes < (ulong)b.Block * SectorBytes + b.Bytes &&
        (ulong)b.Block * SectorBytes < (ulong)a.Block * SectorBytes + a.Bytes;
    private static ushort U16(byte[] bytes, int at) => BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(at));
    private static uint U32(byte[] bytes, int at) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(at));
    private static Ps2DvdIdentificationResult Result(Ps2DvdIdentificationStatus status) => new(status);

    private static async ValueTask FillAsync(IRandomAccessContent source, long offset, Memory<byte> destination, CancellationToken ct)
    {
        while (!destination.IsEmpty)
        {
            ct.ThrowIfCancellationRequested();
            int count = await source.ReadAtAsync(offset, destination, ct);
            if (count == 0)
                throw new EndOfStreamException();
            if (count < 0 || count > destination.Length)
                throw new IOException("Logical content returned an invalid byte count.");
            offset += count;
            destination = destination[count..];
        }
    }
}
