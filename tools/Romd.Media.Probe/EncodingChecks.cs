using System.Diagnostics;
using System.IO.Abstractions;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Xml.Linq;
using NSubstitute;
using Romd.Admin.Application.Source.Rom;
using Romd.Admin.Application.Storage.Files;
using Romd.Application.Common.Configuration;
using Romd.Domain.Source.Rom;
using Romd.Infrastructure.Hashing;
using Romd.Infrastructure.Storage;
using Romd.Media.Inspection;
using Romd.Media.Rvz;

namespace Romd.Media.Probe;

/// <summary>Qualifies supervised encoding independently of publication and convergence.</summary>
internal static class EncodingChecks
{
    public static async Task RunAsync(string[] args, CancellationToken ct)
    {
        if (args.Length != 3 || !Path.IsPathFullyQualified(args[1]) || !Path.IsPathFullyQualified(args[2]))
            throw new ArgumentException("Usage: --encoding-probe <absolute configuration.json> <absolute report.json>");
        var configuration = JsonSerializer.Deserialize<Configuration>(await File.ReadAllTextAsync(args[1], ct))
            ?? throw new InvalidDataException("Missing encoding configuration.");
        await using var report = new FileStream(args[2], FileMode.CreateNew, FileAccess.Write, FileShare.Read,
            bufferSize: 4096, FileOptions.Asynchronous);
        bool capacityOnly = IsCapacityOnly(configuration.Fixtures.Length, configuration.QualifyDefaultProcessingCapacity,
            configuration.RecoveryIso);
        Check.That(configuration.Repetitions is > 0 and <= 4 &&
            configuration.InputFormats.Length is > 0 and <= 2 &&
            configuration.InputFormats[0] == "original" && configuration.InputFormats.Distinct().Count() == configuration.InputFormats.Length &&
            (capacityOnly ? configuration.OracleExecutable is null && configuration.QualifiedReport is null
                : (configuration.OracleExecutable is not null) != (configuration.QualifiedReport is not null)),
            "bounded encoding fixtures and exactly one independent comparison mode");
        Dictionary<string, string>? qualifiedArtifacts = null;
        if (configuration.OracleExecutable is { } executable)
        {
            Check.That(Path.IsPathFullyQualified(executable), "absolute independent oracle path");
            await using var oracleBinary = File.OpenRead(executable);
            Check.That(Convert.ToHexStringLower(await SHA256.HashDataAsync(oracleBinary, ct)) == configuration.OracleSha256,
                "approved independent oracle identity");
        }
        else if (!capacityOnly)
        {
            Check.That(OperatingSystem.IsLinux(), "qualified-artifact comparison is for the Linux production image");
            var reference = configuration.QualifiedReport!;
            Check.That(Path.IsPathFullyQualified(reference.Path), "absolute qualified report path");
            byte[] bytes = await File.ReadAllBytesAsync(reference.Path, ct);
            Check.That(Convert.ToHexStringLower(SHA256.HashData(bytes)) == reference.Sha256, "qualified report identity");
            using var document = JsonDocument.Parse(bytes);
            Check.That(document.RootElement.GetProperty("Status").GetString() == "passed" &&
                document.RootElement.GetProperty("OracleExecutable").ValueKind == JsonValueKind.String,
                "reference independently decoded every artifact");
            qualifiedArtifacts = new(StringComparer.Ordinal);
            foreach (var row in document.RootElement.GetProperty("Reports").EnumerateArray())
            {
                string name = row.GetProperty("Name").GetString()!;
                string hash = row.GetProperty("Artifact").GetProperty("Sha256").GetString()!;
                Check.That(!qualifiedArtifacts.TryGetValue(name, out var existing) || existing == hash,
                    "reference outputs are deterministic");
                qualifiedArtifacts[name] = hash;
            }
        }
        var options = new MediaProcessingOptions();
        var productionAssemblies = new List<object>();
        if (configuration.QualifiedReport is not null || capacityOnly && OperatingSystem.IsLinux())
        {
            Check.That(Environment.ProcessId == 1 && GetEffectiveUserId() != 0, "production Linux non-root PID1 supervisor");
            foreach (var assembly in new[] { typeof(MediaWorkerSupervisor).Assembly, typeof(MediaInspectionService).Assembly,
                typeof(Romd.Storage.IContentAddressableStore).Assembly })
            {
                await using var loaded = File.OpenRead(assembly.Location);
                await using var image = File.OpenRead(Path.Combine("/app", Path.GetFileName(assembly.Location)));
                string hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(loaded, ct));
                Check.That(hash == Convert.ToHexStringLower(await SHA256.HashDataAsync(image, ct)), "untouched production image assembly");
                productionAssemblies.Add(new { Name = assembly.GetName().Name, assembly.ManifestModule.ModuleVersionId, Sha256 = hash });
            }
            await using var packaged = File.OpenRead(options.WorkerPath);
            await using var actual = File.OpenRead("/app/media-worker/Romd.Media.Worker.dll");
            byte[] packagedHash = await SHA256.HashDataAsync(packaged, ct);
            byte[] actualHash = await SHA256.HashDataAsync(actual, ct);
            Check.That(packagedHash.AsSpan().SequenceEqual(actualHash),
                "untouched production image encoding child");
        }
        string directory = Path.Combine(Path.GetTempPath(), "romd-encoding-probe-" + Guid.NewGuid().ToString("N"));
        var stagingOptions = Substitute.For<IRomdOptions>();
        stagingOptions.DataDirectory.Returns(directory);
        stagingOptions.MaxUploadBytes.Returns(16L << 30);
        var runtime = new MediaProcessingRuntime(stagingOptions, options);
        var supervisor = new CapturingSupervisor(new(options, runtime));
        var handshake = await supervisor.HandshakeAsync(ct);
        Check.That(handshake.IsSuccess, "packaged encoding child startup");
        using var verifier = new RvzVerifier(options, supervisor, runtime);
        var preparation = ProbeMediaFacts.ManagedPreparation(new HashingService(), verifier, new ProbeMediaFacts.EmptyReferences());
        var staging = new LocalTempFileFactory(stagingOptions, new FileSystem());
        var encoding = new RvzEncodingService(verifier, staging, options);
        var rows = new List<object>();
        var recovery = new List<object>();
        DefaultProcessingChecks.Evidence? defaultProcessing = null;
        await SaveAsync("in-progress", null, ct);
        try
        {
            foreach (var fixture in configuration.Fixtures)
            {
                Check.That(new[] { fixture.Iso, fixture.UploadedRvz, fixture.Dat }.All(Path.IsPathFullyQualified), "absolute fixture paths");
                var original = await Digests.FileAsync(fixture.Iso, ct);
                var entry = XDocument.Load(fixture.Dat).Descendants("game")
                    .Single(game => (string?)game.Attribute("id") == fixture.DatId).Element("rom")!;
                Check.That(original.Size == (long)entry.Attribute("size")! && original.Crc == (string?)entry.Attribute("crc") &&
                    original.Md5 == (string?)entry.Attribute("md5") && original.Sha1 == (string?)entry.Attribute("sha1"), "independent ISO matches DAT");
                string? firstOutput = null;
                RomIsoEligibility? eligibility = null;
                foreach (string format in configuration.InputFormats)
                {
                    Check.That(format is "original" or "rvz", "explicit supported source format");
                    string sourcePath = format == "original" ? fixture.Iso : fixture.UploadedRvz;
                    for (int repeat = 0; repeat < configuration.Repetitions; repeat++)
                    {
                        Console.WriteLine($"{fixture.Name}: encode {format}, repetition {repeat + 1}");
                        IRomPreparationAdmission? admission = null;
                        long started = Stopwatch.GetTimestamp();
                        try
                        {
                            await using var input = File.OpenRead(sourcePath);
                            await using var source = await staging.CreateAsync(input, ct,
                                magic => preparation.RequiresDatHashes(magic, sourcePath),
                                async (magic, token) => admission = await preparation.AdmitAsync(magic, sourcePath, token),
                                magic => RomPreparationDetection.SelectPrefixLength(magic, sourcePath));
                            Check.That(admission is not null, "source inspection admitted before staging copy");
                            var inspected = await admission!.PrepareAsync(source, sourcePath, ct);
                            admission.Dispose();
                            admission = null;
                            Check.That(!inspected.IsError, "complete source inspection");
                            await using var prepared = (Romd.Admin.Application.Source.Rom.PreparedRom.WholeFile)inspected.Value;
                            var sourceProof = prepared.Verification;
                            Check.That(sourceProof.ContentSha256.ToString() == original.Sha256 && sourceProof.ContentSize == original.Size &&
                                sourceProof.Sha1.ToString() == original.Sha1 && sourceProof.Md5.ToString() == original.Md5 &&
                                sourceProof.Crc32.ToString() == original.Crc, "measured source agrees with independent ISO");
                            if (format == "original")
                                eligibility = prepared.Eligibility;
                            Check.That(eligibility is { Status: RomIsoEligibilityStatus.Supported, DiscKind: not null } &&
                                eligibility.ContentSha256.ToString() == original.Sha256 && eligibility.ContentSize == original.Size,
                                "independent original ISO inspection establishes the requested disc kind");
                            var discKind = eligibility!.DiscKind!.Value;
                            if (fixture.NintendoKind is not null)
                                Check.That(string.Equals(discKind.ToString(), fixture.NintendoKind, StringComparison.OrdinalIgnoreCase) &&
                                    eligibility.StandardKeyPartitions == fixture.StandardKeyPartitions &&
                                    eligibility.KoreanKeyPartitions == fixture.KoreanKeyPartitions,
                                    "expected independent original ISO layout and common-key indexes");
                            var encoded = await encoding.EncodeAsync(source, original.Size, discKind, ct);
                            Check.That(!encoded.IsError, "bounded supervised encoding succeeds");
                            await using var artifact = encoded.Value.Artifact;
                            var encodeRun = supervisor.Encoding!;
                            var observations = supervisor.Observations;
                            Check.That(encodeRun.Reply is { Status: MediaWorkerStatus.Encoded, Inspection: null, IsoInspection: null, Encoding: not null },
                                "encoding returns metadata without verification evidence");
                            var priority = encodeRun.Reply!.Encoding!;
                            Check.That(OperatingSystem.IsWindows() ? priority.PriorityKind == "windows-priority" && priority.PriorityValue == 0x4000 :
                                priority.PriorityKind == "unix-nice" && priority.PriorityValue >= 10, "actual reduced encoding priority");
                            var artifactHashes = await Digests.FileAsync(artifact.Path, ct);
                            var measured = encoded.Value.Measurement;
                            Check.That(measured.Size == artifactHashes.Size && measured.Sha256.ToString() == artifactHashes.Sha256 &&
                                measured.Md5.ToString() == artifactHashes.Md5 && measured.Sha1.ToString() == artifactHashes.Sha1,
                                "encoded artifact checksums describe exact delivered bytes");
                            Check.That(encoded.Value.RecipeVersion == RvzCanonicalRecipe.Id &&
                                encoded.Value.EncoderBuild == (discKind == RomIsoDiscKind.GameCube ? RvzEncoder.EncoderBuild : RvzWiiEncoder.EncoderBuild) &&
                                !string.IsNullOrWhiteSpace(encoded.Value.EncodingProfile),
                                "canonical recipe and actual encoder build");
                            Check.That(firstOutput is null || firstOutput == artifactHashes.Sha256, "deterministic ISO/RVZ source and repeated output");
                            firstOutput ??= artifactHashes.Sha256;
                            if (qualifiedArtifacts is not null)
                                Check.That(qualifiedArtifacts.TryGetValue(fixture.Name, out var qualified) && qualified == artifactHashes.Sha256,
                                    "Linux output equals pinned independently decoded artifact");
                            var verified = await verifier.VerifyAsync(artifact, ct);
                            Check.That(!verified.IsError, "separate fresh child verifies encoded artifact");
                            var verifyRun = supervisor.Inspection!;
                            var proof = verified.Value.Verification;
                            Check.That(RomMediaPolicyComposition.CreateRegistry().Classify(verified.Value.Container, discKind) == RomRepresentationProfileClass.Canonical,
                                "independently measured output parameters match the canonical recipe");
                            Check.That(encodeRun.Execution!.ProcessId != verifyRun.Execution!.ProcessId, "encoding and verification use separate children");
                            Check.That(proof.ContentSha256.ToString() == original.Sha256 && proof.ContentSize == original.Size &&
                                proof.Sha1.ToString() == original.Sha1 && proof.Md5.ToString() == original.Md5 && proof.Crc32.ToString() == original.Crc,
                                "every reconstructed hash and length matches independent ISO");
                            IndependentDecodeResult? oracle = null;
                            bool independentMatches = false;
                            if (configuration.OracleExecutable is { } oracleExecutable)
                            {
                                await using var independent = await ReserveAsync(staging, original.Size, ct);
                                oracle = await DecodeIndependentAsync(oracleExecutable, artifact, independent, fixture.Dat, ct);
                                independentMatches = oracle.ExitCode == 0 && oracle.OutputBytes <= independent.MaximumBytes &&
                                    await Digests.FileAsync(independent.Path, ct) == original;
                            }
                            if (OperatingSystem.IsLinux())
                                Check.That(observations is { Samples: > 0, MinimumNice: >= 10, MaximumNice: <= 19 } &&
                                    observations.ProcessId == encodeRun.Execution!.ProcessId && observations.Threads.Count > 1,
                                    "actual encoding runtime threads inherit reduced priority");
                            if (OperatingSystem.IsLinux())
                                Check.That(observations!.Threads.Any(thread => thread.ThreadId != observations.ProcessId && thread.ObservedCpuTicks > 0),
                                    "sampled nonleader runtime threads consumed CPU during encoding");
                            rows.Add(new
                            {
                                fixture.Name,
                                Source = sourcePath,
                                SourceFormat = format,
                                Repetition = repeat + 1,
                                DiscKind = discKind.ToString(),
                                Eligibility = eligibility,
                                fixture.Iso,
                                fixture.Dat,
                                fixture.DatId,
                                Original = original,
                                Artifact = artifactHashes,
                                encoded.Value.RecipeVersion,
                                encoded.Value.EncoderBuild,
                                encoded.Value.EncodingProfile,
                                Encode = encodeRun,
                                Verify = verifyRun,
                                Oracle = oracle,
                                EncodingObservations = observations,
                                StagingEncodingAndVerificationSeconds = Stopwatch.GetElapsedTime(started).TotalSeconds,
                                SourcePreCopyAdmission = true,
                                SharedStagingPool = true
                            });
                            Check.That(configuration.OracleExecutable is null || independentMatches,
                                "independent nodtool exits successfully and full output matches ISO and DAT");
                            if (configuration.RetainArtifactsDirectory is { } retainedDirectory)
                                await RetainArtifactAsync(artifact, retainedDirectory, artifactHashes, ct);
                            await SaveAsync("in-progress", null, ct);
                        }
                        finally { admission?.Dispose(); }
                    }
                }
            }
            if (configuration.QualifyDefaultProcessingCapacity)
                defaultProcessing = await DefaultProcessingChecks.RunAsync(configuration.RecoveryIso
                    ?? throw new InvalidOperationException("FAILED: default processing qualification requires the tiny recovery ISO."),
                    options, runtime, staging, ct);
            if (configuration.QualifiedReport is not null)
                await CheckRecoveryAsync(configuration.Fixtures[0], ct);
            await SaveAsync("passed", null, ct);
        }
        catch (Exception error)
        {
            await SupervisorChecks.PreserveFailureAsync(error, token => SaveAsync("failed", error.ToString(), token), CancellationToken.None);
        }
        finally { InspectionChecks.CleanupStaging(directory); }

        async Task SaveAsync(string status, string? error, CancellationToken token)
        {
            report.Position = 0;
            report.SetLength(0);
            await JsonSerializer.SerializeAsync(report, new
            {
                Status = status,
                RecordedUtc = DateTimeOffset.UtcNow,
                Environment = new { RuntimeInformation.OSDescription, RuntimeInformation.ProcessArchitecture, Runtime = RuntimeInformation.FrameworkDescription },
                Limits = options,
                Startup = handshake.Reply!.Identity,
                configuration.OracleExecutable,
                configuration.OracleSha256,
                configuration.QualifiedReport,
                Reports = rows,
                RecoveryReports = recovery,
                DefaultProcessingCapacity = defaultProcessing,
                configuration.QualifyDefaultProcessingCapacity,
                ProductionAssemblies = productionAssemblies,
                StagingDirectory = directory,
                Error = error,
                Scope = capacityOnly ? DefaultProcessingChecks.Scope : configuration.QualifiedReport is null
                    ? "Supervised encoding, separate-child verification and independent nodtool/DAT comparison; no publication, convergence or delivery claim."
                    : "Linux supervised encoding and separate-child DAT verification; output equals a pinned artifact independently decoded on macOS. No Linux nodtool, publication, convergence or delivery claim."
            }, JsonOptions.Value, token);
            await report.FlushAsync(token);
        }

        async Task CheckRecoveryAsync(Fixture fixture, CancellationToken token)
        {
            string recoveryPath = configuration.RecoveryIso
                ?? throw new InvalidOperationException("FAILED: synthetic recovery ISO is required.");
            Check.That(Path.IsPathFullyQualified(recoveryPath) &&
                new FileInfo(recoveryPath).Length is > 0x80 and <= (8L << 20), "bounded absolute synthetic recovery ISO");
            var recoveryHashes = await Digests.FileAsync(recoveryPath, token);
            await using var recoveryInput = File.OpenRead(recoveryPath);
            await using var recoverySource = await staging.CreateAsync(recoveryInput, token);
            var inspectedRecovery = await verifier.InspectIsoAsync(recoverySource, token);
            Check.That(!inspectedRecovery.IsError && inspectedRecovery.Value.Eligibility is
            { Status: RomIsoEligibilityStatus.Supported, DiscKind: not null } &&
                inspectedRecovery.Value.Verification.ContentSha256.ToString() == recoveryHashes.Sha256 &&
                inspectedRecovery.Value.Verification.ContentSize == recoveryHashes.Size,
                "independent synthetic recovery ISO inspection establishes its disc kind");
            var recoveryKind = inspectedRecovery.Value.Eligibility.DiscKind!.Value;
            await using var input = File.OpenRead(fixture.Iso);
            await using var source = await staging.CreateAsync(input, token);
            var inspectedSource = await verifier.InspectIsoAsync(source, token);
            Check.That(!inspectedSource.IsError && inspectedSource.Value.Eligibility is
            { Status: RomIsoEligibilityStatus.Supported, DiscKind: not null }, "recovery source independently inspected");
            var sourceKind = inspectedSource.Value.Eligibility.DiscKind!.Value;
            var bound = (RvzCanonicalOutputBound.Success)(sourceKind == RomIsoDiscKind.GameCube
                ? RvzCanonicalBounds.GetGameCubeOutputBound(source.FileSize, options.MaxLogicalBytes, token)
                : RvzCanonicalBounds.GetWiiOutputBound(source.FileSize, options.MaxLogicalBytes, token));
            foreach (string mode in new[] { "output-bound", "caller-cancellation", "wall-deadline", "setup-started", "setup-draining" })
            {
                var limits = new MediaProcessingOptions
                {
                    WallTimeoutSeconds = mode == "wall-deadline"
                        ? sourceKind == RomIsoDiscKind.Wii ? 30 : 5
                        : options.WallTimeoutSeconds
                };
                bool setupInjected = false;
                int? setupProcessId = null;
                long setupOutputBytes = 0;
                MediaWorkerSetupPoint? setupPoint = mode switch
                {
                    "setup-started" => MediaWorkerSetupPoint.Started,
                    "setup-draining" => MediaWorkerSetupPoint.Draining,
                    _ => null
                };
                CapturingSupervisor? captured = null;
                var recoveryRuntime = new MediaProcessingRuntime(stagingOptions, limits);
                captured = new(new(limits, () => new MediaWorkerProcessLifetime(),
                    setup: async (child, reached, setupCt) =>
                    {
                        if (setupInjected || reached != setupPoint)
                            return;
                        setupProcessId = child.Id;
                        using var outputDeadline = CancellationTokenSource.CreateLinkedTokenSource(setupCt);
                        outputDeadline.CancelAfter(TimeSpan.FromSeconds(60));
                        while (ReadOutputLength(captured?.OutputPath) < 0x124)
                            await Task.Delay(25, outputDeadline.Token);
                        setupOutputBytes = ReadOutputLength(captured?.OutputPath);
                        setupInjected = true;
                        throw new IOException("probe post-start setup failure");
                    }, runtime: recoveryRuntime));
                using var capacity = new RvzVerifier(limits, captured, recoveryRuntime);
                using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
                bool outputObserved = false;
                string? outcome = null;
                long partialBytes = 0;
                if (mode == "output-bound")
                {
                    await using var bounded = await ReserveAsync(staging, 0x124, token);
                    var failed = await captured.EncodeAsync(source, bounded, source.FileSize, sourceKind, token);
                    Check.That(failed.Failure == MediaWorkerFailure.LimitExceeded && !File.Exists(bounded.Path),
                        "production child rejects an insufficient output reservation before writing");
                    outcome = "Rom.Media.LimitExceeded";
                }
                else
                {
                    var pending = new RvzEncodingService(capacity, staging, limits).EncodeAsync(source, source.FileSize, sourceKind, cancellation.Token);
                    try
                    {
                        while (!pending.IsCompleted && ReadOutputLength(captured.OutputPath) < 0x124)
                            await Task.WhenAny(pending, Task.Delay(25, token));
                        partialBytes = Math.Max(ReadOutputLength(captured.OutputPath), setupOutputBytes);
                        outputObserved = partialBytes >= 0x124;
                        if (mode == "caller-cancellation")
                        {
                            Check.That(outputObserved && !pending.IsCompleted, "actual encoding output precedes caller cancellation");
                            await cancellation.CancelAsync();
                            try
                            { await pending; }
                            catch (OperationCanceledException error)
                            {
                                Check.That(error.CancellationToken == cancellation.Token, "encoding caller cancellation identity");
                                outcome = "canceled";
                            }
                            Check.That(outcome == "canceled", "encoding cancellation remains cancellation");
                        }
                        else if (setupPoint is not null)
                        {
                            var failed = await pending;
                            outcome = failed.IsError ? failed.FirstError.Code : "success";
                            Check.That(outcome == "Rom.Media.IoFailure" && setupInjected && outputObserved &&
                                captured.Encoding?.Execution?.ProcessId == setupProcessId,
                                "real production output precedes the injected post-start setup failure");
                        }
                        else
                        {
                            var failed = await pending;
                            outcome = failed.IsError ? failed.FirstError.Code : "success";
                            Check.That(outcome == "Rom.Media.WallDeadline" && outputObserved,
                                $"encoder wrote output before its permanent wall deadline failure (outcome={outcome}; outputObserved={outputObserved}; partialBytes={partialBytes}; outputLimit={captured.MaximumOutputBytes})");
                        }
                    }
                    finally
                    {
                        await cancellation.CancelAsync();
                        try
                        { await pending; }
                        catch (OperationCanceledException) { }
                    }
                }
                int pid = captured.Encoding?.Execution?.ProcessId ?? captured.Observations?.ProcessId ?? 0;
                Check.That(pid > 1 && !Directory.Exists("/proc/" + pid) && Kill(-pid, 0) != 0 && Marshal.GetLastPInvokeError() == 3,
                    "encoding child and owned group reaped before admission reuse");
                Check.That(!File.Exists(source.Path + ".quarantine") && !File.Exists(captured.OutputPath + ".quarantine"),
                    "encoding source and output holds ended only after cleanup");
                Check.That(partialBytes <= captured.MaximumOutputBytes && !File.Exists(captured.OutputPath),
                    "owned bounded partial output removed after confirmed child cleanup");
                Check.That(mode == "output-bound" || captured.MaximumOutputBytes == bound.MaximumOutputBytes,
                    "encoding service reserved the complete canonical output bound");
                var recovered = new List<IRomPreparationAdmission>();
                var preparer = ProbeMediaFacts.ManagedPreparation(new HashingService(), capacity, new ProbeMediaFacts.EmptyReferences());
                byte[] prefix = new byte[32];
                await using (var original = source.OpenRead())
                    await original.ReadExactlyAsync(prefix, token);
                try
                {
                    for (int slot = 0; slot < limits.MaxConcurrent; slot++)
                    {
                        var admitted = await preparer.AdmitAsync(prefix, fixture.Iso, token)
                            ?? throw new InvalidOperationException("Actual Nintendo source must reserve media admission.");
                        recovered.Add(admitted);
                    }
                }
                finally { foreach (var slot in recovered) slot.Dispose(); }
                recovery.Add(new
                {
                    Mode = mode,
                    Outcome = outcome,
                    ProcessId = pid,
                    OutputObserved = outputObserved,
                    SetupCheckpoint = setupPoint?.ToString(),
                    SetupProcessId = setupProcessId,
                    SetupInjected = setupInjected,
                    PartialOutputBytesBeforeStop = partialBytes,
                    OutputLimit = captured.MaximumOutputBytes,
                    Limits = limits,
                    AdmissionPath = mode == "output-bound" ? "direct-supervisor" : "shared-encoding-service",
                    OwnedProcessGroupAbsent = true,
                    BothHoldsEnded = true,
                    PartialOutputDeleted = true,
                    AllAdmissionSlotsRecovered = true,
                    Execution = captured.Encoding,
                    Observations = captured.Observations
                });
                var samePoolSibling = await new RvzEncodingService(capacity, staging, limits)
                    .EncodeAsync(recoverySource, recoverySource.FileSize, recoveryKind, token);
                Check.That(!samePoolSibling.IsError, "successful sibling encodes through the same failed admission and supervisor");
                await using var samePoolArtifact = samePoolSibling.Value.Artifact;
                var samePoolEncode = captured.Encoding!;
                var samePoolObservations = captured.Observations;
                var siblingProof = await capacity.VerifyAsync(samePoolArtifact, token);
                Check.That(!siblingProof.IsError, "same failed pool fully verifies its encoded sibling");
                var proof = siblingProof.Value.Verification;
                Check.That(proof.ContentSize == recoveryHashes.Size && proof.ContentSha256.ToString() == recoveryHashes.Sha256 &&
                    proof.Md5.ToString() == recoveryHashes.Md5 && proof.Sha1.ToString() == recoveryHashes.Sha1 &&
                    proof.Crc32.ToString() == recoveryHashes.Crc &&
                    samePoolEncode.Execution!.ProcessId != captured.Inspection!.Execution!.ProcessId,
                    "same-pool sibling logical hashes match independent synthetic ISO in a separate child");
                recovery.Add(new
                {
                    Mode = mode + "-same-pool-sibling",
                    Limits = limits,
                    Source = configuration.RecoveryIso,
                    Original = recoveryHashes,
                    ArtifactSha256 = samePoolArtifact.FileSha256.ToString(),
                    ArtifactBytes = samePoolArtifact.FileSize,
                    Encode = samePoolEncode,
                    Verify = captured.Inspection,
                    Observations = samePoolObservations,
                    SameAdmissionAndSupervisor = true
                });
                await SaveAsync("in-progress", null, token);
            }
            var sibling = await encoding.EncodeAsync(source, source.FileSize, sourceKind, token);
            Check.That(!sibling.IsError, "production encoding sibling succeeds after all failed operations");
            await using var artifact = sibling.Value.Artifact;
            var verified = await verifier.VerifyAsync(artifact, token);
            Check.That(!verified.IsError && verified.Value.Verification.ContentSha256 == source.FileSha256 &&
                verified.Value.Verification.ContentSize == source.FileSize, "recovered encoding sibling verifies in a separate child");
            recovery.Add(new
            {
                Mode = "recovered-sibling",
                ArtifactSha256 = artifact.FileSha256.ToString(),
                ArtifactBytes = artifact.FileSize,
                Encode = supervisor.Encoding,
                Verify = supervisor.Inspection,
                Observations = supervisor.Observations
            });
        }
    }

    [DllImport("libc", EntryPoint = "geteuid")] private static extern uint GetEffectiveUserId();
    [DllImport("libc", EntryPoint = "kill", SetLastError = true)] private static extern int Kill(int pid, int signal);

    internal static bool IsCapacityOnly(int fixtureCount, bool qualifyCapacity, string? recoveryIso)
    {
        bool only = fixtureCount == 0 && qualifyCapacity;
        Check.That(fixtureCount is >= 0 and <= 8 && (fixtureCount > 0 || only),
            "empty ordinary encoding runs cannot qualify");
        Check.That(!qualifyCapacity || recoveryIso is not null && Path.IsPathFullyQualified(recoveryIso),
            "capacity qualification requires an absolute tiny recovery ISO");
        return only;
    }

    internal static long ReadOutputLength(string? path)
    {
        if (path is null)
            return 0;
        try
        { return new FileInfo(path).Length; }
        catch (FileNotFoundException) { return 0; }
        catch (DirectoryNotFoundException) { return 0; }
    }

    internal static Task<string> RetainArtifactAsync(ITempFile artifact, string directory, Digests hashes,
        CancellationToken ct = default) => RetainArtifactAsync(artifact, directory, hashes, ".rvz", ct);

    internal static async Task<string> RetainArtifactAsync(ITempFile artifact, string directory, Digests hashes,
        string extension, CancellationToken ct = default)
    {
        Check.That(extension is ".rvz" or ".chd", "closed retained media extension");
        MediaProbePaths.Validate([directory, artifact.Path]);
        Check.That(!MediaProbePaths.Overlaps(directory, artifact.Path) && hashes.Size == artifact.FileSize &&
            hashes.Sha256 == artifact.FileSha256.ToString(), "bounded qualified artifact retention identity");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, hashes.Sha256 + extension);
        bool created = false, complete = false;
        try
        {
            if (!File.Exists(path))
            {
                await using var source = artifact.OpenRead();
                await using var destination = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                    131072, FileOptions.Asynchronous);
                created = true;
                byte[] buffer = new byte[131072];
                long copied = 0;
                while (copied < artifact.FileSize)
                {
                    int read = await source.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, artifact.FileSize - copied)), ct);
                    Check.That(read > 0, "qualified artifact retention source is complete");
                    await destination.WriteAsync(buffer.AsMemory(0, read), ct);
                    copied += read;
                }
                Check.That(await source.ReadAsync(buffer.AsMemory(0, 1), ct) == 0, "qualified artifact retention source has exact length");
            }
            Check.That(new FileInfo(path).Length == hashes.Size, "retained qualified artifact has exact bounded length before opening");
            await using var retained = File.OpenRead(path);
            Check.That(retained.Length == hashes.Size, "retained qualified artifact has exact opened length");
            Check.That(await Digests.StreamAsync(retained, ct, hashes.Size) == hashes && retained.Length == hashes.Size,
                "retained qualified artifact matches all raw hashes within its exact byte bound");
            complete = true;
            return path;
        }
        finally { if (created && !complete) File.Delete(path); }
    }

    private static async Task<IWritableTempFile> ReserveAsync(ITempFileFactory files, long bytes, CancellationToken ct)
    {
        var result = await files.ReserveOutputAsync(bytes, ct);
        Check.That(!result.IsError, "independent decode output reservation");
        return result.Value;
    }

    private static async Task<IndependentDecodeResult> DecodeIndependentAsync(string executable, ITempFile source, IWritableTempFile output,
        string dat, CancellationToken ct)
    {
        bool sourceHeld = source.TryBeginChildUse(), outputHeld = false, stopped = true;
        string alias = output.Path + ".iso";
        bool aliasCreated = false;
        try
        {
            outputHeld = output.TryBeginChildUse();
            Check.That(sourceHeld && outputHeld, "independent oracle source/output custody");
            // nodtool selects its output format by extension. The alias writes
            // the same reserved payload; it allocates no second disc copy.
            File.CreateSymbolicLink(alias, output.Path);
            aliasCreated = true;
            var start = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            string[] arguments = ["convert", "--md5", "-d", dat, source.Path, alias];
            foreach (string argument in arguments)
                start.ArgumentList.Add(argument);
            using var process = new Process { StartInfo = start };
            Check.That(process.Start(), "independent oracle starts");
            stopped = false;
            var stdout = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
            var stderr = process.StandardError.ReadToEndAsync(CancellationToken.None);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromMinutes(5));
            try
            { await process.WaitForExitAsync(deadline.Token); }
            finally
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
                await Task.WhenAll(stdout, stderr);
                stopped = true;
            }
            return new(executable, arguments, process.Id, process.ExitCode, await stdout, await stderr,
                output.MaximumBytes, File.Exists(output.Path) ? new FileInfo(output.Path).Length : null);
        }
        finally
        {
            if (!stopped)
            {
                if (outputHeld)
                    output.Quarantine();
                if (sourceHeld)
                    source.Quarantine();
            }
            else
            {
                try
                { if (aliasCreated) File.Delete(alias); }
                finally
                {
                    if (outputHeld && !output.TryEndChildUse())
                        output.Quarantine();
                    if (sourceHeld && !source.TryEndChildUse())
                        source.Quarantine();
                }
            }
        }
    }

    private sealed record IndependentDecodeResult(string Executable, string[] Arguments, int ProcessId, int ExitCode,
        string StandardOutput, string StandardError, long OutputLimit, long? OutputBytes);

    private sealed record QualifiedReport(string Path, string Sha256);
    private sealed record Configuration(Fixture[] Fixtures, string? OracleExecutable = null)
    {
        public QualifiedReport? QualifiedReport { get; init; }
        public string? RecoveryIso { get; init; }
        public bool QualifyDefaultProcessingCapacity { get; init; }
        public string? RetainArtifactsDirectory { get; init; }
        public string OracleSha256 { get; init; } = "524c0b27c0c4289e435df4474d288ea7e63d6a34814cc7c650231688dd1e53b4";
        public string[] InputFormats { get; init; } = ["original", "rvz"];
        public int Repetitions { get; init; } = 2;
    }

    private sealed class CapturingSupervisor(MediaWorkerSupervisor inner) : IMediaWorkerSupervisor
    {
        public MediaWorkerResult? Encoding { get; private set; }
        public MediaWorkerResult? Inspection { get; private set; }
        public EncodingObservations? Observations { get; private set; }
        public string? OutputPath { get; private set; }
        public long MaximumOutputBytes { get; private set; }
        public bool IsAvailable => inner.IsAvailable;
        public Task<MediaWorkerResult> HandshakeAsync(CancellationToken ct = default) => inner.HandshakeAsync(ct);
        public async Task<MediaWorkerResult> InspectAsync(ITempFile source, CancellationToken ct = default) =>
            Inspection = await inner.InspectAsync(source, ct);
        public async Task<MediaWorkerResult> InspectIsoAsync(ITempFile source, CancellationToken ct = default) =>
            Inspection = await inner.InspectIsoAsync(source, ct);
        public async Task<MediaWorkerResult> EncodeAsync(ITempFile source, IWritableTempFile output, long logicalBytes,
            RomIsoDiscKind discKind, CancellationToken ct = default)
        {
            OutputPath = output.Path;
            MaximumOutputBytes = output.MaximumBytes;
            Encoding = null;
            Observations = null;
            var operation = inner.EncodeAsync(source, output, logicalBytes, discKind, ct);
            try
            {
                Observations = OperatingSystem.IsLinux()
                    ? await EncodingObservations.SampleAsync(source.Path, operation, ct) : null;
            }
            finally { Encoding = await operation; }
            return await operation;
        }
    }
}
