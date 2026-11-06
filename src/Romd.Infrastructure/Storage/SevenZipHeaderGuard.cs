using System.Buffers.Binary;
using System.IO.Hashing;
using SharpCompress.Compressors.LZMA;

namespace Romd.Infrastructure.Storage;

/// <summary>
/// Checks 7z metadata before SharpCompress opens it. Its 7z decoder does not expose coder
/// properties or honor ReaderOptions.Providers. Keep this parser deliberately fail-closed:
/// inline metadata, linear Copy/LZMA/LZMA2/filter graphs, and a single header coder only.
/// Limits apply to encoded headers too, before any decoder or header-sized allocation.
/// </summary>
internal static class SevenZipHeaderGuard
{
    internal const int MaxHeaderBytes = 16 * 1024 * 1024;
    internal const long MaxDictionaryBytes = 64L * 1024 * 1024;
    private const int MaxItems = 50_000;
    private const ulong Copy = 0, Lzma = 0x030101, Lzma2 = 0x21;

    internal static IReadOnlyList<uint> Validate(Stream source, CancellationToken ct)
    {
        source.Position = 0;
        Span<byte> start = stackalloc byte[32];
        source.ReadExactly(start);
        if (!start[..6].SequenceEqual(new byte[] { 0x37, 0x7a, 0xbc, 0xaf, 0x27, 0x1c }) || start[6] != 0)
            throw Invalid("Signature/version does not match a 7z archive.");
        if (Crc32.HashToUInt32(start[12..]) != BinaryPrimitives.ReadUInt32LittleEndian(start[8..]))
            throw Invalid("Corrupt 7z start header.");
        long offset = ToLong(BinaryPrimitives.ReadUInt64LittleEndian(start[12..]));
        long size = ToLong(BinaryPrimitives.ReadUInt64LittleEndian(start[20..]));
        if (size > MaxHeaderBytes || offset > source.Length - 32 || size > source.Length - 32 - offset)
            throw Invalid("7z header exceeds its memory limit or archive bounds (split archives are unsupported).");
        if (size == 0)
            return [];
        source.Position = 32 + offset;
        var header = new byte[(int)size];
        source.ReadExactly(header);
        if (Crc32.HashToUInt32(header) != BinaryPrimitives.ReadUInt32LittleEndian(start[28..]))
            throw Invalid("Corrupt 7z next header.");
        var parser = new Parser(header, source, ct);
        if (parser.Byte() == 0x17) // EncodedHeader
        {
            var shape = parser.Streams();
            parser.End();
            if (shape.Folders.Count != 1 || shape.PackedSizes.Count != 1 || shape.Folders[0].Coders.Count != 1 || shape.Folders[0].Substreams != 1)
                throw Invalid("Unsupported 7z encoded-header coder graph.");
            var folder = shape.Folders[0];
            long outputSize = folder.Sizes[0];
            if (outputSize > MaxHeaderBytes || shape.PackedSizes[0] > MaxHeaderBytes)
                throw Invalid("Decoded 7z header exceeds its memory limit.");
            var coder = folder.Coders[0];
            if (coder.Id is not (Copy or Lzma or Lzma2))
                throw Invalid("Unsupported 7z header coder.");
            source.Position = 32 + shape.PackOffset;
            var packed = new byte[(int)shape.PackedSizes[0]];
            source.ReadExactly(packed);
            using var input = new MemoryStream(packed, writable: false);
            using Stream decoded = coder.Id == Copy ? input : LzmaStream.Create(
                coder.Properties, input, packed.Length, outputSize, leaveOpen: true);
            header = new byte[(int)outputSize];
            // Synchronous reads avoid SharpCompress 0.50.4's documented async LZMA state bug.
            int read = 0;
            while (read < header.Length)
            {
                ct.ThrowIfCancellationRequested();
                int count = decoded.Read(header, read, Math.Min(64 * 1024, header.Length - read));
                if (count == 0)
                    throw Invalid("Truncated encoded 7z header.");
                read += count;
            }
            if (decoded.ReadByte() != -1 || Crc32.HashToUInt32(header) != shape.PayloadCrcs[0])
                throw Invalid("Corrupt decoded 7z header.");
            parser = new Parser(header, source, ct);
            parser.Expect(1);
        }
        else
        {
            parser = new Parser(header, source, ct);
            parser.Expect(1);
        }
        var crcs = parser.Header();
        parser.End();
        return crcs;
    }

    private static long ToLong(ulong value) => value <= long.MaxValue ? (long)value : throw Invalid("Invalid 7z size.");
    private static InvalidDataException Invalid(string message) => new(message);
    private sealed record Coder(ulong Id, byte[] Properties);
    private sealed class Folder
    {
        public List<Coder> Coders { get; } = [];
        public List<long> Sizes { get; } = [];
        public uint? Crc { get; set; }
        public int Substreams { get; set; } = 1;
        public int Output { get; set; }
    }
    private sealed class Shape
    {
        public long PackOffset { get; set; }
        public List<long> PackedSizes { get; } = [];
        public List<Folder> Folders { get; } = [];
        public List<uint> PayloadCrcs { get; } = [];
    }

    private sealed class Parser(ReadOnlyMemory<byte> data, Stream source, CancellationToken ct)
    {
        private int _position;
        public byte Byte()
        {
            if (_position >= data.Length)
                throw Invalid("Truncated 7z metadata.");
            return data.Span[_position++];
        }
        private ReadOnlySpan<byte> Bytes(int count)
        {
            if (count < 0 || count > data.Length - _position)
                throw Invalid("Truncated 7z metadata.");
            var bytes = data.Span.Slice(_position, count);
            _position += count;
            return bytes;
        }
        private ulong Number()
        {
            byte first = Byte();
            int mask = 0x80;
            ulong value = 0;
            for (int i = 0; i < 8; i++, mask >>= 1)
            {
                if ((first & mask) == 0)
                    return value | ((ulong)(first & (mask - 1)) << (8 * i));
                value |= (ulong)Byte() << (8 * i);
            }
            return value;
        }
        private int Count(int max = MaxItems)
        {
            ulong count = Number();
            if (count > (ulong)max)
                throw Invalid("7z metadata count exceeds its memory limit.");
            return (int)count;
        }
        public void Expect(byte expected)
        {
            if (Byte() != expected)
                throw Invalid("Unsupported or corrupt 7z metadata structure.");
        }
        public void End()
        {
            if (_position != data.Length)
                throw Invalid("Unexpected trailing 7z metadata.");
        }
        private uint[] Digests(int count, out bool[] defined)
        {
            defined = Bits(count, optional: true);
            var values = new uint[count];
            for (int i = 0; i < count; i++)
                if (defined[i])
                    values[i] = BinaryPrimitives.ReadUInt32LittleEndian(Bytes(4));
            return values;
        }
        private bool[] Bits(int count, bool optional = false)
        {
            var result = new bool[count];
            if (optional && Byte() != 0)
            { Array.Fill(result, true); return result; }
            byte value = 0;
            for (int i = 0; i < count; i++)
            {
                if (i % 8 == 0)
                    value = Byte();
                result[i] = (value & (0x80 >> (i % 8))) != 0;
            }
            return result;
        }
        public IReadOnlyList<uint> Header()
        {
            byte id = Byte();
            if (id == 2) // ArchiveProperties, length-delimited
            {
                while (Byte() != 0)
                    Bytes(Count(MaxHeaderBytes));
                id = Byte();
            }
            // AdditionalStreamsInfo/external metadata can allocate decoded blobs before files
            // are enumerated. Reject it rather than leave an unchecked secondary decode path.
            if (id == 3)
                throw Invalid("External 7z metadata streams are unsupported.");
            List<uint> payloadCrcs = [];
            if (id == 4)
            { payloadCrcs = Streams().PayloadCrcs; id = Byte(); }
            IReadOnlyList<uint> fileCrcs = [];
            if (id == 5)
            { fileCrcs = Files(payloadCrcs); id = Byte(); }
            else if (payloadCrcs.Count != 0)
                throw Invalid("Missing 7z file metadata.");
            if (id != 0)
                throw Invalid("Unsupported 7z header structure.");
            return fileCrcs;
        }
        public Shape Streams()
        {
            var shape = new Shape();
            byte id = Byte();
            if (id == 6)
            {
                shape.PackOffset = ToLong(Number());
                int packs = Count();
                Expect(9);
                for (int i = 0; i < packs; i++)
                    shape.PackedSizes.Add(ToLong(Number()));
                id = Byte();
                if (id == 10)
                { Digests(packs, out _); id = Byte(); }
                if (id != 0)
                    throw Invalid("Unsupported 7z pack metadata.");
                id = Byte();
            }
            if (id == 7)
            {
                Expect(11);
                int folders = Count();
                Expect(0); // inline folders only
                for (int i = 0; i < folders; i++)
                { ct.ThrowIfCancellationRequested(); shape.Folders.Add(ReadFolder()); }
                Expect(12);
                foreach (var folder in shape.Folders)
                    for (int i = 0; i < folder.Coders.Count; i++)
                        folder.Sizes.Add(ToLong(Number()));
                id = Byte();
                if (id == 10)
                {
                    uint[] crcs = Digests(folders, out bool[] defined);
                    for (int i = 0; i < folders; i++)
                        if (defined[i])
                            shape.Folders[i].Crc = crcs[i];
                    id = Byte();
                }
                if (id != 0)
                    throw Invalid("Unsupported 7z unpack metadata.");
                id = Byte();
            }
            if (shape.PackedSizes.Count != shape.Folders.Count)
                throw Invalid("Unsupported 7z packed-stream graph.");
            long remaining = source.Length - 32;
            if (shape.PackOffset > remaining)
                throw Invalid("Invalid 7z packed offset.");
            remaining -= shape.PackOffset;
            foreach (long size in shape.PackedSizes)
            {
                if (size > remaining)
                    throw Invalid("Truncated/split 7z packed stream.");
                remaining -= size;
            }
            bool substreamDigests = false;
            if (id == 8)
            {
                id = Byte();
                if (id == 13)
                {
                    int total = 0;
                    foreach (var folder in shape.Folders)
                    {
                        folder.Substreams = Count();
                        total = checked(total + folder.Substreams);
                        if (total > MaxItems)
                            throw Invalid("Too many 7z substreams.");
                    }
                    id = Byte();
                }
                if (id == 9)
                {
                    foreach (var folder in shape.Folders)
                    {
                        long remainingSize = folder.Sizes[folder.Output];
                        for (int i = 1; i < folder.Substreams; i++)
                        {
                            long size = ToLong(Number());
                            if (size > remainingSize)
                                throw Invalid("Invalid 7z substream sizes.");
                            remainingSize -= size;
                        }
                    }
                    id = Byte();
                }
                if (id == 10)
                {
                    int digests = shape.Folders.Sum(f => f.Substreams == 1 && f.Crc.HasValue ? 0 : f.Substreams);
                    uint[] values = Digests(digests, out bool[] defined);
                    if (defined.Any(value => !value))
                        throw Invalid("7z payload CRCs are required.");
                    int index = 0;
                    foreach (var folder in shape.Folders)
                    {
                        if (folder.Substreams == 1 && folder.Crc.HasValue)
                            shape.PayloadCrcs.Add(folder.Crc.Value);
                        else
                            for (int i = 0; i < folder.Substreams; i++)
                                shape.PayloadCrcs.Add(values[index++]);
                    }
                    substreamDigests = true;
                    id = Byte();
                }
                if (id != 0)
                    throw Invalid("Unsupported 7z substream metadata.");
                id = Byte();
            }
            if (!substreamDigests)
                foreach (var folder in shape.Folders)
                {
                    if (folder.Substreams == 0)
                        continue;
                    if (folder.Substreams != 1 || !folder.Crc.HasValue)
                        throw Invalid("7z payload CRCs are required.");
                    shape.PayloadCrcs.Add(folder.Crc.Value);
                }
            if (id != 0)
                throw Invalid("Unsupported 7z stream metadata.");
            return shape;
        }
        private Folder ReadFolder()
        {
            var folder = new Folder();
            int count = Count(8);
            if (count == 0)
                throw Invalid("Empty 7z coder graph.");
            long dictionaries = 0;
            for (int i = 0; i < count; i++)
            {
                byte flags = Byte();
                int idLength = flags & 15;
                if (idLength is < 1 or > 8 || (flags & 0xc0) != 0)
                    throw Invalid("Unsupported 7z coder.");
                ulong id = 0;
                foreach (byte part in Bytes(idLength))
                    id = (id << 8) | part;
                if ((flags & 0x10) != 0 && (Count(4) != 1 || Count(4) != 1))
                    throw Invalid("Non-linear 7z coder graphs are unsupported.");
                byte[] properties = (flags & 0x20) != 0 ? Bytes(Count(16)).ToArray() : [];
                dictionaries += ValidateCoder(id, properties);
                if (dictionaries > MaxDictionaryBytes)
                    throw Invalid("7z dictionary memory limit exceeded (64 MiB per folder).");
                folder.Coders.Add(new Coder(id, properties));
            }
            var next = Enumerable.Repeat(-1, count).ToArray();
            var boundInputs = new HashSet<int>();
            var boundOutputs = new HashSet<int>();
            for (int i = 1; i < count; i++)
            {
                int input = Count(count - 1), output = Count(count - 1);
                if (!boundInputs.Add(input) || !boundOutputs.Add(output))
                    throw Invalid("Overlapping 7z coder bindings.");
                next[output] = input;
            }
            int current = Enumerable.Range(0, count).Single(i => !boundInputs.Contains(i));
            var visited = new HashSet<int>();
            while (current != -1)
            {
                if (!visited.Add(current))
                    throw Invalid("Cyclic 7z coder graph.");
                folder.Output = current;
                current = next[current];
            }
            if (visited.Count != count)
                throw Invalid("Disconnected 7z coder graph.");
            return folder;
        }
        private static long ValidateCoder(ulong id, byte[] p)
        {
            switch (id)
            {
                case Copy when p.Length == 0:
                    return 0;
                case Lzma when p.Length == 5 && p[0] < 225:
                    return BinaryPrimitives.ReadUInt32LittleEndian(p.AsSpan(1));
                case Lzma2 when p.Length == 1 && p[0] <= 40:
                    return p[0] == 40 ? uint.MaxValue : (2L | (p[0] & 1)) << (p[0] / 2 + 11);
                case 3 when p.Length == 1:
                    return 0; // Delta
                case 0x03030103 or 0x03030205 or 0x03030401 or 0x03030501 or 0x03030701 or 0x03030805
                    or 0x0a or 0x0b when p.Length == 0:
                    return 0; // fixed-memory BCJ filters
                case 0x06f10701:
                    throw Invalid("Encrypted 7z archives are unsupported (no password support).");
                default:
                    throw Invalid("Unsupported 7z coder (including PPMd and non-linear graphs).");
            }
        }
        private IReadOnlyList<uint> Files(List<uint> payloadCrcs)
        {
            int files = Count();
            int emptyStreams = 0;
            var empty = new bool[files];
            bool namesSeen = false;
            byte id;
            while ((id = Byte()) != 0)
            {
                ct.ThrowIfCancellationRequested();
                int size = Count(MaxHeaderBytes);
                var property = new Parser(Bytes(size).ToArray(), source, ct);
                switch (id)
                {
                    case 14:
                        empty = property.Bits(files);
                        emptyStreams = empty.Count(b => b);
                        break;
                    case 15 or 16:
                        property.Bits(emptyStreams);
                        break;
                    case 17:
                        if (namesSeen)
                            throw Invalid("Duplicate 7z name metadata.");
                        namesSeen = true;
                        property.Expect(0); // no external names
                        for (int i = 0; i < files; i++)
                        {
                            bool terminated = false;
                            while (property._position < property.dataLength)
                            {
                                if (BinaryPrimitives.ReadUInt16LittleEndian(property.Bytes(2)) == 0)
                                { terminated = true; break; }
                            }
                            if (!terminated)
                                throw Invalid("Unterminated 7z entry name.");
                        }
                        break;
                    case 18 or 19 or 20 or 21 or 24:
                        bool[] defined = property.Bits(files, optional: true);
                        property.Expect(0); // no external attributes/timestamps/start positions
                        property.Bytes(defined.Count(b => b) * (id == 21 ? 4 : 8));
                        break;
                    default:
                        continue; // length-delimited unknown file properties are skipped by SharpCompress too
                }
                property.End();
            }
            if (files > 0 && !namesSeen)
                throw Invalid("Missing 7z entry names.");
            if (files - emptyStreams != payloadCrcs.Count)
                throw Invalid("7z file/stream count mismatch.");
            int stream = 0;
            return empty.Select(isEmpty => isEmpty ? 0u : payloadCrcs[stream++]).ToArray();
        }
        private int dataLength => data.Length;
    }
}
