using System.IO.Abstractions;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using NSubstitute;
using Romd.Application.Common.Configuration;
using Romd.Infrastructure.Storage;
using Romd.Media.Cd;
using Romd.Media.Chd;

namespace Romd.Media.Probe;

/// <summary>Read-only CD measurement through the normal isolated child, optionally checked against supplied track digests.</summary>
internal static class ChdCdInspectionChecks
{
    internal sealed record ExpectedTrack(uint Number, long BinBytes, string Sha1, string Md5, string Crc32);
    internal sealed record Input(string ChdPath, ExpectedTrack[]? ExpectedTracks);
    internal sealed record Configuration(string NativeDirectory, string DataDirectory, string SourceDescription, Input[] Inputs);

    public static async Task RunAsync(string[] args, CancellationToken ct)
    {
        if (args.Length != 3)
            throw new ArgumentException("Usage: --chd-cd-inspection-probe <absolute configuration.json> <absolute new report.json>");
        MediaProbePaths.Validate([args[1], args[2]]);
        byte[] configBytes = BoundedRuntimeFile.Read(args[1], 1 << 20);
        using var document = JsonDocument.Parse(configBytes, new JsonDocumentOptions { MaxDepth = 5 });
        Check.That(MediaRunRecord.Unique(document.RootElement), "unique CD probe configuration fields");
        var config = JsonSerializer.Deserialize<Configuration>(configBytes, new JsonSerializerOptions
        { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, RespectRequiredConstructorParameters = true })
            ?? throw new InvalidDataException("Missing CD probe configuration.");
        ValidateConfiguration(config);
        var candidate = ChdNativeCandidate.FromBuildRecord(config.NativeDirectory);
        MediaProbePaths.Validate([args[1], args[2], config.DataDirectory, candidate.ManifestPath, candidate.BinaryPath,
            .. config.Inputs.Select(input => input.ChdPath)]);
        Check.That(!Directory.Exists(config.DataDirectory) && !File.Exists(config.DataDirectory), "new owned CD probe storage");
        Check.That(!config.Inputs.Any(input => MediaProbePaths.Overlaps(config.DataDirectory, input.ChdPath)) &&
            !MediaProbePaths.Overlaps(config.DataDirectory, args[1]) && !MediaProbePaths.Overlaps(config.DataDirectory, args[2]) &&
            !MediaProbePaths.Overlaps(config.DataDirectory, config.NativeDirectory), "probe evidence and inputs outside managed storage");
        var limits = new MediaProcessingOptions { MaxConcurrent = 1 };
        var storage = Substitute.For<IRomdOptions>();
        storage.DataDirectory.Returns(config.DataDirectory);
        storage.MaxUploadBytes.Returns(ChdCdLimits.MaximumArtifactBytes);
        var runtime = new MediaProcessingRuntime(storage, limits);
        var factory = new LocalTempFileFactory(storage, new FileSystem());
        var supervisor = new MediaWorkerSupervisor(limits, runtime, candidate);
        var rows = new List<object>();
        await using var report = new FileStream(args[2], FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        await SaveAsync("in-progress", null);
        try
        {
            var handshake = await supervisor.HandshakeChdAsync(ct);
            Check.That(handshake.IsSuccess && handshake.ProcessTreeStopped && handshake.Reply!.NativeIdentity == candidate.ExpectedIdentity,
                "verified native child handshake");
            rows.Add(new { Step = "handshake", Result = handshake });
            foreach (var input in config.Inputs)
            {
                await using var source = File.OpenRead(input.ChdPath);
                long size = source.Length;
                Check.That(size is >= 124 and <= ChdCdLimits.MaximumArtifactBytes, "bounded CD artifact input");
                await using var staged = await factory.CreateAsync(source, ct);
                Check.That(staged.FileSize == size && source.Length == size, "exact staged CD snapshot");
                MediaWorkerResult result;
                try
                { result = await supervisor.InspectChdCdAsync(staged, ct); }
                catch (MediaWorkerStoppedCancellationException) { throw; }
                catch
                {
                    staged.Quarantine();
                    throw;
                }
                if (!result.ProcessTreeStopped)
                    staged.Quarantine();
                rows.Add(new { Step = "cd", Input = input, Result = result });
                await SaveAsync("in-progress", null);
                var measured = result.Reply?.CdInspection;
                Check.That(result.IsSuccess && result.ProcessTreeStopped && measured is not null &&
                    result.Reply!.NativeIdentity == candidate.ExpectedIdentity &&
                    measured.ArtifactSha256 == staged.FileSha256.ToString() && measured.Header.ArtifactBytes == size,
                    "successful CD measurement under joined custody");
                if (input.ExpectedTracks is { } expected)
                {
                    Check.That(expected.Length == measured!.Tracks.Count, "expected complete track count");
                    for (int index = 0; index < expected.Length; index++)
                    {
                        var actual = measured.Tracks[index];
                        var oracle = expected[index];
                        Check.That(actual.Number == oracle.Number && actual.BinBytes == oracle.BinBytes &&
                            actual.Sha1 == oracle.Sha1 && actual.Md5 == oracle.Md5 && actual.Crc32 == oracle.Crc32,
                            "track size and three digests match independent supplied oracle");
                    }
                }
                source.Position = 0;
                string after = Convert.ToHexStringLower(await SHA256.HashDataAsync(source, ct));
                Check.That(source.Length == size && after == measured!.ArtifactSha256, "original input remains unchanged");
                Console.WriteLine(Path.GetFileName(input.ChdPath) + ": " + measured!.Tracks.Count + " verified track projection(s)");
            }
            Check.That(!Directory.EnumerateFiles(Path.Combine(config.DataDirectory, "temp"), "run-*.json", SearchOption.AllDirectories).Any(),
                "joined CD children release supervised custody");
            await SaveAsync("passed", null);
        }
        catch (Exception error)
        {
            await SaveAsync("failed", error.ToString());
            throw;
        }

        async Task SaveAsync(string status, string? error)
        {
            report.Position = 0;
            report.SetLength(0);
            await JsonSerializer.SerializeAsync(report, new
            {
                Status = status,
                RecordedUtc = DateTimeOffset.UtcNow,
                RuntimeInformation.OSDescription,
                RuntimeInformation.ProcessArchitecture,
                RuntimeInformation.FrameworkDescription,
                ConfigurationSha256 = Convert.ToHexStringLower(SHA256.HashData(configBytes)),
                config.SourceDescription,
                Native = candidate.ExpectedIdentity,
                Limits = limits,
                Rows = rows,
                Error = error,
                Scope = "CD container integrity and named BIN track projections only. No original CUE/file layout, PS1 identity, import, encoding, storage, delivery or emulator qualification."
            }, JsonOptions.Value, CancellationToken.None);
            await report.FlushAsync(CancellationToken.None);
        }
    }

    internal static void ValidateConfiguration(Configuration config)
    {
        Check.That(config.SourceDescription is { Length: > 0 and <= 4096 } && !config.SourceDescription.Any(char.IsControl),
            "bounded oracle provenance description");
        Check.That(config.Inputs is { Length: > 0 and <= 16 }, "bounded CD probe inputs");
        foreach (var input in config.Inputs)
        {
            Check.That(input is not null && input.ChdPath is not null, "explicit CD input path");
            if (input!.ExpectedTracks is not { } tracks)
                continue;
            Check.That(tracks.Length is > 0 and <= ChdCdLimits.MaximumTracks, "bounded expected CD tracks");
            for (int index = 0; index < tracks.Length; index++)
            {
                var track = tracks[index];
                Check.That(track is not null && track.Number == index + 1 && track.BinBytes is > 0 and <= ChdCdLimits.MaximumLogicalBytes &&
                    track.BinBytes % 2352 == 0 && Hex(track.Sha1, 40, nonzero: true) && Hex(track.Md5, 32, nonzero: true) &&
                    Hex(track.Crc32, 8, nonzero: false), "complete ordered track oracle");
            }
        }
    }

    private static bool Hex(string? text, int length, bool nonzero) => text?.Length == length &&
        text.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f') && (!nonzero || text.Any(c => c != '0'));
}
