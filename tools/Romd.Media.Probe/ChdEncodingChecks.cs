using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.IO.Abstractions;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Win32.SafeHandles;
using NSubstitute;
using Romd.Admin.Application.Source.Rom;
using Romd.Admin.Application.Storage.Files;
using Romd.Application.Common.Configuration;
using Romd.Domain.Hashing;
using Romd.Domain.Source.Rom;
using Romd.Infrastructure.Storage;
using Romd.Media;
using Romd.Media.Chd;
using Romd.Media.Inspection;
using Romd.Media.PlayStation;
using Romd.Media.Processes;
using Romd.Storage;

namespace Romd.Media.Probe;

/// <summary>Closed Linux DVD recipe probe with fresh-child inspection; no publication or eligibility claim.</summary>
internal static class ChdEncodingChecks
{
    private sealed record Configuration(string Mode, string SourceIso, string? OriginalHostPath, Digests ExpectedSource,
        string ToolDirectory, string NativeDirectory, string WorkerPath, string DataDirectory, string? RecipeId = null,
        long? MaxArtifactBytes = null, string? Dat = null, string? DatSha256 = null, string? DatEntryName = null);
    private sealed record SourceSnapshot(Digests Hashes, DateTime LastWriteUtc);

    public static Task RunAsync(string[] args, CancellationToken ct) => RunAsync(args, ct, ProbeFixtures.FromEnvironment);

    internal static async Task RunAsync(string[] args, CancellationToken ct, Func<string> fixtures)
    {
        if (args.Length != 3)
            throw new ArgumentException("Usage: --chd-encoding-probe <absolute configuration.json> <absolute new report.json>");
        Check.That(OperatingSystem.IsLinux(), "genuine CHD encoding probe uses the Linux route");
        MediaProbePaths.Validate([args[1], args[2]]);
        byte[] configBytes = BoundedRuntimeFile.Read(args[1], 16384);
        using var document = JsonDocument.Parse(configBytes, new JsonDocumentOptions { MaxDepth = 4 });
        Check.That(MediaRunRecord.Unique(document.RootElement), "unique encoding probe configuration fields");
        var config = JsonSerializer.Deserialize<Configuration>(configBytes, new JsonSerializerOptions
        { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, RespectRequiredConstructorParameters = true })
            ?? throw new InvalidDataException("Missing encoding probe configuration.");
        bool canonical = SelectRecipe(config.RecipeId);
        if (config.ExpectedSource is null)
            throw new InvalidDataException("Missing independent source facts.");
        Check.That(config.Mode is "synthetic" or "ffx" or "gt4" or "psp",
            "closed probe mode with explicit independent source facts");
        bool psp = config.Mode == "psp";
        bool real = config.Mode is "ffx" or "gt4" or "psp";
        string? fixtureRoot = config.Mode is "ffx" or "gt4" ? fixtures() : null;
        Check.That(config.Mode == "ffx" ? config.OriginalHostPath == ProbeFixtures.FinalFantasyX(fixtureRoot!) &&
                config.ExpectedSource.Size == 4508221440 : config.Mode == "gt4" ?
                config.OriginalHostPath == ProbeFixtures.GranTurismo4(fixtureRoot!) &&
                config.ExpectedSource.Size == 5314478080 : psp ? config.OriginalHostPath is null &&
                config.ExpectedSource.Size is >= 36864 and <= PspUmdIdentifier.MaximumLogicalBytes : config.OriginalHostPath is null &&
                config.ExpectedSource.Size is (16L << 20) or (64L << 20), "only the authorized original or bounded synthetic source");
        Check.That(Hash(config.ExpectedSource.Sha256, 64) && Hash(config.ExpectedSource.Sha1, 40) &&
            Hash(config.ExpectedSource.Md5, 32) && Hash(config.ExpectedSource.Crc, 8, allowZero: true), "strict source digest tuple");
        Check.That(psp ? config.Dat is not null && Hash(config.DatSha256, 64) && !string.IsNullOrWhiteSpace(config.DatEntryName)
            : config.Dat is null && config.DatSha256 is null && config.DatEntryName is null,
            "PSP requires an explicit independently pinned DAT and unique game entry");
        // Each build directory supplies its own record; verification happens in every supervised child.
        var tool = ChdmanCandidate.FromBuildRecord(config.ToolDirectory);
        var native = ChdNativeCandidate.FromBuildRecord(config.NativeDirectory);
        ChdmanToolIdentity toolIdentity = tool.ExpectedIdentity;
        ChdNativeIdentity nativeIdentity = native.ExpectedIdentity;
        Check.That(toolIdentity.Rid == nativeIdentity.Rid && toolIdentity.Rid == NativeRuntimeIdentifier.Current, "paired candidates built for this Linux RID");
        long inputCap = canonical ? ChdDvdLimits.MaximumLogicalBytes : real ? 8L << 30 : config.ExpectedSource.Size;
        Check.That(canonical || config.MaxArtifactBytes is null, "configured artifact maximum belongs only to canonical opt-in");
        long configuredArtifactMaximum = config.MaxArtifactBytes ?? (canonical ? ChdDvdLimits.MaximumArtifactBytes : real ? 8L << 30 : checked(inputCap * 2));
        long outputCap = OutputMaximum(canonical, config.ExpectedSource.Size, inputCap, configuredArtifactMaximum, ct);
        var limits = new MediaProcessingOptions
        {
            MaxConcurrent = 1,
            WorkerPath = config.WorkerPath,
            MaxArtifactBytes = canonical ? configuredArtifactMaximum : Math.Max(inputCap, outputCap),
            MaxLogicalBytes = inputCap,
            WallTimeoutSeconds = psp ? 600 : real ? 3600 : 30,
            CpuTimeoutSeconds = psp ? 600 : real ? 3600 : 30,
            ChdEncodeWallTimeoutSeconds = psp ? 1200 : real ? 3600 : 30,
            ChdEncodeCpuTimeoutSeconds = psp ? 1200 : real ? 3600 : 30
        };
        string[] protectedPaths = [args[1], config.SourceIso, tool.BinaryPath, tool.ManifestPath,
            native.BinaryPath, native.ManifestPath, config.WorkerPath,
            .. config.Dat is { } dat ? new[] { dat } : Array.Empty<string>(),
            Path.GetFullPath(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar)), Path.GetDirectoryName(limits.DotnetPath)!];
        MediaProbePaths.Validate([.. protectedPaths, args[2], config.DataDirectory]);
        Check.That(!MediaProbePaths.Overlaps(args[2], config.DataDirectory) &&
            !protectedPaths.Any(path => MediaProbePaths.Overlaps(path, args[2]) || MediaProbePaths.Overlaps(path, config.DataDirectory)),
            "protected inputs are isolated from report and writable data paths");
        MediaProbePaths.OutsideManagedStorage(config.DataDirectory, args[1], args[2]);
        Check.That(!Directory.Exists(config.DataDirectory) && !File.Exists(config.DataDirectory), "fresh owned encoding storage");
        var options = Substitute.For<IRomdOptions>();
        options.DataDirectory.Returns(config.DataDirectory);
        options.MaxUploadBytes.Returns(Math.Max(config.ExpectedSource.Size, outputCap));
        var stagingLimits = new TempStorageOptions
        {
            MaximumFiles = 2,
            MaximumBytes = checked(config.ExpectedSource.Size + outputCap),
            MinimumFreeBytes = 1L << 30,
            CopyTimeoutSeconds = real ? 3600 : 30
        };
        var runtime = new MediaProcessingRuntime(options, limits);
        var factory = new LocalTempFileFactory(options, new FileSystem(), stagingLimits);
        var supervisor = new MediaWorkerSupervisor(limits, runtime, native);
        await using var report = new FileStream(args[2], FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        var rows = new List<object>();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (canonical)
            deadline.CancelAfter(TimeSpan.FromSeconds(25200));
        ct = deadline.Token;
        string? retainedPath = null;
        Digests? retainedHashes = null;
        ChdHeaderFacts? retainedHeader = null;
        byte[]? retainedRawHeader = null;
        SourceSnapshot? before = null, after = null;
        await SaveAsync("in-progress", null, ct);
        try
        {
            before = await SourceAsync(config.SourceIso, inputCap, ct);
            Check.That(before.Hashes == config.ExpectedSource, "fresh original four digests and length match the explicit oracle");
            if (psp)
            {
                byte[] datBytes = BoundedRuntimeFile.Read(config.Dat!, 8 << 20);
                Check.That(Convert.ToHexStringLower(SHA256.HashData(datBytes)) == config.DatSha256,
                    "independent PSP catalog digest");
                ChdRecipeChecks.ValidateDat(datBytes, config.DatEntryName!, before.Hashes);
            }
            if (canonical)
            {
                PreflightSpace(config.DataDirectory, checked(2 * before.Hashes.Size), "iso-cas-baseline", rows);
                await IsoBaselineAsync(config.SourceIso, config.DataDirectory, before.Hashes, rows,
                    token => SaveAsync("in-progress", null, token), ct);
                PreflightSpace(config.DataDirectory, checked(before.Hashes.Size + 2 * outputCap), "encoding-and-retention", rows);
            }
            Guid job = Guid.NewGuid();
            var reservation = await factory.ReserveAggregateAsync(new(job, "chd-encoding-probe", before.Hashes.Size, outputCap), ct);
            Check.That(!reservation.IsError, "source plus full output cap reserved together before staging");
            await using (var aggregate = reservation.Value)
            {
                var destination = aggregate.Output ?? throw new InvalidDataException("Missing reserved CHD output.");
                using var handle = NativeFileLease.OpenRegularRead(config.SourceIso);
                await using var sourceStream = new FileStream(handle, FileAccess.Read);
                var materialized = await aggregate.MaterializeSourceAsync(sourceStream, Sha256.Parse(before.Hashes.Sha256), before.Hashes.Size, ct);
                Check.That(!materialized.IsError, "staged source matches independent original bytes and SHA256");
                ITempFile source = materialized.Value;
                await using (var staged = source.OpenRead())
                    Check.That(await Digests.StreamAsync(staged, ct, source.FileSize) == before.Hashes, "all staged source digests match original");
                PspUmdObservedFacts? originalPsp = null;
                if (psp)
                {
                    MediaWorkerResult original;
                    using (var admitted = await runtime.AcquireAsync(job, "inspect-iso", ct))
                    using (admitted.Activate())
                    {
                        admitted.TrackAggregate((IMediaStagingAggregate)aggregate);
                        original = await supervisor.InspectIsoAsync(source, ct);
                        Record("psp-original-inspection", original);
                        Check.That(admitted.ProcessesStopped, "PSP original inspector stopped");
                    }
                    originalPsp = original.Reply?.Inspection?.Psp;
                    Check.That(original.IsSuccess && originalPsp is not null && original.Reply!.Inspection!.LogicalSha256 == before.Hashes.Sha256,
                        "independent original child recognizes PSP game layout and complete source identity");
                    CheckReleased(source.Path, destination.Path);
                }
                var wrong = new ChdmanCandidate(config.ToolDirectory,
                    toolIdentity with { BinarySha256 = toolIdentity.BinarySha256 == new string('1', 64) ? new string('2', 64) : new string('1', 64) });
                var rejected = await EncodeAsync("wrong-tool-identity", wrong);
                var rejection = rejected.Reply?.ChdEncodingRejection;
                Check.That(!rejected.IsSuccess && rejected.Failure == MediaWorkerFailure.Malformed && rejected.Reply?.Status == MediaWorkerStatus.Malformed &&
                    rejection is
                    {
                        Stage: ChdDvdEncodingStage.ToolVerification, Cause: ChdDvdEncodingCause.CandidateRejected,
                        CandidateRejection: { Stage: ChdRejectionStage.NativeVerification, Cause: "ManifestIdentityMismatch" }
                    } &&
                    rejection.Path == tool.ManifestPath && rejection.CandidateRejection.Path == tool.ManifestPath &&
                    rejected.Reply.ToolIdentity is null && rejected.Reply.ToolExecution is null && rejected.Reply.ChdEncoding is null &&
                    !File.Exists(destination.Path), "wrong tool pin rejects before tool launch or artifact creation");
                var encoded = await EncodeAsync("restored-tool-encode", tool);
                if (!encoded.IsSuccess || encoded.Reply is not
                    {
                        Status: MediaWorkerStatus.Encoded,
                        ChdEncoding: { } artifact, ToolExecution: { } toolRun
                    } encodedReply)
                    throw new InvalidDataException("Restored genuine tool did not produce raw CHD metadata and joined execution facts.");
                Check.That(encodedReply.ToolIdentity == toolIdentity && encodedReply.Inspection is null && encodedReply.ChdInspection is null &&
                    artifact.SourceBytes == before.Hashes.Size && artifact.LogicalBytes == before.Hashes.Size && artifact.SourceSha256 == before.Hashes.Sha256 &&
                    artifact.RecipeVersion == ChdDvdCanonicalRecipe.Id && artifact.EncodingProfile == ChdDvdCanonicalRecipe.EncoderProfile &&
                    artifact.PriorityKind == "unix-nice" && artifact.PriorityValue is >= 10 and <= 19 &&
                    toolRun.ExitCode == 0 && toolRun.ProcessId > 0 && toolRun.ProcessId != encoded.Execution!.ProcessId &&
                    toolRun.StandardOutputBytes >= 0 && toolRun.StandardErrorBytes >= 0 &&
                    toolRun.StandardOutputBytes + toolRun.StandardErrorBytes <= (256L << 10), "raw requested recipe encoding facts and joined tool metadata");
                var raw = await SourceAsync(destination.Path, outputCap, ct);
                Check.That(raw.Hashes.Size == artifact.ArtifactBytes && raw.Hashes.Sha256 == artifact.ArtifactSha256 &&
                    raw.Hashes.Md5 == artifact.ArtifactMd5 && raw.Hashes.Sha1 == artifact.ArtifactSha1, "three artifact digests and size measured independently");
                byte[] header = new byte[124];
                using (var rawHandle = NativeFileLease.OpenRegularRead(destination.Path))
                await using (var rawInput = new FileStream(rawHandle, FileAccess.Read))
                    await rawInput.ReadExactlyAsync(header, ct);
                var preflight = ChdHeaderPreflight.Inspect(header, raw.Hashes.Size,
                    new(outputCap, (ulong)inputCap, ChdDvdCanonicalRecipe.HunkBytes, uint.MaxValue));
                Check.That(preflight is ChdHeaderPreflightResult.Accepted accepted && accepted.Facts == artifact.Header,
                    "independently read bounded raw header matches encoded metadata");
                var sealedOutput = await destination.SealAsync(new(Sha256.Parse(raw.Hashes.Sha256), Md5.Parse(raw.Hashes.Md5),
                    Sha1.Parse(raw.Hashes.Sha1), raw.Hashes.Size), ct);
                Check.That(!sealedOutput.IsError, "output sealed only after joined encoding with full raw measurement");
                await using (var artifactFile = sealedOutput.Value)
                {
                    var inspected = await InspectChdSourceAsync(artifactFile);
                    if (!inspected.IsSuccess || inspected.Reply is not
                        {
                            Status: MediaWorkerStatus.Inspected,
                            Inspection: { } proof, ChdInspection: { } integrity
                        } inspectedReply)
                        throw new InvalidDataException("Fresh output child did not produce complete CHD inspection facts.");
                    Check.That(inspectedReply.NativeIdentity == nativeIdentity && proof.ArtifactBytes == raw.Hashes.Size && proof.ArtifactSha256 == raw.Hashes.Sha256 &&
                        proof.LogicalBytes == before.Hashes.Size && proof.LogicalSha256 == before.Hashes.Sha256 && proof.Sha1 == before.Hashes.Sha1 &&
                        proof.Md5 == before.Hashes.Md5 && proof.Crc32 == before.Hashes.Crc && proof.DiscKind is null && proof.EncodingProfile == "chd-import/v1",
                        "fresh child compares all logical digests and length to independent original");
                    Check.That(integrity.Version == 5 && integrity.Header == artifact.Header && integrity.Header.LogicalBytes == (ulong)before.Hashes.Size &&
                        ChdDvdCanonicalRecipe.Matches(integrity.Header) &&
                        integrity.StoredRawSha1 == before.Hashes.Sha1 && integrity.ComputedRawSha1 == before.Hashes.Sha1 &&
                        integrity.StoredOverallSha1 == integrity.ComputedOverallSha1 && Hash(integrity.StoredOverallSha1, 40),
                        "actual header and both stored/computed CHD hashes agree after decode");
                    if (psp)
                        Check.That(proof.Psp == originalPsp, "fresh CHD decoding independently reproduces the original PSP layout observations");
                    string observedProfile = ObservedProfile(integrity.Version, integrity.Header, integrity.DvdMetadata);
                    var observedClass = ChdEncodingRecipe.Classify(observedProfile);
                    Check.That(observedClass == RomRepresentationProfileClass.Canonical,
                        "complete independently observed header and DVD metadata classify canonical, independently of recipe label");
                    int[] pids = [rejected.Execution!.ProcessId, encoded.Execution!.ProcessId, toolRun.ProcessId, inspected.Execution!.ProcessId];
                    Check.That(pids.All(pid => pid > 0 && pid != Environment.ProcessId) && pids.Distinct().Count() == pids.Length,
                        "negative worker, encoder, genuine tool and inspector have distinct PIDs");
                    rows.Add(new
                    {
                        Step = "separate-child-proof",
                        RawArtifact = raw.Hashes,
                        Header = integrity.Header,
                        integrity.StoredRawSha1,
                        integrity.ComputedRawSha1,
                        integrity.StoredOverallSha1,
                        integrity.ComputedOverallSha1,
                        ObservedProfile = observedProfile,
                        ObservedProfileClass = observedClass.ToString(),
                        ProcessIds = pids,
                        EncodedAndInspectedTreesStopped = encoded.ProcessTreeStopped && inspected.ProcessTreeStopped
                    });
                    if (canonical)
                    {
                        using var retainDeadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        retainDeadline.CancelAfter(TimeSpan.FromSeconds(1800));
                        long started = Stopwatch.GetTimestamp();
                        retainedPath = await EncodingChecks.RetainArtifactAsync(artifactFile,
                            Path.Combine(config.DataDirectory, "retained"), raw.Hashes, ".chd", retainDeadline.Token);
                        var retained = await SourceAsync(retainedPath, outputCap, retainDeadline.Token);
                        Check.That(retained.Hashes == raw.Hashes, "retained CHD remeasured with all raw hashes before staging disposal");
                        retainedHashes = retained.Hashes;
                        retainedHeader = integrity.Header;
                        retainedRawHeader = header;
                        rows.Add(new
                        {
                            Step = "retained-chd",
                            Path = retainedPath,
                            RawArtifact = retainedHashes,
                            RetentionCopyAndRemeasureSeconds = Stopwatch.GetElapsedTime(started).TotalSeconds
                        });
                        await SaveAsync("in-progress", null, ct);
                    }
                }

                async Task<MediaWorkerResult> EncodeAsync(string step, ChdmanCandidate candidate)
                {
                    MediaWorkerResult result;
                    using (var admitted = await runtime.AcquireAsync(job, "encode-chd", ct))
                    using (admitted.Activate())
                    {
                        admitted.TrackAggregate((IMediaStagingAggregate)aggregate);
                        long started = Stopwatch.GetTimestamp();
                        result = await supervisor.EncodeChdAsync(source, destination, candidate, ct);
                        rows.Add(new
                        {
                            Step = step + "-timing",
                            EncodeOperationSeconds = Stopwatch.GetElapsedTime(started).TotalSeconds,
                            Scope = "Supervisor operation: worker launch, tool/native pinning, encode, artifact hashing and join; not tool CPU or encode-only time."
                        });
                        Record(step, result);
                        Check.That(admitted.ProcessesStopped, "actual encoding reservation has stopped proof");
                    }
                    CheckReleased(source.Path, destination.Path);
                    await SaveAsync("in-progress", null, ct);
                    return result;
                }

                async Task<MediaWorkerResult> InspectChdSourceAsync(ITempFile sealedArtifact)
                {
                    MediaWorkerResult result;
                    using (var admitted = await runtime.AcquireAsync(job, "inspect-chd", ct))
                    using (admitted.Activate())
                    {
                        admitted.TrackAggregate((IMediaStagingAggregate)aggregate);
                        long started = Stopwatch.GetTimestamp();
                        result = await supervisor.InspectChdAsync(sealedArtifact, ct);
                        rows.Add(new
                        {
                            Step = "fresh-artifact-inspection-timing",
                            InspectionOperationSeconds = Stopwatch.GetElapsedTime(started).TotalSeconds,
                            Scope = "Supervisor operation: fresh worker launch, native pinning, full inspection/hashing and join; not decode-only time."
                        });
                        Record("fresh-artifact-inspection", result);
                        Check.That(admitted.ProcessesStopped, "actual inspection reservation has stopped proof");
                    }
                    CheckReleased(source.Path, sealedArtifact.Path);
                    return result;
                }
            }
            if (canonical)
            {
                Check.That(retainedPath is not null && retainedHashes is not null && retainedHeader is not null && retainedRawHeader is not null,
                    "canonical read campaign requires independently retained CHD facts");
                Check.That(!Directory.EnumerateFiles(Path.Combine(config.DataDirectory, "temp"), "slot-*.data", SearchOption.AllDirectories).Any(),
                    "source and encoded staging payloads disposed before raw CAS read campaign");
                PreflightSpace(config.DataDirectory, outputCap, "raw-cas-after-staging-disposal", rows);
                await ReadCampaignAsync(config.SourceIso, retainedPath!, config.DataDirectory, before.Hashes, retainedHashes!,
                    retainedHeader!, retainedRawHeader!, native, rows, token => SaveAsync("in-progress", null, token), ct);
            }
            after = await SourceAsync(config.SourceIso, inputCap, ct);
            Check.That(after == before && after.Hashes == config.ExpectedSource, "fresh final original identity and four digests unchanged");
            using var accounting = JsonDocument.Parse(BoundedRuntimeFile.Read(Path.Combine(config.DataDirectory, "temp", "staging-v1", "reservations.json"), 32768));
            Check.That(accounting.RootElement.GetProperty("Reservations").EnumerateObject().Count() == 0, "all staging reservations released after actual stopped children");
            await SaveAsync("passed", null, ct);
        }
        catch (Exception error)
        {
            await SupervisorChecks.PreserveFailureAsync(error, token => SaveAsync("failed", error.ToString(), token), CancellationToken.None);
        }

        void Record(string step, MediaWorkerResult result)
        {
            rows.Add(new { Step = step, Result = result });
            Check.That(result.ProcessTreeStopped && result.Execution is { ProcessId: > 0, ExitCode: 0 }, "actual fresh worker joined: " + step);
            Console.WriteLine(step + ": " + (result.Failure?.ToString() ?? result.Reply?.Status.ToString()));
        }
        void CheckReleased(string source, string output)
        {
            Check.That(!File.Exists(source + ".quarantine") && !File.Exists(output + ".quarantine") &&
                !Directory.EnumerateFiles(Path.Combine(config.DataDirectory, "temp"), "run-*.json", SearchOption.AllDirectories).Any(),
                "joined operation releases source/output custody and capacity before next sibling");
        }
        async Task SaveAsync(string status, string? error, CancellationToken token)
        {
            report.Position = 0;
            report.SetLength(0);
            await JsonSerializer.SerializeAsync(report, new
            {
                Status = status,
                RecordedUtc = DateTimeOffset.UtcNow,
                config.Mode,
                config.SourceIso,
                config.OriginalHostPath,
                ConfigurationSha256 = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(configBytes)),
                ExpectedSource = config.ExpectedSource,
                config.Dat,
                config.DatSha256,
                config.DatEntryName,
                SourceBefore = before,
                SourceAfter = after,
                RecipeId = ChdDvdCanonicalRecipe.Id,
                RetainedArtifactPath = retainedPath,
                RetainedArtifactHashes = retainedHashes,
                PhaseBudgetSeconds = canonical ? new
                {
                    Total = 25200,
                    IsoCasBaseline = 1800,
                    WorkerEncode = limits.WallTimeoutSeconds,
                    FreshChildInspection = limits.WallTimeoutSeconds,
                    RetentionAndRemeasure = 1800,
                    RawCasAndReadCampaign = 3600
                } : null,
                Tool = toolIdentity,
                Native = nativeIdentity,
                ProtectedInputPaths = protectedPaths,
                RuntimeInformation.OSDescription,
                RuntimeInformation.ProcessArchitecture,
                RuntimeInformation.FrameworkDescription,
                Limits = limits,
                InputMaximumBytes = inputCap,
                OutputMaximumBytes = outputCap,
                ConfiguredArtifactMaximumBytes = configuredArtifactMaximum,
                StagingLimits = stagingLimits,
                Rows = rows,
                Error = error,
                Scope = "Closed DVD recipe encode/seal/fresh-child inspection; opt-in canonical route adds isolated ISO CAS baseline, retained CHD and direct raw CAS pool/cache reads. Timings include named operation/hash work. Original identity is observed before/after, not an inode or hostile-remount guarantee. Root must bind readonly inputs with no writable aliases. No normal-worker ingestion, PS2 eligibility, serving/VFS/HTTP/playback, native allocation attribution, publication, release or gate acceptance."
            }, JsonOptions.Value, token);
            await report.FlushAsync(token);
        }
    }

    /// <summary>Every run encodes the one canonical recipe. Omitting the ID keeps the bounded probe; naming it opts
    /// into the full canonical campaign.</summary>
    internal static bool SelectRecipe(string? recipe)
    {
        Check.That(recipe is null or ChdDvdCanonicalRecipe.Id,
            "closed requested CHD recipe before report, storage, reservation or worker effects");
        return recipe is not null;
    }

    internal static long OutputMaximum(bool canonical, long sourceBytes, long inputMaximum, long artifactMaximum,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        Check.That(artifactMaximum is >= 4 and <= (16L << 30), "bounded configured artifact maximum before capacity");
        if (!canonical)
            return artifactMaximum;
        long maximum = ChdDvdOutputBounds.GetMaximumOutputBytes(sourceBytes, inputMaximum, ct);
        Check.That(maximum > 0 && maximum <= artifactMaximum, "canonical derived output bound fits configured artifact maximum before capacity");
        return maximum;
    }

    internal static string ObservedProfile(uint version, ChdHeaderFacts header, ChdDvdMetadataObservation? metadata)
    {
        ArgumentNullException.ThrowIfNull(header);
        Check.That(metadata is not null, "independent complete DVD metadata required for observed profile");
        var observed = metadata!;
        return FormattableString.Invariant($"chd-observed/v1;version={version};logical={header.LogicalBytes};hunk={header.HunkBytes};unit={header.UnitBytes};count={header.HunkCount};codecs={header.Codec0:x8},{header.Codec1:x8},{header.Codec2:x8},{header.Codec3:x8};parent=none;metadata={observed.Profile}:{observed.EntryCount}:{observed.Tag:x8}:{observed.Flags}:{observed.PayloadBytes}:{observed.PayloadByte}:{observed.NormalizedSha256}");
    }

    private static void PreflightSpace(string directory, long requiredAdditionalBytes, string phase, List<object> rows)
    {
        const long safetyFloor = 1L << 30;
        var volume = DriveInfo.GetDrives().Where(drive => MediaProbePaths.Contains(drive.RootDirectory.FullName, directory))
            .MaxBy(drive => drive.RootDirectory.FullName.Length)
            ?? throw new IOException("No attributable storage volume for qualification preflight.");
        long free = volume.AvailableFreeSpace;
        rows.Add(new
        {
            Step = "space-preflight",
            Phase = phase,
            Volume = volume.RootDirectory.FullName,
            AvailableFreeBytes = free,
            RequiredAdditionalBytes = requiredAdditionalBytes,
            SafetyFloorBytes = safetyFloor
        });
        Check.That(requiredAdditionalBytes > 0 && free >= checked(requiredAdditionalBytes + safetyFloor),
            "actual free space covers the next closed phase plus its safety floor: " + phase);
    }

    private static async Task IsoBaselineAsync(string sourcePath, string dataDirectory, Digests expected,
        List<object> rows, Func<CancellationToken, Task> save, CancellationToken ct)
    {
        string root = Path.Combine(dataDirectory, "iso-baseline-cas");
        Check.That(!Directory.Exists(root) && !File.Exists(root), "fresh owned ISO baseline CAS");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(1800));
        ct = deadline.Token;
        var services = new ServiceCollection();
        services.AddContentAddressableStorage(options =>
        {
            options.RootPath = root;
            options.WritePolicy = ContentWritePolicy.SeekableZstandard;
            options.CompressionLevel = 3;
            options.SeekableFrameSize = 256 << 10;
            options.MinCompressionRatio = .95;
            options.MaxContentSize = 64L << 30;
        });
        await using (var provider = services.BuildServiceProvider())
        {
            var cas = provider.GetRequiredService<IContentAddressableStore>();
            var key = StorageKey.FromHash(Sha256.Parse(expected.Sha256));
            Check.That(await cas.GetInfoAsync(key, ct) is null, "no prior ISO baseline object can be claimed as owned");
            long started = Stopwatch.GetTimestamp();
            StoreResult stored;
            using (var handle = NativeFileLease.OpenRegularRead(sourcePath))
            await using (var source = new FileStream(handle, FileAccess.Read))
                stored = await cas.StoreAsync(source, ct: ct);
            var info = await cas.GetInfoAsync(key, ct);
            rows.Add(new
            {
                Step = "iso-cas-baseline-published",
                Root = root,
                CasKey = stored.Key.ToString(),
                ActualFormat = info?.Format.ToString(),
                Store = stored,
                Info = info,
                StoreOperationSeconds = Stopwatch.GetElapsedTime(started).TotalSeconds,
                WritePolicy = "SeekableZstandard",
                CompressionLevel = 3,
                SeekableFrameBytes = 256 << 10,
                MinimumCompressionRatio = .95,
                MaximumContentBytes = 64L << 30
            });
            await save(ct);
            Check.That(stored.Key == key && stored.Size == expected.Size && !stored.WasDeduplicated &&
                info is { } measured && measured.Key == key && measured.StoredSize == stored.StoredSize &&
                measured.IsCompressed == stored.IsCompressed && measured.Format ==
                    (stored.IsCompressed ? ContentStorageFormat.SeekableZstandard : ContentStorageFormat.Raw),
                "new ISO baseline records actual physical size and format, including raw fallback");
            started = Stopwatch.GetTimestamp();
            Check.That(await cas.VerifyAsync(key, ct), "whole ISO baseline CAS hash verified");
            await using (var decoded = await cas.RetrieveAsync(key, ct) ?? throw new IOException("Baseline missing after publication."))
                Check.That(await Digests.StreamAsync(decoded, ct, expected.Size) == expected,
                    "stored ISO baseline fully matches the independent logical quartet");
            rows.Add(new
            {
                Step = "iso-cas-baseline-verified",
                Logical = expected,
                VerificationAndFullHashSeconds = Stopwatch.GetElapsedTime(started).TotalSeconds
            });
            await save(ct);
            // Delete only this newly owned, successfully verified object. On an earlier failure its body and receipts remain.
            Check.That(await cas.DeleteAsync(key, ct) && await cas.GetInfoAsync(key, ct) is null,
                "owned successful ISO baseline object deleted before encoding");
            Check.That(await cas.CleanupTemporaryFilesAsync(ct) == 0, "successful ISO baseline has no abandoned staged payloads");
            rows.Add(new { Step = "iso-cas-baseline-owned-object-removed", Root = root, Key = key.ToString() });
            await save(ct);
        }
        Directory.Delete(root, recursive: true);
    }

    internal readonly record struct ReadRange(long Offset, int Length, string Kind);

    internal static ReadRange[] ReadRanges(long length)
    {
        Check.That(length >= 2048 && length <= ChdDvdLimits.MaximumLogicalBytes, "bounded logical read campaign length");
        var ranges = new List<ReadRange>(512);
        var random = new Random(123456);
        for (int i = 0; i < 500; i++)
        {
            int bytes = i < 250 ? 4096 : 65536;
            ranges.Add(new(random.NextInt64(0, length), bytes, i < 250 ? "random-4KiB" : "random-64KiB"));
        }
        ranges.AddRange([new(0, 1, "first-byte"), new(16383, 1, "hunk-last-byte"),
            new(16383, 4096, "hunk-crossing"), new(length - 1, 1, "final-byte"),
            new(length - 1, 65536, "truncated-final"), new(length, 1, "eof"), new(length + 1, 65536, "beyond-eof")]);
        if (length > (1L << 32))
            ranges.AddRange([new((1L << 32) - 1, 1, "4GiB-minus-one"), new((1L << 32) - 1, 65536, "4GiB-crossing"),
                new(1L << 32, 4096, "4GiB"), new((1L << 32) + 1, 65536, "above-4GiB")]);
        return ranges.ToArray();
    }

    internal static void AssertRange(int received, ReadOnlySpan<byte> actual, ReadOnlySpan<byte> expected)
    {
        Check.That(received == expected.Length && actual.Length >= received, "direct CHD exact range length");
        Check.That(actual[..received].SequenceEqual(expected), "direct CHD range bytes equal independent original");
    }

    internal sealed class RawCounters
    {
        private long _calls, _bytes, _opened, _disposed;
        internal void Opened() => Interlocked.Increment(ref _opened);
        internal void Disposed() => Interlocked.Increment(ref _disposed);
        internal void Called() => Interlocked.Increment(ref _calls);
        internal void Read(int count) => Interlocked.Add(ref _bytes, count);
        internal RawSnapshot Snapshot() => new(Interlocked.Read(ref _calls), Interlocked.Read(ref _bytes),
            Interlocked.Read(ref _opened), Interlocked.Read(ref _disposed));
    }
    internal sealed record RawSnapshot(long Calls, long Bytes, long SourcesOpened, long SourcesDisposed);

    internal sealed class CountingRawContent : ISynchronousRawContent
    {
        private readonly ISynchronousRawContent _source;
        private readonly RawCounters _counters;
        private int _disposed;
        internal CountingRawContent(ISynchronousRawContent source, RawCounters counters)
        { _source = source; _counters = counters; counters.Opened(); }
        public long Length => _source.Length;
        public int ReadAt(long offset, Span<byte> destination)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            _counters.Called();
            int count = _source.ReadAt(offset, destination);
            Check.That(count >= 0 && count <= destination.Length, "raw callback returned a bounded actual count");
            _counters.Read(count);
            return count;
        }
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;
            _source.Dispose();
            _counters.Disposed();
        }
    }

    private static async Task ReadCampaignAsync(string originalPath, string retainedPath, string dataDirectory,
        Digests logical, Digests raw, ChdHeaderFacts header, byte[] rawHeader, ChdNativeCandidate native,
        List<object> rows, Func<CancellationToken, Task> save, CancellationToken ct)
    {
        string root = Path.Combine(dataDirectory, "read-cas");
        Check.That(!Directory.Exists(root) && !File.Exists(root), "fresh owned raw read CAS");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(3600));
        ct = deadline.Token;
        var services = new ServiceCollection();
        services.AddContentAddressableStorage(options => { options.RootPath = root; options.MaxContentSize = 64L << 30; });
        await using (var provider = services.BuildServiceProvider())
        {
            var cas = provider.GetRequiredService<IContentAddressableStore>();
            var key = StorageKey.FromHash(Sha256.Parse(raw.Sha256));
            Check.That(await cas.GetInfoAsync(key, ct) is null, "no prior read artifact can be claimed as owned");
            using (var handle = NativeFileLease.OpenRegularRead(retainedPath))
            await using (var input = new FileStream(handle, FileAccess.Read))
            await using (var staged = await cas.StageRawAsync(input, key, ct: ct))
            {
                var stored = await staged.PublishAsync(ct);
                var info = await cas.GetInfoAsync(key, ct);
                rows.Add(new
                {
                    Step = "raw-read-cas-published",
                    Root = root,
                    CasKey = stored.Key.ToString(),
                    ActualFormat = info?.Format.ToString(),
                    Store = stored,
                    Info = info
                });
                await save(ct);
                Check.That(stored.Key == key && stored.Size == raw.Size && stored.StoredSize == raw.Size &&
                    !stored.IsCompressed && !stored.WasDeduplicated && info is { Format: ContentStorageFormat.Raw } measured &&
                    measured.StoredSize == raw.Size, "explicit raw CAS publication matches the retained artifact identity and length");
            }
            Check.That(await cas.VerifyAsync(key, ct), "raw read CAS complete artifact SHA256 verified before native open");
            await using (var input = await cas.RetrieveAsync(key, ct) ?? throw new IOException("Raw read CAS artifact missing."))
                Check.That(await Digests.StreamAsync(input, ct, raw.Size) == raw, "raw read CAS full quartet equals retained artifact");
            var cache = new MediaBlockCache(64L << 20);
            await using var pool = new ChdReadPool(cache, maxConcurrent: 4, maxIdle: 2);
            var counters = new RawCounters();
            var resources = new Dictionary<string, ResourcePeaks>(StringComparer.Ordinal);
            object sampleGate = new();
            string phase = "before-native-load";
            void Sample()
            {
                using var process = Process.GetCurrentProcess();
                process.Refresh();
                var cgroup = ReadCgroup();
                var observed = new ReadResources(process.WorkingSet64, process.PeakWorkingSet64,
                    GC.GetGCMemoryInfo().HeapSizeBytes, GC.GetTotalAllocatedBytes(), cache.UsedBytes, pool.Snapshot,
                    counters.Snapshot(), cgroup.Current, cgroup.Peak, cgroup.Maximum);
                Check.That(observed.CacheUsedBytes <= cache.BudgetBytes && observed.Pool.LiveHandles <= 4 &&
                    observed.Pool.ActiveHandles <= 4 && observed.Pool.IdleHandles <= 2,
                    "actual shared read cache and handle pool stay within configured bounds");
                lock (sampleGate)
                {
                    string name = Volatile.Read(ref phase);
                    if (!resources.TryGetValue(name, out var peaks))
                        resources.Add(name, peaks = new());
                    peaks.Add(observed);
                }
            }
            Sample();
            rows.Add(new
            {
                Step = "direct-read-resource-attempt",
                Root = root,
                Scope = "Native load/open/reads use this process, observed separately from prior supervised children; root must bind the actual image and complete managed assembly graph."
            });
            await save(ct);
            using var sampling = new CancellationTokenSource();
            async Task ObserveAsync()
            {
                try
                {
                    while (true)
                    { await Task.Delay(250, sampling.Token); Sample(); }
                }
                catch (OperationCanceledException) when (sampling.IsCancellationRequested) { }
            }
            Task observation = ObserveAsync();
            try
            {
                Volatile.Write(ref phase, "native-verification-and-load");
                Sample();
                long loadStarted = Stopwatch.GetTimestamp();
                var resident = native.VerifyAndLoad(ct);
                double nativeVerificationAndLoadSeconds = Stopwatch.GetElapsedTime(loadStarted).TotalSeconds;
                Check.That(resident == native.ExpectedIdentity, "direct read process verifies and loads the exact image native candidate");
                Sample();
                Volatile.Write(ref phase, "cold-open");
                Sample();
                var limits = new ChdHeaderPreflightLimits(raw.Size, (ulong)logical.Size, header.HunkBytes, header.HunkCount);
                async ValueTask<ChdBlockDevice> OpenAsync()
                {
                    var source = cas.OpenSynchronousRaw(key) ?? throw new IOException("Owned raw read CAS source missing.");
                    return await ChdBlockDevice.OpenAsync(new CountingRawContent(source, counters), pool, limits,
                        key.ToString(), admissionTimeout: TimeSpan.FromSeconds(30), ct: ct);
                }
                long opened = Stopwatch.GetTimestamp();
                await using (var anchor = await OpenAsync())
                {
                    double coldOpenSeconds = Stopwatch.GetElapsedTime(opened).TotalSeconds;
                    Check.That(anchor.Length == logical.Size, "direct device observed logical length equals original");
                    ulong mapOffset = BinaryPrimitives.ReadUInt64BigEndian(rawHeader.AsSpan(40, 8));
                    Check.That(mapOffset >= 124 && mapOffset <= (ulong)(raw.Size - 16), "bounded compressed map header location");
                    byte[] mapHeader = new byte[16];
                    using (var mapSource = cas.OpenSynchronousRaw(key) ?? throw new IOException("Raw map snapshot missing."))
                        Check.That(mapSource.ReadAt((long)mapOffset, mapHeader) == 16, "complete compressed map header read");
                    uint mapPayloadBytes = BinaryPrimitives.ReadUInt32BigEndian(mapHeader);
                    Check.That(mapPayloadBytes > 0 && mapPayloadBytes <= (ulong)raw.Size - mapOffset - 16,
                        "compressed map payload fits actual artifact");
                    rows.Add(new
                    {
                        Step = "direct-read-native-open",
                        Native = resident,
                        Header = header,
                        MediaAssemblyMvid = typeof(ChdBlockDevice).Module.ModuleVersionId,
                        ManagedBindingAssemblyMvid = typeof(Moonlark.Libchdr.ChdFile).Module.ModuleVersionId,
                        ProbeAssemblyMvid = typeof(ChdEncodingChecks).Module.ModuleVersionId,
                        RawSourceCounterScope = "All positional raw-source reads through the owned device/pool source, including managed fixed-header preflight and native callbacks. The diagnostic map-header read is separate. Counts are not native-only calls or allocation measurements.",
                        NativeVerificationAndLoadSeconds = nativeVerificationAndLoadSeconds,
                        ColdOpenSeconds = coldOpenSeconds,
                        ColdOpenScope = "First native handle in a fresh pool/cache, after separately measured native file/manifest verification and library loading. OS caches are warm/uncontrolled.",
                        CompressedMapHeaderBytes = 16,
                        CompressedMapPayloadBytes = mapPayloadBytes,
                        SourceDerivedExpandedMapEntryBytes = checked(12L * header.HunkCount),
                        MapByteScope = "12*hunkCount is source-derived map-entry geometry, not an allocated-byte measurement or total native map/codec memory."
                    });
                    Sample();
                    await save(ct);
                    using var oracle = NativeFileLease.OpenRegularRead(originalPath);
                    var ranges = ReadRanges(logical.Size);
                    var rangeRows = new List<RangeObservation>(ranges.Length);
                    rows.Add(new
                    {
                        Step = "direct-random-boundary-read-attempt",
                        FixedSeed = 123456,
                        PlannedRandomPairs = 500,
                        PlannedBoundaryPairs = ranges.Length - 500,
                        Ranges = rangeRows,
                        TimingScope = "Separate open/read timers exclude independent oracle reads, assertions and disposal. First/repeat names are request order, not disk-cold/cache-miss claims. One shared 64MiB cache and 4/2 pool, anchor held; OS caches warm/uncontrolled."
                    });
                    await save(ct);
                    byte[] expected = new byte[65536], actual = new byte[65536];
                    Volatile.Write(ref phase, "first-decode");
                    await ReadOracleAsync(oracle, 0, expected.AsMemory(0, 1), ct);
                    AssertRange(await anchor.ReadAtAsync(0, actual.AsMemory(0, 1), ct), actual, expected.AsSpan(0, 1));
                    Sample();
                    Volatile.Write(ref phase, "random-and-boundary-pairs");
                    foreach (var range in ranges)
                    {
                        ct.ThrowIfCancellationRequested();
                        int wanted = range.Offset >= logical.Size ? 0 : (int)Math.Min(range.Length, logical.Size - range.Offset);
                        await ReadOracleAsync(oracle, range.Offset, expected.AsMemory(0, wanted), ct);
                        var observations = new List<ReadObservation>(2);
                        for (int pass = 0; pass < 2; pass++)
                        {
                            var before = counters.Snapshot();
                            long started = Stopwatch.GetTimestamp();
                            await using var device = await OpenAsync();
                            double openMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                            started = Stopwatch.GetTimestamp();
                            int received = await device.ReadAtAsync(range.Offset, actual.AsMemory(0, range.Length), ct);
                            double readMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                            AssertRange(received, actual, expected.AsSpan(0, wanted));
                            await device.DisposeAsync();
                            var after = counters.Snapshot();
                            observations.Add(new(pass == 0 ? "first" : "immediate-repeat", openMs, readMs,
                                received, after.Calls - before.Calls, after.Bytes - before.Bytes));
                        }
                        rangeRows.Add(new(range.Offset, range.Length, range.Kind, wanted, observations));
                        if (rangeRows.Count % 50 == 0)
                            await save(ct);
                    }
                    Sample();
                    rows.Add(new
                    {
                        Step = "direct-random-boundary-reads",
                        FixedSeed = 123456,
                        RandomPairs = 500,
                        BoundaryPairs = ranges.Length - 500,
                        ByteComparisons = ranges.Length * 2 + 1,
                        Workloads = rangeRows.GroupBy(range => range.Kind).Select(group => new
                        {
                            Kind = group.Key,
                            Pairs = group.Count(),
                            FirstOpen = TimingSummary(group.Select(range => range.Passes[0].OpenMilliseconds).ToArray()),
                            RepeatOpen = TimingSummary(group.Select(range => range.Passes[1].OpenMilliseconds).ToArray()),
                            FirstRead = TimingSummary(group.Select(range => range.Passes[0].ReadMilliseconds).ToArray()),
                            RepeatRead = TimingSummary(group.Select(range => range.Passes[1].ReadMilliseconds).ToArray())
                        }).ToArray()
                    });
                    await save(ct);
                    foreach (int bytes in new[] { 4 << 20, 1 << 20 })
                    {
                        Volatile.Write(ref phase, bytes == 4 << 20 ? "4MiB-positional-full-pass" : "1MiB-positional-full-pass");
                        var before = counters.Snapshot();
                        long started = Stopwatch.GetTimestamp();
                        await using var device = await OpenAsync();
                        double openSeconds = Stopwatch.GetElapsedTime(started).TotalSeconds;
                        started = Stopwatch.GetTimestamp();
                        var full = await FullPassAsync(device, bytes, logical.Size, ct);
                        double seconds = Stopwatch.GetElapsedTime(started).TotalSeconds;
                        Check.That(full == logical, "full positional CHD pass matches complete original quartet and length");
                        await device.DisposeAsync();
                        var after = counters.Snapshot();
                        Sample();
                        rows.Add(new
                        {
                            Step = "direct-positional-full-pass",
                            RequestBytes = bytes,
                            OpenSeconds = openSeconds,
                            FullReadAndHashSeconds = seconds,
                            LogicalMiBPerSecond = logical.Size / (double)(1 << 20) / seconds,
                            Digests = full,
                            RawCallbackCalls = after.Calls - before.Calls,
                            RawCallbackBytes = after.Bytes - before.Bytes,
                            Scope = "Direct device positional reads plus streaming hashes; no decoded ISO output, VFS/rclone/HTTP or serving qualification."
                        });
                        await save(ct);
                    }
                }
            }
            catch (Exception error)
            {
                rows.Add(new { Step = "direct-read-campaign-failed", Error = error.ToString(), RawCallbacks = counters.Snapshot() });
                throw;
            }
            finally
            {
                sampling.Cancel();
                try
                { await observation; }
                finally
                {
                    await pool.DisposeAsync();
                    cache.Evict("chd:artifact:" + key);
                    Volatile.Write(ref phase, "after-pool-disposal");
                    Sample();
                    lock (sampleGate)
                        rows.Add(new
                        {
                            Step = "direct-read-resource-observations",
                            Phases = resources,
                            Scope = "250ms process/cgroup aggregate samples and explicit checkpoints. Managed heap/allocation/cache and actual pool handles are distinct. Process lifetime/cgroup kernel peaks include prior phases; native map/codec allocation attribution remains unqualified."
                        });
                }
            }
            var closed = pool.Snapshot;
            var rawCounters = counters.Snapshot();
            Check.That(closed.LiveHandles == 0 && closed.ActiveHandles == 0 && closed.IdleHandles == 0 &&
                closed.Flights == 0 && closed.FlightWaiters == 0 && closed.Artifacts == 0 &&
                closed.OpenedHandles > 0 && closed.OpenedHandles == closed.ClosedHandles && cache.UsedBytes == 0 &&
                rawCounters.SourcesOpened > 0 && rawCounters.SourcesOpened == rawCounters.SourcesDisposed,
                "all actual native handles, raw sources, flights and owned cached blocks close before successful raw CAS removal");
            rows.Add(new { Step = "direct-read-closure", Pool = closed, RawCallbacks = rawCounters, CacheUsedBytes = cache.UsedBytes });
            await save(ct);
            Check.That(await cas.DeleteAsync(key, ct) && await cas.GetInfoAsync(key, ct) is null,
                "only the successful owned raw CAS artifact removed; retained CHD remains");
            Check.That(await cas.CleanupTemporaryFilesAsync(ct) == 0, "successful read CAS has no abandoned staged payloads");
        }
        Directory.Delete(root, recursive: true);
        rows.Add(new { Step = "direct-read-owned-cas-removed", Root = root, RetainedArtifactPath = retainedPath });
        await save(ct);
    }

    private sealed record ReadObservation(string Pass, double OpenMilliseconds, double ReadMilliseconds,
        int ReceivedBytes, long RawCallbackCalls, long RawCallbackBytes);
    private sealed record RangeObservation(long Offset, int Length, string Kind, int ExpectedBytes,
        IReadOnlyList<ReadObservation> Passes);

    private static object TimingSummary(double[] values)
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
            MaxMs = sorted[^1]
        };
    }

    private static async Task ReadOracleAsync(SafeFileHandle source, long offset, Memory<byte> destination, CancellationToken ct)
    {
        int read = 0;
        while (read < destination.Length)
        {
            int count = await RandomAccess.ReadAsync(source, destination[read..], offset + read, ct);
            Check.That(count > 0, "independent original range is complete");
            read += count;
        }
    }

    private static async Task<Digests> FullPassAsync(IRandomAccessContent device, int requestBytes, long expectedBytes, CancellationToken ct)
    {
        byte[] buffer = new byte[requestBytes];
        using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        using var sha1 = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        using var sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var crc = new System.IO.Hashing.Crc32();
        long offset = 0;
        while (offset < expectedBytes)
        {
            int wanted = (int)Math.Min(requestBytes, expectedBytes - offset);
            int read = await device.ReadAtAsync(offset, buffer.AsMemory(0, wanted), ct);
            Check.That(read == wanted, "full direct CHD pass returns every requested byte, including shortened final request");
            crc.Append(buffer.AsSpan(0, read));
            md5.AppendData(buffer, 0, read);
            sha1.AppendData(buffer, 0, read);
            sha256.AppendData(buffer, 0, read);
            offset += read;
        }
        Check.That(await device.ReadAtAsync(expectedBytes, buffer.AsMemory(0, 1), ct) == 0, "full direct CHD pass exact EOF");
        return new(offset, crc.GetCurrentHashAsUInt32().ToString("x8", CultureInfo.InvariantCulture),
            Convert.ToHexStringLower(md5.GetHashAndReset()), Convert.ToHexStringLower(sha1.GetHashAndReset()),
            Convert.ToHexStringLower(sha256.GetHashAndReset()));
    }

    private sealed record ReadResources(long ProcessWorkingSetBytes, long ProcessLifetimePeakWorkingSetBytes,
        long ManagedHeapBytes, long ManagedTotalAllocatedBytes, long CacheUsedBytes, ChdReadPoolSnapshot Pool,
        RawSnapshot RawCallbacks, long? CgroupCurrentBytes, long? CgroupKernelPeakBytes, string? CgroupMaximum);

    private sealed class ResourcePeaks
    {
        public int Samples { get; private set; }
        public ReadResources? First { get; private set; }
        public ReadResources? Last { get; private set; }
        public long PeakSampledProcessWorkingSetBytes { get; private set; }
        public long PeakSampledManagedHeapBytes { get; private set; }
        public long PeakSampledCacheUsedBytes { get; private set; }
        public long? PeakSampledCgroupCurrentBytes { get; private set; }
        internal void Add(ReadResources value)
        {
            Samples++;
            First ??= value;
            Last = value;
            PeakSampledProcessWorkingSetBytes = Math.Max(PeakSampledProcessWorkingSetBytes, value.ProcessWorkingSetBytes);
            PeakSampledManagedHeapBytes = Math.Max(PeakSampledManagedHeapBytes, value.ManagedHeapBytes);
            PeakSampledCacheUsedBytes = Math.Max(PeakSampledCacheUsedBytes, value.CacheUsedBytes);
            if (value.CgroupCurrentBytes is { } current)
                PeakSampledCgroupCurrentBytes = Math.Max(PeakSampledCgroupCurrentBytes ?? 0, current);
        }
    }

    private static (long? Current, long? Peak, string? Maximum) ReadCgroup()
    {
        const string root = "/sys/fs/cgroup";
        if (!File.Exists(Path.Combine(root, "memory.current")))
            return (null, null, null);
        string[] unified = System.Text.Encoding.ASCII.GetString(ReadResourceChecks.ReadBounded("/proc/self/cgroup", 4096))
            .Split('\n', StringSplitOptions.RemoveEmptyEntries).Where(line => line.StartsWith("0::", StringComparison.Ordinal)).ToArray();
        if (unified.Length != 1 || unified[0] != "0::/")
            return (null, null, null);
        string Read(string name) => System.Text.Encoding.ASCII.GetString(ReadResourceChecks.ReadBounded(Path.Combine(root, name), 64)).Trim();
        long current = long.Parse(Read("memory.current"), CultureInfo.InvariantCulture);
        long peak = long.Parse(Read("memory.peak"), CultureInfo.InvariantCulture);
        string maximum = Read("memory.max");
        Check.That(current >= 0 && peak >= 0 && (maximum == "max" ||
            long.TryParse(maximum, NumberStyles.None, CultureInfo.InvariantCulture, out long bound) && bound >= 0),
            "bounded cgroup aggregate memory facts");
        return (current, peak, maximum);
    }

    private static async Task<SourceSnapshot> SourceAsync(string path, long maximumBytes, CancellationToken ct)
    {
        DateTime before = File.GetLastWriteTimeUtc(path);
        using var handle = NativeFileLease.OpenRegularRead(path);
        await using var input = new FileStream(handle, FileAccess.Read);
        long length = input.Length;
        Check.That(length > 0 && length <= maximumBytes, "bounded regular-file digest input");
        var digests = await Digests.StreamAsync(input, ct, length);
        Check.That(digests.Size == length && input.Length == length && new FileInfo(path).Length == length &&
            File.GetLastWriteTimeUtc(path) == before, "source length and timestamp stable during bounded digest pass");
        return new(digests, before);
    }
    private static bool Hash(string? value, int length, bool allowZero = false) => value?.Length == length &&
        value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f') && (allowZero || value.Any(c => c != '0'));
}
