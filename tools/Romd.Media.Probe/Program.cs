using Romd.Infrastructure.Hashing;
using Romd.Infrastructure.Storage;
using System.Diagnostics;
using System.Buffers.Binary;
using System.IO.Hashing;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.Extensions.DependencyInjection;
using Romd.Media.Probe;
using Romd.Storage;
using System.IO.Abstractions;
using NSubstitute;
using Romd.Application.Common.Configuration;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using OpenIddict.EntityFrameworkCore;
using Romd.Persistence;
using Romd.Persistence.Search;

if (args.Length > 0 && args[0] is "--chd-cd-roundtrip-probe" or "--chd-cd-qualification-probe" or "--chd-cd-recipe-probe" or "--ps1-cd-identification-probe" or "--chd-cd-cue-regeneration-probe" or "--chd-cd-vfs-load-probe" or "--chd-cd-interactive-read-probe")
{
    using var cancellation = new CancellationTokenSource();
    ConsoleCancelEventHandler cancel = (_, signal) => { signal.Cancel = true; cancellation.Cancel(); };
    Console.CancelKeyPress += cancel;
    using var terminate = OperatingSystem.IsLinux() || OperatingSystem.IsMacOS()
        ? PosixSignalRegistration.Create(PosixSignal.SIGTERM, signal => { signal.Cancel = true; cancellation.Cancel(); }) : null;
    try
    {
        if (args[0] == "--chd-cd-interactive-read-probe")
            await ChdCdInteractiveReadChecks.RunAsync(args, cancellation.Token);
        else if (args[0] == "--chd-cd-vfs-load-probe")
            await ChdCdVfsReadChecks.RunAsync(args, cancellation.Token);
        else if (args[0] == "--chd-cd-cue-regeneration-probe")
            await ChdCdCueRegenerationChecks.RunAsync(args, cancellation.Token);
        else if (args[0] == "--ps1-cd-identification-probe")
            await ChdCdBootChecks.RunAsync(args, cancellation.Token);
        else if (args[0] == "--chd-cd-recipe-probe")
            await ChdCdRecipeChecks.RunAsync(args, cancellation.Token);
        else if (args[0] == "--chd-cd-qualification-probe")
            await ChdCdQualificationChecks.RunAsync(args, ChdCdQualificationChecks.Retention.MeasurementsOnly, [], cancellation.Token);
        else
            await ChdCdRoundTripChecks.RunAsync(args, cancellation.Token);
    }
    catch (Exception error) { Console.Error.WriteLine(error); Environment.ExitCode = 1; }
    finally { Console.CancelKeyPress -= cancel; }
    return;
}

if (args.Length > 0 && args[0] is "--convergence-probe" or "--convergence-load-probe")
{
    try
    {
        if (args[0] == "--convergence-load-probe")
        {
            if (args.Length is not (4 or 5) || !Path.IsPathFullyQualified(args[3]) ||
                args.Length == 5 && !int.TryParse(args[4], out _))
                throw new ArgumentException("Usage: --convergence-load-probe <absolute fixtures.json> <absolute new convergence.json> <absolute new load.json> [seconds>=1800]");
            int seconds = args.Length == 5 ? int.Parse(args[4], System.Globalization.CultureInfo.InvariantCulture) : 1800;
            if (seconds is < 1800 or > 86400)
                throw new ArgumentOutOfRangeException(nameof(seconds));
            var duration = TimeSpan.FromSeconds(seconds);
            await ConvergenceChecks.RunAsync(["--convergence-probe", args[1], args[2]], CancellationToken.None,
                async (cas, catalog, library, stagingScope, ct) =>
                {
                    await ConvergedReadChecks.RunAsync(cas, catalog, library, RomMediaPolicyComposition.Create(), ProbeMediaFacts.ManagedAdmission, ConvergedReadDecoder.Managed(), ConvergedReadProfile.NintendoWholeFiles, duration, args[3], ct, stagingScope);
                    return new(duration, args[3]);
                });
        }
        else
            await ConvergenceChecks.RunAsync(args, CancellationToken.None);
    }
    catch (Exception error)
    {
        Console.Error.WriteLine(error);
        Environment.ExitCode = 1;
    }
    return;
}

if (args.Length > 0 && args[0] == "--chd-recipe-probe")
{
    using var cancellation = new CancellationTokenSource();
    ConsoleCancelEventHandler cancel = (_, signal) => { signal.Cancel = true; cancellation.Cancel(); };
    Console.CancelKeyPress += cancel;
    using var terminate = OperatingSystem.IsLinux() || OperatingSystem.IsMacOS()
        ? PosixSignalRegistration.Create(PosixSignal.SIGTERM, signal => { signal.Cancel = true; cancellation.Cancel(); })
        : null;
    try
    { await ChdRecipeChecks.RunAsync(args, cancellation.Token); }
    catch (Exception error)
    {
        Console.Error.WriteLine(error);
        Environment.ExitCode = 1;
    }
    finally { Console.CancelKeyPress -= cancel; }
    return;
}

if (args.Length > 0 && args[0] == "--chdman-candidate-probe")
{
    try
    { await ChdmanCandidateChecks.RunAsync(args, CancellationToken.None); }
    catch (Exception error)
    {
        Console.Error.WriteLine(error);
        Environment.ExitCode = 1;
    }
    return;
}

if (args.Length > 0 && args[0] == "--chd-encoding-probe")
{
    try
    { await ChdEncodingChecks.RunAsync(args, CancellationToken.None); }
    catch (Exception error)
    {
        Console.Error.WriteLine(error);
        Environment.ExitCode = 1;
    }
    return;
}

if (args.Length > 0 && args[0] == "--chd-cd-inspection-probe")
{
    using var cancellation = new CancellationTokenSource();
    ConsoleCancelEventHandler cancel = (_, signal) => { signal.Cancel = true; cancellation.Cancel(); };
    Console.CancelKeyPress += cancel;
    using var terminate = OperatingSystem.IsLinux() || OperatingSystem.IsMacOS()
        ? PosixSignalRegistration.Create(PosixSignal.SIGTERM, signal => { signal.Cancel = true; cancellation.Cancel(); })
        : null;
    try
    { await ChdCdInspectionChecks.RunAsync(args, cancellation.Token); }
    catch (Exception error)
    {
        Console.Error.WriteLine(error);
        Environment.ExitCode = 1;
    }
    finally { Console.CancelKeyPress -= cancel; }
    return;
}

if (args.Length > 0 && args[0] == "--chd-inspection-probe")
{
    try
    { await ChdInspectionChecks.RunAsync(args, CancellationToken.None); }
    catch (Exception error)
    {
        Console.Error.WriteLine(error);
        Environment.ExitCode = 1;
    }
    return;
}

if (args.Length > 0 && args[0] is "--inspection-probe" or "--iso-inspection-probe")
{
    await InspectionChecks.RunAsync(args, CancellationToken.None);
    return;
}

if (args.Length > 0 && args[0] == "--encoding-probe")
{
    try
    { await EncodingChecks.RunAsync(args, CancellationToken.None); }
    catch (Exception error)
    {
        Console.Error.WriteLine(error);
        Environment.ExitCode = 1;
    }
    return;
}

if (args.Length > 0 && args[0] == "--supervisor-probe")
{
    try
    { await SupervisorChecks.RunAsync(args, CancellationToken.None); }
    catch (Exception error)
    {
        Console.Error.WriteLine(error);
        Environment.ExitCode = 1;
    }
    return;
}

if (args.Length > 0 && args[0] == "--custody-probe")
{
    try
    { await CustodyChecks.RunAsync(args, CancellationToken.None); }
    catch (Exception error)
    {
        Console.Error.WriteLine(error);
        Environment.ExitCode = 1;
    }
    return;
}

if (args.Length > 0 && args[0] == "--convergence-recovery-probe")
{
    try
    {
        var configuration = ConvergenceRecoveryChecks.ValidateArguments(args);
        string connection = Environment.GetEnvironmentVariable("ROMD_CONVERGENCE_PROBE_CONNECTION")
            ?? throw new InvalidOperationException("Missing disposable recovery database connection.");
        if (new NpgsqlConnectionStringBuilder(connection).Database != configuration.DatabaseName)
            throw new InvalidDataException("Recovery database does not match its disposable configuration.");
        var builder = new DbContextOptionsBuilder<RomdDbContext>();
        PostgreSqlConfiguration.Configure(builder, connection);
        builder.UseOpenIddict();
        builder.AddInterceptors(new SearchDocumentInterceptor());
        if (configuration.Operation == "seed")
        {
            await using var database = new RomdDbContext(builder.Options);
            await database.Database.MigrateAsync(CancellationToken.None);
        }
        await ConvergenceRecoveryChecks.RunAsync(args, builder.Options, CancellationToken.None);
    }
    catch (Exception error)
    {
        Console.Error.WriteLine(error);
        Environment.ExitCode = 1;
    }
    return;
}

if (args.Length > 0 && args[0] == "--recipe-probe")
{
    await RecipeChecks.RunAsync(args, CancellationToken.None);
    return;
}

if (args.Length > 0 && args[0].StartsWith("--extraction-", StringComparison.Ordinal))
{
    await ExtractionChecks.RunAsync(args);
    return;
}

if (args.Length > 0 && args[0].StartsWith("--staging-", StringComparison.Ordinal))
{
    await StagingChecks.RunAsync(args);
    return;
}

if (args.Length != 2)
    throw new ArgumentException("Usage: probe <fixtures.json> <results.json>");
var mediaOptions = new MediaProcessingOptions();
string root = Path.Combine(Path.GetTempPath(), "romd-media-probe-" + Guid.NewGuid().ToString("N"));
var stagingOptions = Substitute.For<IRomdOptions>();
stagingOptions.DataDirectory.Returns(root);
stagingOptions.MaxUploadBytes.Returns(64L << 30);
var mediaRuntime = new MediaProcessingRuntime(stagingOptions, mediaOptions);
var mediaWorker = new MediaWorkerSupervisor(mediaOptions, mediaRuntime);
Check.That((await mediaWorker.HandshakeAsync(CancellationToken.None)).IsSuccess, "dedicated media child startup");
using var verifier = new RvzVerifier(mediaOptions, mediaWorker, mediaRuntime);
var fixtures = JsonSerializer.Deserialize<Fixture[]>(File.ReadAllText(args[0]))!;
var reports = new List<object>();
string resultsPath = args[1];
File.WriteAllText(resultsPath, JsonSerializer.Serialize(new { Status = "in-progress", Reports = reports }, JsonOptions.Value));
try
{
    var storageBuilder = Host.CreateApplicationBuilder();
    storageBuilder.Services.AddContentAddressableStorage(o => { o.RootPath = root; o.WritePolicy = ContentWritePolicy.Raw; });
    using var storageHost = storageBuilder.Build();
    var cas = storageHost.Services.GetRequiredService<IContentAddressableStore>();
    var staging = new LocalTempFileFactory(stagingOptions, new FileSystem());
    foreach (var fixture in fixtures)
    {
        Console.WriteLine($"{fixture.Name}: hashing independent ISO oracle");
        var oracle = await Digests.FileAsync(fixture.Iso);
        var dat = XDocument.Load(fixture.Dat);
        var expected = dat.Descendants("game").Single(g => (string?)g.Attribute("id") == fixture.DatId).Element("rom")!;
        Check.That(oracle.Size == (long)expected.Attribute("size")!, "DAT size");
        foreach (var (key, value) in new[] { ("crc", oracle.Crc), ("md5", oracle.Md5), ("sha1", oracle.Sha1) })
            Check.That(value == (string?)expected.Attribute(key), "DAT " + key);
        var artifacts = new List<Artifact>();
        foreach (string path in new[] { fixture.UploadedRvz, fixture.NormalizedRvz })
        {
            Console.WriteLine($"{fixture.Name}: CAS / decoded hashing / positional reads: {Path.GetFileName(path)}");
            var bytes = await Digests.FileAsync(path);
            await using var sourceUpload = File.OpenRead(path);
            await using var upload = await staging.CreateAsync(sourceUpload);
            Check.That(upload.FileSha256.ToString() == bytes.Sha256 && upload.FileSize == bytes.Size,
                "production bounded staging preserves exact upload bytes");
            long verifying = Stopwatch.GetTimestamp();
            var preparation = await ProbeMediaFacts.ManagedPreparation(new HashingService(), verifier, new ProbeMediaFacts.EmptyReferences())
                .PrepareAsync(upload, Path.GetFileName(path), CancellationToken.None);
            Check.That(!preparation.IsError, "supervised upload preparation succeeded");
            await using var prepared = (Romd.Admin.Application.Source.Rom.PreparedRom.WholeFile)preparation.Value;
            double verificationSeconds = Stopwatch.GetElapsedTime(verifying).TotalSeconds;
            Check.That(prepared.ContentSha256.ToString() == oracle.Sha256 && prepared.Hashes.BytesRead == oracle.Size,
                "production upload preparation logical SHA-256/size");
            Check.That(prepared.Hashes.Sha1.ToString() == oracle.Sha1 && prepared.Hashes.Md5.ToString() == oracle.Md5 &&
                prepared.Hashes.Crc32.ToString() == oracle.Crc, "production upload preparation DAT hashes");
            Check.That(ReferenceEquals(upload, prepared.Artifact), "existing RVZ adopted without reencoding");
            Check.That(prepared.Verification.ArtifactSha256.ToString() == bytes.Sha256 &&
                prepared.Verification.ContentSha256.ToString() == oracle.Sha256 &&
                prepared.Verification.Profile == RvzVerifier.Profile &&
                prepared.Verification.DecoderBuild.StartsWith("romd-media/mvid:", StringComparison.Ordinal),
                "verification binds exact artifact/output and the managed decoder build");
            await using var input = prepared.Artifact.OpenRead();
            var stored = await cas.StoreAsync(input);
            Check.That(stored.Key.ToString() == bytes.Sha256, "CAS identity");
            Check.That(!stored.IsCompressed, "Raw storage policy");
            input.Position = 0;
            var duplicate = await cas.StoreAsync(input);
            Check.That(duplicate.Key == stored.Key && duplicate.WasDeduplicated, "CAS deduplication");
            Check.That(await cas.VerifyAsync(stored.Key), "CAS full integrity");
            await using var decoded = await RvzContent.OpenAsync((await cas.OpenRandomAccessAsync(stored.Key))!);
            Check.That(decoded.Length == oracle.Size, "decoded length");
            long started = Stopwatch.GetTimestamp();
            var reconstructed = await Digests.ReaderAsync(decoded);
            double fullSeconds = Stopwatch.GetElapsedTime(started).TotalSeconds;
            Check.That(reconstructed == oracle, "complete decoded byte hashes");
            var ranges = await RangeChecks.Run(cas, stored.Key, fixture.Iso, fixture.Name);
            artifacts.Add(new(path, stored.Key, bytes));
            reports.Add(new
            {
                fixture.Name,
                Path = path,
                CasKey = stored.Key.ToString(),
                StoredBytes = bytes.Size,
                Disc = oracle,
                BoundedStaging = true,
                VerificationSeconds = verificationSeconds,
                FullDecodeSeconds = fullSeconds,
                Ranges = ranges,
                Verification = new
                {
                    prepared.Verification.Id,
                    Claim = Romd.Domain.Source.Rom.RomVerification.Claim,
                    prepared.Verification.Profile,
                    prepared.Verification.DecoderBuild,
                    prepared.Verification.VerifierBuild,
                    prepared.Verification.VerifiedAt,
                    ArtifactSha256 = prepared.Verification.ArtifactSha256.ToString(),
                    ContentSha256 = prepared.Verification.ContentSha256.ToString()
                }
            });
        }
        Check.That(artifacts[0].Key != artifacts[1].Key, "different RVZ encodings have distinct keys");
        Console.WriteLine($"{fixture.Name}: production WebDAV endpoint, ISO and RVZ representations");
        var web = await WebChecks.Run(cas, artifacts[1], artifacts[0], oracle, fixture.Iso);
        reports.Add(new { fixture.Name, WebDav = web });
        Console.WriteLine($"{fixture.Name}: PASS");
        File.WriteAllText(resultsPath, JsonSerializer.Serialize(new { Status = "in-progress", Reports = reports }, JsonOptions.Value));
    }
    File.WriteAllText(resultsPath, JsonSerializer.Serialize(new
    {
        Status = "passed",
        Reports = reports,
        Scope = $"{RuntimeInformation.RuntimeIdentifier}; raw CAS; managed Romd.Media reader and supervised verifier; real WebDAV endpoint in TestServer with fake catalog/authentication; no mount, WAN, gameplay or database migration qualification"
    }, JsonOptions.Value));
}
catch (Exception error)
{
    File.WriteAllText(resultsPath, JsonSerializer.Serialize(new { Status = "failed", Error = error.ToString(), Reports = reports }, JsonOptions.Value));
    throw;
}
finally { if (Directory.Exists(root)) Directory.Delete(root, true); }

internal record Fixture(string Name, string UploadedRvz, string NormalizedRvz, string Iso, string Dat, string DatId,
    string? NintendoKind = null, int? StandardKeyPartitions = null, int? KoreanKeyPartitions = null);
internal record Artifact(string Path, StorageKey Key, Digests Hashes);
internal static class JsonOptions { internal static readonly JsonSerializerOptions Value = new() { WriteIndented = true }; }
internal static class Check
{
    internal static void That(bool condition, string name) { if (!condition) throw new InvalidOperationException("FAILED: " + name); }
    internal static async Task Throws<T>(Func<Task> action) where T : Exception
    {
        try
        { await action(); }
        catch (T) { return; }
        throw new InvalidOperationException("Expected " + typeof(T).Name);
    }
}
internal sealed record Digests(long Size, string Crc, string Md5, string Sha1, string Sha256)
{
    public static async Task<Digests> FileAsync(string path, CancellationToken ct = default)
    {
        await using var stream = File.OpenRead(path);
        return await StreamAsync(stream, ct);
    }
    public static async Task<Digests> ReaderAsync(IRandomAccessContent reader, CancellationToken ct = default)
    {
        using var stream = reader.AsStream(leaveOpen: true);
        return await StreamAsync(stream, ct);
    }
    public static async Task<Digests> StreamAsync(Stream stream, CancellationToken ct = default, long? maximumBytes = null)
    {
        ct.ThrowIfCancellationRequested();
        Check.That(maximumBytes is null or >= 0 and <= (16L << 30), "valid bounded digest limit");
        using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        using var sha1 = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        using var sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var crc = new Crc32();
        long size = 0;
        byte[] buffer = new byte[1024 * 1024];
        int read;
        while ((read = await stream.ReadAsync(buffer.AsMemory(0, maximumBytes is { } cap
                   ? (int)Math.Min(buffer.Length, cap - size + 1) : buffer.Length), ct)) != 0)
        {
            Check.That(maximumBytes is null || read <= maximumBytes - size, "bounded digest input exceeds its exact byte limit");
            size += read;
            crc.Append(buffer.AsSpan(0, read));
            md5.AppendData(buffer, 0, read);
            sha1.AppendData(buffer, 0, read);
            sha256.AppendData(buffer, 0, read);
        }
        return new(size, crc.GetCurrentHashAsUInt32().ToString("x8"),
            Convert.ToHexStringLower(md5.GetHashAndReset()), Convert.ToHexStringLower(sha1.GetHashAndReset()),
            Convert.ToHexStringLower(sha256.GetHashAndReset()));
    }
}
internal static class RangeChecks
{
    internal static async Task<object> Run(IContentAddressableStore cas, StorageKey key, string iso, string name)
    {
        using var oracle = File.OpenHandle(iso);
        long size = new FileInfo(iso).Length;
        var ranges = new List<(long Offset, int Length)> { (0, 0), (0, 32), (size, 1), (size + 1, 4096), (size - 1024, 4096) };
        foreach (long boundary in new long[] { 32768, 65536, 131072, 262144, 2097152, 0x40000, 0x50000, 0xf800000, 0xf820000, size })
            foreach (int delta in new[] { -1, 0, 1 })
                ranges.Add((boundary + delta, 8192));
        var partitionStarts = new List<long>();
        byte[] magic = new byte[4];
        await RandomAccess.ReadAsync(oracle, magic, 0x18);
        if (BinaryPrimitives.ReadUInt32BigEndian(magic) == 0x5d1c9ea3)
        {
            // Read this disc's actual partition boundaries, rather than relying on typical offsets.
            byte[] groups = new byte[32];
            await RandomAccess.ReadAsync(oracle, groups, 0x40000);
            for (int group = 0; group < 4; group++)
            {
                uint count = BinaryPrimitives.ReadUInt32BigEndian(groups.AsSpan(group * 8));
                long table = BinaryPrimitives.ReadUInt32BigEndian(groups.AsSpan(group * 8 + 4)) * 4L;
                Check.That(count <= 64, "bounded Wii partition table");
                for (int i = 0; i < count; i++)
                {
                    byte[] entry = new byte[8];
                    await RandomAccess.ReadAsync(oracle, entry, table + i * 8);
                    long partition = BinaryPrimitives.ReadUInt32BigEndian(entry) * 4L;
                    byte[] data = new byte[4];
                    await RandomAccess.ReadAsync(oracle, data, partition + 0x2b8);
                    long start = partition + BinaryPrimitives.ReadUInt32BigEndian(data) * 4L;
                    partitionStarts.Add(start);
                    foreach (long boundary in new[] { partition, start, start + 32768, start + 2097152 })
                        foreach (int delta in new[] { -1, 0, 1 })
                            ranges.Add((boundary + delta, 8192));
                }
            }
        }
        if (size > 4294967296)
            foreach (int delta in new[] { -1, 0, 1 })
                ranges.Add((4294967296 + delta, 8192));
        var random = new Random(123456);
        for (int i = 0; i < 500; i++)
            ranges.Add((random.NextInt64(size), new[] { 1, 4096, 32768, 131072, 1048576 }[i % 5]));
        var times = new List<double>();
        long maxInput = 0;
        await using var reader = await RvzContent.OpenAsync((await cas.OpenRandomAccessAsync(key))!);
        foreach (var (offset, length) in ranges)
        {
            byte[] actual = new byte[length];
            long before = reader.SourceBytes;
            long start = Stopwatch.GetTimestamp();
            int got = await reader.ReadAtAsync(offset, actual);
            times.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
            maxInput = Math.Max(maxInput, reader.SourceBytes - before);
            int wanted = (int)Math.Max(0, Math.Min(length, size - offset));
            Check.That(got == wanted, $"range length {offset}/{length}");
            byte[] expected = new byte[wanted];
            int read = 0;
            while (read < wanted)
                read += await RandomAccess.ReadAsync(oracle, expected.AsMemory(read), offset + read);
            Check.That(actual.AsSpan(0, got).SequenceEqual(expected), $"range bytes {offset}/{length}");
        }
        await Check.Throws<ArgumentOutOfRangeException>(async () => await reader.ReadAtAsync(-1, new byte[1]));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Check.Throws<OperationCanceledException>(async () => await reader.ReadAtAsync(0, new byte[4096], cancelled.Token));
        // Concurrent requests share one image, its decoded-block cache and artifact reader.
        await Task.WhenAll(Enumerable.Range(0, 64).Select(i => Task.Run(async () =>
        {
            long offset = (long)i * (size / 65);
            byte[] expected = new byte[4096], actual = new byte[4096];
            await RandomAccess.ReadAsync(oracle, expected, offset);
            Check.That(await reader.ReadAtAsync(offset, actual) == 4096 && actual.SequenceEqual(expected), "shared-reader concurrency");
        })));
        // Separate images exercise independent concurrent decoder state.
        await Task.WhenAll(Enumerable.Range(0, 8).Select(i => Task.Run(async () =>
        {
            await using var independent = await RvzContent.OpenAsync((await cas.OpenRandomAccessAsync(key))!);
            for (int j = 0; j < 16; j++)
            {
                long offset = (i * 16L + j) * (size / 130);
                byte[] expected = new byte[4096], actual = new byte[4096];
                await RandomAccess.ReadAsync(oracle, expected, offset);
                Check.That(await independent.ReadAtAsync(offset, actual) == 4096 && actual.SequenceEqual(expected), "independent-reader concurrency");
            }
        })));
        await using var fresh = await RvzContent.OpenAsync((await cas.OpenRandomAccessAsync(key))!);
        byte[] tail = new byte[4096];
        await fresh.ReadAtAsync(size - tail.Length, tail);
        Check.That(fresh.SourceBytes < new FileInfo(iso).Length / 10, "tail read must not scan the disc");
        await reader.DisposeAsync();
        await Check.Throws<ObjectDisposedException>(async () => await reader.ReadAtAsync(0, new byte[1]));
        // Structural corruption probes do not mutate uploaded or CAS files.
        foreach (bool truncate in new[] { false, true })
            await Check.Throws<InvalidDataException>(async () =>
            {
                await using var bad = await RvzContent.OpenAsync(new DamagedContent((await cas.OpenRandomAccessAsync(key))!, truncate));
                await bad.ReadAtAsync(size - 4096, new byte[4096]);
            });
        await CancellationChecks.Run(cas, key);
        times.Sort();
        return new
        {
            Samples = ranges.Count,
            ReadsAtOrAbove4GiB = ranges.Count(r => r.Offset >= 4294967296),
            WiiPartitionDataOffsets = partitionStarts,
            SharedConcurrentReads = 64,
            IndependentConcurrentReads = 128,
            MedianMs = times[times.Count / 2],
            P95Ms = times[(int)(times.Count * .95)],
            MaxMs = times[^1],
            MaxSourceBytesPerRange = maxInput,
            FreshTailSourceBytesIncludingOpen = fresh.SourceBytes,
            NegativeOffset = "passed",
            PreCancelledRead = "passed",
            DisposedRead = "passed",
            CorruptHeaderAndTruncation = "passed",
            OpenCancellation = "passed",
            ArtifactReadCancellation = "passed",
            BlockedReadIndependence = "passed"
        };
    }
    private sealed class DamagedContent(IRandomAccessContent source, bool truncate) : IRandomAccessContent
    {
        public long Length => truncate ? 128 : source.Length;
        public ContentReadIntegrity Integrity => ContentReadIntegrity.None;
        public async ValueTask<int> ReadAtAsync(long offset, Memory<byte> buffer, CancellationToken ct = default)
        {
            if (offset >= Length)
                return 0;
            int n = await source.ReadAtAsync(offset, buffer[..(int)Math.Min(buffer.Length, Length - offset)], ct);
            if (!truncate && offset == 0 && n > 0)
                buffer.Span[0] ^= 0xff;
            return n;
        }
        public ValueTask DisposeAsync() => source.DisposeAsync();
    }
}
