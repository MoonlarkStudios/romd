using System.Buffers.Binary;
using System.IO.Hashing;
using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using Romd.Domain.Hashing;
using Romd.Storage.Exceptions;
using Romd.Storage.FileSystem;
using Shouldly;
using Xunit;
using ZstdSharp;

namespace Romd.Storage.Tests.FileSystem;

public sealed class RandomAccessContentTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "romd-random-tests", Guid.NewGuid().ToString("N"));
    private FileSystemContentStore _store = null!;
    private const int FrameSize = 64 * 1024;

    public Task InitializeAsync()
    {
        _store = CreateStore();
        return Task.CompletedTask;
    }

    private FileSystemContentStore CreateStore(double ratio = 0.95) => new(Options.Create(new ContentStoreOptions
    {
        RootPath = _root,
        WritePolicy = ContentWritePolicy.SeekableZstandard,
        SeekableFrameSize = FrameSize,
        MinCompressionRatio = ratio,
        MaxContentSize = 32 * 1024 * 1024
    }));

    public async Task DisposeAsync()
    {
        await _store.DisposeAsync();
        Directory.Delete(_root, true);
    }

    private static byte[] Content(int size = FrameSize * 4 + 73)
    {
        byte[] bytes = new byte[size];
        for (int i = 0; i < size; i++)
            bytes[i] = (byte)((i / 13 + i / FrameSize) % 251);
        return bytes;
    }

    private async Task<StoreResult> Store(byte[] bytes) => await _store.StoreAsync(new MemoryStream(bytes));
    private string Blob(StorageKey key, string extension) =>
        Path.Combine(_root, key.ToString()[..2], key.ToString()[2..4], key + extension);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadAtAsync_RandomAndBoundaryRanges_MatchOriginal(bool raw)
    {
        byte[] bytes = raw ? RandomNumberGenerator.GetBytes(FrameSize * 4 + 73) : Content();
        var stored = await Store(bytes);
        stored.IsCompressed.ShouldBe(!raw);
        await using var reader = (await _store.OpenRandomAccessAsync(stored.Key))!;
        reader.Length.ShouldBe(bytes.Length);
        reader.Integrity.ShouldBe(raw ? ContentReadIntegrity.None : ContentReadIntegrity.FrameChecksum);
        var random = new Random(42);
        var offsets = new long[] { 0, FrameSize - 1, FrameSize, bytes.Length - 1, bytes.Length, long.MaxValue }
            .Concat(Enumerable.Range(0, 100).Select(_ => (long)random.Next(bytes.Length)));
        foreach (long offset in offsets)
        {
            byte[] output = new byte[random.Next(1, FrameSize * 2)];
            int count = await reader.ReadAtAsync(offset, output);
            int expected = offset >= bytes.Length ? 0 : (int)Math.Min(output.Length, bytes.Length - offset);
            count.ShouldBe(expected);
            output.AsSpan(0, count).SequenceEqual(bytes.AsSpan((int)Math.Min(offset, bytes.Length), count)).ShouldBeTrue();
        }
        (await reader.ReadAtAsync(0, Memory<byte>.Empty)).ShouldBe(0);
        await Should.ThrowAsync<ArgumentOutOfRangeException>(async () => await reader.ReadAtAsync(-1, new byte[1]));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StoreAsync_SourceChangesOnRewind_DoesNotPublishWrongIdentity(bool duringRawFallback)
    {
        // The compressible prefix passes the sample, so the discarded candidate's raw fallback is the third rewind.
        await using var store = CreateStore(duringRawFallback ? 0.5 : 0.95);
        byte[] bytes = duringRawFallback
            ? [.. Content(FrameSize * 4), .. RandomNumberGenerator.GetBytes(FrameSize * 8)]
            : Content();
        await using var source = new ChangingSource(bytes, duringRawFallback ? 3 : 1);
        await Should.ThrowAsync<ContentCorruptedException>(() => store.StoreAsync(source));
        Directory.GetFiles(_root, "*", SearchOption.AllDirectories).Where(path => Path.GetFileName(path) != MutationLease.FileName).ToArray().ShouldBeEmpty();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StoreAsync_SourceWithinSampleChangesOnRewind_DoesNotPublishWrongIdentity(bool compressible)
    {
        byte[] bytes = compressible ? Content(FrameSize * 2) : RandomNumberGenerator.GetBytes(FrameSize * 2);
        await using var source = new ChangingSource(bytes, 1);
        await Should.ThrowAsync<ContentCorruptedException>(() => _store.StoreAsync(source));
        Directory.GetFiles(_root, "*", SearchOption.AllDirectories).Where(path => Path.GetFileName(path) != MutationLease.FileName).ToArray().ShouldBeEmpty();
    }

    [Theory]
    [InlineData(1)] // before the compression sample
    [InlineData(2)] // between the sample and the early raw write
    public async Task StoreAsync_SampledIncompressibleSourceChangesOnAnyRewind_DoesNotPublishWrongIdentity(int changeOnRewind)
    {
        byte[] bytes = RandomNumberGenerator.GetBytes(FrameSize * 4 + 73);
        await using var source = new ChangingSource(bytes, changeOnRewind);
        await Should.ThrowAsync<ContentCorruptedException>(() => _store.StoreAsync(source));
        Directory.GetFiles(_root, "*", SearchOption.AllDirectories).Where(path => Path.GetFileName(path) != MutationLease.FileName).ToArray().ShouldBeEmpty();
    }

    private sealed class ChangingSource : MemoryStream
    {
        private readonly byte[] _bytes;
        private readonly int _changeOnRewind;
        private int _rewinds;

        public ChangingSource(byte[] bytes, int changeOnRewind) : base(bytes)
        {
            _bytes = bytes;
            _changeOnRewind = changeOnRewind;
        }
        public override long Position
        {
            get => base.Position;
            set
            {
                if (++_rewinds == _changeOnRewind)
                    _bytes[0] ^= 1;
                base.Position = value;
            }
        }
    }

    [Fact]
    public async Task ReadAtAsync_ConcurrentReads_KeepPositionsIndependent()
    {
        byte[] bytes = Content();
        var stored = await Store(bytes);
        await using var reader = (await _store.OpenRandomAccessAsync(stored.Key))!;
        await Task.WhenAll(Enumerable.Range(0, 64).Select(async i =>
        {
            int offset = i * 3001;
            byte[] result = new byte[4096];
            (await reader.ReadAtAsync(offset, result)).ShouldBe(result.Length);
            result.ShouldBe(bytes[offset..(offset + result.Length)]);
        }));
    }

    [Fact]
    public async Task ReadAtAsync_LateFrame_DoesNotDecodeEarlierFrames()
    {
        var stored = await Store(Content());
        string path = Blob(stored.Key, ".szst");
        byte[] encoded = await File.ReadAllBytesAsync(path);
        encoded[0] ^= 0xff; // Earlier frame is deliberately undecodable.
        await File.WriteAllBytesAsync(path, encoded);
        await using var reader = (await _store.OpenRandomAccessAsync(stored.Key))!;
        byte[] output = new byte[73];
        (await reader.ReadAtAsync(FrameSize * 4, output)).ShouldBe(73);
        output.ShouldBe(Content()[^73..]);
        await Should.ThrowAsync<InvalidDataException>(async () => await reader.ReadAtAsync(0, output));
    }

    [Fact]
    public async Task ReadAtAsync_BadChecksum_RejectsFrameBeforeCopyingBytes()
    {
        var stored = await Store(Content());
        string path = Blob(stored.Key, ".szst");
        byte[] encoded = await File.ReadAllBytesAsync(path);
        int table = encoded.Length - 17 - 5 * 12;
        encoded[table + 16] ^= 1;
        await File.WriteAllBytesAsync(path, encoded);
        await using var reader = (await _store.OpenRandomAccessAsync(stored.Key))!;
        byte[] destination = Enumerable.Repeat((byte)0xaa, 100).ToArray();
        await Should.ThrowAsync<InvalidDataException>(async () => await reader.ReadAtAsync(0, destination));
        destination.ShouldAllBe(b => b == 0xaa);
        await Should.ThrowAsync<InvalidDataException>(async () => await reader.ReadAtAsync(0, destination));
    }

    [Theory]
    [InlineData("footer")]
    [InlineData("count")]
    [InlineData("flags")]
    [InlineData("size")]
    [InlineData("offset")]
    [InlineData("truncated")]
    [InlineData("nonuniform")]
    [InlineData("oversized-last")]
    public async Task OpenRandomAccessAsync_MalformedIndex_RejectsBeforeReading(string corruption)
    {
        var stored = await Store(Content());
        string path = Blob(stored.Key, ".szst");
        byte[] encoded = await File.ReadAllBytesAsync(path);
        int table = encoded.Length - 17 - 5 * 12;
        switch (corruption)
        {
            case "footer":
                encoded[^1] ^= 1;
                break;
            case "count":
                BinaryPrimitives.WriteUInt32LittleEndian(encoded.AsSpan(encoded.Length - 9), uint.MaxValue);
                break;
            case "flags":
                encoded[^5] = 0;
                break;
            case "size":
                BinaryPrimitives.WriteUInt32LittleEndian(encoded.AsSpan(table + 12), uint.MaxValue);
                break;
            case "offset":
                BinaryPrimitives.WriteUInt32LittleEndian(encoded.AsSpan(table + 8), uint.MaxValue);
                break;
            case "truncated":
                encoded = encoded[..10];
                break;
            case "nonuniform":
                // Same logical length, but a short middle frame.
                BinaryPrimitives.WriteUInt32LittleEndian(encoded.AsSpan(table + 8 + 12 + 4), FrameSize - 1);
                BinaryPrimitives.WriteUInt32LittleEndian(encoded.AsSpan(table + 8 + 4 * 12 + 4), 74);
                break;
            case "oversized-last":
                BinaryPrimitives.WriteUInt32LittleEndian(encoded.AsSpan(table + 8 + 4 * 12 + 4), FrameSize + 1);
                break;
        }
        await File.WriteAllBytesAsync(path, encoded);
        await Should.ThrowAsync<InvalidDataException>(() => _store.OpenRandomAccessAsync(stored.Key));
    }

    [Fact]
    public async Task StoreAsync_ForcedCompressedEmptyObject_ReadsAndVerifies()
    {
        await using var store = CreateStore(1);
        var stored = await store.StoreAsync(new MemoryStream([]));
        stored.IsCompressed.ShouldBeTrue();
        await using var reader = (await store.OpenRandomAccessAsync(stored.Key))!;
        reader.Length.ShouldBe(0);
        (await reader.ReadAtAsync(0, new byte[1])).ShouldBe(0);
        await using var sequential = (await store.RetrieveAsync(stored.Key))!;
        await sequential.CopyToAsync(Stream.Null);
        ((HashVerifyingStream)sequential).IsVerified.ShouldBeTrue();
    }

    [Fact]
    public async Task Readers_ObjectsBeyondWriterSettings_OpenRetrieveAndVerify()
    {
        byte[] compressible = Content(), random = RandomNumberGenerator.GetBytes(FrameSize * 2);
        var compressed = await Store(compressible);
        var raw = await Store(random);
        compressed.IsCompressed.ShouldBeTrue();
        raw.IsCompressed.ShouldBeFalse();
        // Size limits and frame size only admit new content; each object bounds its own reads.
        await using var limited = new FileSystemContentStore(Options.Create(new ContentStoreOptions
        {
            RootPath = _root,
            MaxContentSize = 1024,
            SeekableFrameSize = 1024 * 1024
        }));
        foreach (var (key, bytes) in new[] { (compressed.Key, compressible), (raw.Key, random) })
        {
            await using (var reader = (await limited.OpenRandomAccessAsync(key))!)
            {
                byte[] all = new byte[bytes.Length];
                await reader.ReadExactlyAtAsync(0, all);
                all.SequenceEqual(bytes).ShouldBeTrue();
            }
            await using (var sequential = (await limited.RetrieveAsync(key))!)
            {
                using var copy = new MemoryStream();
                await sequential.CopyToAsync(copy);
                copy.ToArray().SequenceEqual(bytes).ShouldBeTrue();
                ((HashVerifyingStream)sequential).IsVerified.ShouldBeTrue();
            }
            (await limited.VerifyAsync(key)).ShouldBeTrue();
        }
    }

    [Theory]
    [InlineData(64 * 1024)]
    [InlineData(256 * 1024)]
    [InlineData(1024 * 1024)]
    public async Task SeekableObjects_AroundFrameBoundaries_RoundTrip(int frameSize)
    {
        await using var store = new FileSystemContentStore(Options.Create(new ContentStoreOptions
        {
            RootPath = _root,
            SeekableFrameSize = frameSize
        }));
        foreach (int size in new[] { 1000, frameSize - 1, frameSize, frameSize + 1, frameSize * 3 })
        {
            byte[] bytes = Content(size);
            var stored = await store.StoreAsync(new MemoryStream(bytes));
            stored.IsCompressed.ShouldBeTrue();
            await using (var reader = (await store.OpenRandomAccessAsync(stored.Key))!)
            {
                reader.Length.ShouldBe(size);
                byte[] all = new byte[size];
                await reader.ReadExactlyAtAsync(0, all);
                all.SequenceEqual(bytes).ShouldBeTrue();
            }
            (await store.VerifyAsync(stored.Key)).ShouldBeTrue();
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1000)]
    [InlineData(FrameSize)]
    [InlineData(FrameSize * 2 + 73)]
    public async Task Encode_InMemoryContent_MatchesStreamingWriterByteForByte(int size)
    {
        byte[] bytes = Content(size);
        var key = StorageKey.FromHash(Sha256.FromSpan(SHA256.HashData(bytes)));
        using var streamed = new MemoryStream();
        await SeekableZstandard.WriteAsync(new MemoryStream(bytes), streamed, FrameSize, 3, long.MaxValue, key,
            null, null, CancellationToken.None);
        byte[] encoded = new byte[SeekableZstandard.EncodedBound(size, FrameSize)];

        int length = SeekableZstandard.Encode(bytes, encoded, FrameSize, 3, key);

        encoded[..length].ShouldBe(streamed.ToArray());
    }

    [Fact]
    public async Task RetrieveAsync_FrameDecodingPastItsIndexedSize_IsRejected()
    {
        var stored = await Store(Content());
        string path = Blob(stored.Key, ".szst");
        byte[] encoded = await File.ReadAllBytesAsync(path);
        int table = encoded.Length - 17 - 5 * 12;
        BinaryPrimitives.WriteUInt32LittleEndian(encoded.AsSpan(table + 8 + 4 * 12 + 4), 72);
        await File.WriteAllBytesAsync(path, encoded);
        await using var sequential = (await _store.RetrieveAsync(stored.Key))!;
        await Should.ThrowAsync<InvalidDataException>(() => sequential.CopyToAsync(Stream.Null));
    }

    [Fact]
    public async Task OpenRandomAccessAsync_TableSpanningReadBatches_MapsEveryFrame()
    {
        // More entries than one table read batch; frames around the batch boundary differ.
        const int frames = 65_536 + 3;
        using var compressor = new Compressor(1);
        static byte FillOf(int frame) => (byte)Math.Max(0, frame - 65_533);
        var encoded = Enumerable.Range(0, FillOf(frames - 1) + 1).Select(fill =>
        {
            byte[] bytes = Enumerable.Repeat((byte)fill, FrameSize).ToArray();
            return (Stored: compressor.Wrap(bytes).ToArray(), Checksum: (uint)XxHash64.HashToUInt64(bytes));
        }).ToArray();
        var key = StorageKey.FromHash(Sha256.FromSpan(SHA256.HashData("batched"u8)));
        string path = Blob(key, ".szst");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        byte[] table = new byte[17 + frames * 12];
        BinaryPrimitives.WriteUInt32LittleEndian(table, 0x184D2A5E);
        BinaryPrimitives.WriteUInt32LittleEndian(table.AsSpan(4), (uint)(table.Length - 8));
        await using (var file = new FileStream(path, FileMode.CreateNew))
        {
            for (int frame = 0; frame < frames; frame++)
            {
                var (stored, checksum) = encoded[FillOf(frame)];
                file.Write(stored);
                var entry = table.AsSpan(8 + frame * 12);
                BinaryPrimitives.WriteUInt32LittleEndian(entry, (uint)stored.Length);
                BinaryPrimitives.WriteUInt32LittleEndian(entry[4..], FrameSize);
                BinaryPrimitives.WriteUInt32LittleEndian(entry[8..], checksum);
            }
            BinaryPrimitives.WriteUInt32LittleEndian(table.AsSpan(table.Length - 9), frames);
            table[^5] = 0x80;
            BinaryPrimitives.WriteUInt32LittleEndian(table.AsSpan(table.Length - 4), 0x8F92EAB1);
            file.Write(table);
        }
        await using var reader = (await _store.OpenRandomAccessAsync(key))!;
        reader.Length.ShouldBe((long)frames * FrameSize);
        long start = 65_534L * FrameSize - 10;
        byte[] output = new byte[FrameSize * 3 + 20];
        await reader.ReadExactlyAtAsync(start, output);
        byte[] expected = Enumerable.Range(65_533, 5).SelectMany(frame => Enumerable.Repeat(FillOf(frame), FrameSize))
            .Skip(FrameSize - 10).Take(output.Length).ToArray();
        output.SequenceEqual(expected).ShouldBeTrue();
    }

    [Fact]
    public async Task OpenRandomAccessAsync_CorruptCountInLargeFile_RejectsWithoutProportionalAllocation()
    {
        var key = StorageKey.FromHash(Sha256.FromSpan(SHA256.HashData("sparse"u8)));
        string path = Blob(key, ".szst");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        const long length = 1L << 30;
        await using (var file = new FileStream(path, FileMode.CreateNew))
        {
            file.SetLength(length); // Sparse, so the test writes only the footer.
            byte[] footer = new byte[9];
            BinaryPrimitives.WriteUInt32LittleEndian(footer, (uint)((length - 17) / 12));
            footer[4] = 0x80;
            BinaryPrimitives.WriteUInt32LittleEndian(footer.AsSpan(5), 0x8F92EAB1);
            file.Position = length - footer.Length;
            await file.WriteAsync(footer);
        }
        long before = GC.GetTotalAllocatedBytes(precise: true);
        await Should.ThrowAsync<InvalidDataException>(() => _store.OpenRandomAccessAsync(key));
        // The counter is process-wide: the margin absorbs concurrent tests, not a gigabyte-scale index.
        (GC.GetTotalAllocatedBytes(precise: true) - before).ShouldBeLessThan(64L * 1024 * 1024);
    }

    [Fact]
    public async Task DeleteAsync_OpenReader_KeepsSnapshotUsable()
    {
        var stored = await Store(Content());
        await using var reader = (await _store.OpenRandomAccessAsync(stored.Key))!;
        (await _store.DeleteAsync(stored.Key)).ShouldBeTrue();
        (await _store.ExistsAsync(stored.Key)).ShouldBeFalse();
        byte[] output = new byte[128];
        (await reader.ReadAtAsync(FrameSize, output)).ShouldBe(128);
        output.ShouldBe(Content()[FrameSize..(FrameSize + 128)]);
    }

    [Fact]
    public async Task OpenRandomAccessAsync_MissingEmptyAndDisposed_RespectContract()
    {
        var missing = StorageKey.FromHash(Sha256.FromSpan(SHA256.HashData("missing"u8)));
        (await _store.OpenRandomAccessAsync(missing)).ShouldBeNull();
        var stored = await Store([]);
        var reader = (await _store.OpenRandomAccessAsync(stored.Key))!;
        reader.Length.ShouldBe(0);
        (await reader.ReadAtAsync(0, new byte[1])).ShouldBe(0);
        await _store.DisposeAsync();
        (await reader.ReadAtAsync(0, new byte[1])).ShouldBe(0);
        await reader.DisposeAsync();
        await reader.DisposeAsync();
        await Should.ThrowAsync<ObjectDisposedException>(async () => await reader.ReadAtAsync(0, new byte[1]));
    }

    [Fact]
    public async Task ReadAtAsync_Cancelled_LeavesReaderUsable()
    {
        var stored = await Store(Content());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await using var reader = (await _store.OpenRandomAccessAsync(stored.Key))!;
        await Should.ThrowAsync<OperationCanceledException>(async () => await reader.ReadAtAsync(0, new byte[1], cancellation.Token));
        (await reader.ReadAtAsync(0, new byte[1])).ShouldBe(1);
    }
}
