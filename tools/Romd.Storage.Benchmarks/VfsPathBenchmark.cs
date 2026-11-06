using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Romd.Storage;

/// <summary>
/// The storage half of a /vfs GET: open a reader, wrap it as a stream, seek, and copy the range through
/// ASP.NET Core's 64 KiB range-copy buffer. HTTP, authentication and the catalog lookup are excluded.
/// Objects are synthetic, a random 4 KiB block repeated, so every frame decodes while stored bytes stay small.
/// </summary>
internal static class VfsPathBenchmark
{
    private const int CopyBufferSize = 64 * 1024;
    private const int BlockSize = 4096;

    internal static async Task RunAsync(string[] arguments)
    {
        long[] sizes = arguments.Length > 0
            ? arguments.Select(ParseSize).ToArray()
            : [16L << 20, 1L << 30, 16L << 30, 64L << 30];
        byte[] block = new byte[BlockSize];
        new Random(1729).NextBytes(block);
        string root = Path.Combine(Path.GetTempPath(), "romd-cas-vfs", Guid.NewGuid().ToString("N"));
        var results = new List<object>();
        try
        {
            // Two stores over one root: seek-table sharing off (every request reads its table) and on.
            await using var uncachedProvider = CreateStore(root, sizes.Max(), seekIndexCacheBytes: 0);
            await using var cachedProvider = CreateStore(root, sizes.Max(), seekIndexCacheBytes: null);
            var uncached = uncachedProvider.GetRequiredService<IContentAddressableStore>();
            var cached = cachedProvider.GetRequiredService<IContentAddressableStore>();

            // Discarded pass so tiered JIT does not bias the first measured object.
            var warmup = await StoreAsync(uncached, block, 16L << 20);
            await MeasureAllAsync(uncached, warmup.Key, 16L << 20);
            await MeasureAllAsync(cached, warmup.Key, 16L << 20);
            await uncached.DeleteAsync(warmup.Key);

            foreach (long size in sizes)
            {
                Console.Error.WriteLine($"storing {size:N0} bytes");
                var timer = Stopwatch.StartNew();
                var stored = await StoreAsync(uncached, block, size);
                double storeSeconds = timer.Elapsed.TotalSeconds;
                await VerifyRangesAsync(cached, stored.Key, block, size);
                results.Add(new
                {
                    bytes = size,
                    storedBytes = stored.StoredSize,
                    storeSeconds,
                    uncached = await MeasureAllAsync(uncached, stored.Key, size),
                    cached = await MeasureAllAsync(cached, stored.Key, size)
                });
                await uncached.DeleteAsync(stored.Key);
            }
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }

        Console.WriteLine(JsonSerializer.Serialize(new
        {
            runtime = RuntimeInformation.FrameworkDescription,
            os = RuntimeInformation.OSDescription,
            architecture = RuntimeInformation.ProcessArchitecture.ToString(),
            note = "Default store settings (seekable zstd, 256 KiB frames, level 3); uncached sets " +
                "SeekIndexCacheBytes = 0, cached keeps the 64 MiB default. Each request opens a reader, " +
                "seeks a stream adapter and copies the range with a 64 KiB buffer, as /vfs range responses do, then " +
                "disposes it; openOnly opens and disposes. Sequential requests after 10 warm-ups; allocation is " +
                "process-wide over the timed requests. Warm OS cache; no HTTP, auth or catalog lookup.",
            results
        }, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static ServiceProvider CreateStore(string root, long maxContentSize, long? seekIndexCacheBytes) =>
        new ServiceCollection().AddContentAddressableStorage(options =>
        {
            options.RootPath = root;
            options.MaxContentSize = maxContentSize;
            if (seekIndexCacheBytes is { } bytes)
                options.SeekIndexCacheBytes = bytes;
        }).BuildServiceProvider();

    private static async Task<StoreResult> StoreAsync(IContentAddressableStore cas, byte[] block, long size)
    {
        await using var source = new SyntheticSource(block, size);
        var stored = await cas.StoreAsync(source);
        if (!stored.IsCompressed)
            throw new InvalidDataException("Synthetic content must store seekable.");
        return stored;
    }

    private static async Task<object> MeasureAllAsync(IContentAddressableStore cas, StorageKey key, long size)
    {
        var random = new Random(42);
        return new
        {
            openOnly = await MeasureAsync(cas, key, size, 0, 200, random),
            range64KiB = await MeasureAsync(cas, key, size, 64 * 1024, 200, random),
            range1MiB = await MeasureAsync(cas, key, size, 1024 * 1024, 100, random),
            range4MiB = await MeasureAsync(cas, key, size, 4 * 1024 * 1024, 50, random)
        };
    }

    private static async Task<object> MeasureAsync(IContentAddressableStore cas, StorageKey key, long size,
        int length, int requests, Random random)
    {
        byte[] buffer = new byte[CopyBufferSize];
        for (int i = 0; i < 10; i++)
            await RequestAsync();
        var timings = new double[requests];
        long allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        int gen2Before = GC.CollectionCount(2);
        for (int i = 0; i < requests; i++)
        {
            long started = Stopwatch.GetTimestamp();
            await RequestAsync();
            timings[i] = Stopwatch.GetElapsedTime(started).TotalMicroseconds;
        }

        long allocated = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
        int gen2 = GC.CollectionCount(2) - gen2Before;
        Array.Sort(timings);
        return new
        {
            requests,
            p50Us = timings[requests / 2],
            p95Us = timings[(int)(requests * 0.95)],
            allocatedBytesPerRequest = allocated / requests,
            gen2Collections = gen2
        };

        async Task RequestAsync()
        {
            long offset = length == 0 ? 0 : random.NextInt64(size - length + 1);
            await using var reader = (await cas.OpenRandomAccessAsync(key))!;
            if (length == 0)
                return;
            await using var stream = reader.AsStream(leaveOpen: true);
            stream.Seek(offset, SeekOrigin.Begin);
            int remaining = length;
            while (remaining > 0)
            {
                int read = await stream.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, remaining)));
                if (read == 0)
                    throw new EndOfStreamException();
                remaining -= read;
            }
        }
    }

    private static async Task VerifyRangesAsync(IContentAddressableStore cas, StorageKey key, byte[] block, long size)
    {
        var random = new Random(7);
        byte[] actual = new byte[64 * 1024];
        byte[] expected = new byte[actual.Length];
        await using var reader = (await cas.OpenRandomAccessAsync(key))!;
        for (int i = 0; i < 20; i++)
        {
            long offset = random.NextInt64(size - actual.Length + 1);
            await reader.ReadExactlyAtAsync(offset, actual);
            SyntheticSource.Fill(block, offset, expected);
            if (!actual.AsSpan().SequenceEqual(expected))
                throw new InvalidDataException("Range verification failed.");
        }
    }

    private static long ParseSize(string text)
    {
        (string Suffix, long Scale)[] units = [("GiB", 1L << 30), ("MiB", 1L << 20), ("KiB", 1L << 10)];
        foreach (var (suffix, scale) in units)
            if (text.EndsWith(suffix, StringComparison.Ordinal))
                return long.Parse(text[..^suffix.Length], CultureInfo.InvariantCulture) * scale;
        return long.Parse(text, CultureInfo.InvariantCulture);
    }

    /// <summary>A seekable stream whose byte at position p is block[p % block.Length].</summary>
    private sealed class SyntheticSource(byte[] block, long length) : Stream
    {
        private long _position;

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => length;

        public override long Position
        {
            get => _position;
            set => _position = value;
        }

        internal static void Fill(byte[] block, long offset, Span<byte> destination)
        {
            while (!destination.IsEmpty)
            {
                int start = (int)(offset % block.Length);
                int count = Math.Min(block.Length - start, destination.Length);
                block.AsSpan(start, count).CopyTo(destination);
                destination = destination[count..];
                offset += count;
            }
        }

        public override int Read(Span<byte> buffer)
        {
            int count = (int)Math.Min(buffer.Length, Math.Max(0, length - _position));
            Fill(block, _position, buffer[..count]);
            _position += count;
            return count;
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) =>
            ValueTask.FromResult(Read(buffer.Span));

        public override long Seek(long offset, SeekOrigin origin) => _position = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            _ => length + offset
        };

        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
