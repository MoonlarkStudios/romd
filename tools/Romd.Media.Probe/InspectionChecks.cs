using System.Diagnostics;
using System.IO.Abstractions;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Xml.Linq;
using NSubstitute;
using Romd.Application.Common.Configuration;
using Romd.Infrastructure.Hashing;
using Romd.Infrastructure.Storage;

namespace Romd.Media.Probe;

/// <summary>Qualifies fresh inspection children against independent ISO and DAT oracles.</summary>
internal static class InspectionChecks
{
    public static async Task RunAsync(string[] args, CancellationToken ct)
    {
        if (args.Length != 3 || !Path.IsPathFullyQualified(args[1]) || !Path.IsPathFullyQualified(args[2]))
            throw new ArgumentException("Usage: --inspection-probe or --iso-inspection-probe <absolute fixtures.json> <absolute report.json>");
        bool isoInput = args[0] == "--iso-inspection-probe";
        var fixtures = JsonSerializer.Deserialize<Fixture[]>(await File.ReadAllTextAsync(args[1], ct))
            ?? throw new InvalidDataException("Missing inspection fixtures.");
        Check.That(fixtures.Length > 0, "inspection fixtures provided");
        await using var reportOutput = new FileStream(args[2], FileMode.CreateNew, FileAccess.Write, FileShare.Read,
            bufferSize: 4096, FileOptions.Asynchronous);
        var options = new MediaProcessingOptions();
        string directory = Path.Combine(Path.GetTempPath(), "romd-inspection-probe-" + Guid.NewGuid().ToString("N"));
        var stagingOptions = Substitute.For<IRomdOptions>();
        stagingOptions.DataDirectory.Returns(directory);
        stagingOptions.MaxUploadBytes.Returns(16L << 30);
        var runtime = new MediaProcessingRuntime(stagingOptions, options);
        var supervisor = new MediaWorkerSupervisor(options, runtime);
        var handshake = await supervisor.HandshakeAsync(ct);
        Check.That(handshake.IsSuccess, "packaged child startup validation");
        using var verifier = new RvzVerifier(options, supervisor, runtime);
        var preparation = ProbeMediaFacts.ManagedPreparation(new HashingService(), verifier, new ProbeMediaFacts.EmptyReferences());
        var staging = new LocalTempFileFactory(stagingOptions, new FileSystem());
        var reports = new List<object>();
        await SaveAsync(reportOutput, "in-progress", options, handshake.Reply!.Identity, reports, directory, null, ct);
        try
        {
            foreach (var fixture in fixtures)
            {
                Check.That(new[] { fixture.UploadedRvz, fixture.Iso, fixture.Dat }.All(Path.IsPathFullyQualified), "absolute fixture paths");
                Console.WriteLine($"{fixture.Name}: independent ISO and artifact hashes, supervised inspection");
                var oracle = await Digests.FileAsync(fixture.Iso, ct);
                var artifact = isoInput ? oracle : await Digests.FileAsync(fixture.UploadedRvz, ct);
                string inputPath = isoInput ? fixture.Iso : fixture.UploadedRvz;
                var dat = XDocument.Load(fixture.Dat);
                var expected = dat.Descendants("game").Single(game => (string?)game.Attribute("id") == fixture.DatId).Element("rom")!;
                Check.That(oracle.Size == (long)expected.Attribute("size")!, "DAT size");
                Check.That(oracle.Crc == (string?)expected.Attribute("crc") && oracle.Md5 == (string?)expected.Attribute("md5") &&
                    oracle.Sha1 == (string?)expected.Attribute("sha1"), "independent ISO matches DAT");
                Romd.Admin.Application.Source.Rom.IRomPreparationAdmission? admission = null;
                long began = Stopwatch.GetTimestamp();
                try
                {
                    await using var input = File.OpenRead(inputPath);
                    await using var source = await staging.CreateAsync(input, ct,
                        magic => preparation.RequiresDatHashes(magic, inputPath),
                        async (magic, token) => admission = await preparation.AdmitAsync(magic, inputPath, token),
                        selectPrefixLength: magic => RomPreparationDetection.SelectPrefixLength(magic, inputPath));
                    Check.That(admission is not null, "media admission precedes remaining staging copy");
                    var result = await admission!.PrepareAsync(source, inputPath, ct);
                    admission.Dispose();
                    admission = null;
                    Check.That(!result.IsError, "fresh child inspection succeeds");
                    await using var prepared = (Romd.Admin.Application.Source.Rom.PreparedRom.WholeFile)result.Value;
                    var proof = prepared.Verification;
                    Check.That(source.FileSha256.ToString() == artifact.Sha256 && source.FileSize == artifact.Size, "staged artifact exact identity");
                    Check.That(proof.ArtifactSha256.ToString() == artifact.Sha256 && proof.ArtifactSize == artifact.Size, "child artifact exact identity");
                    Check.That(proof.ContentSha256.ToString() == oracle.Sha256 && proof.ContentSize == oracle.Size &&
                        proof.Sha1.ToString() == oracle.Sha1 && proof.Md5.ToString() == oracle.Md5 && proof.Crc32.ToString() == oracle.Crc,
                        "all reconstructed hashes and length match independent ISO");
                    Check.That(proof.Profile == (isoInput ? RvzVerifier.IsoProfile : RvzVerifier.Profile) && proof.DecoderBuild == handshake.Reply.Identity.DecoderBuild &&
                        proof.VerifierBuild == handshake.Reply.Identity.WorkerBuild, "new evidence profile and exact packaged builds");
                    if (isoInput)
                    {
                        var eligibility = prepared.Eligibility;
                        Check.That(eligibility is { Status: Romd.Domain.Source.Rom.RomIsoEligibilityStatus.Supported } &&
                            eligibility.ContentSha256 == proof.ContentSha256 && eligibility.ContentSize == proof.ContentSize &&
                            eligibility.Adapter == "nintendo-iso" && eligibility.Profile == Romd.Media.Nintendo.NintendoIsoInspector.ProfileVersion &&
                            eligibility.InspectorBuild == proof.DecoderBuild, "ISO eligibility binds exact measured identity and inspector");
                        Check.That(eligibility!.DiscKind?.ToString().ToLowerInvariant() == fixture.NintendoKind &&
                            eligibility.StandardKeyPartitions == fixture.StandardKeyPartitions &&
                            eligibility.KoreanKeyPartitions == fixture.KoreanKeyPartitions, "independently specified disc kind and ticket index counts");
                    }
                    reports.Add(new
                    {
                        fixture.Name,
                        fixture.UploadedRvz,
                        fixture.Iso,
                        fixture.Dat,
                        fixture.DatId,
                        InputFormat = isoInput ? "iso" : "rvz",
                        Eligibility = prepared.Eligibility is { } observed ? new
                        {
                            observed.Id,
                            ContentSha256 = observed.ContentSha256.ToString(),
                            observed.ContentSize,
                            Status = observed.Status.ToString(),
                            DiscKind = observed.DiscKind?.ToString(),
                            observed.Adapter,
                            observed.Profile,
                            observed.InspectorBuild,
                            observed.WiiPartitionCount,
                            observed.StandardKeyPartitions,
                            observed.KoreanKeyPartitions,
                            observed.InspectedAt
                        } : null,
                        Artifact = artifact,
                        Original = oracle,
                        InspectionAndStagingSeconds = Stopwatch.GetElapsedTime(began).TotalSeconds,
                        prepared.EncodingProfile,
                        proof.Profile,
                        proof.DecoderBuild,
                        proof.VerifierBuild,
                        BoundedStaging = true,
                        PreCopyAdmission = true
                    });
                    await SaveAsync(reportOutput, "in-progress", options, handshake.Reply.Identity, reports, directory, null, ct);
                }
                finally { admission?.Dispose(); }
            }
            await SaveAsync(reportOutput, "passed", options, handshake.Reply.Identity, reports, directory, null, ct);
        }
        catch (Exception exception)
        {
            await SupervisorChecks.PreserveFailureAsync(exception, token => SaveAsync(reportOutput, "failed", options,
                handshake.Reply.Identity, reports, directory, exception.ToString(), token), CancellationToken.None);
        }
        finally { CleanupStaging(directory); }
    }

    internal static bool CleanupStaging(string directory)
    {
        if (!Directory.Exists(directory))
            return true;
        try
        {
            if (Directory.EnumerateFileSystemEntries(directory, "*.quarantine", SearchOption.AllDirectories).Any() ||
                Directory.EnumerateFiles(directory, "run-*.json*", SearchOption.AllDirectories).Any())
            {
                Console.WriteLine("Preserved inspection staging with child custody: " + directory);
                return false;
            }
            Directory.Delete(directory, recursive: true);
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Console.WriteLine("Preserved inspection staging after " + error.GetType().Name + ": " + directory);
            return false;
        }
    }

    private static async Task SaveAsync(FileStream output, string status, MediaProcessingOptions options,
        Romd.Media.Inspection.MediaWorkerIdentity identity, List<object> reports, string directory, string? error, CancellationToken ct)
    {
        output.Position = 0;
        output.SetLength(0);
        await JsonSerializer.SerializeAsync(output, new
        {
            Status = status,
            RecordedUtc = DateTimeOffset.UtcNow,
            Environment = new { RuntimeInformation.OSDescription, RuntimeInformation.ProcessArchitecture, Runtime = RuntimeInformation.FrameworkDescription },
            Limits = options,
            Startup = identity,
            Reports = reports,
            StagingDirectory = directory,
            Error = error,
            Scope = "Supervised inspection and staging with independent ISO/DAT oracles; no database ingestion, encoding, delivery or allocation-enforcement claim."
        }, JsonOptions.Value, ct);
        await output.FlushAsync(ct);
    }
}
