using System.Buffers.Binary;
using System.Text;
using System.Text.RegularExpressions;
using Moonlark.Libchdr;
using Romd.Storage;

namespace Romd.Media.PlayStation;

/// <summary>Observed root boot program, not DAT identity, sector integrity or emulator compatibility.</summary>
public sealed record Ps1CdObservedFacts(string Profile, long FirstTrackIndex01, uint VolumeBlocks,
    uint RootBlock, uint RootBytes, string BootPath, uint BootBlock, uint BootBytes, uint EntryPoint, uint LoadAddress, uint LoadBytes);

/// <summary>Bounded retail PS1 boot recognition in the first raw data track. Root PS-X EXE boot only;
/// cooked sectors, Form-2 filesystem sectors, nested boot paths, ambiguous records and PS2 BOOT2 fail closed.</summary>
public static class Ps1CdIdentifier
{
    public const string Profile = "ps1-cd-root-exe/v1";
    private const int _blockBytes = 2048, _maximumRootBytes = 256 << 10;

    public static Ps1CdObservedFacts? Inspect(ISynchronousRawContent bin, ChdCdTrackType firstTrackType,
        long firstTrackIndex01, long discFrames, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(bin);
        ct.ThrowIfCancellationRequested();
        if (firstTrackIndex01 is < 0 or > ChdCdLimits.MaximumFrames || firstTrackType is not (ChdCdTrackType.Mode1Raw or ChdCdTrackType.Mode2Raw) || bin.Length % 2352 != 0 ||
            bin.Length / 2352 <= firstTrackIndex01 + 17 || bin.Length / 2352 > ChdCdLimits.MaximumFrames)
            return null;
        long wholeDiscFrames = discFrames;
        if (wholeDiscFrames < bin.Length / 2352 || wholeDiscFrames > ChdCdLimits.MaximumFrames)
            return null;
        var blocks = new RawBlocks(bin, firstTrackIndex01, firstTrackType);
        try
        {
            byte[]? primary = null;
            uint descriptorEnd = 0;
            for (uint block = 16; block < 80 && block < blocks.Length / _blockBytes; block++)
            {
                byte[] bytes = blocks.Read(block, _blockBytes, ct);
                if (!bytes.AsSpan(1, 5).SequenceEqual("CD001"u8) || bytes[6] != 1)
                    return null;
                if (bytes[0] == 1)
                { if (primary is not null) return null; primary = bytes; }
                else if (bytes[0] == 255)
                { descriptorEnd = block + 1; break; }
                else if (bytes[0] is not (0 or 2 or 3))
                    return null;
            }
            if (primary is null || descriptorEnd == 0 || Encoding.ASCII.GetString(primary, 8, 32).TrimEnd(' ') != "PLAYSTATION" ||
                !Both32(primary, 80, out uint volumeBlocks) || volumeBlocks < descriptorEnd || volumeBlocks > wholeDiscFrames - firstTrackIndex01 ||
                !Both16(primary, 120, out ushort setSize) || setSize != 1 || !Both16(primary, 124, out ushort sequence) || sequence != 1 ||
                !Both16(primary, 128, out ushort size) || size != _blockBytes ||
                !Record(primary.AsSpan(156), volumeBlocks, descriptorEnd, out var root) || root!.Flags != 2 || root.Name != "\0" ||
                root.Bytes is < 1 or > _maximumRootBytes)
                return null;
            byte[] directory = blocks.Read(root.Block, checked((int)root.Bytes), ct);
            Dictionary<string, Entry> entries = new(StringComparer.OrdinalIgnoreCase);
            for (int at = 0; at < directory.Length;)
            {
                ct.ThrowIfCancellationRequested();
                int remaining = Math.Min(_blockBytes - at % _blockBytes, directory.Length - at);
                if (directory[at] == 0)
                {
                    if (directory.AsSpan(at, remaining).IndexOfAnyExcept((byte)0) >= 0)
                        return null;
                    at += remaining;
                    continue;
                }
                int count = directory[at];
                if (count > remaining || !Record(directory.AsSpan(at, count), volumeBlocks, descriptorEnd, out var entry) ||
                    !entries.TryAdd(entry!.Name, entry))
                    return null;
                at += count;
            }
            if (!entries.TryGetValue("\0", out var self) || !entries.TryGetValue("\u0001", out var parent) ||
                self != root || parent.Flags != 2 || parent.Block != root.Block || parent.Bytes != root.Bytes)
                return null;
            var configs = entries.Values.Where(entry => entry.Name.Split(';')[0].Equals("SYSTEM.CNF", StringComparison.OrdinalIgnoreCase)).ToArray();
            string bootName = "PSX.EXE;1";
            if (configs.Length > 1)
                return null;
            if (configs.Length == 1)
            {
                var config = configs[0];
                if (config.Flags != 0 || config.Bytes is < 1 or > (16 << 10) || Overlaps(config, root))
                    return null;
                byte[] bytes = blocks.Read(config.Block, (int)config.Bytes, ct);
                int length = bytes.Length;
                while (length > 0 && bytes[length - 1] == 0)
                    length--;
                // Retail SYSTEM.CNF files may end with a DOS text EOF marker. It is a
                // terminator only at the end; embedded control bytes still reject.
                if (length > 0 && bytes[length - 1] == 0x1a)
                    length--;
                foreach (byte value in bytes.AsSpan(0, length))
                    if (value is not (9 or 10 or 13) && (value < 32 || value > 126))
                        return null;
                string? observed = null;
                foreach (string raw in Encoding.ASCII.GetString(bytes, 0, length).Split(['\r', '\n']))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line[0] is '#' or ';')
                        continue;
                    int equals = line.IndexOf('=');
                    if (equals < 1)
                        return null;
                    string key = line[..equals].Trim();
                    if (key.Equals("BOOT2", StringComparison.OrdinalIgnoreCase))
                        return null;
                    if (!key.Equals("BOOT", StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (observed is not null)
                        return null;
                    observed = line[(equals + 1)..].Trim();
                }
                if (observed is null)
                    return null;
                var match = Regex.Match(observed, @"^cdrom:\\?([A-Za-z0-9_][A-Za-z0-9_.-]{0,63})(;1)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
                if (!match.Success || match.Groups[1].Value.Contains("..", StringComparison.Ordinal))
                    return null;
                bootName = match.Groups[1].Value + ";1";
            }
            var programs = entries.Values.Where(entry => entry.Name.Equals(bootName, StringComparison.OrdinalIgnoreCase) ||
                entry.Name.Equals(bootName[..^2], StringComparison.OrdinalIgnoreCase)).ToArray();
            if (programs.Length != 1)
                return null;
            var boot = programs[0];
            if (boot.Flags != 0 || boot.Bytes < _blockBytes || (ulong)boot.Block * _blockBytes + boot.Bytes > (ulong)blocks.Length || Overlaps(boot, root) || configs.Any(config => Overlaps(config, boot)))
                return null;
            byte[] executable = blocks.Read(boot.Block, _blockBytes, ct);
            uint pc = BinaryPrimitives.ReadUInt32LittleEndian(executable.AsSpan(0x10)), address = BinaryPrimitives.ReadUInt32LittleEndian(executable.AsSpan(0x18)),
                countBytes = BinaryPrimitives.ReadUInt32LittleEndian(executable.AsSpan(0x1c));
            if (!executable.AsSpan(0, 8).SequenceEqual("PS-X EXE"u8) || countBytes is < 1 or > (2 << 20) ||
                boot.Bytes < (ulong)_blockBytes + countBytes || !RetailRam(pc, 4) || !RetailRam(address, countBytes) || pc % 4 != 0)
                return null;
            return new(Profile, firstTrackIndex01, volumeBlocks, root.Block, root.Bytes, boot.Name, boot.Block, boot.Bytes, pc, address, countBytes);
        }
        catch (EndOfStreamException) { return null; }
        catch (InvalidDataException) { return null; }
    }
    public static bool Valid(Ps1CdObservedFacts facts) => facts.Profile == Profile && facts.FirstTrackIndex01 is >= 0 and <= 450000 &&
        facts.VolumeBlocks is > 16 and <= 450000 && facts.RootBlock < facts.VolumeBlocks && facts.RootBytes is > 0 and <= _maximumRootBytes &&
        (ulong)facts.RootBlock * _blockBytes + facts.RootBytes <= (ulong)facts.VolumeBlocks * _blockBytes &&
        facts.BootPath is { Length: > 0 and <= 66 } && !facts.BootPath.Any(c => char.IsControl(c) || "/\\".Contains(c)) &&
        facts.BootBlock < facts.VolumeBlocks && facts.BootBytes >= _blockBytes &&
        (ulong)facts.BootBlock * _blockBytes + facts.BootBytes <= (ulong)facts.VolumeBlocks * _blockBytes && facts.LoadBytes is > 0 and <= (2 << 20) &&
        facts.BootBytes >= (ulong)_blockBytes + facts.LoadBytes && RetailRam(facts.EntryPoint, 4) && facts.EntryPoint % 4 == 0 && RetailRam(facts.LoadAddress, facts.LoadBytes);
    private static bool RetailRam(uint address, uint bytes) => address >> 24 is 0x80 or 0xa0 && (address & 0x00ffffff) < (2 << 20) &&
        (ulong)(address & 0x00ffffff) + bytes <= (2 << 20);
    private sealed record Entry(string Name, uint Block, uint Bytes, byte Flags);
    private static bool Record(ReadOnlySpan<byte> bytes, uint volume, uint first, out Entry? entry)
    {
        entry = null;
        if (bytes.Length < 34 || bytes[0] < 34 || bytes[0] > bytes.Length || bytes[1] != 0 || bytes[26] != 0 || bytes[27] != 0 ||
            !Both32(bytes, 2, out uint block) || !Both32(bytes, 10, out uint count) || !Both16(bytes, 28, out ushort sequence) || sequence != 1 ||
            block < first || (ulong)block * _blockBytes + count > (ulong)volume * _blockBytes || bytes[25] is not (0 or 2) ||
            bytes[32] == 0 || 33 + bytes[32] > bytes[0])
            return false;
        string name = Encoding.ASCII.GetString(bytes.Slice(33, bytes[32]));
        if (name.Length != 1 || name[0] is not ('\0' or '\u0001'))
            foreach (char value in name)
                if (value < 32 || value > 126 || "/\\".Contains(value))
                    return false;
        entry = new(name, block, count, bytes[25]);
        return true;
    }
    private static bool Both32(ReadOnlySpan<byte> bytes, int at, out uint value)
    { value = BinaryPrimitives.ReadUInt32LittleEndian(bytes[at..]); return value == BinaryPrimitives.ReadUInt32BigEndian(bytes[(at + 4)..]); }
    private static bool Both16(ReadOnlySpan<byte> bytes, int at, out ushort value)
    { value = BinaryPrimitives.ReadUInt16LittleEndian(bytes[at..]); return value == BinaryPrimitives.ReadUInt16BigEndian(bytes[(at + 2)..]); }
    private static bool Overlaps(Entry left, Entry right) => (ulong)left.Block * _blockBytes < (ulong)right.Block * _blockBytes + right.Bytes &&
        (ulong)right.Block * _blockBytes < (ulong)left.Block * _blockBytes + left.Bytes;
    private sealed class RawBlocks(ISynchronousRawContent source, long start, ChdCdTrackType kind)
    {
        internal long Length => (source.Length / 2352 - start) * _blockBytes;
        internal byte[] Read(uint first, int count, CancellationToken ct)
        {
            if (count < 1 || count > _maximumRootBytes || (long)first * _blockBytes + count > Length)
                throw new InvalidDataException("PS1 boot extent exceeds its raw data track.");
            byte[] output = new byte[count], sector = new byte[2352];
            int written = 0;
            while (written < count)
            {
                ct.ThrowIfCancellationRequested();
                long offset = (start + first + written / _blockBytes) * 2352;
                int got = 0;
                while (got < sector.Length)
                { int read = source.ReadAt(offset + got, sector.AsSpan(got)); if (read <= 0 || read > sector.Length - got) throw new EndOfStreamException(); got += read; }
                if (sector[0] != 0 || sector.AsSpan(1, 10).IndexOfAnyExcept((byte)0xff) >= 0 || sector[11] != 0 ||
                    sector[15] != (kind == ChdCdTrackType.Mode1Raw ? 1 : 2) || kind == ChdCdTrackType.Mode2Raw &&
                    (!sector.AsSpan(16, 4).SequenceEqual(sector.AsSpan(20, 4)) || (sector[18] & 0x20) != 0))
                    throw new InvalidDataException("PS1 boot filesystem requires raw Form-1 data sectors.");
                int take = Math.Min(_blockBytes, count - written);
                sector.AsSpan(kind == ChdCdTrackType.Mode1Raw ? 16 : 24, take).CopyTo(output.AsSpan(written));
                written += take;
            }
            return output;
        }
    }
}
