using System.Diagnostics;
using System.Globalization;
using System.IO.Hashing;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml;
using System.Xml.Linq;
using Microsoft.Win32.SafeHandles;
using Romd.Infrastructure.Storage;
using Romd.Media.Chd;
using Romd.Media.Processes;
using Romd.Storage;

namespace Romd.Media.Probe;

/// <summary>Read-only DVD candidate measurements, independent of the canonical recipe and publication.</summary>
internal static class ChdRecipeChecks
{
    internal const int WarmupReads = 1000, MeasuredReads = 10000, Trials = 3, RandomRequestBytes = 4096;
    internal const int Seed = 123456;
    internal sealed record Specification(string Name, string SourceRevision, string Hardware, string Artifact,
        string Iso, string Dat, string DatEntryName, Digests ExpectedSource, string NativeDirectory, string ToolDirectory,
        string EncodeReceipt, string EncodeReceiptSha256, ChdHeaderFacts ExpectedHeader, int SequentialRequestBytes);
    internal sealed record EncodeReceipt(string SourcePath, string CandidatePath, string ToolPath, Digests Source,
        long ArtifactBytes, string ArtifactSha256, ChdmanToolIdentity ToolIdentity, string[] Arguments,
        double ElapsedSeconds, DateTimeOffset StartedUtc, DateTimeOffset EndedUtc, int ExitCode);
    internal sealed record Distribution(double P50Milliseconds, double P95Milliseconds, double MinimumMilliseconds,
        double MaximumMilliseconds, double StandardDeviationMilliseconds);
    internal sealed record CounterDelta(long RetainedHits, long FlightJoins, long NewFactories,
        long RawReadCalls, long RawReadBytes);
    internal sealed record TrialResult(int Trial, string Kind, int Requests, long LogicalBytes,
        double Seconds, double DecimalMegabytesPerSecond, Distribution? Latency, CounterDelta Counters,
        MediaBlockCacheSnapshot Cache, ChdReadPoolSnapshot Pool, ResourceSummary Resources,
        MediaBlockCacheSnapshot ClosedCache, ChdReadPoolSnapshot ClosedPool);
    internal sealed record ResourceSummary(int Samples, long PeakSampledWorkingSetBytes, long PeakSampledManagedHeapBytes,
        long ProcessLifetimePeakWorkingSetBytes, long TotalManagedAllocatedBytes, long? CgroupCurrentBytes,
        long? CgroupKernelPeakBytes, string? CgroupMaximum);

    internal static readonly JsonSerializerOptions StrictJson = new()
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
        MaxDepth = 12
    };

    public static async Task RunAsync(string[] args, CancellationToken ct)
    {
        if (args.Length != 3)
            throw new ArgumentException("Usage: --chd-recipe-probe <absolute specification.json> <absolute new report.json>");
        MediaProbePaths.Validate([args[1], args[2]]);
        Specification spec = Parse<Specification>(BoundedRuntimeFile.Read(args[1], 65536));
        Validate(spec);
        string[] protectedPaths = [args[1], spec.Artifact, spec.Iso, spec.Dat, spec.EncodeReceipt];
        MediaProbePaths.Validate([.. protectedPaths, args[2]]);
        Check.That(!protectedPaths.Any(path => MediaProbePaths.Overlaps(path, args[2])) &&
            !MediaProbePaths.Overlaps(spec.NativeDirectory, args[2]) &&
            !MediaProbePaths.Overlaps(spec.ToolDirectory, args[2]) &&
            !MediaProbePaths.Overlaps(Path.GetFullPath(AppContext.BaseDirectory), args[2]),
            "report isolated from input and runtime destinations");
        await using var report = new FileStream(args[2], FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        var rows = new List<object>();
        string phase = "initialization";
        await Save("in-progress", null);
        try
        {
            ct.ThrowIfCancellationRequested();
            var native = ChdNativeCandidate.FromBuildRecord(spec.NativeDirectory);
            var tool = ChdmanCandidate.FromBuildRecord(spec.ToolDirectory);
            MediaProbePaths.Validate([native.BinaryPath, native.ManifestPath, tool.BinaryPath, tool.ManifestPath]);
            ChdmanToolIdentity toolIdentity = tool.Verify(ct);
            ChdNativeIdentity nativeIdentity = native.VerifyAndLoad(ct);
            Check.That(toolIdentity.Rid == nativeIdentity.Rid, "native/tool RID pairing");
            using var artifact = NativeFileLease.OpenRegularRead(spec.Artifact);
            using var iso = NativeFileLease.OpenRegularRead(spec.Iso);
            long artifactLength = RandomAccess.GetLength(artifact), isoLength = RandomAccess.GetLength(iso);
            Check.That(isoLength == spec.ExpectedSource.Size && artifactLength is > 0 and <= ChdDvdLimits.MaximumArtifactBytes,
                "bounded held source and candidate lengths");
            phase = "independent-input-verification";
            await Save("in-progress", null);
            long started = Stopwatch.GetTimestamp();
            Digests source = await HashAsync(iso, isoLength, ct);
            RequireQuartet(source, spec.ExpectedSource);
            ValidateDat(spec, source);
            Digests candidate = await HashAsync(artifact, artifactLength, ct);
            using var receiptHandle = NativeFileLease.OpenRegularRead(spec.EncodeReceipt);
            byte[] receiptBytes = ReadBounded(receiptHandle, 65536);
            Check.That(Convert.ToHexStringLower(SHA256.HashData(receiptBytes)) == spec.EncodeReceiptSha256,
                "retained encode receipt SHA256");
            EncodeReceipt receipt = Parse<EncodeReceipt>(receiptBytes);
            ValidateReceipt(spec, receipt, candidate, toolIdentity, tool.BinaryPath);
            ChdHeaderFacts header = Header(artifact, artifactLength);
            Check.That(header == spec.ExpectedHeader && header.LogicalBytes == (ulong)source.Size &&
                header.UnitBytes == 2048 && new[] { header.Codec0, header.Codec1, header.Codec2, header.Codec3 }.All(ChdDvdLimits.IsDvdCodec),
                "effective DVD geometry and all four codec slots");
            rows.Add(new
            {
                Phase = phase,
                Source = source,
                Candidate = candidate,
                Header = header,
                EncodeReceiptSha256 = spec.EncodeReceiptSha256,
                Receipt = receipt,
                Seconds = Stopwatch.GetElapsedTime(started).TotalSeconds,
                Tool = toolIdentity,
                Native = nativeIdentity,
                ManagedMediaMvid = typeof(MediaBlockCache).Module.ModuleVersionId,
                ManagedProbeMvid = typeof(ChdRecipeChecks).Module.ModuleVersionId,
                ManagedBindingMvid = typeof(Moonlark.Libchdr.ChdFile).Module.ModuleVersionId,
                Runtime = RuntimeInformation.FrameworkDescription,
                Rid = NativeRuntimeIdentifier.Current,
                Os = RuntimeInformation.OSDescription,
                Architecture = RuntimeInformation.ProcessArchitecture.ToString(),
                AvailableProcessorCount = Environment.ProcessorCount,
                spec.SourceRevision,
                spec.Hardware
            });
            await Save("in-progress", null);
            phase = "separate-full-reconstructed-quartet";
            started = Stopwatch.GetTimestamp();
            var cache = new MediaBlockCache(new ReadDecoderOptions().CacheBytes);
            await using (var pool = NewPool(cache))
            {
                await using var device = await ChdBlockDevice.OpenAsync(new HeldRaw(artifact, artifactLength), pool,
                    ChdDvdLimits.Preflight, ct: ct);
                Digests decoded = await HashDeviceAsync(device, source.Size, ct);
                RequireQuartet(decoded, source);
                rows.Add(new { Phase = phase, Decoded = decoded, Seconds = Stopwatch.GetElapsedTime(started).TotalSeconds });
            }
            await Save("in-progress", null);
            long[] randomOffsets = RandomOffsets(source.Size);
            for (int trial = 1; trial <= Trials; trial++)
            {
                phase = $"random-trial-{trial}";
                await Save("in-progress", null);
                rows.Add(await MeasureAsync(artifact, artifactLength, iso, source.Size,
                    trial, true, randomOffsets, spec.SequentialRequestBytes, ct));
                await Save("in-progress", null);
            }
            var sequential = new List<TrialResult>();
            for (int trial = 1; trial <= Trials; trial++)
            {
                phase = $"sequential-trial-{trial}";
                await Save("in-progress", null);
                TrialResult result = await MeasureAsync(artifact, artifactLength, iso, source.Size,
                    trial, false, randomOffsets, spec.SequentialRequestBytes, ct);
                sequential.Add(result);
                rows.Add(result);
                await Save("in-progress", null);
            }
            phase = "final-input-custody-verification";
            await Save("in-progress", null);
            // Rehash the original held inodes, then the names, after every owned decoder operation drained.
            RequireQuartet(await HashAsync(iso, isoLength, ct), source);
            RequireQuartet(await HashAsync(artifact, artifactLength, ct), candidate);
            using (var named = NativeFileLease.OpenRegularRead(spec.Iso))
                RequireQuartet(await HashAsync(named, RandomAccess.GetLength(named), ct), source);
            using (var named = NativeFileLease.OpenRegularRead(spec.Artifact))
                RequireQuartet(await HashAsync(named, RandomAccess.GetLength(named), ct), candidate);
            Check.That(ReadBounded(receiptHandle, 65536).AsSpan().SequenceEqual(receiptBytes), "held encode receipt unchanged");
            using (var named = NativeFileLease.OpenRegularRead(spec.EncodeReceipt))
                Check.That(ReadBounded(named, 65536).AsSpan().SequenceEqual(receiptBytes), "named encode receipt unchanged");
            Check.That(tool.Verify(ct) == toolIdentity && native.VerifyAndLoad(ct) == nativeIdentity,
                "runtime candidate identities unchanged");
            double median = Quantile(sequential.Select(row => row.Seconds).ToArray(), .5);
            rows.Add(new
            {
                Phase = "summary",
                MedianSequentialSeconds = median,
                MedianSequentialDecimalMegabytesPerSecond = DecimalMegabytesPerSecond(source.Size, median),
                RandomP95VariationMilliseconds = Variation(rows.OfType<TrialResult>().Where(row => row.Kind == "random")
                    .Select(row => row.Latency!.P95Milliseconds).ToArray())
            });
            phase = "complete";
            await Save("passed", null);
        }
        catch (Exception error)
        {
            await Save("failed", error.ToString());
            throw;
        }

        async Task Save(string status, string? error)
        {
            report.Position = 0;
            report.SetLength(0);
            await JsonSerializer.SerializeAsync(report, new
            {
                Status = status,
                Phase = phase,
                Error = error,
                Specification = spec,
                Rows = rows,
                Settings = new
                {
                    Trials,
                    WarmupReads,
                    MeasuredReads,
                    Seed,
                    RandomRequestBytes,
                    spec.SequentialRequestBytes,
                    CacheBytes = new ReadDecoderOptions().CacheBytes,
                    NativeHandleCap = 4,
                    IdleHandleCap = 2,
                    ManagedReadAhead = "adjacent-range one-hunk opportunistic",
                    NativeReadAheadBytes = 0,
                    OsCache = "warm/uncontrolled; no disk-cold claim",
                    Timing = "ReadAtAsync only; oracle, opening, hashing and comparisons excluded. 250ms resource sampler runs concurrently.",
                    Memory = "Retained cache and live/peak rounded rentals are separate from RSS/GC/cgroup. Unused ArrayPool retention is not measured; CHD-only returned arrays have a <32MiB upper bound; generic full pool <1GiB. Native raw maps require 12 bytes/hunk/handle; codec/compressed-map allocations are not individually attributed. Process/cgroup lifetime peaks include prior phases."
                }
            },
                JsonOptions.Value, CancellationToken.None);
            await report.FlushAsync(CancellationToken.None);
        }
    }

    private static byte[] ReadBounded(SafeFileHandle file, int maximum)
    {
        long length = RandomAccess.GetLength(file);
        Check.That(length is > 0 && length <= maximum, "bounded held receipt");
        byte[] bytes = new byte[length];
        ReadOracle(file, 0, bytes);
        Check.That(RandomAccess.GetLength(file) == length, "held receipt length stable");
        return bytes;
    }

    internal static T Parse<T>(byte[] bytes)
    {
        if (bytes.Length is < 1 or > 65536)
            throw new InvalidDataException("Bounded recipe JSON required.");
        using var json = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 12 });
        Check.That(MediaRunRecord.Unique(json.RootElement), "recursive unique recipe JSON properties");
        RejectNull(json.RootElement);
        return JsonSerializer.Deserialize<T>(bytes, StrictJson) ?? throw new InvalidDataException("Missing recipe JSON.");
    }

    private static void RejectNull(JsonElement element)
    {
        Check.That(element.ValueKind != JsonValueKind.Null, "no null recipe JSON fields");
        if (element.ValueKind == JsonValueKind.Object)
            foreach (var property in element.EnumerateObject())
                RejectNull(property.Value);
        if (element.ValueKind == JsonValueKind.Array)
            foreach (var child in element.EnumerateArray())
                RejectNull(child);
    }

    internal static void Validate(Specification spec)
    {
        Check.That(!string.IsNullOrWhiteSpace(spec.Name) && spec.Name.Length <= 256 &&
            !string.IsNullOrWhiteSpace(spec.Hardware) && spec.Hardware.Length <= 2048 && Hash(spec.SourceRevision, 40) &&
            !string.IsNullOrWhiteSpace(spec.DatEntryName) && spec.DatEntryName.Length <= 128 && Hash(spec.EncodeReceiptSha256, 64),
            "complete bounded recipe provenance");
        if (spec.ExpectedSource is null || spec.ExpectedHeader is null)
            throw new InvalidDataException("Required independent facts.");
        ValidateDigests(spec.ExpectedSource);
        Check.That(spec.ExpectedSource.Size is >= RandomRequestBytes and <= ChdDvdLimits.MaximumLogicalBytes &&
            spec.SequentialRequestBytes is >= 4096 and <= (8 << 20) && spec.SequentialRequestBytes % 4096 == 0,
            "bounded exact workload geometry");
        MediaProbePaths.Validate([spec.Artifact, spec.Iso, spec.Dat, spec.EncodeReceipt, spec.NativeDirectory, spec.ToolDirectory]);
    }

    internal static void ValidateReceipt(Specification spec, EncodeReceipt receipt, Digests artifact,
        ChdmanToolIdentity tool, string toolPath)
    {
        Check.That(receipt.Source == spec.ExpectedSource && receipt.SourcePath == spec.Iso &&
            receipt.CandidatePath == spec.Artifact && receipt.ToolPath == toolPath && receipt.ToolIdentity == tool &&
            receipt.ArtifactBytes == artifact.Size && receipt.ArtifactSha256 == artifact.Sha256 && receipt.ExitCode == 0 &&
            double.IsFinite(receipt.ElapsedSeconds) && receipt.ElapsedSeconds > 0 && receipt.ElapsedSeconds <= 86400 &&
            receipt.StartedUtc.Offset == TimeSpan.Zero && receipt.EndedUtc.Offset == TimeSpan.Zero &&
            receipt.StartedUtc != default && receipt.EndedUtc >= receipt.StartedUtc,
            "complete independent successful encode receipt bindings");
        string[] args = receipt.Arguments;
        Check.That(args is { Length: >= 7 and <= 15 } && args[0] == "createdvd" &&
            args.All(value => !string.IsNullOrWhiteSpace(value) && value.Length <= 2048), "bounded structured encoding argv");
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int index = 1; index < args.Length; index += 2)
        {
            Check.That(index + 1 < args.Length && args[index] is "-i" or "-o" or "-np" or "-hs" or "-c" &&
                options.TryAdd(args[index], args[index + 1]), "unique supported createdvd argv");
        }
        Check.That(options.GetValueOrDefault("-i") == spec.Iso && options.GetValueOrDefault("-o") == spec.Artifact &&
            options.GetValueOrDefault("-np") == "1", "receipt records exact source/output and single processor");
        ValidateRequestedGeometry(options, spec.ExpectedHeader);
    }

    private static void ValidateRequestedGeometry(Dictionary<string, string> options, ChdHeaderFacts header)
    {
        if (options.TryGetValue("-hs", out string? hunk))
            Check.That(uint.TryParse(hunk, NumberStyles.None, CultureInfo.InvariantCulture, out uint bytes) &&
                bytes == header.HunkBytes, "explicit encoder hunk size matches effective header");
        if (options.TryGetValue("-c", out string? codecs))
        {
            string[] names = codecs.Split(',');
            Check.That(names.Length is >= 1 and <= 4 && names.All(name => name is "zlib" or "lzma" or "huff" or "flac" or "zstd"),
                "supported explicit DVD codec list");
            uint[] expected = new uint[4];
            for (int slot = 0; slot < names.Length; slot++)
                expected[slot] = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(System.Text.Encoding.ASCII.GetBytes(names[slot]));
            Check.That(expected.SequenceEqual(new[] { header.Codec0, header.Codec1, header.Codec2, header.Codec3 }),
                "explicit encoder codec list matches all four effective header slots");
        }
    }

    internal static void RequireQuartet(Digests actual, Digests expected)
    {
        ValidateDigests(actual);
        ValidateDigests(expected);
        Check.That(actual == expected, "independent length/SHA256/SHA1/MD5/CRC32 match");
    }

    private static void ValidateDigests(Digests value) => Check.That(value.Size is > 0 and <= ChdDvdLimits.MaximumArtifactBytes &&
        Hash(value.Sha256, 64) && Hash(value.Sha1, 40) && Hash(value.Md5, 32) && Hash(value.Crc, 8), "strict complete digest tuple");
    private static bool Hash(string value, int length) => value is not null && value.Length == length &&
        value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static void ValidateDat(Specification spec, Digests source) => ValidateDat(spec.Dat, spec.DatEntryName, source);

    internal static void ValidateDat(string datPath, string entryName, Digests source)
    {
        using var handle = NativeFileLease.OpenRegularRead(datPath);
        using var stream = new FileStream(handle, FileAccess.Read);
        ValidateDat(stream, entryName, source);
    }

    internal static void ValidateDat(byte[] bytes, string entryName, Digests source)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        ValidateDat(stream, entryName, source);
    }

    private static void ValidateDat(Stream stream, string entryName, Digests source)
    {
        Check.That(stream.Length is > 0 and <= (64 << 20), "bounded DAT input");
        using var reader = XmlReader.Create(stream, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Ignore,
            XmlResolver = null,
            MaxCharactersInDocument = 64 << 20
        });
        XElement rom = XDocument.Load(reader).Descendants("game").Single(game => (string?)game.Attribute("name") == entryName)
            .Elements("rom").Single();
        Check.That((string?)rom.Attribute("size") == source.Size.ToString(CultureInfo.InvariantCulture) &&
            (string?)rom.Attribute("sha1") == source.Sha1 && (string?)rom.Attribute("md5") == source.Md5 &&
            (string?)rom.Attribute("crc") == source.Crc, "independent fixture quartet matches selected DAT entry");
    }

    internal static ChdHeaderFacts Header(SafeFileHandle source, long length)
    {
        byte[] header = new byte[124];
        int read = 0;
        while (read < header.Length)
        {
            int count = RandomAccess.Read(source, header.AsSpan(read), read);
            if (count == 0)
                break;
            read += count;
        }
        if (ChdHeaderPreflight.Inspect(header.AsSpan(0, read), length, ChdDvdLimits.Preflight) is not ChdHeaderPreflightResult.Accepted accepted)
            throw new InvalidDataException("Recipe candidate fails bounded CHD preflight.");
        return accepted.Facts;
    }

    internal static long[] RandomOffsets(long logicalBytes)
    {
        Check.That(logicalBytes is >= RandomRequestBytes and <= ChdDvdLimits.MaximumLogicalBytes, "random workload logical bound");
        var random = new Random(Seed);
        long slots = logicalBytes / RandomRequestBytes;
        return Enumerable.Range(0, WarmupReads + MeasuredReads)
            .Select(_ => checked(random.NextInt64(slots) * RandomRequestBytes)).ToArray();
    }

    internal static double Quantile(double[] samples, double quantile)
    {
        Check.That(samples.Length > 0 && quantile is > 0 and <= 1 && samples.All(value => double.IsFinite(value) && value >= 0),
            "finite timing distribution");
        double[] ordered = (double[])samples.Clone();
        Array.Sort(ordered);
        return ordered[(int)Math.Ceiling(ordered.Length * quantile) - 1];
    }
    internal static double DecimalMegabytesPerSecond(long bytes, double seconds)
    {
        Check.That(bytes >= 0 && double.IsFinite(seconds) && seconds > 0, "finite elapsed throughput");
        return bytes / seconds / 1_000_000d;
    }
    private static object Variation(double[] values) => new
    {
        Minimum = values.Min(),
        Maximum = values.Max(),
        Range = values.Max() - values.Min(),
        StandardDeviation = StandardDeviation(values)
    };
    private static double StandardDeviation(double[] values)
    { double mean = values.Average(); return Math.Sqrt(values.Average(value => (value - mean) * (value - mean))); }
    private static ChdReadPool NewPool(MediaBlockCache cache)
    { var options = new ReadDecoderOptions(); return new(cache, options.MaxConcurrent, options.MaxIdle); }

    private static async Task<TrialResult> MeasureAsync(SafeFileHandle artifact, long artifactLength, SafeFileHandle iso,
        long logicalLength, int trial, bool random, long[] offsets, int sequentialRequestBytes, CancellationToken ct)
    {
        var cache = new MediaBlockCache(new ReadDecoderOptions().CacheBytes);
        var pool = NewPool(cache);
        var raw = new HeldRaw(artifact, artifactLength);
        var resources = new ResourceSampler();
        await using (pool)
        {
            await using (var device = await ChdBlockDevice.OpenAsync(raw, pool, ChdDvdLimits.Preflight, ct: ct))
            {
                using var sampling = new CancellationTokenSource();
                Task sampler = resources.SampleAsync(sampling.Token);
                try
                {
                    byte[] buffer = new byte[random ? RandomRequestBytes : sequentialRequestBytes];
                    byte[] oracle = new byte[buffer.Length];
                    if (random)
                        for (int index = 0; index < WarmupReads; index++)
                            await CompareRead(device, iso, offsets[index], buffer, oracle, ct);
                    if (random)
                        Check.That(await device.ReadAtAsync(logicalLength, Memory<byte>.Empty, ct) == 0,
                            "warm-up work drains before counter baseline");
                    var beforeCache = cache.Snapshot;
                    long beforeCalls = raw.Calls, beforeBytes = raw.Bytes;
                    var times = random ? new double[MeasuredReads] : null;
                    long bytes = 0;
                    double seconds = 0;
                    int requests = 0;
                    if (random)
                    {
                        for (int index = 0; index < MeasuredReads; index++)
                        {
                            ct.ThrowIfCancellationRequested();
                            long offset = offsets[WarmupReads + index];
                            ReadOracle(iso, offset, oracle);
                            long started = Stopwatch.GetTimestamp();
                            int read = await device.ReadAtAsync(offset, buffer, ct);
                            double elapsed = Stopwatch.GetElapsedTime(started).TotalSeconds;
                            Check.That(read == buffer.Length && buffer.AsSpan().SequenceEqual(oracle), "random logical bytes equal independent ISO");
                            times![index] = elapsed * 1000;
                            seconds += elapsed;
                            bytes += read;
                            requests++;
                        }
                    }
                    else
                    {
                        // One contiguous timer covers only the production read loop; no oracle/hash calls.
                        long started = Stopwatch.GetTimestamp();
                        while (bytes < logicalLength)
                        {
                            int wanted = (int)Math.Min(buffer.Length, logicalLength - bytes);
                            int read = await device.ReadAtAsync(bytes, buffer.AsMemory(0, wanted), ct);
                            Check.That(read == wanted, "full sequential logical read including short final request");
                            bytes += read;
                            requests++;
                        }
                        seconds = Stopwatch.GetElapsedTime(started).TotalSeconds;
                        Check.That(await device.ReadAtAsync(logicalLength, buffer.AsMemory(0, 1), ct) == 0, "sequential exact EOF");
                    }
                    if (random)
                        Check.That(await device.ReadAtAsync(logicalLength, Memory<byte>.Empty, ct) == 0,
                            "measured random work drains before counters");
                    var afterCache = cache.Snapshot;
                    var afterPool = pool.Snapshot;
                    var counters = new CounterDelta(afterCache.RetainedHits - beforeCache.RetainedHits,
                        afterCache.FlightJoins - beforeCache.FlightJoins, afterCache.NewFactories - beforeCache.NewFactories,
                        raw.Calls - beforeCalls, raw.Bytes - beforeBytes);
                    Distribution? distribution = times is null ? null : new(Quantile(times, .5), Quantile(times, .95),
                        times.Min(), times.Max(), StandardDeviation(times));
                    await device.DisposeAsync();
                    await pool.DisposeAsync();
                    Check.That(pool.Snapshot is { LiveHandles: 0, ActiveHandles: 0, Flights: 0, Speculations: 0, Artifacts: 0 } &&
                        cache.Snapshot is { Flights: 0, RetainedBytes: 0, LiveRentedBytes: 0 }, "trial drains owned handles/flights/rentals");
                    sampling.Cancel();
                    await sampler;
                    return new(trial, random ? "random" : "sequential", requests, bytes, seconds,
                        DecimalMegabytesPerSecond(bytes, seconds), distribution, counters, afterCache, afterPool,
                        resources.Summary(), cache.Snapshot, pool.Snapshot);
                }
                finally { sampling.Cancel(); await sampler; }
            }
        }
    }

    private static async Task CompareRead(IRandomAccessContent device, SafeFileHandle iso, long offset,
        byte[] actual, byte[] oracle, CancellationToken ct)
    {
        ReadOracle(iso, offset, oracle);
        Check.That(await device.ReadAtAsync(offset, actual, ct) == actual.Length && actual.AsSpan().SequenceEqual(oracle),
            "warm-up logical bytes equal independent ISO");
    }
    private static void ReadOracle(SafeFileHandle iso, long offset, Span<byte> buffer)
    {
        int read = 0;
        while (read < buffer.Length)
        {
            int count = RandomAccess.Read(iso, buffer[read..], offset + read);
            Check.That(count > 0, "independent ISO range complete");
            read += count;
        }
    }

    private static async Task<Digests> HashAsync(SafeFileHandle file, long size, CancellationToken ct)
    {
        Check.That(size is > 0 and <= ChdDvdLimits.MaximumArtifactBytes && RandomAccess.GetLength(file) == size,
            "bounded held digest input");
        using var sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var sha1 = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        var crc = new Crc32();
        byte[] buffer = new byte[1 << 20];
        long position = 0;
        while (position < size)
        {
            int read = await RandomAccess.ReadAsync(file, buffer.AsMemory(0, (int)Math.Min(buffer.Length, size - position)), position, ct);
            Check.That(read > 0, "held digest input not truncated");
            sha256.AppendData(buffer, 0, read);
            sha1.AppendData(buffer, 0, read);
            md5.AppendData(buffer, 0, read);
            crc.Append(buffer.AsSpan(0, read));
            position += read;
        }
        Check.That(RandomAccess.GetLength(file) == size && RandomAccess.Read(file, buffer.AsSpan(0, 1), size) == 0,
            "held digest input length unchanged");
        return new(size, crc.GetCurrentHashAsUInt32().ToString("x8", CultureInfo.InvariantCulture),
            Convert.ToHexStringLower(md5.GetHashAndReset()), Convert.ToHexStringLower(sha1.GetHashAndReset()),
            Convert.ToHexStringLower(sha256.GetHashAndReset()));
    }
    private static async Task<Digests> HashDeviceAsync(IRandomAccessContent device, long size, CancellationToken ct)
    {
        await using var stream = device.AsStream(leaveOpen: true);
        return await Digests.StreamAsync(stream, ct, size);
    }

    private sealed class HeldRaw(SafeFileHandle file, long length) : ISynchronousRawContent
    {
        private long _calls, _bytes;
        private bool _disposed;
        public long Length => length;
        internal long Calls => Interlocked.Read(ref _calls);
        internal long Bytes => Interlocked.Read(ref _bytes);
        public int ReadAt(long offset, Span<byte> destination)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            int read = RandomAccess.Read(file, destination, offset);
            Interlocked.Increment(ref _calls);
            Interlocked.Add(ref _bytes, read);
            return read;
        }
        // The campaign owner retains the readonly descriptor until every trial has drained.
        public void Dispose() => _disposed = true;
    }

    private sealed class ResourceSampler
    {
        private int _samples;
        private long _rss, _heap;
        internal async Task SampleAsync(CancellationToken ct)
        {
            try
            {
                while (true)
                {
                    Sample();
                    await Task.Delay(250, ct);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        }
        private void Sample()
        {
            using var process = Process.GetCurrentProcess();
            process.Refresh();
            Interlocked.Increment(ref _samples);
            _rss = Math.Max(_rss, process.WorkingSet64);
            _heap = Math.Max(_heap, GC.GetTotalMemory(false));
        }
        internal ResourceSummary Summary()
        {
            Sample();
            using var process = Process.GetCurrentProcess();
            process.Refresh();
            long? current = null, peak = null;
            string? maximum = null;
            if (OperatingSystem.IsLinux() && File.Exists("/sys/fs/cgroup/memory.current"))
            {
                current = ReadNumber("memory.current");
                peak = ReadNumber("memory.peak");
                maximum = System.Text.Encoding.ASCII.GetString(ReadResourceChecks.ReadBounded("/sys/fs/cgroup/memory.max", 64)).Trim();
            }
            return new(_samples, _rss, _heap, process.PeakWorkingSet64, GC.GetTotalAllocatedBytes(), current, peak, maximum);
        }
        private static long ReadNumber(string name) => long.Parse(System.Text.Encoding.ASCII.GetString(
            ReadResourceChecks.ReadBounded("/sys/fs/cgroup/" + name, 64)).Trim(), CultureInfo.InvariantCulture);
    }
}
