using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Xml.Linq;
using ErrorOr;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Microsoft.Win32.SafeHandles;
using NSubstitute;
using Romd.Application.Common.Content;
using Romd.Application.Common.Security;
using Romd.Consumer.Application.Libraries;
using Romd.Consumer.Application.Vfs;
using Romd.Domain.Hashing;
using Romd.Domain.Source.Rom;
using Romd.Host.Endpoints;
using Romd.Hosting.Vfs;
using Romd.Infrastructure.Identity;
using Romd.Infrastructure.Storage;
using Romd.Storage;

namespace Romd.Media.Probe;

internal static class RecipeChecks
{
    private const int Samples = 1000, BufferBytes = 1 << 20;
    private const long FourGiB = 1L << 32;
    private const string Url = "/vfs/recipe/disc.iso";
    private static readonly JsonSerializerOptions SpecOptions = new() { PropertyNameCaseInsensitive = true };

    internal static async Task RunAsync(string[] args, CancellationToken ct)
    {
        if (args.Length != 3 || args[0] != "--recipe-probe")
            throw new ArgumentException("Usage: probe --recipe-probe <spec.json> <results.json>");
        string resultsPath = Path.GetFullPath(args[2]);
        string root = Path.Combine(Path.GetTempPath(), "romd-recipe-probe-" + Guid.NewGuid().ToString("N"));
        var reports = new List<object>();
        FileStream? reportOutput = null;
        try
        {
            await using var specInput = File.OpenRead(args[1]);
            using var document = await JsonDocument.ParseAsync(specInput, cancellationToken: ct);
            var specs = document.RootElement.ValueKind == JsonValueKind.Array
                ? document.RootElement.Deserialize<Spec[]>(SpecOptions)!
                : [document.RootElement.Deserialize<Spec>(SpecOptions)!];
            Check.That(specs.Length > 0, "recipe specifications are present");
            Check.That(resultsPath != Path.GetFullPath(args[1]), "report does not overwrite the specification");
            foreach (var spec in specs)
            {
                Check.That(spec is not null && !string.IsNullOrWhiteSpace(spec.Name), "recipe specification name");
                foreach (string source in new[] { spec!.Artifact, spec.Iso, spec.Dat })
                    Check.That(resultsPath != Path.GetFullPath(source), "report does not overwrite a source");
            }
            reportOutput = new FileStream(resultsPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read,
                4096, FileOptions.Asynchronous);
            await WriteReportAsync(reportOutput, new { Status = "in-progress", Reports = reports }, ct);
            try
            {
                var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder();
                builder.Logging.ClearProviders();
                builder.Services.AddContentAddressableStorage(options =>
                {
                    options.RootPath = root;
                    options.WritePolicy = ContentWritePolicy.Raw;
                });
                using var host = builder.Build();
                var cas = host.Services.GetRequiredService<IContentAddressableStore>();
                foreach (var spec in specs)
                {
                    Console.WriteLine($"{spec.Name}: independent ISO/DAT and artifact hashes");
                    var iso = await HashFileAsync(spec.Iso, ct);
                    Check.That(iso.Size > 0, "nonempty ISO oracle");
                    await CheckDatAsync(spec, iso, ct);
                    var artifact = await HashFileAsync(spec.Artifact, ct);
                    var profile = await ReadProfileAsync(spec.Artifact, ct);
                    await using (var input = OpenFile(spec.Artifact))
                    {
                        var stored = await cas.StoreAsync(input, ct: ct);
                        Check.That(stored.Key.ToString() == artifact.Sha256 && !stored.IsCompressed,
                            "raw CAS publication preserves artifact SHA-256");
                        var info = await cas.GetInfoAsync(stored.Key, ct);
                        Check.That(info is not null && info.Value.StoredSize == artifact.Size,
                            "raw CAS artifact length");
                    }
                    var key = StorageKey.FromHash(Sha256.Parse(artifact.Sha256));
                    Check.That(await cas.VerifyAsync(key, ct), "complete CAS artifact integrity");
                    var full = new List<object>(3);
                    for (int trial = 0; trial < 3; trial++)
                    {
                        Console.WriteLine($"{spec.Name}: full decode trial {trial + 1}/3");
                        await using var decoder = NewDecoder();
                        long opened = Stopwatch.GetTimestamp();
                        await using var content = await decoder.DecodeAsync(RomRepresentationEncoding.Rvz,
                            (await cas.OpenRandomAccessAsync(key, ct))!, ct, key.Hash);
                        double openSeconds = Stopwatch.GetElapsedTime(opened).TotalSeconds;
                        Check.That(content.Length == iso.Size, "full decode logical length");
                        long started = Stopwatch.GetTimestamp();
                        using var stream = content.AsStream(leaveOpen: true);
                        var decoded = await HashStreamAsync(stream, ct);
                        double seconds = Stopwatch.GetElapsedTime(started).TotalSeconds;
                        Check.That(decoded == iso, "complete decoded size/CRC32/MD5/SHA-1/SHA-256");
                        full.Add(new
                        {
                            Trial = trial + 1,
                            OpenSeconds = openSeconds,
                            FullDecodeAndHashSeconds = seconds,
                            TotalSeconds = openSeconds + seconds,
                            Digests = decoded
                        });
                    }
                    using var oracle = File.OpenHandle(spec.Iso, FileMode.Open, FileAccess.Read, FileShare.Read,
                        FileOptions.Asynchronous | FileOptions.RandomAccess);
                    var measuredContent = await MeasuredProbeContent.VerifyRvzAsync(cas, key, iso, ct);
                    var direct = new List<object>(5);
                    var web = new List<object>(5);
                    foreach (string workload in new[] { "4KiB", "64KiB", "1MiB", "chunk-boundary", "EOF" })
                    {
                        var ranges = Ranges(workload, iso.Size, profile);
                        Console.WriteLine($"{spec.Name}: direct decoder {workload}, {Samples} first/repeat pairs");
                        direct.Add(await DirectAsync(cas, key, oracle, iso.Size, workload, ranges, ct));
                        Console.WriteLine($"{spec.Name}: VFS {workload}, {Samples} first/repeat pairs");
                        web.Add(await VfsAsync(cas, key, measuredContent.Logical, iso, oracle, workload, ranges, ct));
                    }
                    reports.Add(new
                    {
                        spec.Name,
                        ArtifactPath = spec.Artifact,
                        IsoPath = spec.Iso,
                        spec.Dat,
                        spec.DatId,
                        Artifact = artifact,
                        Iso = iso,
                        Header = profile,
                        FullDecodeTrials = full,
                        DirectDecoder = direct,
                        Vfs = web
                    });
                    await WriteReportAsync(reportOutput, new { Status = "in-progress", Reports = reports }, ct);
                }
            }
            finally
            {
                if (Directory.Exists(root))
                    Directory.Delete(root, recursive: true);
            }
            await WriteReportAsync(reportOutput, new
            {
                Status = "passed",
                Reports = reports,
                Environment = new
                {
                    RuntimeInformation.RuntimeIdentifier,
                    RuntimeInformation.OSDescription,
                    RuntimeInformation.FrameworkDescription,
                    Architecture = RuntimeInformation.ProcessArchitecture.ToString(),
                    DecoderBuild = "romd-media/mvid:" + typeof(Rvz.RvzImage).Module.ModuleVersionId
                },
                DecoderLimits = new ReadDecoderOptions(),
                Timing = "Full-decode timings include hashing. Range timers include CAS/decoder opening, reading and disposal; VFS timers include the HTTP body. Oracle reads and byte assertions are excluded. Each workload starts with a fresh empty decoder pool/cache, then uses normal pooling/cache reuse. First means the first request of each identical pair, not a disk-cold read. The immediate repeat follows that request. OS caches are warm/uncontrolled.",
                Memory = "Peak RSS is measured externally; this probe makes no PeakWorkingSet64 claim.",
                Scope = "Disposable raw CAS; production decoder and real VFS endpoint in TestServer with production service/content resolver/ROM reader, fake catalog/layout/authentication. No TCP/TLS, mount, WAN, database, gameplay or process-containment qualification."
            }, ct);
        }
        catch (Exception error)
        {
            if (reportOutput is not null)
                await WriteReportAsync(reportOutput, new { Status = "failed", Error = error.ToString(), Reports = reports }, CancellationToken.None);
            throw;
        }
        finally
        {
            if (reportOutput is not null)
                await reportOutput.DisposeAsync();
        }
    }

    private static RomRepresentationDecoder NewDecoder() => new(new RomMediaAdapterRegistry(), new ReadDecoderOptions());

    private static async Task<object> DirectAsync(IContentAddressableStore cas, StorageKey key, SafeFileHandle oracle,
        long size, string workload, RequestRange[] ranges, CancellationToken ct)
    {
        await using var decoder = NewDecoder();
        byte[] expected = new byte[BufferBytes], actual = new byte[BufferBytes];
        var first = new double[Samples];
        var repeat = new double[Samples];
        var firstOpen = new double[Samples];
        var repeatOpen = new double[Samples];
        for (int i = 0; i < ranges.Length; i++)
        {
            ct.ThrowIfCancellationRequested();
            var range = ranges[i];
            int wanted = Wanted(size, range);
            await ReadOracleAsync(oracle, range.Offset, expected.AsMemory(0, wanted), ct);
            for (int pass = 0; pass < 2; pass++)
            {
                long started = Stopwatch.GetTimestamp();
                var source = await cas.OpenRandomAccessAsync(key, ct);
                Check.That(source is not null, "direct artifact exists");
                var reader = await decoder.DecodeAsync(RomRepresentationEncoding.Rvz, source!, ct, key.Hash);
                double openMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                int got;
                await using (reader)
                    got = await reader.ReadAtAsync(range.Offset, actual.AsMemory(0, range.Length), ct);
                double elapsedMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                (pass == 0 ? first : repeat)[i] = elapsedMs;
                (pass == 0 ? firstOpen : repeatOpen)[i] = openMs;
                Check.That(got == wanted, $"direct {workload} exact length {range.Offset}/{range.Length}");
                Check.That(actual.AsSpan(0, got).SequenceEqual(expected.AsSpan(0, wanted)),
                    $"direct {workload} bytes {range.Offset}/{range.Length}");
            }
        }
        return new
        {
            Workload = workload,
            Pairs = ranges.Length,
            ByteComparisons = ranges.Length * 2,
            ReadsAtOrAbove4GiB = ranges.Count(range => range.Offset >= FourGiB) * 2,
            PartitionBoundaryPairs = ranges.Count(range => range.PartitionBoundary),
            ExactEofOrBeyondPairs = ranges.Count(range => range.Offset >= size),
            First = Summary(first),
            ImmediateRepeat = Summary(repeat),
            FirstOpen = Summary(firstOpen),
            ImmediateRepeatOpen = Summary(repeatOpen)
        };
    }

    private static async Task<object> VfsAsync(IContentAddressableStore cas, StorageKey key, RomContentDescriptor content,
        Digests iso, SafeFileHandle oracle, string workload, RequestRange[] ranges, CancellationToken ct)
    {
        await using var decoder = NewDecoder();
        var decoderCounter = new CountingDecoder(decoder);
        int casOpens = 0;
        var readStore = Substitute.For<IContentAddressableStore>();
        readStore.OpenRandomAccessAsync(Arg.Any<StorageKey>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            Interlocked.Increment(ref casOpens);
            return cas.OpenRandomAccessAsync(call.Arg<StorageKey>(), call.Arg<CancellationToken>());
        });
        var file = new ConsumerVfsFile("disc.iso", Sha256.Parse(iso.Sha256), iso.Size, Md5.Parse(iso.Md5), Sha1.Parse(iso.Sha1));
        var catalog = Substitute.For<IRomContentCatalog>();
        catalog.FindAsync(file.Sha256, Arg.Any<CancellationToken>()).Returns(
            content);
        var resolver = new CountingResolver(new ConsumerVfsContentResolver(readStore,
            new RomContentReader(catalog, readStore, decoderCounter, ProbeMediaFacts.ManagedAdmission)));
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Romd:Vfs:Enabled"] = "true",
            ["Romd:Vfs:AllowDevelopmentBearer"] = "true"
        });
        builder.Services.AddAuthentication("RecipeProbe").AddScheme<AuthenticationSchemeOptions, ProbeAuth>("RecipeProbe", _ => { });
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddScoped<ICurrentUser, CurrentUser>();
        builder.Services.AddRomdConsumerVfs(builder.Configuration);
        builder.Services.AddAuthorization(options => options.AddPolicy(VfsBasicAuthenticationHandler.PolicyName,
            policy => policy.AddAuthenticationSchemes("RecipeProbe").RequireAuthenticatedUser()));
        builder.Services.RemoveAll<IVfsLayout>();
        builder.Services.AddSingleton<IVfsLayout>(new Layout());
        var repository = Substitute.For<IConsumerVfsRepository>();
        repository.GetFileAsync(Arg.Any<ConsumerLibraryScope>(), 1, "disc.iso", Arg.Any<CancellationToken>()).Returns(
            new ConsumerLibraryReadResult<ConsumerVfsResolvedFile>.Found(1,
                new(new(1, 1, "Recipe probe", "Recipe probe"), file, [file.RelativePath])));
        builder.Services.AddSingleton(repository);
        builder.Services.AddSingleton<IConsumerVfsContentResolver>(resolver);
        await using var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapConsumerVfsEndpoints();
        await app.StartAsync(ct);
        using var client = app.GetTestClient();
        client.Timeout = TimeSpan.FromMinutes(5);
        await MetadataAsync(client, file, ct);
        Check.That(resolver.Opened == 0 && decoderCounter.Opened == 0 && casOpens == 0,
            "HEAD/PROPFIND/304/416 never open content, CAS or decoder");
        var first = new double[Samples];
        var repeat = new double[Samples];
        byte[] expected = new byte[BufferBytes], actual = new byte[BufferBytes], eof = new byte[1];
        int contentPairs = 0;
        for (int i = 0; i < ranges.Length; i++)
        {
            ct.ThrowIfCancellationRequested();
            var range = ranges[i];
            int wanted = Wanted(iso.Size, range);
            await ReadOracleAsync(oracle, range.Offset, expected.AsMemory(0, wanted), ct);
            if (wanted > 0)
                contentPairs++;
            for (int pass = 0; pass < 2; pass++)
            {
                int beforeResolver = resolver.Opened, beforeDecoder = decoderCounter.Opened, beforeCas = casOpens;
                using var request = new HttpRequestMessage(HttpMethod.Get, Url);
                request.Headers.TryAddWithoutValidation("Range", FormattableString.Invariant($"bytes={range.Offset}-{range.Offset + range.Length - 1}"));
                long started = Stopwatch.GetTimestamp();
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
                int got = 0, extra;
                await using (var body = await response.Content.ReadAsStreamAsync(ct))
                {
                    while (got < wanted)
                    {
                        int read = await body.ReadAsync(actual.AsMemory(got, wanted - got), ct);
                        if (read == 0)
                            break;
                        got += read;
                    }
                    extra = await body.ReadAsync(eof, ct);
                }
                double elapsedMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                (pass == 0 ? first : repeat)[i] = elapsedMs;
                Check.That(got == wanted && extra == 0, $"VFS {workload} exact body length/EOF");
                Check.That(actual.AsSpan(0, got).SequenceEqual(expected.AsSpan(0, wanted)),
                    $"VFS {workload} bytes {range.Offset}/{range.Length}");
                Check.That(response.Content.Headers.ContentRange is { } contentRange && contentRange.Unit == "bytes" &&
                    contentRange.Length == iso.Size, "VFS Content-Range complete logical length");
                if (wanted == 0)
                {
                    Check.That(response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable,
                        "VFS EOF/beyond EOF is 416");
                    Check.That(response.Content.Headers.ContentRange!.From is null && response.Content.Headers.ContentRange.To is null,
                        "VFS unsatisfiable range has no interval");
                    Check.That(resolver.Opened == beforeResolver && decoderCounter.Opened == beforeDecoder && casOpens == beforeCas,
                        "VFS EOF/beyond EOF never opens content, CAS or decoder");
                }
                else
                {
                    Check.That(response.StatusCode == HttpStatusCode.PartialContent &&
                        response.Content.Headers.ContentLength == wanted &&
                        response.Content.Headers.ContentRange!.From == range.Offset &&
                        response.Content.Headers.ContentRange.To == range.Offset + wanted - 1 &&
                        response.Headers.ETag?.Tag == Tag(file), "VFS range metadata describes exact logical bytes");
                    Check.That(resolver.Opened == beforeResolver + 1 && decoderCounter.Opened == beforeDecoder + 1 && casOpens == beforeCas + 1,
                        "VFS successful range traverses production content/CAS/decoder");
                }
            }
        }
        await MetadataAsync(client, file, ct);
        Check.That(resolver.Opened == contentPairs * 2 && decoderCounter.Opened == contentPairs * 2 && casOpens == contentPairs * 2,
            "metadata after data reads never opens content, CAS or decoder");
        return new
        {
            Workload = workload,
            Pairs = ranges.Length,
            ByteComparisons = ranges.Length * 2,
            ReadsAtOrAbove4GiB = ranges.Count(range => range.Offset >= FourGiB) * 2,
            PartitionBoundaryPairs = ranges.Count(range => range.PartitionBoundary),
            ExactEofOrBeyondPairs = ranges.Count(range => range.Offset >= iso.Size),
            First = Summary(first),
            ImmediateRepeat = Summary(repeat),
            MetadataChecks = new
            {
                Head = "passed",
                Propfind = "passed",
                NotModified = "passed",
                Unsatisfiable = "passed",
                BeforeAndAfterData = true,
                ContentOpens = 0,
                CasOpens = 0,
                DecoderOpens = 0
            },
            DataContentOpens = resolver.Opened,
            DataCasOpens = casOpens,
            DataDecoderOpens = decoderCounter.Opened
        };
    }

    private static async Task MetadataAsync(HttpClient client, ConsumerVfsFile file, CancellationToken ct)
    {
        byte[] eof = new byte[1];
        using (var request = new HttpRequestMessage(HttpMethod.Head, Url))
        {
            request.Headers.TryAddWithoutValidation("Range", "bytes=0-4095");
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            Check.That(response.StatusCode == HttpStatusCode.OK && response.Content.Headers.ContentLength == file.Length &&
                response.Headers.ETag?.Tag == Tag(file), "HEAD ignores Range and reports full logical length/SHA-256");
            await using var body = await response.Content.ReadAsStreamAsync(ct);
            Check.That(await body.ReadAsync(eof, ct) == 0, "HEAD streamed empty body");
        }
        using (var request = new HttpRequestMessage(new HttpMethod("PROPFIND"), Url))
        {
            request.Headers.Add("Depth", "0");
            request.Content = new StringContent("<D:propfind xmlns:D='DAV:' xmlns:oc='http://owncloud.org/ns'><D:prop><D:getcontentlength/><D:getetag/><oc:checksums/></D:prop></D:propfind>", Encoding.UTF8, "application/xml");
            using var response = await client.SendAsync(request, ct);
            Check.That((int)response.StatusCode == 207, "PROPFIND 207");
            var xml = XDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            Check.That(xml.Descendants(XName.Get("getcontentlength", "DAV:")).Single().Value == file.Length.ToString(CultureInfo.InvariantCulture),
                "PROPFIND full logical length");
            Check.That(xml.Descendants(XName.Get("getetag", "DAV:")).Single().Value == Tag(file), "PROPFIND logical SHA-256");
            Check.That(xml.Descendants(XName.Get("checksum", "http://owncloud.org/ns")).Single().Value == $"MD5:{file.Md5} SHA1:{file.Sha1}",
                "PROPFIND delivered original MD5/SHA-1");
        }
        foreach (bool notModified in new[] { true, false })
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, Url);
            request.Headers.TryAddWithoutValidation(notModified ? "If-None-Match" : "Range",
                notModified ? Tag(file) : FormattableString.Invariant($"bytes={file.Length}-"));
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            Check.That(response.StatusCode == (notModified ? HttpStatusCode.NotModified : HttpStatusCode.RequestedRangeNotSatisfiable),
                "bodyless conditional/unsatisfiable status");
            if (notModified)
                Check.That(response.Headers.ETag?.Tag == Tag(file), "304 logical SHA-256");
            else
                Check.That(response.Content.Headers.ContentRange?.Length == file.Length, "416 complete logical length");
            await using var body = await response.Content.ReadAsStreamAsync(ct);
            Check.That(await body.ReadAsync(eof, ct) == 0, "304/416 streamed empty body");
        }
    }

    private static RequestRange[] Ranges(string workload, long size, Profile profile)
    {
        uint chunkBytes = profile.ChunkBytes;
        var result = new RequestRange[Samples];
        var random = new Random(7654321);
        int length = workload switch { "4KiB" => 4096, "64KiB" or "chunk-boundary" => 65536, _ => BufferBytes };
        for (int i = 0; i < result.Length; i++)
        {
            long offset;
            bool partitionBoundary = false;
            if (workload == "EOF")
                offset = (i % 5) switch { 0 => size, 1 => size + 1, 2 => size - 1, 3 => Math.Max(0, size - 2048), _ => Math.Max(0, size - (BufferBytes / 2)) };
            else if (workload == "chunk-boundary")
            {
                long boundary;
                if (profile.PartitionSegments.Length > 0 && i % 2 == 1)
                {
                    var segment = profile.PartitionSegments[(i / 2) % profile.PartitionSegments.Length];
                    long count = (segment.End - segment.Start - 1) / chunkBytes;
                    long index = count == 0 ? 0 : random.NextInt64(1, count + 1);
                    long aboveFourGiB = Math.Max(1, (FourGiB + 32768 - segment.Start + chunkBytes - 1) / chunkBytes);
                    if (i < 16 && aboveFourGiB <= count)
                        index = aboveFourGiB;
                    boundary = segment.Start + index * chunkBytes;
                    partitionBoundary = true;
                }
                else
                {
                    long count = Math.Max(1, (size - 1) / chunkBytes);
                    boundary = random.NextInt64(1, count + 1) * chunkBytes;
                    if (size > FourGiB + 2L * chunkBytes && i < 3)
                        boundary = FourGiB + i * (long)chunkBytes;
                }
                offset = Math.Max(0, boundary - 32768);
            }
            else
            {
                offset = random.NextInt64(Math.Max(1, size - length + 1));
                if (size > FourGiB + length && i < 3)
                    offset = FourGiB + i - 1;
            }
            result[i] = new(offset, length, partitionBoundary);
        }
        return result;
    }

    private static int Wanted(long size, RequestRange range) => (int)Math.Max(0, Math.Min(range.Length, size - range.Offset));

    private static object Summary(double[] values)
    {
        double first = values[0];
        double[] sorted = (double[])values.Clone();
        Array.Sort(sorted);
        return new
        {
            Samples = values.Length,
            InitialRequestMs = first,
            MedianMs = sorted[(int)Math.Ceiling(sorted.Length * .50) - 1],
            P95Ms = sorted[(int)Math.Ceiling(sorted.Length * .95) - 1],
            P99Ms = sorted[(int)Math.Ceiling(sorted.Length * .99) - 1],
            MaxMs = sorted[^1]
        };
    }

    private static async Task ReadOracleAsync(SafeFileHandle source, long offset, Memory<byte> destination, CancellationToken ct)
    {
        int read = 0;
        while (read < destination.Length)
        {
            int got = await RandomAccess.ReadAsync(source, destination[read..], offset + read, ct);
            Check.That(got > 0, "ISO oracle does not truncate within a requested range");
            read += got;
        }
    }

    private static async Task<Digests> HashFileAsync(string path, CancellationToken ct)
    {
        await using var source = OpenFile(path);
        return await HashStreamAsync(source, ct);
    }

    private static async Task<Digests> HashStreamAsync(Stream source, CancellationToken ct)
    {
        using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        using var sha1 = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        using var sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var crc = new System.IO.Hashing.Crc32();
        long size = 0;
        byte[] buffer = new byte[BufferBytes];
        int read;
        while ((read = await source.ReadAsync(buffer, ct)) != 0)
        {
            size += read;
            crc.Append(buffer.AsSpan(0, read));
            md5.AppendData(buffer, 0, read);
            sha1.AppendData(buffer, 0, read);
            sha256.AppendData(buffer, 0, read);
        }
        return new(size, crc.GetCurrentHashAsUInt32().ToString("x8", CultureInfo.InvariantCulture),
            Convert.ToHexStringLower(md5.GetHashAndReset()), Convert.ToHexStringLower(sha1.GetHashAndReset()),
            Convert.ToHexStringLower(sha256.GetHashAndReset()));
    }

    private static async Task CheckDatAsync(Spec spec, Digests iso, CancellationToken ct)
    {
        await using var input = OpenFile(spec.Dat);
        var dat = await XDocument.LoadAsync(input, LoadOptions.None, ct);
        var rom = dat.Descendants("game").Single(game => (string?)game.Attribute("id") == spec.DatId).Elements("rom").Single();
        Check.That(iso.Size == (long)rom.Attribute("size")!, "independent ISO DAT size");
        foreach (var (key, digest) in new[] { ("crc", iso.Crc), ("md5", iso.Md5), ("sha1", iso.Sha1) })
            Check.That(string.Equals((string?)rom.Attribute(key), digest, StringComparison.OrdinalIgnoreCase), "independent ISO DAT " + key);
    }

    private static async Task<Profile> ReadProfileAsync(string path, CancellationToken ct)
    {
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 1, FileOptions.Asynchronous | FileOptions.RandomAccess);
        byte[] header = new byte[0xe8];
        await input.ReadExactlyAsync(header, ct);
        Check.That(header.AsSpan(0, 4).SequenceEqual("RVZ\u0001"u8), "candidate RVZ magic");
        uint chunk = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(0x54));
        Check.That(chunk > 0, "nonzero candidate chunk size");
        ulong logicalSize = BinaryPrimitives.ReadUInt64BigEndian(header.AsSpan(0x24));
        Check.That(logicalSize > 0 && logicalSize <= long.MaxValue, "bounded logical size in candidate header");
        uint partitionCount = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(0xd8));
        uint entrySize = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(0xdc));
        ulong tableOffset = BinaryPrimitives.ReadUInt64BigEndian(header.AsSpan(0xe0));
        Check.That(partitionCount <= 64 && (partitionCount == 0 || entrySize is >= 48 and <= 0x1000),
            "bounded complete partition entries in candidate header");
        Check.That(tableOffset <= (ulong)input.Length && (ulong)partitionCount * entrySize <= (ulong)input.Length - tableOffset,
            "candidate partition table fits artifact");
        var segments = new List<PublicSegment>((int)partitionCount * 2);
        byte[] segmentBytes = new byte[16];
        for (int partition = 0; partition < partitionCount; partition++)
        {
            for (int segment = 0; segment < 2; segment++)
            {
                // Seek past the 16-byte key; only public segment descriptors are read here.
                input.Position = (long)tableOffset + partition * (long)entrySize + 16 + segment * 16;
                await input.ReadExactlyAsync(segmentBytes, ct);
                uint firstSector = BinaryPrimitives.ReadUInt32BigEndian(segmentBytes);
                uint sectors = BinaryPrimitives.ReadUInt32BigEndian(segmentBytes.AsSpan(4));
                if (sectors == 0)
                    continue;
                long start = firstSector * 32768L;
                long end = start + sectors * 32768L;
                Check.That((ulong)end <= logicalSize, "candidate partition segment fits logical disc");
                segments.Add(new(partition, segment, start, end));
            }
        }
        var ordered = segments.OrderBy(segment => segment.Start).ToArray();
        for (int i = 1; i < ordered.Length; i++)
            Check.That(ordered[i - 1].End <= ordered[i].Start, "candidate public partition segments do not overlap");
        return new(BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(0x4c)), BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(0x50)),
            chunk, BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(4)).ToString("x8", CultureInfo.InvariantCulture), ordered);
    }

    private static FileStream OpenFile(string path) => new(path, FileMode.Open, FileAccess.Read, FileShare.Read,
        128 << 10, FileOptions.Asynchronous | FileOptions.SequentialScan);

    private static async Task WriteReportAsync(FileStream output, object report, CancellationToken ct)
    {
        output.Position = 0;
        await JsonSerializer.SerializeAsync(output, report, JsonOptions.Value, ct);
        output.SetLength(output.Position);
        await output.FlushAsync(ct);
    }

    private static string Tag(ConsumerVfsFile file) => "\"" + file.Sha256 + "\"";
    private sealed record Spec(string Name, string Artifact, string Iso, string Dat, string DatId);
    private sealed record Profile(uint Codec, int Level, uint ChunkBytes, string FormatVersion, PublicSegment[] PartitionSegments);
    private sealed record PublicSegment(int Partition, int Segment, long Start, long End);
    private sealed record RequestRange(long Offset, int Length, bool PartitionBoundary);

    private sealed class CountingDecoder(RomRepresentationDecoder decoder) : IRomRepresentationDecoder
    {
        public int Opened;
        public bool CanDecode(RomRepresentationEncoding format) => decoder.CanDecode(format);
        public Task<IRandomAccessContent> DecodeAsync(RomRepresentationEncoding format, IRandomAccessContent source,
            CancellationToken ct = default, Sha256? artifact = null, TimeSpan? admissionTimeout = null)
        {
            Interlocked.Increment(ref Opened);
            return decoder.DecodeAsync(format, source, ct, artifact, admissionTimeout);
        }
        public Task<IRandomAccessContent> DecodeAsync(RomRepresentationEncoding format, RomArtifactSource source,
            RomMediaOpenContext context, CancellationToken ct = default)
        {
            Interlocked.Increment(ref Opened);
            return decoder.DecodeAsync(format, source, context, ct);
        }
    }

    private sealed class CountingResolver(ConsumerVfsContentResolver resolver) : IConsumerVfsContentResolver
    {
        public int Opened;
        public Task<Stream?> OpenAsync(ConsumerVfsFile file, CancellationToken ct = default)
        {
            Interlocked.Increment(ref Opened);
            return resolver.OpenAsync(file, ct);
        }
    }

    private sealed class Layout : IVfsLayout
    {
        public string Key => "recipe";
        public ConsumerVfsFileLocator? LocateFile(string path) => new(1, path);
        public string? FilePath(ConsumerVfsResolvedFile file) => file.File.RelativePath;
        public Task<ErrorOr<VfsLayoutRead>> ReadAsync(ConsumerLibraryScope scope, string path, bool children,
            int maximum, CancellationToken ct = default, Romd.Domain.Vfs.VfsFormat format = Romd.Domain.Vfs.VfsFormat.Stored) =>
            Task.FromResult<ErrorOr<VfsLayoutRead>>(ConsumerVfsErrors.NotFound());
    }

    private sealed class ProbeAuth(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync() => Task.FromResult(AuthenticateResult.Success(
            new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier,
                "c7155b2c-98ef-4f54-96bf-17ffb396ef1c")], "RecipeProbe")), "RecipeProbe")));
    }
}
