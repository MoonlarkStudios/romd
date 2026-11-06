using System.Diagnostics;
using System.Globalization;
using System.IO.Abstractions;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using NSubstitute;
using Romd.Admin.Application.Storage.Files;
using Romd.Application.Common.Configuration;
using Romd.Application.Common.Content;
using Romd.Domain.Source.Rom;
using Romd.Infrastructure.Storage;
using Romd.Media;
using Romd.Media.PlayStation;
using Romd.Media.Processes;
using Romd.Storage;

namespace Romd.Media.Probe;

/// <summary>Fixed research candidates on independently matched originals. Does not change production policy.</summary>
internal static class ChdCdRecipeChecks
{
    internal sealed record Candidate(string Name, string Codecs, int HunkBytes);
    internal static readonly Candidate[] Candidates =
    [
        new("cdzs-8", "cdzs", 19584),
        new("cdzs-cdfl-8", "cdzs,cdfl", 19584),
        new("cdlz-cdzl-cdfl-8", "cdlz,cdzl,cdfl", 19584),
        new("cdzs-cdfl-16", "cdzs,cdfl", 39168)
    ];
    internal const int Trials = 3, Warmup = 1000, RandomReads = 10000, RequestBytes = 4096;
    internal static string[] Arguments(Candidate candidate, string cue, string output) =>
        ["createcd", "-i", cue, "-o", output, "-hs", candidate.HunkBytes.ToString(CultureInfo.InvariantCulture), "-c", candidate.Codecs, "-np", "1"];

    internal static bool Matches(Candidate candidate, RomMeasuredRepresentation measured)
    {
        var codecs = candidate.Codecs.Split(',').Select(name => System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(Encoding.ASCII.GetBytes(name)))
            .Concat(Enumerable.Repeat(0u, 4)).Take(4);
        return measured.Container is RomContainerFacts.Chd header && header.HunkBytes == candidate.HunkBytes && header.Codecs.SequenceEqual(codecs);
    }

    internal static async Task RunAsync(string[] args, CancellationToken ct)
    {
        if (args.Length != 3)
            throw new ArgumentException("Usage: --chd-cd-recipe-probe <absolute configuration.json> <absolute new report.json>");
        if (!OperatingSystem.IsLinux() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
            throw new PlatformNotSupportedException("This recipe campaign targets native Linux x64.");
        MediaProbePaths.Validate([args[1], args[2]]);
        byte[] configBytes = BoundedRuntimeFile.Read(args[1], 1 << 20);
        var config = ChdCdQualificationInputs.Parse(configBytes);
        ChdCdQualificationInputs.ValidatePaths(config, args[1], args[2]);
        // Validate every descriptor and oracle before reserving outputs or launching tools.
        var layouts = config.Inputs.Select(ChdCdQualificationInputs.ReadLayout).ToArray();
        var native = ChdNativeCandidate.FromBuildRecord(config.NativeDirectory);
        var tool = ChdmanCandidate.FromBuildRecord(config.ToolDirectory);
        var nativeIdentity = native.VerifyAndLoad(ct);
        var toolIdentity = tool.Verify(ct);
        var options = Substitute.For<IRomdOptions>();
        options.DataDirectory.Returns(config.DataDirectory);
        options.MaxUploadBytes.Returns(ChdCdLimits.MaximumArtifactBytes);
        var limits = new MediaProcessingOptions { MaxConcurrent = 1 };
        var runtime = new MediaProcessingRuntime(options, limits);
        var factory = new LocalTempFileFactory(options, new FileSystem());
        var workflow = new ChdCdArchiveWorkflow(factory, new MediaWorkerSupervisor(limits, runtime, native), TimeProvider.System);
        List<object> rows = [];
        string phase = "initialization";
        await using var report = new FileStream(args[2], FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        await Save("in-progress", null);
        try
        {
            foreach (var layout in layouts)
            {
                var originals = new Dictionary<string, (FileStream Stream, Digests Hashes, ITempFile Staged)>(StringComparer.Ordinal);
                try
                {
                    foreach (string name in layout.Names)
                    {
                        var stream = new FileStream(NativeFileLease.OpenRegularRead(Path.Combine(layout.Input.FixtureDirectory, name)), FileAccess.Read);
                        try
                        {
                            var hashes = await Digests.StreamAsync(stream, ct, name == layout.Input.CueName ? ChdCdCue.MaximumBytes : ChdCdLimits.MaximumArtifactBytes);
                            if (layout.Dat.TryGetValue(name, out var expected))
                                ChdCdQualificationInputs.MatchDat(hashes, expected);
                            stream.Position = 0;
                            var staged = await factory.CreateAsync(stream, ct);
                            Check.That(staged.FileSha256.ToString() == hashes.Sha256 && staged.FileSize == hashes.Size, "exact staged original identity");
                            originals.Add(name, (stream, hashes, staged));
                        }
                        catch { await stream.DisposeAsync(); throw; }
                    }
                    var bins = layout.Cue.Files.ToDictionary(f => f.Name, f => originals[f.Name].Staged, StringComparer.Ordinal);
                    var sidecars = layout.Input.Sidecars.ToDictionary(n => n, n => originals[n].Staged, StringComparer.Ordinal);
                    await using var sources = await workflow.PrepareAsync(layout.Input.CueName, originals[layout.Input.CueName].Staged, bins, sidecars, ct);
                    foreach (var candidate in Candidates)
                    {
                        phase = layout.Input.Name + ":" + candidate.Name;
                        await Save("in-progress", null);
                        string directory = Path.Combine(config.DataDirectory, "candidates", layout.Input.Name, candidate.Name);
                        Directory.CreateDirectory(directory);
                        string output = Path.Combine(directory, "disc.chd");
                        string[] argv = Arguments(candidate, sources.Specification.ToolCuePath, output);
                        var encode = await EncodeAsync(tool, argv, layout.Input.MaximumOutputBytes, ct);
                        Check.That(tool.Verify(ct) == toolIdentity, "tool unchanged after candidate encode");
                        Digests artifact = await Digests.FileAsync(output, ct);
                        await using var encodedStream = new FileStream(NativeFileLease.OpenRegularRead(output), FileAccess.Read);
                        await using var staged = await factory.CreateAsync(encodedStream, ct);
                        Check.That(staged.FileSha256.ToString() == artifact.Sha256 && staged.FileSize == artifact.Size, "exact staged candidate");
                        var verifying = Stopwatch.StartNew();
                        var comparison = await workflow.VerifyExistingAsync(staged, sources, ct);
                        var measured = comparison.Measured;
                        var manifest = (RomReconstructionManifest.CdTracks)measured.Manifest;
                        verifying.Stop();
                        Check.That(Matches(candidate, measured), "effective codec slots and hunk match requested recipe");
                        foreach (var member in manifest.Members)
                        {
                            var original = originals[member.Path].Hashes;
                            Check.That(member.Identity.Size == original.Size && member.Identity.Sha256.ToString() == original.Sha256 &&
                                member.Identity.Sha1.ToString() == original.Sha1 && member.Identity.Md5.ToString() == original.Md5 &&
                                member.Identity.Crc32.ToString() == original.Crc, "fresh-child complete original member comparison");
                        }
                        List<object> reads = [];
                        for (int trial = 1; trial <= Trials; trial++)
                            reads.Add(await MeasureAsync(output, measured, layout.Input.FixtureDirectory, trial, ct));
                        var manifestJson = ChdCdFactReport.Create(measured);
                        rows.Add(new
                        {
                            Fixture = layout.Input.Name,
                            Candidate = candidate,
                            ArtifactPath = output,
                            Artifact = artifact,
                            OriginalBinBytes = bins.Values.Sum(b => b.FileSize),
                            Tool = toolIdentity,
                            Arguments = argv,
                            Encode = encode,
                            IndependentVerificationSeconds = verifying.Elapsed.TotalSeconds,
                            Reads = reads,
                            Manifest = manifestJson,
                            DatSha256 = layout.Input.DatSha256,
                            layout.Input.DatEntryName,
                            layout.DatNames,
                            layout.CatalogCueReference
                        });
                        Check.That(await Digests.FileAsync(output, ct) == artifact, "retained candidate unchanged after reads");
                        await File.WriteAllTextAsync(Path.Combine(directory, "manifest.json"), manifestJson.GetRawText(), ct);
                        await Save("in-progress", null);
                        Console.WriteLine(phase + $": {artifact.Size} bytes, encode {encode.Seconds:F2}s, fresh whole-member verification passed.");
                    }
                    foreach (var (name, original) in originals)
                    {
                        original.Stream.Position = 0;
                        Check.That(await Digests.StreamAsync(original.Stream, ct, original.Hashes.Size) == original.Hashes &&
                            await Digests.FileAsync(Path.Combine(layout.Input.FixtureDirectory, name), ct) == original.Hashes, "held and named originals unchanged");
                    }
                    Check.That((await Digests.FileAsync(layout.Input.DatPath, ct)).Sha256 == layout.Input.DatSha256, "independent DAT unchanged");
                }
                finally { foreach (var original in originals.Values) try { await original.Staged.DisposeAsync(); } finally { await original.Stream.DisposeAsync(); } }
            }
            Check.That(!Directory.EnumerateFiles(Path.Combine(config.DataDirectory, "temp"), "run-*.json", SearchOption.AllDirectories).Any(), "no active media custody");
            phase = "complete";
            await Save("passed", null);
        }
        catch (Exception error) { await Save("failed", error.ToString()); throw; }

        async Task Save(string status, string? error)
        {
            report.Position = 0;
            report.SetLength(0);
            await JsonSerializer.SerializeAsync(report, new
            {
                Status = status,
                Phase = phase,
                Profile = "chd-cd-recipe-comparison/v1",
                Error = error,
                RecordedUtc = DateTimeOffset.UtcNow,
                Source = config.SourceDescription,
                config.FixtureKind,
                ConfigurationSha256 = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(configBytes)),
                ProbeBuild = typeof(ChdCdRecipeChecks).Assembly.GetCustomAttributesData().Where(a => a.AttributeType == typeof(System.Reflection.AssemblyInformationalVersionAttribute)).Select(a => a.ConstructorArguments[0].Value).Single(),
                ProbeAssemblySha256 = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(BoundedRuntimeFile.Read(typeof(ChdCdRecipeChecks).Assembly.Location, 64 << 20))),
                Native = nativeIdentity,
                Tool = toolIdentity,
                RuntimeInformation.OSDescription,
                RuntimeInformation.ProcessArchitecture,
                RuntimeInformation.FrameworkDescription,
                ProcessorCount = Environment.ProcessorCount,
                Rows = rows,
                Settings = new
                {
                    Candidates,
                    Trials,
                    Warmup,
                    RandomReads,
                    RequestBytes,
                    CacheBytes = new ReadDecoderOptions().CacheBytes,
                    Timing = "Encode: wrapper start through joined exit, excludes hashing/verification. Reads: ReadAtAsync only; oracle and comparisons outside timer. Three fresh pools/cache trials; warm uncontrolled OS cache.",
                    Scope = "Trusted original fixtures only; research encoder under prlimit, single processor. Fresh supervised whole-file verifier. No production admission or emulator compatibility claim."
                }
            }, JsonOptions.Value, CancellationToken.None);
            await report.FlushAsync(CancellationToken.None);
        }
    }

    private sealed record EncodeMeasurement(double Seconds, long PeakSampledWorkingSetBytes, int ExitCode);
    private static async Task<EncodeMeasurement> EncodeAsync(ChdmanCandidate tool, string[] argv, long maximumBytes, CancellationToken ct)
    {
        var start = new ProcessStartInfo("/usr/bin/prlimit") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        start.Environment.Clear();
        foreach (string arg in new[] { "--as=2147483648:2147483648", "--data=1073741824:1073741824", "--cpu=1200:1200",
            "--fsize=" + maximumBytes.ToString(CultureInfo.InvariantCulture) + ":" + maximumBytes.ToString(CultureInfo.InvariantCulture), "--", tool.BinaryPath }.Concat(argv))
            start.ArgumentList.Add(arg);
        using var process = new Process { StartInfo = start };
        var clock = Stopwatch.StartNew();
        Check.That(process.Start(), "research encoder started");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromMinutes(20));
        Task<string> stdout = Drain(process.StandardOutput), stderr = Drain(process.StandardError);
        long peak = 0;
        try
        {
            while (!process.HasExited)
            {
                deadline.Token.ThrowIfCancellationRequested();
                try
                { process.Refresh(); peak = Math.Max(peak, process.WorkingSet64); }
                catch (InvalidOperationException) when (process.HasExited) { break; }
                await Task.WhenAny(process.WaitForExitAsync(deadline.Token), Task.Delay(250, deadline.Token));
            }
            await process.WaitForExitAsync(CancellationToken.None);
            clock.Stop();
            string output = await stdout, errors = await stderr;
            Check.That(process.ExitCode == 0, "research encoder exit: " + process.ExitCode + ": " + errors);
            return new(clock.Elapsed.TotalSeconds, peak, process.ExitCode);
        }
        finally
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
            await Task.WhenAll(stdout, stderr);
        }
        static async Task<string> Drain(StreamReader reader)
        {
            char[] buffer = new char[4096];
            var retained = new StringBuilder();
            int read;
            while ((read = await reader.ReadAsync(buffer)) != 0)
                if (retained.Length < 262144)
                    retained.Append(buffer, 0, Math.Min(read, 262144 - retained.Length));
            return retained.ToString();
        }
    }

    private static async Task<object> MeasureAsync(string artifact, RomMeasuredRepresentation measured, string originals, int trial, CancellationToken ct)
    {
        var manifest = (RomReconstructionManifest.CdTracks)measured.Manifest;
        var cache = new MediaBlockCache(new ReadDecoderOptions().CacheBytes);
        await using var pool = new ChdReadPool(cache, 4, 2);
        var members = manifest.Members.OfType<RomCdBinMember>().ToArray();
        List<(IRandomAccessContent Content, FileStream Oracle)> readers = [];
        try
        {
            foreach (var member in members)
            {
                var device = await ChdBlockDevice.OpenAsync(new ChdCdRawFile(artifact), pool, ChdCdLimits.Preflight,
                    measured.Measurement.ArtifactSha256.ToString(), ct: ct);
                var projection = await ChdCdBinContent.CreateAsync(device, member.Tracks.Select(n => manifest.Tracks[n - 1])
                    .Select(t => new ChdCdBinExtent(t.FirstFrame, t.Frames, t.Kind == RomCdTrackKind.Audio)).ToArray());
                readers.Add((projection, new FileStream(NativeFileLease.OpenRegularRead(Path.Combine(originals, member.Path)), FileAccess.Read)));
            }
            var random = new Random(123456);
            byte[] actual = new byte[RequestBytes], expected = new byte[RequestBytes];
            double[] latencies = new double[RandomReads];
            for (int i = 0; i < Warmup + RandomReads; i++)
            {
                var (reader, oracle) = readers[i % readers.Count];
                long offset = random.NextInt64(reader.Length - RequestBytes + 1);
                oracle.Position = offset;
                await oracle.ReadExactlyAsync(expected, ct);
                long started = Stopwatch.GetTimestamp();
                int count = await reader.ReadAtAsync(offset, actual, ct);
                double ms = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                Check.That(count == actual.Length && actual.AsSpan().SequenceEqual(expected), "random selected-member bytes equal independent original");
                if (i >= Warmup)
                    latencies[i - Warmup] = ms;
            }
            // Discard random cache contents before sequential timing, retaining no prior trial cache.
            foreach (var (reader, oracle) in readers)
            { await reader.DisposeAsync(); await oracle.DisposeAsync(); }
            readers.Clear();
            await pool.DisposeAsync();
            var sequentialCache = new MediaBlockCache(new ReadDecoderOptions().CacheBytes);
            await using var sequentialPool = new ChdReadPool(sequentialCache, 4, 2);
            long bytes = 0;
            double seconds = 0;
            byte[] buffer = new byte[1 << 20];
            foreach (var member in members)
            {
                var device = await ChdBlockDevice.OpenAsync(new ChdCdRawFile(artifact), sequentialPool, ChdCdLimits.Preflight,
                    measured.Measurement.ArtifactSha256.ToString(), ct: ct);
                await using var projection = await ChdCdBinContent.CreateAsync(device, member.Tracks.Select(n => manifest.Tracks[n - 1])
                    .Select(t => new ChdCdBinExtent(t.FirstFrame, t.Frames, t.Kind == RomCdTrackKind.Audio)).ToArray());
                long offset = 0, started = Stopwatch.GetTimestamp();
                while (offset < projection.Length)
                {
                    int wanted = (int)Math.Min(buffer.Length, projection.Length - offset);
                    int read = await projection.ReadAtAsync(offset, buffer.AsMemory(0, wanted), ct);
                    Check.That(read == wanted, "sequential member length");
                    offset += read;
                }
                seconds += Stopwatch.GetElapsedTime(started).TotalSeconds;
                bytes += offset;
                Check.That(await projection.ReadAtAsync(offset, buffer, ct) == 0, "exact selected-member EOF");
            }
            return new
            {
                Trial = trial,
                RandomP50Ms = ChdRecipeChecks.Quantile(latencies, .5),
                RandomP95Ms = ChdRecipeChecks.Quantile(latencies, .95),
                RandomMaxMs = latencies.Max(),
                SequentialBytes = bytes,
                SequentialSeconds = seconds,
                SequentialMBps = ChdRecipeChecks.DecimalMegabytesPerSecond(bytes, seconds),
                ProcessWorkingSetBytes = Environment.WorkingSet
            };
        }
        finally { foreach (var (reader, oracle) in readers) try { await reader.DisposeAsync(); } finally { await oracle.DisposeAsync(); } }
    }
}
