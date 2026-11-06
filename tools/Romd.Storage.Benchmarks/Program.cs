using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Romd.Storage;
using ZstdSharp;

if (args.Length > 0 && args[0] == "--vfs")
{
    await VfsPathBenchmark.RunAsync(args[1..]);
    return;
}

if (args.Length > 0 && (args[0] == "--streaming" ||
    args.Any(path => new FileInfo(path).Length > 128L * 1024 * 1024)))
{
    await LargeFileBenchmark.RunAsync(args[0] == "--streaming" ? args[1..] : args);
    return;
}

// Repeatable microbenchmark; OS file cache remains warm. Never modifies supplied files or a live CAS.
// Optional arguments are paths to representative files; no ROM bytes or paths are emitted in results.
const int sampleSize = 16 * 1024 * 1024;
const int readSize = 4096;
const int randomReads = 500;
var inputs = new List<(string Name, byte[] Bytes)>();
if (args.Length > 0)
{
    for (int i = 0; i < args.Length; i++)
        inputs.Add(($"input-{i + 1}", await File.ReadAllBytesAsync(args[i])));
}
else
{
    byte[] seed = new byte[sampleSize];
    new Random(1729).NextBytes(seed);
    byte[] repeated = new byte[sampleSize];
    for (int i = 0; i < repeated.Length; i++)
        repeated[i] = seed[i % 32768];
    byte[] mixed = (byte[])seed.Clone();
    for (int i = 0; i < mixed.Length; i += 65536)
        Array.Clear(mixed, i, 32768);
    inputs.Add(("synthetic-repeated-32KiB", repeated));
    inputs.Add(("synthetic-half-zero-half-random", mixed));
    inputs.Add(("synthetic-random", seed));
}
var results = new List<object>();
foreach (var (name, bytes) in inputs)
{
    if (bytes.Length < readSize)
        throw new ArgumentException("Inputs must be at least 4 KiB.");
    using var compressor = new Compressor(3);
    byte[] legacy = compressor.Wrap(bytes).ToArray();
    for (int warmup = 0; warmup < 5; warmup++)
    {
        using var decoder = new DecompressionStream(new MemoryStream(legacy));
        decoder.CopyTo(Stream.Null);
    }
    foreach (int frameSize in new[] { 0, 64 * 1024, 256 * 1024, 1024 * 1024 })
    {
        string root = Path.Combine(Path.GetTempPath(), "romd-cas-benchmark", Guid.NewGuid().ToString("N"));
        try
        {
            var services = new ServiceCollection();
            services.AddContentAddressableStorage(options =>
            {
                options.RootPath = root;
                options.SeekableFrameSize = frameSize == 0 ? 256 * 1024 : frameSize;
                options.WritePolicy = frameSize == 0 ? ContentWritePolicy.Raw : ContentWritePolicy.SeekableZstandard;
            });
            await using var provider = services.BuildServiceProvider();
            var cas = provider.GetRequiredService<IContentAddressableStore>();
            var timer = Stopwatch.StartNew();
            var stored = await cas.StoreAsync(new MemoryStream(bytes));
            double storeMs = timer.Elapsed.TotalMilliseconds;
            await using var reader = (await cas.OpenRandomAccessAsync(stored.Key))!;
            byte[] buffer = new byte[readSize];
            // Warm the JIT and decoder. Random offsets mostly miss the one-frame cache.
            for (int i = 0; i < 50; i++)
                await reader.ReadAtAsync(i * 7919L % (bytes.Length - readSize + 1), buffer);
            var random = new Random(42);
            long allocationsBefore = GC.GetTotalAllocatedBytes(precise: true);
            var samples = new double[randomReads];
            for (int i = 0; i < samples.Length; i++)
            {
                int offset = random.Next(bytes.Length - readSize + 1);
                long start = Stopwatch.GetTimestamp();
                int read = await reader.ReadAtAsync(offset, buffer);
                samples[i] = Stopwatch.GetElapsedTime(start).TotalMicroseconds;
                if (read != readSize || !buffer.AsSpan().SequenceEqual(bytes.AsSpan(offset, readSize)))
                    throw new InvalidDataException("Benchmark range mismatch.");
            }
            long randomReadAllocatedBytes = GC.GetTotalAllocatedBytes(precise: true) - allocationsBefore;
            Array.Sort(samples);
            await reader.ReadAtAsync(bytes.Length - readSize, buffer);
            timer.Restart();
            for (int i = 0; i < 1000; i++)
                await reader.ReadAtAsync(bytes.Length - readSize, buffer);
            double hotUs = timer.Elapsed.TotalMicroseconds / 1000;
            timer.Restart();
            await using (var sequential = (await cas.RetrieveAsync(stored.Key))!)
                await sequential.CopyToAsync(Stream.Null);
            double sequentialMiBs = bytes.Length / 1048576d / timer.Elapsed.TotalSeconds;
            // Baseline cost of reaching the tail of the old single-frame representation.
            byte[] discard = new byte[81920];
            timer.Restart();
            for (int i = 0; i < 5; i++)
            {
                using var input = new DecompressionStream(new MemoryStream(legacy));
                long remaining = bytes.Length - readSize;
                while (remaining > 0)
                {
                    int read = input.Read(discard, 0, (int)Math.Min(discard.Length, remaining));
                    if (read == 0)
                        throw new EndOfStreamException();
                    remaining -= read;
                }
                input.ReadExactly(buffer);
            }
            double legacyTailUs = timer.Elapsed.TotalMicroseconds / 5;
            // Exercise cache eviction rather than only repeated reads within one frame.
            int boundary = Math.Min(frameSize == 0 ? 256 * 1024 : frameSize, bytes.Length / 2);
            async Task<double[]> MeasurePattern(Func<int, int> offsetForRead)
            {
                var timings = new double[200];
                for (int i = 0; i < timings.Length; i++)
                {
                    int offset = Math.Clamp(offsetForRead(i), 0, bytes.Length - readSize);
                    long start = Stopwatch.GetTimestamp();
                    int count = await reader.ReadAtAsync(offset, buffer);
                    timings[i] = Stopwatch.GetElapsedTime(start).TotalMicroseconds;
                    if (count != readSize || !buffer.AsSpan().SequenceEqual(bytes.AsSpan(offset, readSize)))
                        throw new InvalidDataException("Pattern range mismatch.");
                }
                Array.Sort(timings);
                return timings;
            }
            var boundarySamples = await MeasurePattern(_ => boundary - readSize / 2);
            var alternatingSamples = await MeasurePattern(i => i % 2 == 0 ? 0 : bytes.Length - readSize);
            timer.Restart();
            for (int i = 0; i < 100; i++)
            {
                await using var opened = (await cas.OpenRandomAccessAsync(stored.Key))!;
                if (opened.Length != bytes.Length)
                    throw new InvalidDataException("Open length mismatch.");
            }
            double openCloseUs = timer.Elapsed.TotalMicroseconds / 100;
            // Same-reader mixed request sizes include queueing behind larger reads.
            timer.Restart();
            var concurrentLatencies = await Task.WhenAll(Enumerable.Range(0, 32).Select(async i =>
            {
                int size = Math.Min(i % 4 == 0 ? 1024 * 1024 : readSize, bytes.Length);
                byte[] output = new byte[size];
                int offset = (int)(i * 7919L % (bytes.Length - size + 1));
                long start = Stopwatch.GetTimestamp();
                int count = await reader.ReadAtAsync(offset, output);
                double latency = Stopwatch.GetElapsedTime(start).TotalMicroseconds;
                if (count != size || !output.AsSpan().SequenceEqual(bytes.AsSpan(offset, size)))
                    throw new InvalidDataException("Concurrent range mismatch.");
                return latency;
            }));
            double concurrentBatchMs = timer.Elapsed.TotalMilliseconds;
            Array.Sort(concurrentLatencies);
            // Simultaneous independent readers: report allocations and latency separately.
            long openingAllocationStart = GC.GetTotalAllocatedBytes(precise: true);
            var readers = new List<IRandomAccessContent>();
            double independentBatchMs;
            try
            {
                for (int i = 0; i < 32; i++)
                    readers.Add((await cas.OpenRandomAccessAsync(stored.Key))!);
                timer.Restart();
                await Task.WhenAll(readers.Select(async (opened, i) =>
                {
                    byte[] output = new byte[readSize];
                    int offset = (int)(i * 7919L % (bytes.Length - readSize + 1));
                    int count = await opened.ReadAtAsync(offset, output);
                    if (count != readSize || !output.AsSpan().SequenceEqual(bytes.AsSpan(offset, readSize)))
                        throw new InvalidDataException("Independent range mismatch.");
                }));
                independentBatchMs = timer.Elapsed.TotalMilliseconds;
            }
            finally { foreach (var opened in readers) await opened.DisposeAsync(); }
            long independentAllocatedBytes = GC.GetTotalAllocatedBytes(precise: true) - openingAllocationStart;
            results.Add(new
            {
                dataset = name,
                bytes = bytes.Length,
                policy = frameSize == 0 ? "raw" : "seekable",
                frameKiB = frameSize / 1024,
                compressed = stored.IsCompressed,
                storedBytes = stored.CompressedSize,
                legacyCompressedBytes = legacy.Length,
                storeMs,
                random4KiBP50Us = samples[samples.Length / 2],
                random4KiBP95Us = samples[(int)(samples.Length * 0.95)],
                random4KiBP99Us = samples[(int)(samples.Length * 0.99)],
                randomReadAllocatedBytes,
                boundary4KiBP50Us = boundarySamples[100],
                boundary4KiBP95Us = boundarySamples[190],
                alternating4KiBP50Us = alternatingSamples[100],
                alternating4KiBP95Us = alternatingSamples[190],
                openCloseUs,
                concurrentBatchMs,
                concurrentP95Us = concurrentLatencies[30],
                independentBatchMs,
                independentAllocatedBytes,
                hotTail4KiBUs = hotUs,
                legacyTail4KiBUs = legacyTailUs,
                sequentialMiBs
            });
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
long peakWorkingSet = Process.GetCurrentProcess().PeakWorkingSet64;
Console.WriteLine(JsonSerializer.Serialize(new
{
    runtime = RuntimeInformation.FrameworkDescription,
    os = RuntimeInformation.OSDescription,
    architecture = RuntimeInformation.ProcessArchitecture.ToString(),
    peakProcessWorkingSetBytes = peakWorkingSet > 0 ? (long?)peakWorkingSet : null,
    note = "Synthetic inputs unless file arguments supplied. Warm OS cache; one decoded frame per handle; no filesystem mount or network. Legacy tail baseline decodes from memory without SHA-256 verification. Allocations are process-wide deltas; peak working set covers the whole run, including input buffers. Timing is exploratory, not a CI threshold.",
    results
}, new JsonSerializerOptions { WriteIndented = true }));
