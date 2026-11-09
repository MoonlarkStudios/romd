using System.Diagnostics;
using System.IO.Abstractions;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Moonlark.Libchdr;
using NSubstitute;
using Romd.Admin.Application.Storage.Files;
using Romd.Application.Common.Configuration;
using Romd.Application.Common.Content;
using Romd.Domain.Source.Rom;
using Romd.Infrastructure.Storage;
using Romd.Media.Cd;
using Romd.Media.Chd;
using Romd.Media.Processes;

namespace Romd.Media.Probe;

/// <summary>Real or synthetic original-file qualification through the CD archive workflow.</summary>
internal static class ChdCdQualificationChecks
{
    internal enum Retention { MeasurementsOnly, ArchiveForReadLoad }
    internal sealed record QualifiedArchive(ChdCdQualificationInputs.Layout Layout, string ArtifactPath, RomMeasuredRepresentation Measured);
    internal sealed record QualifiedRun(ChdCdQualificationInputs.Configuration Configuration, IReadOnlyList<QualifiedArchive> Archives);

    internal static async Task<QualifiedRun> RunAsync(string[] args, Retention retention, IReadOnlyList<string> additionalReports, CancellationToken ct)
    {
        if (!Enum.IsDefined(retention))
            throw new ArgumentOutOfRangeException(nameof(retention));
        if (args.Length != 3)
            throw new ArgumentException("Usage: --chd-cd-qualification-probe <absolute configuration.json> <absolute new report.json>");
        if (!OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException("Supervised CD encoding qualification requires native Linux.");
        MediaProbePaths.Validate([args[1], args[2]]);
        byte[] configBytes = BoundedRuntimeFile.Read(args[1], 1 << 20);
        var config = ChdCdQualificationInputs.Parse(configBytes);
        ArgumentNullException.ThrowIfNull(additionalReports);
        Check.That(additionalReports.Count <= 1, "bounded explicit qualification report destinations");
        var reports = additionalReports.Prepend(args[2]).ToArray();
        MediaProbePaths.Validate(reports);
        for (int index = 0; index < reports.Length; index++)
        {
            Check.That(!reports.Take(index).Any(other => MediaProbePaths.Overlaps(reports[index], other)),
                "qualification report destinations cannot overlap each other");
            ChdCdQualificationInputs.ValidatePaths(config, args[1], reports[index]);
        }
        var native = ChdNativeCandidate.FromBuildRecord(config.NativeDirectory);
        var tool = ChdmanCandidate.FromBuildRecord(config.ToolDirectory);
        var options = Substitute.For<IRomdOptions>();
        options.DataDirectory.Returns(config.DataDirectory);
        options.MaxUploadBytes.Returns(ChdCdLimits.MaximumArtifactBytes);
        var limits = new MediaProcessingOptions { MaxConcurrent = 1 };
        string probeSha256 = Convert.ToHexStringLower(SHA256.HashData(
            BoundedRuntimeFile.Read(typeof(ChdCdQualificationChecks).Assembly.Location, 64 << 20)));
        List<object> rows = [];
        var retained = new List<QualifiedArchive>();
        string phase = "preflight";
        await using var report = new FileStream(args[2], FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        await Save("in-progress", null);
        try
        {
            List<ChdCdQualificationInputs.Layout> layouts = [];
            foreach (var input in config.Inputs)
            {
                ct.ThrowIfCancellationRequested();
                phase = input.Name + ":preflight";
                layouts.Add(ChdCdQualificationInputs.ReadLayout(input));
            }
            var runtime = new MediaProcessingRuntime(options, limits);
            var factory = new LocalTempFileFactory(options, new FileSystem());
            var workflow = new ChdCdArchiveWorkflow(factory, new MediaWorkerSupervisor(limits, runtime, native), TimeProvider.System);
            foreach (var layout in layouts)
            {
                var input = layout.Input;
                phase = input.Name + ":oracle";
                Dictionary<string, (FileStream Stream, Digests Digests, ITempFile Staged)> originals = new(StringComparer.Ordinal);
                try
                {
                    foreach (string name in layout.Names)
                    {
                        long maximum = name == input.CueName ? ChdCdCue.MaximumBytes : layout.Dat.TryGetValue(name, out var dat) ? dat.Size : 16 << 20;
                        var stream = new FileStream(NativeFileLease.OpenRegularRead(Path.Combine(input.FixtureDirectory, name)), FileAccess.Read);
                        ITempFile? staged = null;
                        try
                        {
                            var hashes = await Digests.StreamAsync(stream, ct, maximum);
                            Check.That(hashes.Size > 0, "nonempty original member");
                            if (layout.Dat.TryGetValue(name, out var expected))
                                ChdCdQualificationInputs.MatchDat(hashes, expected);
                            stream.Position = 0;
                            staged = await factory.CreateAsync(stream, ct);
                            Check.That(staged.FileSize == hashes.Size && staged.FileSha256.ToString() == hashes.Sha256, "staging preserves measured original");
                            originals.Add(name, (stream, hashes, staged));
                        }
                        catch { if (staged is not null) await staged.DisposeAsync(); await stream.DisposeAsync(); throw; }
                    }
                    // Reparse the measured staged CUE, rather than trusting a pathname read made before staging.
                    await using (var stagedCue = originals[input.CueName].Staged.OpenRead())
                    {
                        byte[] bytes = new byte[checked((int)stagedCue.Length)];
                        await stagedCue.ReadExactlyAsync(bytes, ct);
                        Check.That(ChdCdCue.Parse(bytes).Files.Select(file => file.Name).SequenceEqual(layout.Cue.Files.Select(file => file.Name)),
                            "measured descriptor names agree with the complete DAT set");
                    }
                    var bins = layout.Cue.Files.ToDictionary(file => file.Name, file => originals[file.Name].Staged, StringComparer.Ordinal);
                    var sidecars = input.Sidecars.ToDictionary(name => name, name => originals[name].Staged, StringComparer.Ordinal);
                    await using var sources = await workflow.PrepareAsync(input.CueName, originals[input.CueName].Staged, bins, sidecars, ct);
                    phase = input.Name + ":encode-and-verify";
                    await Save("in-progress", null);
                    var elapsed = Stopwatch.StartNew();
                    await using var result = await workflow.EncodeAsync(sources, tool, input.MaximumOutputBytes, ct);
                    elapsed.Stop();
                    Check.That(result.EncodingRun.Execution!.ProcessId != result.VerificationRun.Execution!.ProcessId &&
                        result.EncodingRun.ProcessTreeStopped && result.VerificationRun.ProcessTreeStopped, "independent joined encoder and verifier");
                    foreach (var member in ((RomReconstructionManifest.CdTracks)result.Measured.Manifest).Members)
                    {
                        var expected = originals[member.Path].Digests;
                        Check.That(member.Identity.Size == expected.Size && member.Identity.Sha256.ToString() == expected.Sha256 &&
                            member.Identity.Sha1.ToString() == expected.Sha1 && member.Identity.Md5.ToString() == expected.Md5 &&
                            member.Identity.Crc32.ToString() == expected.Crc, "all original member digests bound by independent verification");
                    }
                    phase = input.Name + ":source-retention";
                    foreach (var (name, original) in originals)
                    {
                        original.Stream.Position = 0;
                        Check.That(await Digests.StreamAsync(original.Stream, ct, original.Digests.Size) == original.Digests, "held originals unchanged");
                        await using var named = new FileStream(NativeFileLease.OpenRegularRead(Path.Combine(input.FixtureDirectory, name)), FileAccess.Read);
                        Check.That(await Digests.StreamAsync(named, ct, original.Digests.Size) == original.Digests, "named originals unchanged");
                    }
                    using (var dat = new FileStream(NativeFileLease.OpenRegularRead(input.DatPath), FileAccess.Read))
                        Check.That((await Digests.StreamAsync(dat, ct, 64 << 20)).Sha256 == input.DatSha256, "named independent DAT unchanged");
                    var tracks = result.VerificationRun.Reply!.CdInspection!.Tracks;
                    var manifest = ChdCdFactReport.Create(result.Measured);
                    string? retainedPath = null;
                    if (retention == Retention.ArchiveForReadLoad)
                    {
                        string directory = Path.Combine(config.DataDirectory, "qualified-archives");
                        Directory.CreateDirectory(directory);
                        retainedPath = Path.Combine(directory, $"archive-{retained.Count + 1:D4}.chd");
                        await using (var source = result.Artifact.OpenRead())
                        await using (var target = new FileStream(retainedPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
                        {
                            byte[] buffer = new byte[1 << 20];
                            long left = result.Artifact.FileSize;
                            while (left > 0)
                            {
                                int count = await source.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, left)), ct);
                                Check.That(count > 0, "qualified archive copy reaches its exact measured length");
                                await target.WriteAsync(buffer.AsMemory(0, count), ct);
                                left -= count;
                            }
                            Check.That(await source.ReadAsync(buffer.AsMemory(0, 1), ct) == 0, "qualified archive has no trailing bytes");
                        }
                        var copied = await Digests.FileAsync(retainedPath, ct);
                        Check.That(copied.Size == result.Artifact.FileSize && copied.Sha256 == result.Artifact.FileSha256.ToString(),
                            "retained read-load archive matches the independent whole-member verifier");
                        retained.Add(new(layout, retainedPath, result.Measured));
                    }
                    rows.Add(new
                    {
                        input.Name,
                        input.DatPath,
                        input.DatSha256,
                        input.DatEntryName,
                        input.FixtureDirectory,
                        DatMatchedMembers = layout.DatNames.Values.ToArray(),
                        DatBindings = layout.DatNames.Select(pair => new { OriginalName = pair.Key, DatName = pair.Value }).ToArray(),
                        layout.CatalogCueReference,
                        Originals = originals.Select(pair => new { Name = pair.Key, Digests = pair.Value.Digests }),
                        Coverage = new
                        {
                            DataOnly = tracks.All(track => track.Type != ChdCdTrackType.Audio),
                            MixedDataAudio = tracks.Any(track => track.Type == ChdCdTrackType.Audio) && tracks.Any(track => track.Type != ChdCdTrackType.Audio),
                            MultipleBins = bins.Count > 1,
                            StoredPregap = tracks.Any(track => track.PregapFrames > 0 && track.PregapStored),
                            VirtualPregap = tracks.Any(track => track.PregapFrames > 0 && !track.PregapStored)
                        },
                        EncodeAndVerifySeconds = elapsed.Elapsed.TotalSeconds,
                        OriginalBinBytes = bins.Values.Sum(bin => bin.FileSize),
                        ArtifactBytes = result.Artifact.FileSize,
                        Encoding = result.EncodingRun,
                        Verification = result.VerificationRun,
                        Manifest = manifest,
                        RetainedArchivePath = retainedPath,
                        OriginalsUnchanged = true
                    });
                }
                finally
                {
                    foreach (var original in originals.Values)
                        try
                        { await original.Staged.DisposeAsync(); }
                        finally { await original.Stream.DisposeAsync(); }
                }
                await Save("in-progress", null);
                Console.WriteLine(input.Name + ": complete DAT match, independent whole-file verification and unchanged originals.");
            }
            Check.That(!Directory.EnumerateFiles(Path.Combine(config.DataDirectory, "temp"), "run-*.json", SearchOption.AllDirectories).Any(),
                "all supervised children release custody");
            phase = "complete";
            await Save("passed", null);
        }
        catch (Exception error) { await Save("failed", error.ToString()); throw; }
        return new(config, retained);

        async Task Save(string status, string? error)
        {
            report.Position = 0;
            report.SetLength(0);
            await JsonSerializer.SerializeAsync(report, new
            {
                Status = status,
                Profile = "chd-cd-original-qualification/v2",
                Retention = retention.ToString(),
                Phase = phase,
                RecordedUtc = DateTimeOffset.UtcNow,
                RuntimeInformation.OSDescription,
                RuntimeInformation.ProcessArchitecture,
                RuntimeInformation.FrameworkDescription,
                ProbeBuild = typeof(ChdCdQualificationChecks).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
                ProbeAssemblySha256 = probeSha256,
                ConfigurationSha256 = Convert.ToHexStringLower(SHA256.HashData(configBytes)),
                config.SourceDescription,
                config.FixtureKind,
                Native = native.ExpectedIdentity,
                Tool = tool.ExpectedIdentity,
                Recipe = ChdCdEncodingRecipe.Id,
                Limits = limits,
                Rows = rows,
                Error = error,
                Scope = "DAT matches cover only explicitly bound originals; a catalog CUE reference does not establish an original descriptor match. Named fixtures and observed layouts only. Original CUE/sidecars retained separately; explicit read-load retention keeps independently verified CHDs under the owned data directory. No production recipe approval, PS1 admission, durable publication/recovery, delivery, emulator or multi-disc qualification."
            }, new JsonSerializerOptions { WriteIndented = true }, CancellationToken.None);
            await report.FlushAsync(CancellationToken.None);
        }
    }
}
