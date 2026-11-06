using System.Buffers.Binary;
using System.Text;
using Romd.Storage;

namespace Romd.Media.PlayStation;

/// <summary>Observed game-disc filesystem facts, independent of its catalog, storage policy and runtime.</summary>
public sealed record PspUmdObservedFacts(long LogicalBytes, uint VolumeBlocks, string DiscId, string DiscVersion);

/// <summary>Recognizes PSP UMD game layouts through bounded ISO9660, UMD and SFO reads. Never executes or decrypts code.</summary>
public static class PspUmdIdentifier
{
    public const string Profile = "psp-iso-umd-game/v1";
    public const long MaximumLogicalBytes = 2L << 30;
    public const int MaximumDirectoryBytes = 256 << 10, MaximumSfoBytes = 64 << 10;
    private const int _sector = 2048;

    public static bool ValidFacts(PspUmdObservedFacts facts) =>
        facts.LogicalBytes is >= 18 * _sector and <= MaximumLogicalBytes && facts.LogicalBytes % _sector == 0 &&
        facts.VolumeBlocks >= 18 && (ulong)facts.VolumeBlocks * _sector <= (ulong)facts.LogicalBytes &&
        facts.DiscId.Length == 9 && facts.DiscId.AsSpan(0, 4).IndexOfAnyExceptInRange('A', 'Z') < 0 &&
        facts.DiscId.AsSpan(4).IndexOfAnyExceptInRange('0', '9') < 0 && facts.DiscVersion.Length == 4 &&
        char.IsAsciiDigit(facts.DiscVersion[0]) && facts.DiscVersion[1] == '.' &&
        char.IsAsciiDigit(facts.DiscVersion[2]) && char.IsAsciiDigit(facts.DiscVersion[3]);

    public static async Task<PspUmdObservedFacts?> InspectAsync(IRandomAccessContent source, long maxLogicalBytes,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxLogicalBytes);
        ct.ThrowIfCancellationRequested();
        long length = source.Length;
        if (length < 18 * _sector || length > Math.Min(maxLogicalBytes, MaximumLogicalBytes) || length % _sector != 0)
            return null;
        try
        {
            var result = await InspectCoreAsync(source, length, ct);
            ct.ThrowIfCancellationRequested();
            return source.Length == length ? result : null;
        }
        catch (EndOfStreamException) { return null; }
        catch (InvalidDataException) { return null; }
        catch (OverflowException) { return null; }
    }

    private static async Task<PspUmdObservedFacts?> InspectCoreAsync(IRandomAccessContent source, long length, CancellationToken ct)
    {
        byte[]? primary = null;
        uint descriptorEnd = 0;
        for (uint block = 16; block < 80 && (long)(block + 1) * _sector <= length; block++)
        {
            byte[] descriptor = await ReadAsync(source, (long)block * _sector, _sector, ct);
            if (!descriptor.AsSpan(1, 5).SequenceEqual("CD001"u8) || descriptor[6] != 1)
                return null;
            if (descriptor[0] == 1)
            {
                if (primary is not null || Encoding.ASCII.GetString(descriptor, 8, 32).TrimEnd(' ') != "PSP GAME")
                    return null;
                primary = descriptor;
            }
            else if (descriptor[0] == 255)
            { descriptorEnd = block + 1; break; }
            else if (descriptor[0] is not (0 or 2 or 3))
                return null;
        }
        if (primary is null || descriptorEnd == 0 || !Both32(primary, 80, out uint blocks) ||
            blocks < descriptorEnd || (ulong)blocks * _sector > (ulong)length ||
            !Both16(primary, 120, 1) || !Both16(primary, 124, 1) || !Both16(primary, 128, _sector) || primary[881] is not (1 or 2))
            return null;
        ulong volumeBytes = (ulong)blocks * _sector;
        var root = Record(primary.AsSpan(156), descriptorEnd, volumeBytes);
        if (root is not { Name: "\0", Directory: true, Conforming: true })
            return null;
        var entries = await DirectoryAsync(source, root, descriptorEnd, volumeBytes, ct);
        var game = Find(entries, "PSP_GAME", true);
        var umd = Find(entries, "UMD_DATA.BIN;1", false);
        if (game is null || umd is null || umd.Bytes is < 10 or > 1024)
            return null;
        var gameEntries = await DirectoryAsync(source, game, descriptorEnd, volumeBytes, ct);
        var sfo = Find(gameEntries, "PARAM.SFO;1", false);
        var sys = Find(gameEntries, "SYSDIR", true);
        if (sfo is null || sfo.Bytes is < 20 or > MaximumSfoBytes || sys is null)
            return null;
        var executable = Find(await DirectoryAsync(source, sys, descriptorEnd, volumeBytes, ct), "EBOOT.BIN;1", false);
        if (executable is null || executable.Bytes < 0x150)
            return null;
        Entry[] selected = [root, game, umd, sfo, sys, executable];
        for (int i = 0; i < selected.Length; i++)
            for (int j = i + 1; j < selected.Length; j++)
                if ((ulong)selected[i].Block * _sector < (ulong)selected[j].Block * _sector + selected[j].Bytes &&
                    (ulong)selected[j].Block * _sector < (ulong)selected[i].Block * _sector + selected[i].Bytes)
                    return null;
        byte[] header = await ReadAsync(source, (long)executable.Block * _sector, 4, ct);
        if (!header.AsSpan().SequenceEqual("~PSP"u8))
            return null;
        var fields = Sfo(await ReadAsync(source, (long)sfo.Block * _sector, (int)sfo.Bytes, ct));
        if (fields is null || !fields.TryGetValue("DISC_ID", out var id) ||
            !fields.TryGetValue("DISC_VERSION", out var version) || !fields.TryGetValue("CATEGORY", out var category) || category != "UG" ||
            !fields.TryGetValue("BOOTABLE", out var bootable) || bootable != "1")
            return null;
        byte[] umdBytes = await ReadAsync(source, (long)umd.Block * _sector, (int)umd.Bytes, ct);
        if (umdBytes.Length < 11 || umdBytes[4] != '-' || umdBytes[10] != '|' ||
            Encoding.ASCII.GetString(umdBytes, 0, 4) + Encoding.ASCII.GetString(umdBytes, 5, 5) != id)
            return null;
        var facts = new PspUmdObservedFacts(length, blocks, id, version);
        return ValidFacts(facts) ? facts : null;
    }

    /// <summary>One framed directory record. Only a conforming record can be a selected layout entry.</summary>
    /// <param name="Conforming">
    ///     Single-extent, single-volume, no extended attributes or interleave, both-endian fields, an extent inside the
    ///     volume and a printable ASCII name. Unselected entries need not conform; they are only checked for name ambiguity.
    /// </param>
    private sealed record Entry(string Name, uint Block, uint Bytes, bool Directory, bool Conforming);

    /// <summary>A directory's records by name; a null value marks a name recorded more than once, such as a multi-extent file.</summary>
    private static Entry? Find(Dictionary<string, Entry?> entries, string name, bool directory)
    {
        bool found = entries.TryGetValue(name, out var entry);
        if (found && entry is null)
            return null;
        if (name.EndsWith(";1", StringComparison.Ordinal) && entries.TryGetValue(name[..^2], out var unversioned))
        {
            if (found)
                return null;
            entry = unversioned;
        }
        return entry is { Conforming: true } && entry.Directory == directory ? entry : null;
    }

    private static async Task<Dictionary<string, Entry?>> DirectoryAsync(IRandomAccessContent source, Entry entry,
        uint first, ulong volumeBytes, CancellationToken ct)
    {
        if (!entry.Directory || entry.Bytes is 0 or > MaximumDirectoryBytes)
            throw new InvalidDataException("Directory exceeds the bounded PSP profile.");
        byte[] bytes = await ReadAsync(source, (long)entry.Block * _sector, (int)entry.Bytes, ct);
        var entries = new Dictionary<string, Entry?>(StringComparer.OrdinalIgnoreCase);
        for (int at = 0; at < bytes.Length;)
        {
            ct.ThrowIfCancellationRequested();
            if (bytes[at] == 0)
            { at = (at / _sector + 1) * _sector; continue; }
            int count = bytes[at];
            // Framing must hold for every record so the walk stays exact; record semantics only matter for selected entries.
            if (count > bytes.Length - at || at % _sector + count > _sector ||
                Record(bytes.AsSpan(at, count), first, volumeBytes) is not { } record)
                throw new InvalidDataException("Invalid PSP directory record framing.");
            if (!entries.TryAdd(record.Name, record))
                entries[record.Name] = null;
            if (entries.Count > 4096)
                throw new InvalidDataException("PSP directory exceeds the bounded entry count.");
            at += count;
        }
        return entries;
    }

    /// <summary>Returns null only when the record framing is invalid; semantic defects yield a non-conforming entry.</summary>
    private static Entry? Record(ReadOnlySpan<byte> bytes, uint first, ulong volumeBytes)
    {
        if (bytes.Length < 34 || bytes[0] < 34 || bytes[0] > bytes.Length || bytes[32] == 0 || 33 + bytes[32] > bytes[0])
            return null;
        bool blockAgrees = Both32(bytes, 2, out uint block), countAgrees = Both32(bytes, 10, out uint count);
        bool conforming = blockAgrees && countAgrees && bytes[1] == 0 && bytes[26] == 0 && bytes[27] == 0 &&
            Both16(bytes, 28, 1) && bytes[25] is 0 or 2 && block >= first && (ulong)block * _sector + count <= volumeBytes;
        var name = bytes.Slice(33, bytes[32]);
        if (!(name.Length == 1 && name[0] is 0 or 1))
            foreach (byte value in name)
                if (value < 32 || value > 126 || value is (byte)'/' or (byte)'\\' or (byte)':')
                    conforming = false;
        // Latin-1 keeps every name byte distinct; a non-ASCII name cannot fold onto a selected ASCII name.
        return new(Encoding.Latin1.GetString(name), block, count, (bytes[25] & 2) != 0, conforming);
    }

    private static Dictionary<string, string>? Sfo(byte[] bytes)
    {
        if (!bytes.AsSpan(0, 4).SequenceEqual("\0PSF"u8) || U32(bytes, 4) != 0x101)
            return null;
        uint keys = U32(bytes, 8), data = U32(bytes, 12), count = U32(bytes, 16);
        if (count is 0 or > 128 || keys < 20 + count * 16 || keys >= data || data > bytes.Length)
            return null;
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        var occupied = new List<(uint Start, uint End)>();
        for (int i = 0; i < count; i++)
        {
            int at = 20 + i * 16;
            uint key = keys + BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(at));
            ushort format = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(at + 2));
            uint length = U32(bytes, at + 4), maximum = U32(bytes, at + 8), offset = U32(bytes, at + 12);
            if (key >= data || length == 0 || length > maximum || (ulong)data + offset + maximum > (ulong)bytes.Length ||
                occupied.Any(range => offset < range.End && range.Start < (ulong)offset + maximum))
                return null;
            occupied.Add((offset, checked(offset + maximum)));
            int end = Array.IndexOf(bytes, (byte)0, (int)key, (int)(data - key));
            if (end < 0 || end == key || end - key > 128)
                return null;
            var nameBytes = bytes.AsSpan((int)key, end - (int)key);
            foreach (byte keyByte in nameBytes)
                if (keyByte is < 33 or > 126)
                    return null;
            string name = Encoding.ASCII.GetString(nameBytes);
            if (fields.ContainsKey(name))
                return null;
            var valueBytes = bytes.AsSpan(checked((int)(data + offset)), (int)length);
            string value;
            if (name == "BOOTABLE")
            {
                if (format != 0x404 || length != 4 || BinaryPrimitives.ReadUInt32LittleEndian(valueBytes) != 1)
                    return null;
                value = "1";
            }
            else if (name is "DISC_ID" or "DISC_VERSION" or "CATEGORY")
            {
                if (format != 0x204 || valueBytes[^1] != 0 || valueBytes[..^1].Contains((byte)0))
                    return null;
                foreach (byte item in valueBytes[..^1])
                    if (item is < 32 or > 126)
                        return null;
                value = Encoding.ASCII.GetString(valueBytes[..^1]);
            }
            else
                value = "";
            fields.Add(name, value);
        }
        return fields;
    }

    private static bool Both32(ReadOnlySpan<byte> bytes, int at, out uint value)
    {
        value = BinaryPrimitives.ReadUInt32LittleEndian(bytes[at..]);
        return value == BinaryPrimitives.ReadUInt32BigEndian(bytes[(at + 4)..]);
    }
    private static bool Both16(ReadOnlySpan<byte> bytes, int at, ushort expected) =>
        BinaryPrimitives.ReadUInt16LittleEndian(bytes[at..]) == expected && BinaryPrimitives.ReadUInt16BigEndian(bytes[(at + 2)..]) == expected;
    private static uint U32(byte[] bytes, int at) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(at));
    private static async Task<byte[]> ReadAsync(IRandomAccessContent source, long offset, int count, CancellationToken ct)
    {
        byte[] bytes = new byte[count];
        for (int at = 0; at < count;)
        {
            ct.ThrowIfCancellationRequested();
            int read = await source.ReadAtAsync(offset + at, bytes.AsMemory(at), ct);
            if (read <= 0 || read > count - at)
                throw new EndOfStreamException();
            at += read;
        }
        return bytes;
    }
}
