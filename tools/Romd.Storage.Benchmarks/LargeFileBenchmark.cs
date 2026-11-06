using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Romd.Storage;
using ZstdSharp;

internal static class LargeFileBenchmark
{
    internal static async Task RunAsync(string[] arguments)
    {
        var (trace, paths) = ParseTrace(arguments);
        if (paths.Length == 0)
            throw new ArgumentException("Provide input files after --streaming.");
        long traceEnd = trace.Count == 0 ? 0 : trace.Max(read => read.Offset + read.Length);
        var decoded = new StrongBox<long>();
        var storedRead = new StrongBox<long>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, meterListener) =>
            {
                if (instrument.Meter.Name == ContentStoreDiagnostics.MeterName &&
                    instrument.Name is "cas.read.decoded" or "cas.read.stored")
                    meterListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, _, _) =>
            Interlocked.Add(ref (instrument.Name == "cas.read.decoded" ? decoded : storedRead).Value, value));
        listener.Start();
        var results = new List<object>();
        for (int inputIndex = 0; inputIndex < paths.Length; inputIndex++)
        {
            string name = $"input-{inputIndex + 1}";
            string path = paths[inputIndex];
            var originalInfo = new FileInfo(path);
            long size = originalInfo.Length;
            var lastWrite = originalInfo.LastWriteTimeUtc;
            if (size < 4096)
                throw new ArgumentException("Inputs must be at least 4 KiB.");
            string temporary = Path.Combine(Path.GetTempPath(), "romd-cas-large", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temporary);
            try
            {
                Console.Error.WriteLine($"{name}: measuring continuous compression ({size:N0} bytes)");
                string baselinePath = Path.Combine(temporary, "baseline.zst");
                byte[] transfer = new byte[1024 * 1024];
                byte[] expectedHash;
                var timer = Stopwatch.StartNew();
                using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
                {
                    await using var source = OpenSource(path);
                    await using var destination = File.Create(baselinePath);
                    await using (var compressor = new CompressionStream(destination, 3, leaveOpen: true))
                    {
                        int read;
                        while ((read = await source.ReadAsync(transfer)) > 0)
                        {
                            hash.AppendData(transfer.AsSpan(0, read));
                            await compressor.WriteAsync(transfer.AsMemory(0, read));
                        }
                    }
                    destination.Flush(flushToDisk: true);
                    expectedHash = hash.GetHashAndReset();
                }
                double legacyCompressMs = timer.Elapsed.TotalMilliseconds;
                long legacyBytes = new FileInfo(baselinePath).Length;
                await using (var source = File.OpenRead(baselinePath))
                await using (var decoder = new DecompressionStream(source))
                {
                    if (!(await SHA256.HashDataAsync(decoder)).AsSpan().SequenceEqual(expectedHash))
                        throw new InvalidDataException("Continuous compression round-trip mismatch.");
                }
                File.Delete(baselinePath);

                foreach (int frameSize in new[] { 0, 64 * 1024, 256 * 1024, 1024 * 1024 })
                {
                    Console.Error.WriteLine($"{name}: {(frameSize == 0 ? "raw" : $"{frameSize / 1024} KiB frames")}");
                    string root = Path.Combine(temporary, "cas");
                    try
                    {
                        var services = new ServiceCollection();
                        services.AddContentAddressableStorage(options =>
                        {
                            options.RootPath = root;
                            options.WritePolicy = frameSize == 0 ? ContentWritePolicy.Raw : ContentWritePolicy.SeekableZstandard;
                            options.SeekableFrameSize = frameSize == 0 ? 256 * 1024 : frameSize;
                            options.MaxContentSize = Math.Max(options.MaxContentSize, size);
                        });
                        await using var provider = services.BuildServiceProvider();
                        var cas = provider.GetRequiredService<IContentAddressableStore>();
                        StoreResult stored;
                        timer.Restart();
                        await using (var source = OpenSource(path))
                            stored = await cas.StoreAsync(source);
                        double storeMs = timer.Elapsed.TotalMilliseconds;
                        if (stored.Key.ToString() != Convert.ToHexString(expectedHash).ToLowerInvariant())
                            throw new InvalidDataException("CAS identity mismatch.");
                        timer.Restart();
                        await using (var sequential = (await cas.RetrieveAsync(stored.Key))!)
                            await sequential.CopyToAsync(Stream.Null);
                        double sequentialMiBs = size / 1048576d / timer.Elapsed.TotalSeconds;
                        timer.Restart();
                        await using var reader = (await cas.OpenRandomAccessAsync(stored.Key))!;
                        double openMs = timer.Elapsed.TotalMilliseconds;
                        if (reader.Length != size)
                            throw new InvalidDataException("Logical size mismatch.");
                        using var original = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                            FileOptions.Asynchronous | FileOptions.RandomAccess);
                        byte[] expected = new byte[4096], actual = new byte[4096];
                        var random = new Random(42);
                        async Task<double> ReadAndCheck(long offset)
                        {
                            long started = Stopwatch.GetTimestamp();
                            int count = await reader.ReadAtAsync(offset, actual);
                            double microseconds = Stopwatch.GetElapsedTime(started).TotalMicroseconds;
                            int filled = 0;
                            while (filled < expected.Length)
                            {
                                int read = await RandomAccess.ReadAsync(original, expected.AsMemory(filled), offset + filled);
                                if (read == 0)
                                    throw new EndOfStreamException();
                                filled += read;
                            }
                            if (count != actual.Length || !actual.AsSpan().SequenceEqual(expected))
                                throw new InvalidDataException("Positional read mismatch.");
                            return microseconds;
                        }
                        for (int i = 0; i < 50; i++)
                            await ReadAndCheck(random.NextInt64(size - 4096 + 1));
                        var samples = new double[500];
                        for (int i = 0; i < samples.Length; i++)
                            samples[i] = await ReadAndCheck(random.NextInt64(size - 4096 + 1));
                        Array.Sort(samples);
                        var boundarySamples = new double[200];
                        var alternatingSamples = new double[200];
                        long boundary = Math.Min(frameSize == 0 ? 256 * 1024 : frameSize, size / 2);
                        for (int i = 0; i < 200; i++)
                            boundarySamples[i] = await ReadAndCheck(boundary - 2048);
                        for (int i = 0; i < 200; i++)
                            alternatingSamples[i] = await ReadAndCheck(i % 2 == 0 ? 0 : size - 4096);
                        Array.Sort(boundarySamples);
                        Array.Sort(alternatingSamples);

                        // /vfs opens a new reader for every HTTP request: time open + read + dispose.
                        async Task<(double Microseconds, long Decoded, long Stored)> PerRequestRead(long offset, int length)
                        {
                            byte[] buffer = new byte[length], expectedBytes = new byte[length];
                            long decodedBefore = Interlocked.Read(ref decoded.Value);
                            long storedBefore = Interlocked.Read(ref storedRead.Value);
                            long started = Stopwatch.GetTimestamp();
                            await using (var request = (await cas.OpenRandomAccessAsync(stored.Key))!)
                                await request.ReadExactlyAtAsync(offset, buffer);
                            double microseconds = Stopwatch.GetElapsedTime(started).TotalMicroseconds;
                            int filled = 0;
                            while (filled < length)
                            {
                                int read = await RandomAccess.ReadAsync(original, expectedBytes.AsMemory(filled), offset + filled);
                                if (read == 0)
                                    throw new EndOfStreamException();
                                filled += read;
                            }
                            if (!buffer.AsSpan().SequenceEqual(expectedBytes))
                                throw new InvalidDataException("Per-request read mismatch.");
                            return (microseconds, Interlocked.Read(ref decoded.Value) - decodedBefore,
                                Interlocked.Read(ref storedRead.Value) - storedBefore);
                        }
                        async Task<object?> Pattern(IReadOnlyList<(long Offset, int Length)> reads)
                        {
                            if (reads.Count == 0)
                                return null;
                            var timings = new double[reads.Count];
                            long requested = 0, decodedTotal = 0, storedTotal = 0;
                            for (int i = 0; i < reads.Count; i++)
                            {
                                var (microseconds, decodedBytes, storedBytes) = await PerRequestRead(reads[i].Offset, reads[i].Length);
                                timings[i] = microseconds;
                                requested += reads[i].Length;
                                decodedTotal += decodedBytes;
                                storedTotal += storedBytes;
                            }
                            double totalMs = timings.Sum() / 1000;
                            Array.Sort(timings);
                            return new
                            {
                                reads = reads.Count,
                                requestedBytes = requested,
                                totalMs,
                                p50Us = timings[timings.Length / 2],
                                p95Us = timings[(int)(timings.Length * 0.95)],
                                decodedBytes = decodedTotal,
                                storedBytesRead = storedTotal,
                                decodedPerRequested = requested == 0 ? 0 : (double)decodedTotal / requested
                            };
                        }
                        List<(long Offset, int Length)> RandomReads(int count, int length) => size < length ? [] :
                            Enumerable.Range(0, count).Select(_ => (random.NextInt64(size - length + 1), length)).ToList();
                        object? perRequest4KiB = await Pattern(RandomReads(200, 4096));
                        object? dolphinTrace = traceEnd <= size ? await Pattern(trace) : null;
                        object? range1MiB = await Pattern(RandomReads(100, 1024 * 1024));
                        object? range4MiB = await Pattern(RandomReads(50, 4 * 1024 * 1024));
                        results.Add(new
                        {
                            dataset = name,
                            bytes = size,
                            policy = frameSize == 0 ? "raw" : "seekable",
                            frameKiB = frameSize / 1024,
                            compressed = stored.IsCompressed,
                            storedBytes = stored.StoredSize,
                            legacyCompressedBytes = legacyBytes,
                            legacyCompressMs,
                            storeMs,
                            sequentialMiBs,
                            openMs,
                            random4KiBP50Us = samples[250],
                            random4KiBP95Us = samples[475],
                            random4KiBP99Us = samples[495],
                            boundary4KiBP95Us = boundarySamples[190],
                            alternating4KiBP95Us = alternatingSamples[190],
                            frames = frameSize == 0 ? 0 : (size + frameSize - 1) / frameSize,
                            perRequest4KiB,
                            dolphinTrace,
                            range1MiB,
                            range4MiB
                        });
                    }
                    finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
                }
                originalInfo.Refresh();
                if (originalInfo.Length != size || originalInfo.LastWriteTimeUtc != lastWrite)
                    throw new InvalidDataException("Source metadata changed during the benchmark.");
            }
            finally { Directory.Delete(temporary, recursive: true); }
        }
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            runtime = RuntimeInformation.FrameworkDescription,
            os = RuntimeInformation.OSDescription,
            architecture = RuntimeInformation.ProcessArchitecture.ToString(),
            note = "Streaming inputs; originals opened read-only. Level 3, production 5% savings threshold. " +
                "Sequential verified retrieval precedes samples; OS cache is not controlled. Original-file verification is outside read timings. " +
                "Fixed raw/ascending-frame ordering; no VFS, network or emulator traces. All sampled ranges and sequential hashes checked. " +
                "Continuous size/round-trip baseline is streamed to disk; compression time is not directly comparable to CAS multi-pass storage. " +
                "Per-request patterns open, read and dispose a reader per range, as the /vfs endpoint does per HTTP request; " +
                "the optional trace replays recorded emulator (offset, length) reads.",
            results
        }, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static (IReadOnlyList<(long Offset, int Length)> Trace, string[] Paths) ParseTrace(string[] arguments)
    {
        int index = Array.IndexOf(arguments, "--trace");
        if (index < 0)
            return ([], arguments);
        if (index + 1 >= arguments.Length)
            throw new ArgumentException("--trace requires a JSON file containing dvdReads.");
        using var document = JsonDocument.Parse(File.ReadAllBytes(arguments[index + 1]));
        var reads = FindArray(document.RootElement, "dvdReads") ?? throw new ArgumentException("Trace has no dvdReads array.");
        var trace = reads.EnumerateArray()
            .Select(read => (read.GetProperty("Offset").GetInt64(), read.GetProperty("Length").GetInt32())).ToList();
        return (trace, [.. arguments[..index], .. arguments[(index + 2)..]]);
    }

    private static JsonElement? FindArray(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return null;
        foreach (var property in element.EnumerateObject())
        {
            if (property.Name == name && property.Value.ValueKind == JsonValueKind.Array)
                return property.Value;
            if (FindArray(property.Value, name) is { } nested)
                return nested;
        }
        return null;
    }

    private static FileStream OpenSource(string path) => new(path, FileMode.Open, FileAccess.Read, FileShare.Read,
        1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
}
