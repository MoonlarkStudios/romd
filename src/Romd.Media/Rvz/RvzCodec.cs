using System.Buffers.Binary;
using System.Collections.Concurrent;
using SharpCompress.Compressors;
using SharpCompress.Compressors.BZip2;
using SharpCompress.Compressors.LZMA;
using ZstdSharp;

namespace Romd.Media.Rvz;

/// <summary>
/// Decompression methods allowed in RVZ files. Every method decompresses a complete, independent
/// stream into a caller-bounded destination and rejects output that would exceed it.
/// </summary>
internal abstract class RvzCodec : IDisposable
{
    public const uint None = 0, Purge = 1, Bzip2 = 2, Lzma = 3, Lzma2 = 4, Zstd = 5;
    private const int MinDictionaryBytes = 1 << 12;

    public static RvzCodec? Create(uint method, ReadOnlySpan<byte> properties) => method switch
    {
        None when properties.IsEmpty => null,
        Bzip2 when properties.IsEmpty => new Bzip2Codec(),
        Lzma when properties.Length == 5 => new LzmaCodec(properties),
        Lzma2 when properties.Length == 1 => new Lzma2Codec(properties[0]),
        Zstd when properties.IsEmpty => new ZstdCodec(),
        None or Bzip2 or Lzma or Lzma2 or Zstd => throw new InvalidDataException("RVZ compression properties do not match the method."),
        _ => throw new NotSupportedException($"RVZ compression method {method} is not supported.")
    };

    public abstract int Decompress(byte[] source, int sourceLength, Span<byte> destination);

    public virtual void Dispose() { }

    private static int ReadStream(Func<Stream> open, Span<byte> destination)
    {
        try
        {
            using var stream = open();
            int total = 0;
            while (total < destination.Length)
            {
                int read = stream.Read(destination[total..]);
                if (read == 0)
                    return total;
                total += read;
            }
            Span<byte> probe = stackalloc byte[1];
            if (stream.Read(probe) != 0)
                throw new InvalidDataException("Decompressed RVZ data exceeds its limit.");
            return total;
        }
        catch (Exception error) when (error is not (InvalidDataException or OutOfMemoryException))
        {
            throw new InvalidDataException("Invalid compressed RVZ data.", error);
        }
    }

    // Each RVZ stream decompresses independently to at most the destination length, so no match
    // distance can exceed it; a smaller dictionary than declared is therefore sufficient.
    private static uint DictionaryFor(uint declared, int destination) =>
        Math.Min(declared, (uint)Math.Max(MinDictionaryBytes, destination));

    private sealed class Bzip2Codec : RvzCodec
    {
        public override int Decompress(byte[] source, int sourceLength, Span<byte> destination) =>
            ReadStream(() => BZip2Stream.Create(new MemoryStream(source, 0, sourceLength, writable: false),
                CompressionMode.Decompress, decompressConcatenated: false, leaveOpen: false, tolerateTruncatedStream: false), destination);
    }

    private sealed class LzmaCodec : RvzCodec
    {
        private readonly byte _parameters;
        private readonly uint _dictionary;

        public LzmaCodec(ReadOnlySpan<byte> properties)
        {
            if (properties[0] >= 9 * 5 * 5)
                throw new InvalidDataException("Invalid RVZ LZMA properties.");
            _parameters = properties[0];
            _dictionary = BinaryPrimitives.ReadUInt32LittleEndian(properties[1..]);
        }

        public override int Decompress(byte[] source, int sourceLength, Span<byte> destination)
        {
            byte[] properties = new byte[5];
            properties[0] = _parameters;
            BinaryPrimitives.WriteUInt32LittleEndian(properties.AsSpan(1), DictionaryFor(_dictionary, destination.Length));
            return ReadStream(() => LzmaStream.Create(properties, new MemoryStream(source, 0, sourceLength, writable: false),
                sourceLength, -1, leaveOpen: false), destination);
        }
    }

    private sealed class Lzma2Codec : RvzCodec
    {
        private readonly uint _dictionary;

        public Lzma2Codec(byte property)
        {
            if (property > 40)
                throw new InvalidDataException("Invalid RVZ LZMA2 properties.");
            _dictionary = DictionarySize(property);
        }

        public override int Decompress(byte[] source, int sourceLength, Span<byte> destination)
        {
            uint wanted = DictionaryFor(_dictionary, destination.Length);
            byte property = 0;
            while (DictionarySize(property) < wanted)
                property++;
            return ReadStream(() => LzmaStream.Create([property], new MemoryStream(source, 0, sourceLength, writable: false),
                sourceLength, -1, presetDictionary: null, isLzma2: true, leaveOpen: false), destination);
        }

        private static uint DictionarySize(byte property) =>
            property == 40 ? uint.MaxValue : (2u | (property & 1u)) << (property / 2 + 11);
    }

    private sealed class ZstdCodec : RvzCodec
    {
        private readonly ConcurrentBag<Decompressor> _decompressors = [];

        public override int Decompress(byte[] source, int sourceLength, Span<byte> destination)
        {
            if (!_decompressors.TryTake(out var decompressor))
                decompressor = new Decompressor();
            try
            { return decompressor.Unwrap(source.AsSpan(0, sourceLength), destination); }
            catch (ZstdException error) { throw new InvalidDataException("Invalid compressed RVZ data.", error); }
            finally { _decompressors.Add(decompressor); }
        }

        public override void Dispose()
        {
            while (_decompressors.TryTake(out var decompressor))
                decompressor.Dispose();
        }
    }
}
