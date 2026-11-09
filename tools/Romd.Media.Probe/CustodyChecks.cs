using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO.Abstractions;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32.SafeHandles;
using Romd.Admin.Application.Storage.Files;
using Romd.Application.Common.Configuration;
using Romd.Application.Common.Content;
using Romd.Domain.Hashing;
using Romd.Domain.Source.Rom;
using Romd.Infrastructure.Storage;
using Romd.Media.Chd;
using Romd.Media.Inspection;
using Romd.Media.Processes;
using Romd.Media.Rvz;

namespace Romd.Media.Probe;

/// <summary>Disposable PID1 custody probe, externally killed/restarted by its caller; CHD is representative-runtime only.</summary>
internal static class CustodyChecks
{
    private const int MaximumReportBytes = 256 * 1024;
    private static readonly JsonSerializerOptions StrictJson = new() { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };

    public static async Task RunAsync(string[] args, CancellationToken ct)
    {
        if (args.Length != 3 || !Path.IsPathFullyQualified(args[1]) || !Path.IsPathFullyQualified(args[2]))
            throw new ArgumentException("Usage: --custody-probe <absolute configuration.json> <absolute new report.json>");
        byte[] configurationBytes = BoundedRuntimeFile.Read(args[1], 16384);
        using var parsed = JsonDocument.Parse(configurationBytes, new JsonDocumentOptions { MaxDepth = 3 });
        Check.That(MediaRunRecord.Unique(parsed.RootElement), "unique custody configuration fields");
        var configuration = JsonSerializer.Deserialize<Configuration>(configurationBytes, StrictJson)
            ?? throw new InvalidDataException("Missing custody qualification configuration.");
        Validate(configuration, args[1], args[2]);
        await using var report = new FileStream(args[2], FileMode.CreateNew, FileAccess.Write, FileShare.Read,
            bufferSize: 4096, FileOptions.Asynchronous);
        var rows = new List<object>();
        object? failure = null;
        object? checkpointFailure = null;
        MediaWorkerResult? lastInspection = null;
        MediaWorkerResult? lastEncoding = null;
        var foreignPidLookupAttempts = new List<int>();
        var here = MediaProcessIdentity.Read(Environment.ProcessId);
        Check.That(here is not null && here.ProcessId == 1 && here.Platform == "linux" && GetEffectiveUserId() != 0,
            "non-root Linux container PID1 with measured process identity");
        bool chd = configuration.DiscKind == "dvd";
        var assemblies = await CheckImageAsync(configuration.ProductionWorker, ct, chd);
        var limits = new MediaProcessingOptions
        {
            WorkerPath = configuration.ProductionWorker,
            MaxConcurrent = 1,
            AdmissionTimeoutSeconds = 1,
            WallTimeoutSeconds = configuration.WallTimeoutSeconds,
            CpuTimeoutSeconds = configuration.CpuTimeoutSeconds,
            ChdEncodeWallTimeoutSeconds = configuration.WallTimeoutSeconds,
            ChdEncodeCpuTimeoutSeconds = configuration.CpuTimeoutSeconds
        };
        if (chd)
        {
            limits.MaxLogicalBytes = configuration.Chd!.ExpectedSource.Size;
            limits.MaxArtifactBytes = Math.Max(limits.MaxLogicalBytes, configuration.Chd.MaximumOutputBytes);
        }
        var options = new Options(configuration.DataDirectory);
        var stagingLimits = new TempStorageOptions
        {
            MaximumFiles = 3,
            MaximumBytes = configuration.MaximumStagingBytes,
            MinimumFreeBytes = configuration.MinimumFreeBytes,
            AdmissionTimeoutSeconds = 1
        };
        var runtime = new MediaProcessingRuntime(options, limits);
        var factory = new LocalTempFileFactory(options, new FileSystem(), stagingLimits);
        long inputBytes = new FileInfo(configuration.InputIso).Length;
        Check.That(inputBytes is > 128 && inputBytes <= limits.MaxLogicalBytes, "bounded independent original input");
        var kind = configuration.DiscKind == "gamecube" ? RomIsoDiscKind.GameCube : RomIsoDiscKind.Wii;
        long outputBytes;
        if (chd)
        {
            Check.That(inputBytes == configuration.Chd!.ExpectedSource.Size && inputBytes % 2048 == 0,
                "explicit DVD source bytes match approved bounded input before staging");
            outputBytes = configuration.Chd.MaximumOutputBytes;
        }
        else
        {
            var bound = kind == RomIsoDiscKind.GameCube
                ? RvzCanonicalBounds.GetGameCubeOutputBound(inputBytes, limits.MaxLogicalBytes, ct)
                : RvzCanonicalBounds.GetWiiOutputBound(inputBytes, limits.MaxLogicalBytes, ct);
            Check.That(bound is RvzCanonicalOutputBound.Success, "canonical output bound established before staging");
            outputBytes = ((RvzCanonicalOutputBound.Success)bound).MaximumOutputBytes;
        }
        var request = new TempFileAggregateRequest(configuration.JobId, "custody-probe", inputBytes, outputBytes);
        await SaveAsync("in-progress", null, ct);
        try
        {
            switch (configuration.Operation)
            {
                case "start":
                    await StartAsync();
                    break;
                case "recover":
                    await RecoverAsync();
                    break;
                case "assert-held":
                    await AssertHeldAsync();
                    break;
                case "same-namespace":
                    await SameNamespaceAsync();
                    break;
            }
            if (configuration.Operation == "same-namespace" || configuration.Operation == "recover" && configuration.ExpectedRecovery == "recovered")
            {
                using var accounting = JsonDocument.Parse(BoundedRuntimeFile.Read(AccountingPath(configuration), 32768));
                Check.That(!Directory.EnumerateFiles(RunRoot(configuration), "run-*.json").Any() &&
                    accounting.RootElement.GetProperty("Reservations").EnumerateObject().Count() == 0 &&
                    !Directory.EnumerateFiles(Path.GetDirectoryName(AccountingPath(configuration))!, "*.quarantine").Any(),
                    "successful operations release confirmed processing/staging custody after sibling completion");
                rows.Add(new { Step = "final-release", NoMediaRuns = true, ReservedBytes = 0, HeldStagingMarkers = 0 });
            }
            await SaveAsync("passed", null, ct);
        }
        catch (Exception error)
        {
            failure = CaptureFailure(error, lastInspection, lastEncoding);
            await SupervisorChecks.PreserveFailureAsync(error,
                token => SaveAsync("failed", error.GetType().Name, token), CancellationToken.None);
        }

        async Task StartAsync()
        {
            if (chd)
            { await StartChdAsync(); return; }
            Check.That(!Directory.EnumerateFiles(RunRoot(configuration), "run-*.json").Any(), "start uses fresh run custody");
            using var processing = await runtime.AcquireAsync(configuration.JobId, "custody-probe", ct);
            await using var aggregate = await ReserveAsync(factory, request, ct);
            var destination = aggregate.Output ?? throw new InvalidDataException("Missing aggregate output reservation.");
            processing.TrackAggregate((IMediaStagingAggregate)aggregate);
            var oracle = await OriginalAsync(configuration.InputIso, inputBytes, ct);
            await using var input = File.OpenRead(configuration.InputIso);
            var source = (await aggregate.MaterializeSourceAsync(input, Sha256.Parse(oracle.Sha256), inputBytes, ct));
            Check.That(!source.IsError, "source materialization matches independent bytes");
            var artifact = source.Value;
            if (configuration.Checkpoint == "prepared")
                await PauseAsync("prepared", processing, aggregate, null);
            var ordinary = new CapturingSupervisor(new(limits, runtime));
            using var verifier = new RvzVerifier(limits, ordinary, runtime);
            using var active = processing.Activate();
            var inspected = await verifier.InspectIsoAsync(artifact, ct);
            lastInspection = ordinary.Last;
            Check.That(!inspected.IsError && inspected.Value.Eligibility.DiscKind == kind,
                "actual independent ISO inspection establishes configured kind");
            AssertLogical(inspected.Value.Verification, oracle);
            rows.Add(new { Step = "independent-source", Oracle = oracle, Inspect = ordinary.Last });
            var encoder = new CapturingSupervisor(new MediaWorkerSupervisor(limits, () => new MediaWorkerProcessLifetime(),
                setup: async (process, point, token) =>
                {
                    try
                    {
                        string pointName = point switch
                        {
                            MediaWorkerSetupPoint.Spawned => "spawned",
                            MediaWorkerSetupPoint.Identified => "identified",
                            MediaWorkerSetupPoint.Permitted => "permitted",
                            MediaWorkerSetupPoint.Draining => "nonempty",
                            _ => "other"
                        };
                        if (configuration.Checkpoint != pointName)
                            return;
                        if (pointName == "nonempty")
                        {
                            using var wait = CancellationTokenSource.CreateLinkedTokenSource(token);
                            wait.CancelAfter(TimeSpan.FromSeconds(configuration.CheckpointWaitSeconds));
                            while (!File.Exists(destination.Path) || new FileInfo(destination.Path).Length == 0)
                            {
                                Check.That(ObserveChild(process.Id).Live, "encoding produces bytes before child exit");
                                await Task.Delay(10, wait.Token);
                            }
                        }
                        await PauseAsync(pointName, processing, aggregate, process.Id);
                    }
                    catch (Exception error)
                    {
                        checkpointFailure = CaptureFailure(error, null, null);
                        throw;
                    }
                }, runtime: runtime));
            using var encoderVerifier = new RvzVerifier(limits, encoder, runtime);
            using var admission = await encoderVerifier.AcquireAsync(ct);
            var encoded = await admission.EncodeAsync(artifact, aggregate.Output!, inputBytes, kind, ct);
            lastEncoding = encoder.Last;
            Check.That(!encoded.IsError && processing.ProcessesStopped, "encode stopped before output seal");
            var facts = encoded.Value;
            var sealedOutput = await aggregate.Output!.SealAsync(new(Sha256.Parse(facts.ArtifactSha256),
                Md5.Parse(facts.ArtifactMd5), Sha1.Parse(facts.ArtifactSha1), facts.ArtifactBytes), ct);
            Check.That(!sealedOutput.IsError, "one-way output seal with independent artifact facts");
            processing.TrackAggregate((IMediaStagingAggregate)aggregate);
            var verified = await verifier.VerifyAsync(sealedOutput.Value, ct);
            lastInspection = ordinary.Last;
            Check.That(!verified.IsError && processing.ProcessesStopped, "fresh output child fully stopped");
            AssertLogical(verified.Value.Verification, oracle);
            Check.That(encoder.Last!.Execution!.ProcessId != ordinary.Last!.Execution!.ProcessId,
                "encode and output proof use different child processes");
            rows.Add(new
            {
                Step = "encoded-and-verified",
                Encode = encoder.Last,
                Verify = ordinary.Last,
                facts.ArtifactSha256,
                facts.ArtifactMd5,
                facts.ArtifactSha1,
                facts.ArtifactBytes,
                AllLogicalHashesMatched = true,
                ExtraVerificationStagingBytes = 0
            });
            Check.That(configuration.Checkpoint == "verified", "requested start checkpoint reached");
            await PauseAsync("verified", processing, aggregate, null);
        }

        async Task PauseAsync(string checkpoint, MediaProcessingRuntime.MediaProcessingReservation processing,
            ITempFileAggregate aggregate, int? childPid)
        {
            var snapshot = Capture(configuration);
            Check.That(snapshot.Record.JobId == configuration.JobId && snapshot.Record.Staging.Count == 2,
                "durable job and complete source/output reservation binding");
            Check.That(snapshot.Record.Admission is { Inode: > 0 }, "original admission device/inode recorded");
            using var independent = NativeFileLease.TryAcquire(LeasePath(configuration));
            Check.That(independent is null, "parent retains original admission OFD at external-kill checkpoint");
            if (checkpoint == "prepared")
                Check.That(snapshot.Record.Phase == "prepared" && snapshot.Record.Identity is null &&
                    !File.Exists(RecordPath(configuration) + ".identity") && processing.ProcessesStopped,
                    "prepared checkpoint certifies no child launch");
            if (checkpoint is "identified" or "permitted" or "nonempty")
                Check.That(snapshot.Identity is not null && snapshot.Identity.PidNamespace == here!.PidNamespace &&
                    snapshot.Identity.Boot == here!.Boot && snapshot.Identity.ProcessId == childPid,
                    "child identity matches current boot/namespace/session");
            if (checkpoint is "permitted" or "nonempty")
                Check.That(snapshot.Record.Phase == "permitted", "durable identity/permit precedes payload");
            if (checkpoint == "nonempty")
                Check.That(new FileInfo(aggregate.Output!.Path).Length > 0, "nonempty output retained at checkpoint");
            rows.Add(new
            {
                Step = "external-kill-ready",
                Checkpoint = checkpoint,
                Snapshot = snapshot,
                ChildObservation = childPid is { } pid ? ObserveChild(pid) : null,
                ProcessingStopped = processing.ProcessesStopped,
                ExtraVerificationStagingBytes = 0,
                RootMustKillContainerExternally = true
            });
            await SaveAsync("ready", null, ct);
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        }

        async Task RecoverAsync()
        {
            var before = Capture(configuration);
            Check.That(before.Record.JobId == configuration.JobId, "recovery names exact durable job");
            using (var original = NativeFileLease.TryAcquire(LeasePath(configuration)))
            {
                Check.That(original is not null, "old parent/child admission descriptions independently absent");
                var acquired = original ?? throw new InvalidDataException("Original admission remains occupied.");
                Check.That(acquired.Identity == before.Record.Admission, "independent acquisition has original device/inode");
                bool foreign = MediaProcessingRuntime.DifferentNamespaceIncarnation(before.Record.Parent, here!);
                bool exception = foreign && before.Identity is not null && before.Record.Phase is "launching" or "running" or "permitted";
                if (exception && configuration.ExpectedRecovery == "recovered")
                    Check.That(MediaProcessingRuntime.ForeignNamespaceExtinct(before.Record, before.Identity!, here!, acquired.Identity),
                        "approved foreign PID1 extinction facts established without foreign PID query/signal");
                rows.Add(new
                {
                    Step = "independent-original-ofd",
                    before.Record.RunId,
                    Admission = acquired.Identity,
                    OldParent = before.Record.Parent,
                    OldChild = before.Identity,
                    CurrentParent = here,
                    ForeignNamespace = foreign,
                    PayloadExtinctionException = exception,
                    ObservedForeignOsReap = false
                });
            }
            if (configuration.ExpectedRecovery == "recovered" && before.Identity is not null &&
                MediaProcessingRuntime.DifferentNamespaceIncarnation(before.Record.Parent, here!) && before.Record.Phase != "stopped")
                await NegativeChecksAsync(before);
            var recovery = NoForeignPidLookupRuntime();
            var stopped = await recovery.EnsureJobStoppedAsync(configuration.JobId, ct);
            Check.That(foreignPidLookupAttempts.Count == 0, "recovery attempted no old numeric PID lookup");
            if (configuration.ExpectedRecovery == "pinned")
            {
                Check.That(stopped.IsError, "unconfirmed launch remains typed unresolved custody");
                await AssertCapacityDeniedAsync(recovery, configuration.JobId, ct);
                Check.That(SameSnapshot(Capture(configuration), before), "unconfirmed launch preserves every durable custody byte");
                rows.Add(new { Step = "recognized-unconfirmed-launch", StillPinned = true, Code = stopped.FirstError.Code });
                return;
            }
            Check.That(!stopped.IsError, "fresh process returns affirmative typed stopped/extinct custody proof");
            Check.That(!File.Exists(RecordPath(configuration)) && !File.Exists(RecordPath(configuration) + ".identity") &&
                before.Record.Staging.All(binding => !File.Exists(binding.Path + ".quarantine")), "recognized old run and matching holds reclaimed");
            rows.Add(new
            {
                Step = "old-run-recovered",
                OldSnapshot = before,
                TypedStoppedProof = true,
                ForeignNumericPidLookupAttempts = foreignPidLookupAttempts.ToArray()
            });
            await SiblingAsync(before.Record.RunId);
        }

        async Task AssertHeldAsync()
        {
            var before = Capture(configuration);
            Check.That(before.Record.JobId == configuration.JobId && MediaProcessingRuntime.DifferentNamespaceIncarnation(before.Record.Parent, here!),
                "parallel container observes exact job from another namespace incarnation");
            using var original = NativeFileLease.TryAcquire(LeasePath(configuration));
            Check.That(original is null, "live old namespace retains the original admission OFD");
            var recovery = NoForeignPidLookupRuntime();
            var stopped = await recovery.EnsureJobStoppedAsync(configuration.JobId, ct);
            Check.That(stopped.IsError, "live parent/child cannot be certified stopped");
            await AssertCapacityDeniedAsync(recovery, configuration.JobId, ct);
            Check.That(foreignPidLookupAttempts.Count == 0, "parallel observer attempted no old numeric PID lookup");
            bool stagingDenied = false;
            try
            { await using var extra = await ReserveAsync(factory, request, ct); }
            catch (ResourceCapacityException) { stagingDenied = true; }
            Check.That(stagingDenied, "live aggregate charges prevent all-or-none competing aggregate");
            var after = Capture(configuration);
            Check.That(after.RecordSha256 == before.RecordSha256 && after.IdentitySha256 == before.IdentitySha256 &&
                after.AccountingSha256 == before.AccountingSha256 && before.Files.All(old =>
                    after.Files.Any(current => current.Path == old.Path && (!old.Exists || current.Exists && current.Bytes >= old.Bytes))),
                "parallel denial preserves record, accounting and nondecreasing payloads");
            rows.Add(new
            {
                Step = "parallel-live-denied",
                Before = before,
                After = after,
                ProcessingAndStagingDenied = true,
                ForeignNumericPidLookupAttempts = foreignPidLookupAttempts.ToArray()
            });
        }

        MediaProcessingRuntime NoForeignPidLookupRuntime() => new(RunRoot(configuration), limits, pid =>
        {
            foreignPidLookupAttempts.Add(pid);
            throw new InvalidDataException("Foreign recovery attempted a numeric PID lookup.");
        });

        async Task SameNamespaceAsync()
        {
            using var processing = await runtime.AcquireAsync(configuration.JobId, "custody-probe", ct);
            await using var aggregate = await ReserveAsync(factory, request, ct);
            processing.TrackAggregate((IMediaStagingAggregate)aggregate);
            var oracle = await OriginalAsync(configuration.InputIso, inputBytes, ct);
            await using var input = File.OpenRead(configuration.InputIso);
            var materialized = await aggregate.MaterializeSourceAsync(input, Sha256.Parse(oracle.Sha256), inputBytes, ct);
            Check.That(!materialized.IsError, "same-namespace original materialization");
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
            MediaProcessIdentity? child = null;
            var supervisor = new MediaWorkerSupervisor(limits, () => new MediaWorkerProcessLifetime(), setup: async (process, point, token) =>
            {
                if (point != MediaWorkerSetupPoint.Draining)
                    return;
                child = MediaProcessIdentity.Read(process.Id);
                Check.That(child is not null && child.PidNamespace == here!.PidNamespace && child.Boot == here!.Boot,
                    "ordinary supervisor owns exact same-namespace live child");
                await cancellation.CancelAsync();
            }, runtime: runtime);
            using var verifier = new RvzVerifier(limits, supervisor, runtime);
            using (processing.Activate())
            {
                bool canceled = false;
                try
                { await verifier.InspectIsoAsync(materialized.Value, cancellation.Token); }
                catch (OperationCanceledException error)
                { Check.That(error.CancellationToken == cancellation.Token, "ordinary requested cancellation token preserved"); canceled = true; }
                Check.That(canceled && processing.ProcessesStopped, "ordinary supervisor cancellation affirms stop/reap/join");
            }
            Check.That(child is not null && !Directory.Exists("/proc/" + child.ProcessId) &&
                Kill(-child.GroupId, 0) != 0 && Marshal.GetLastPInvokeError() == 3, "same-namespace child reaped and owned group extinct");
            Check.That(!(await runtime.EnsureJobStoppedAsync(configuration.JobId, ct)).IsError,
                "durable same-namespace stopped certificate while aggregate remains charged");
            rows.Add(new
            {
                Step = "same-namespace-stopped",
                Child = child,
                ActualReapAndGroupExtinction = true,
                Snapshot = Capture(configuration),
                ProcessingStopped = processing.ProcessesStopped
            });
            string oldRun = Capture(configuration).Record.RunId;
            processing.Dispose();
            using var sibling = await runtime.AcquireAsync(configuration.JobId, "custody-sibling", ct);
            sibling.TrackAggregate((IMediaStagingAggregate)aggregate);
            using var active = sibling.Activate();
            var captured = new CapturingSupervisor(new(limits, runtime));
            using var next = new RvzVerifier(limits, captured, runtime);
            var result = await next.InspectIsoAsync(materialized.Value, ct);
            Check.That(!result.IsError && sibling.ProcessesStopped, "same shared processing capacity accepts successful sibling");
            AssertLogical(result.Value.Verification, oracle);
            Check.That(Capture(configuration).Record.RunId != oldRun && captured.Last!.Execution!.ProcessId != child!.ProcessId,
                "sibling has a fresh run and child identity");
            rows.Add(new { Step = "same-pool-sibling", Inspect = captured.Last, AllLogicalHashesMatched = true });
        }

        async Task NegativeChecksAsync(Snapshot before)
        {
            byte[] original = BoundedRuntimeFile.Read(RecordPath(configuration), MediaRunRecord.MaximumBytes);
            string identityPath = RecordPath(configuration) + ".identity";
            byte[]? sidecar = File.Exists(identityPath) ? BoundedRuntimeFile.Read(identityPath, 2048) : null;
            foreach (string mutation in new[] { "parent-not-init", "boot-mismatch", "namespace-mismatch", "missing-namespace", "missing-admission",
                "inode-mismatch", "unknown-identity", "legacy-version", "parallel-lock", "replaced-lease-inode" })
            {
                string backup = LeasePath(configuration) + ".qualification-original";
                Check.That(!File.Exists(backup) && !Directory.Exists(backup), "qualification never adopts an earlier lease backup");
                NativeFileLease? parallel = null;
                MediaAdmissionIdentity? replacedIdentity = null;
                bool movedOriginal = false;
                try
                {
                    var record = before.Record;
                    record = mutation switch
                    {
                        "parent-not-init" => record with { Parent = record.Parent with { ProcessId = 2 } },
                        "boot-mismatch" => record with
                        {
                            Parent = record.Parent with { Boot = "qualification-other-kernel" },
                            Identity = before.Identity! with { Boot = "qualification-other-kernel" }
                        },
                        "namespace-mismatch" => record with { Parent = record.Parent with { PidNamespace = OtherNamespace(here!, before.Identity!) } },
                        "missing-namespace" => record with { Parent = record.Parent with { PidNamespace = string.Empty } },
                        "missing-admission" => record with { Admission = null },
                        "inode-mismatch" => record with { Admission = record.Admission! with { Inode = checked(record.Admission!.Inode + 1) } },
                        "unknown-identity" => record with { Identity = null },
                        "legacy-version" => record with { Version = "romd-media-run/v1" },
                        _ => record
                    };
                    if (mutation == "unknown-identity")
                        File.Delete(identityPath);
                    if (mutation == "parallel-lock")
                    {
                        parallel = NativeFileLease.TryAcquire(LeasePath(configuration));
                        Check.That(parallel is not null, "qualification contender actually holds original admission OFD");
                    }
                    if (mutation == "replaced-lease-inode")
                    {
                        File.Move(LeasePath(configuration), backup, overwrite: false);
                        movedOriginal = true;
                        using var replacement = NativeFileLease.TryAcquire(LeasePath(configuration));
                        Check.That(replacement is not null && replacement.Identity != before.Record.Admission,
                            "qualification replacement actually changes lease inode");
                        replacedIdentity = replacement!.Identity;
                    }
                    MediaRunRecord.Write(RecordPath(configuration), record);
                    var refused = await runtime.EnsureJobStoppedAsync(configuration.JobId, ct);
                    Check.That(refused.IsError, "negative custody stays unresolved: " + mutation);
                    await AssertCapacityDeniedAsync(runtime, configuration.JobId, ct);
                    Check.That(File.Exists(RecordPath(configuration)) &&
                        FileHash(BoundedRuntimeFile.Read(AccountingPath(configuration), 32768)) == before.AccountingSha256 &&
                        before.Files.All(file => File.Exists(file.Path) == file.Exists && (!file.Exists || new FileInfo(file.Path).Length == file.Bytes)),
                        "negative custody does not delete charges or payload: " + mutation);
                    rows.Add(new
                    {
                        Step = "negative-custody",
                        Mutation = mutation,
                        StillPinned = true,
                        Code = refused.FirstError.Code,
                        record.Version,
                        Parent = record.Parent,
                        Child = record.Identity,
                        RecordAdmission = record.Admission,
                        ReplacedLeaseAdmission = replacedIdentity,
                        NumericPidLookupInstrumentation = "not measured for mutated facts"
                    });
                }
                finally
                {
                    parallel?.Dispose();
                    if (movedOriginal)
                    {
                        using var originalLease = NativeFileLease.TryAcquire(backup);
                        Check.That(originalLease is not null && originalLease.Identity == before.Record.Admission,
                            "only this invocation's exact original lease inode is restored");
                        File.Delete(LeasePath(configuration));
                        File.Move(backup, LeasePath(configuration), overwrite: false);
                    }
                    WriteExact(RecordPath(configuration), original);
                    if (sidecar is not null)
                        WriteExact(identityPath, sidecar);
                }
            }
            Check.That(SameSnapshot(Capture(configuration), before), "negative qualification restores exact original custody bytes");
        }

        async Task SiblingAsync(string oldRun)
        {
            if (chd)
            { await StartChdAsync(oldRun); return; }
            using var processing = await runtime.AcquireAsync(configuration.JobId, "custody-sibling", ct);
            await using var aggregate = await ReserveAsync(factory, request, ct);
            processing.TrackAggregate((IMediaStagingAggregate)aggregate);
            var oracle = await OriginalAsync(configuration.InputIso, inputBytes, ct);
            await using var stream = File.OpenRead(configuration.InputIso);
            var materialized = await aggregate.MaterializeSourceAsync(stream, Sha256.Parse(oracle.Sha256), inputBytes, ct);
            Check.That(!materialized.IsError, "recovered same-pool source materialization");
            var captured = new CapturingSupervisor(new(limits, runtime));
            using var verifier = new RvzVerifier(limits, captured, runtime);
            using var active = processing.Activate();
            var result = await verifier.InspectIsoAsync(materialized.Value, ct);
            Check.That(!result.IsError && processing.ProcessesStopped, "actual valid same-pool inspection sibling succeeds");
            AssertLogical(result.Value.Verification, oracle);
            Check.That(Capture(configuration).Record.RunId != oldRun && !(await runtime.EnsureJobStoppedAsync(configuration.JobId, ct)).IsError,
                "fresh sibling uses a new run with affirmative stopped proof");
            rows.Add(new
            {
                Step = "recovered-same-pool-sibling",
                Inspect = captured.Last,
                Oracle = oracle,
                Snapshot = Capture(configuration),
                AllLogicalHashesMatched = true,
                ExtraVerificationStagingBytes = 0
            });
        }

        async Task StartChdAsync(string? oldRun = null)
        {
            Check.That(!Directory.EnumerateFiles(RunRoot(configuration), "run-*.json").Any(), "CHD uses fresh recovered run custody");
            var inputs = configuration.Chd!;
            var oracle = await ChdOriginalAsync(configuration.InputIso, inputBytes, ct);
            Check.That(oracle == inputs.ExpectedSource, "fresh original digests match explicit approved oracle before tool access");
            using var processing = await runtime.AcquireAsync(configuration.JobId, oldRun is null ? "custody-probe" : "custody-sibling", ct);
            await using var aggregate = await ReserveAsync(factory, request, ct);
            processing.TrackAggregate((IMediaStagingAggregate)aggregate);
            using var sourceHandle = NativeFileLease.OpenRegularRead(configuration.InputIso);
            await using var sourceStream = new FileStream(sourceHandle, FileAccess.Read);
            var materialized = await aggregate.MaterializeSourceAsync(sourceStream, Sha256.Parse(oracle.Sha256), inputBytes, ct);
            Check.That(!materialized.IsError, "CHD source materializes from bounded regular original with exact bytes/SHA256");
            using var active = processing.Activate();
            if (oldRun is null && configuration.Checkpoint == "prepared")
                await PauseAsync("prepared", processing, aggregate, null);
            var tool = ChdmanCandidate.FromBuildRecord(inputs.ToolDirectory);
            var native = ChdNativeCandidate.FromBuildRecord(inputs.NativeDirectory);
            ChdmanToolIdentity toolIdentity = tool.ExpectedIdentity;
            ChdNativeIdentity nativeIdentity = native.ExpectedIdentity;
            Check.That(tool.Verify(ct) == toolIdentity, "recorded tool build verifies only after independent source facts");
            var destination = aggregate.Output ?? throw new InvalidDataException("Missing reserved CHD output.");
            var encoder = new MediaWorkerSupervisor(limits, () => new MediaWorkerProcessLifetime(),
                setup: oldRun is null ? ChdCheckpointAsync : null, runtime: runtime, chdCandidate: native);
            var encoded = await encoder.EncodeChdAsync(materialized.Value, destination, tool, ct);
            lastEncoding = encoded;
            if (!encoded.IsSuccess || encoded.Reply is not
                {
                    Status: MediaWorkerStatus.Encoded,
                    ChdEncoding: { } artifact, ToolExecution: { } execution
                } reply)
                throw new InvalidDataException("CHD encode did not produce raw metadata with joined tool facts.");
            var encoderExecution = encoded.Execution ?? throw new InvalidDataException("Missing joined encoder worker identity.");
            Check.That(processing.ProcessesStopped && encoded.ProcessTreeStopped && encoderExecution.ExitCode == 0 &&
                reply.ToolIdentity == toolIdentity && execution.ExitCode == 0 && execution.ProcessId > 1 &&
                execution.ProcessId != encoderExecution.ProcessId && execution.StandardOutputBytes >= 0 && execution.StandardErrorBytes >= 0 &&
                execution.StandardOutputBytes + execution.StandardErrorBytes <= (256L << 10) &&
                artifact.SourceBytes == oracle.Size && artifact.LogicalBytes == oracle.Size && artifact.SourceSha256 == oracle.Sha256 &&
                artifact.RecipeVersion == ChdDvdCanonicalRecipe.Id && artifact.EncodingProfile == ChdDvdCanonicalRecipe.EncoderProfile &&
                artifact.PriorityKind == "unix-nice" && artifact.PriorityValue is >= 10 and <= 19,
                "CHD encoding is joined raw recipe metadata, not logical integrity");
            var raw = await ChdOriginalAsync(destination.Path, artifact.ArtifactBytes, ct);
            Check.That(raw.Size <= outputBytes && raw.Sha256 == artifact.ArtifactSha256 && raw.Md5 == artifact.ArtifactMd5 &&
                raw.Sha1 == artifact.ArtifactSha1, "raw CHD output size/digests independently match bounded measurement");
            var sealedOutput = await destination.SealAsync(new(Sha256.Parse(raw.Sha256), Md5.Parse(raw.Md5), Sha1.Parse(raw.Sha1), raw.Size), ct);
            Check.That(!sealedOutput.IsError, "CHD output seals only after stopped encoder and full raw measurement");
            processing.TrackAggregate((IMediaStagingAggregate)aggregate);
            var inspector = new MediaWorkerSupervisor(limits, runtime, native);
            var inspected = await inspector.InspectChdAsync(sealedOutput.Value, ct);
            lastInspection = inspected;
            if (!inspected.IsSuccess || inspected.Reply is not
                {
                    Status: MediaWorkerStatus.Inspected,
                    Inspection: { } proof, ChdInspection: { } integrity
                } inspectionReply)
                throw new InvalidDataException("Fresh CHD inspection did not produce complete independent logical proof.");
            var inspectorExecution = inspected.Execution ?? throw new InvalidDataException("Missing joined inspection worker identity.");
            Check.That(processing.ProcessesStopped && inspected.ProcessTreeStopped && inspectorExecution.ExitCode == 0 &&
                inspectorExecution.ProcessId != encoderExecution.ProcessId && inspectorExecution.ProcessId != execution.ProcessId &&
                inspectionReply.NativeIdentity == nativeIdentity && proof.ArtifactBytes == raw.Size && proof.ArtifactSha256 == raw.Sha256 &&
                proof.LogicalBytes == oracle.Size && proof.LogicalSha256 == oracle.Sha256 && proof.Sha1 == oracle.Sha1 &&
                proof.Md5 == oracle.Md5 && proof.Crc32 == oracle.Crc && proof.DiscKind is null && proof.EncodingProfile == "chd-import/v1" &&
                integrity.Version == 5 && integrity.Header == artifact.Header && integrity.Header.UnitBytes == ChdDvdCanonicalRecipe.UnitBytes &&
                integrity.Header.HunkBytes == ChdDvdCanonicalRecipe.HunkBytes && integrity.Header.LogicalBytes == (ulong)oracle.Size &&
                integrity.Header.Codec0 == (uint)ChdDvdCanonicalRecipe.Codec && integrity.Header.Codec1 == 0 && integrity.Header.Codec2 == 0 && integrity.Header.Codec3 == 0 &&
                integrity.StoredRawSha1 == oracle.Sha1 && integrity.ComputedRawSha1 == oracle.Sha1 &&
                integrity.StoredOverallSha1 == integrity.ComputedOverallSha1 && Hex(integrity.StoredOverallSha1, 40),
                "distinct fresh CHD child proves all logical digests/length and stored/computed CHD hashes");
            Check.That(await ChdOriginalAsync(configuration.InputIso, inputBytes, ct) == oracle,
                "fresh final original four digests/length remain unchanged");
            rows.Add(new
            {
                Step = oldRun is null ? "encoded-and-verified" : "recovered-same-pool-sibling",
                Encode = encoded,
                Verify = inspected,
                Oracle = oracle,
                RawArtifact = raw,
                AllLogicalHashesMatched = true,
                ExtraVerificationStagingBytes = 0
            });
            if (oldRun is null)
            {
                Check.That(configuration.Checkpoint == "verified", "requested CHD checkpoint reached");
                await PauseAsync("verified", processing, aggregate, null);
            }
            else
                Check.That(Capture(configuration).Record.RunId != oldRun &&
                !(await runtime.EnsureJobStoppedAsync(configuration.JobId, ct)).IsError,
                "recovered CHD sibling uses fresh run with affirmative stopped proof");

            async Task ChdCheckpointAsync(Process process, MediaWorkerSetupPoint point, CancellationToken token)
            {
                try
                {
                    string checkpoint = point switch
                    {
                        MediaWorkerSetupPoint.Spawned => "spawned",
                        MediaWorkerSetupPoint.Identified => "identified",
                        MediaWorkerSetupPoint.Permitted => "permitted",
                        MediaWorkerSetupPoint.Draining => "nonempty",
                        _ => "other"
                    };
                    if (configuration.Checkpoint != checkpoint)
                        return;
                    if (checkpoint == "nonempty")
                    {
                        using var wait = CancellationTokenSource.CreateLinkedTokenSource(token);
                        wait.CancelAfter(TimeSpan.FromSeconds(configuration.CheckpointWaitSeconds));
                        var deadline = Stopwatch.StartNew();
                        LiveToolWitness? witness = null;
                        while (witness is null)
                        {
                            wait.Token.ThrowIfCancellationRequested();
                            Check.That(ObserveChild(process.Id).Live, "CHD worker still executes at live-tool checkpoint");
                            long size = File.Exists(destination.Path) ? new FileInfo(destination.Path).Length : 0;
                            if (size > 0 && size <= outputBytes)
                                witness = await ObserveLiveToolAsync(configuration, processing, process.Id, tool, materialized.Value.Path,
                                    destination.Path, deadline, wait.Token);
                            if (witness is null)
                                await Task.Delay(10, wait.Token);
                        }
                        rows.Add(new
                        {
                            Step = "live-chdman-grandchild",
                            Witness = witness,
                            RootMustFreshlyReconfirmExactLiveToolBeforeKill = true,
                            ObservedAtKill = false
                        });
                    }
                    await PauseAsync(checkpoint, processing, aggregate, process.Id);
                }
                catch (Exception error) { checkpointFailure = CaptureFailure(error, null, null); throw; }
            }
        }

        async Task SaveAsync(string status, string? failureType, CancellationToken token)
        {
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(new
            {
                Version = "romd-custody-qualification/v1",
                Status = status,
                configuration.Operation,
                configuration.Checkpoint,
                configuration.ExpectedRecovery,
                configuration.JobId,
                Parent = here,
                UserId = GetEffectiveUserId(),
                Runtime = RuntimeInformation.FrameworkDescription,
                RuntimeInformation.ProcessArchitecture,
                Assemblies = assemblies,
                Rows = rows,
                FailureType = failureType,
                Failure = failure,
                CheckpointFailure = checkpointFailure,
                ProcessingLimits = limits,
                StagingLimits = stagingLimits,
                Scope = chd
                    ? "Experimental DVD custody using representative Linux runtime and mounted frozen product assemblies; PID1 recreation, original-inode admission and matching aggregate recovery. Live-grandchild witness is point-in-time; root must freshly reconfirm before kill. Foreign namespace extinction is not an observed old-process reap. No final production-image, database, publication, canonical recipe or G4 qualification."
                    : "Disposable Linux production-image PID1 kill/restart custody, original-inode admission, aggregate accounting and fresh-child recovery. Foreign PID1 recovery establishes payload extinction, not observed OS reap; no database or publication claim."
            }, JsonOptions.Value);
            Check.That(bytes.Length <= MaximumReportBytes && rows.Count <= 32, "bounded custody qualification report");
            report.Position = 0;
            report.SetLength(0);
            await report.WriteAsync(bytes, token);
            await report.FlushAsync(token);
            report.Flush(flushToDisk: true);
        }
    }

    internal static object CaptureFailure(Exception error, MediaWorkerResult? inspection, MediaWorkerResult? encoding) =>
        new
        {
            FailureType = error.GetType().Name,
            FailureMessage = error.Message.Length <= 4096 ? error.Message : error.Message[..4096],
            Inspection = inspection,
            Encoding = encoding
        };

    private static void Validate(Configuration configuration, string configPath, string reportPath)
    {
        Check.That(configuration.Operation is "start" or "recover" or "assert-held" or "same-namespace", "closed custody operation");
        Check.That(configuration.Checkpoint is "prepared" or "spawned" or "identified" or "permitted" or "nonempty" or "verified", "closed custody checkpoint");
        Check.That((configuration.ExpectedRecovery is "recovered" or "pinned") && (configuration.DiscKind is "gamecube" or "wii" or "dvd"), "closed recovery and disc-kind values");
        if (configuration.DiscKind == "dvd")
            ValidateChd(configuration, configPath, reportPath);
        else
            Check.That(configuration.Chd is null, "legacy RVZ does not adopt experimental CHD inputs");
        Check.That(configuration.JobId != Guid.Empty && new[] { configuration.DataDirectory, configuration.InputIso, configuration.ProductionWorker }
            .All(path => path is not null && Path.IsPathFullyQualified(path)), "absolute qualification paths and job identity");
        MediaProbePaths.Validate([configPath, reportPath, configuration.DataDirectory, configuration.InputIso, configuration.ProductionWorker]);
        Check.That(!MediaProbePaths.Overlaps(configuration.DataDirectory, configuration.InputIso) &&
            !MediaProbePaths.Overlaps(configPath, configuration.InputIso) && !MediaProbePaths.Overlaps(reportPath, configuration.InputIso),
            "immutable input is isolated from qualification data and controls");
        MediaProbePaths.OutsideManagedStorage(configuration.DataDirectory, configPath, reportPath);
        string backup = LeasePath(configuration) + ".qualification-original";
        Check.That(!File.Exists(backup) && !Directory.Exists(backup) && new FileInfo(backup).LinkTarget is null,
            "qualification never adopts an earlier lease backup");
        Check.That(configuration.WallTimeoutSeconds is >= 30 and <= 3600 && configuration.CpuTimeoutSeconds is >= 30 and <= 3600 &&
            configuration.CheckpointWaitSeconds is >= 1 and <= 60 && configuration.MaximumStagingBytes is >= (16L << 30) and <= (128L << 30) &&
            configuration.MinimumFreeBytes is >= 0 and <= (16L << 30), "bounded qualification budgets");
    }
    private static async Task<object[]> CheckImageAsync(string worker, CancellationToken ct, bool representative = false)
    {
        Check.That(Path.GetFullPath(worker) == "/app/media-worker/Romd.Media.Worker.dll",
            representative ? "frozen mounted qualification worker path" : "untouched production image worker path");
        var results = new List<object>();
        foreach (var assembly in new[] { typeof(MediaProcessingRuntime).Assembly, typeof(MediaInspectionService).Assembly,
            typeof(Romd.Storage.IContentAddressableStore).Assembly })
        {
            string path = Path.Combine("/app", Path.GetFileName(assembly.Location));
            string loaded = await AssemblyHashAsync(assembly.Location, ct);
            Check.That(loaded == await AssemblyHashAsync(path, ct),
                (representative ? "loaded frozen mounted assembly matches: " : "loaded production image assembly matches: ") + assembly.GetName().Name);
            results.Add(new { Name = assembly.GetName().Name, assembly.ManifestModule.ModuleVersionId, Sha256 = loaded });
        }
        results.Add(new { Name = "Romd.Media.Worker", Sha256 = await AssemblyHashAsync(worker, ct) });
        return results.ToArray();
    }
    private static async Task<string> AssemblyHashAsync(string path, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        Check.That(stream.Length is > 0 and <= (64L << 20), "bounded qualification assembly");
        return (await Digests.StreamAsync(stream, ct, stream.Length)).Sha256;
    }
    private static async Task<Digests> OriginalAsync(string path, long length, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        Check.That(stream.Length == length, "original size stable before bounded hash");
        var hashes = await Digests.StreamAsync(stream, ct, length);
        Check.That(hashes.Size == length, "complete independent original hashes");
        return hashes;
    }
    private static void AssertLogical(RomVerification proof, Digests oracle) => Check.That(
        proof.ContentSha256.ToString() == oracle.Sha256 && proof.ContentSize == oracle.Size && proof.Sha1.ToString() == oracle.Sha1 &&
        proof.Md5.ToString() == oracle.Md5 && proof.Crc32.ToString() == oracle.Crc, "all five logical identity fields match independent original");
    private static async Task<ITempFileAggregate> ReserveAsync(LocalTempFileFactory factory, TempFileAggregateRequest request, CancellationToken ct)
    {
        var aggregate = await factory.ReserveAggregateAsync(request, ct);
        Check.That(!aggregate.IsError, "complete aggregate admitted before payload");
        return aggregate.Value;
    }
    private static async Task AssertCapacityDeniedAsync(MediaProcessingRuntime runtime, Guid jobId, CancellationToken ct)
    {
        bool denied = false;
        try
        { using var attempt = await runtime.AcquireAsync(jobId, "custody-contender", ct); }
        catch (ResourceCapacityException) { denied = true; }
        Check.That(denied, "unconfirmed custody cannot reuse one processing slot");
    }
    private static Snapshot Capture(Configuration configuration)
    {
        string path = RecordPath(configuration);
        byte[] bytes = BoundedRuntimeFile.Read(path, MediaRunRecord.MaximumBytes);
        var record = MediaRunRecord.Read(path, 0, 1) ?? throw new InvalidDataException("Missing expected media run.");
        Check.That(record.Staging.All(binding => binding.PoolRoot == Path.GetFullPath(Path.Combine(configuration.DataDirectory, "temp", "staging-v1")) &&
            binding.Path == Path.Combine(binding.PoolRoot, $"slot-{binding.Slot}.data")), "only qualification-owned pool paths are observed");
        string identity = path + ".identity";
        return new(record, record.Identity ?? MediaProcessingRuntime.ReadIdentity(path, record.RunId, record.Admission), FileHash(bytes),
            File.Exists(identity) ? FileHash(BoundedRuntimeFile.Read(identity, 2048)) : null,
            FileHash(BoundedRuntimeFile.Read(AccountingPath(configuration), 32768)),
            record.Staging.Select(binding => Witness(binding.Path)).ToArray());
    }
    internal static FileWitness Witness(string path)
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            if (!File.Exists(path))
                return new(path, false, 0, null, null);
            throw new PlatformNotSupportedException("Custody observation requires a POSIX host.");
        }
        // The live writer holds an exclusive flock. Observe its opened inode without
        // acquiring ownership, following a final symlink, or blocking on a FIFO.
        // Linux arm64 assigns a different O_NOFOLLOW bit from x64.
        int flags = OperatingSystem.IsMacOS() ? 0x01000104
            : RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? 0x00088800 : 0x000a0800;
        int descriptor = Open(path, flags);
        if (descriptor < 0)
        {
            int error = Marshal.GetLastPInvokeError();
            if (error is 2 or 20)
                return new(path, false, 0, null, null);
            throw new Win32Exception(error);
        }
        using var handle = new SafeFileHandle(descriptor, ownsHandle: true);
        byte[] stat = new byte[512];
        int result = OperatingSystem.IsMacOS() && RuntimeInformation.ProcessArchitecture == Architecture.X64
            ? FstatDarwin64(descriptor, stat) : Fstat(descriptor, stat);
        if (result != 0)
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        uint mode = OperatingSystem.IsMacOS() ? BitConverter.ToUInt16(stat, 4)
            : BitConverter.ToUInt32(stat, RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? 16 : 24);
        Check.That((mode & 0xf000) == 0x8000, "custody payload is a regular file");
        ulong device = OperatingSystem.IsMacOS() ? BitConverter.ToUInt32(stat, 0) : BitConverter.ToUInt64(stat, 0);
        ulong inode = BitConverter.ToUInt64(stat, 8);
        Check.That(inode > 0, "actual staging inode observation");
        long length = RandomAccess.GetLength(handle);
        byte[] prefix = new byte[(int)Math.Min(length, 65536)];
        int read = 0;
        while (read < prefix.Length)
        {
            int count = RandomAccess.Read(handle, prefix.AsSpan(read), read);
            if (count == 0)
                throw new EndOfStreamException("Custody payload shrank during observation.");
            read += count;
        }
        return new(path, true, length, new(OperatingSystem.IsMacOS() ? "darwin" : "linux", device, inode), FileHash(prefix));
    }
    private static ChildObservation ObserveChild(int pid)
    {
        // Only the direct child of this current namespace is observed here. A
        // kernel-visible zombie has an identity but cannot execute payload.
        try
        {
            using var input = File.OpenRead($"/proc/{pid}/stat");
            byte[] bytes = new byte[4097];
            int count = input.ReadAtLeast(bytes, bytes.Length, throwOnEndOfStream: false);
            if (count == bytes.Length)
                return new(pid, "unknown", false, false);
            string stat = System.Text.Encoding.UTF8.GetString(bytes, 0, count);
            int end = stat.LastIndexOf(')');
            if (end < 0 || stat.Length <= end + 2)
                return new(pid, "unknown", false, false);
            char state = stat[end + 2];
            return state switch
            {
                'Z' or 'X' => new(pid, state.ToString(), false, true),
                'R' or 'S' or 'D' or 'T' or 't' or 'I' => new(pid, state.ToString(), true, false),
                _ => new(pid, "unknown", false, false)
            };
        }
        catch (FileNotFoundException) { return new(pid, "absent", false, true); }
        catch (DirectoryNotFoundException) { return new(pid, "absent", false, true); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return new(pid, "unknown", false, false); }
    }
    private static string FileHash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static string RunRoot(Configuration configuration) => Path.GetFullPath(Path.Combine(configuration.DataDirectory, "temp", "media-runs-v1"));
    private static string RecordPath(Configuration configuration) => Path.Combine(RunRoot(configuration), "run-0.json");
    private static string LeasePath(Configuration configuration) => Path.Combine(RunRoot(configuration), "processing-0.lease");
    private static string AccountingPath(Configuration configuration) => Path.GetFullPath(Path.Combine(configuration.DataDirectory, "temp", "staging-v1", "reservations.json"));
    private static void WriteExact(string path, byte[] bytes)
    {
        using var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        file.Write(bytes);
        file.Flush(flushToDisk: true);
    }
    private sealed record Configuration(string Operation, string DataDirectory, string InputIso, string ProductionWorker, Guid JobId, string DiscKind)
    {
        public ChdInputs? Chd { get; init; }
        public string Checkpoint { get; init; } = "verified";
        public string ExpectedRecovery { get; init; } = "recovered";
        public int WallTimeoutSeconds { get; init; } = 600;
        public int CpuTimeoutSeconds { get; init; } = 600;
        public int CheckpointWaitSeconds { get; init; } = 60;
        public long MaximumStagingBytes { get; init; } = 16L << 30;
        public long MinimumFreeBytes { get; init; } = 1L << 30;
    }
    private sealed record ChdInputs(string Mode, string? OriginalHostPath, Digests ExpectedSource, long MaximumOutputBytes,
        string ToolDirectory, string NativeDirectory);
    private static void ValidateChd(Configuration configuration, string configPath, string reportPath)
    {
        var inputs = configuration.Chd ?? throw new InvalidDataException("DVD custody requires explicit CHD inputs.");
        Check.That(configuration.Operation != "same-namespace", "CHD same-namespace qualification is separately scoped");
        if (inputs.ExpectedSource is null)
            throw new InvalidDataException("Missing explicit CHD source oracle.");
        Check.That(inputs.Mode is "synthetic" or "ffx", "explicit closed authorized CHD source mode");
        bool real = inputs.Mode == "ffx";
        Check.That(real ? inputs.OriginalHostPath == ProbeFixtures.FinalFantasyX(ProbeFixtures.FromEnvironment()) &&
                inputs.ExpectedSource.Size == 4508221440 : inputs.OriginalHostPath is null && inputs.ExpectedSource.Size is (16L << 20) or (64L << 20),
            "only A4 original or explicitly frozen 16/64 MiB synthetic source; no automatic growth");
        Check.That(Hex(inputs.ExpectedSource.Sha256, 64) && Hex(inputs.ExpectedSource.Sha1, 40) &&
            Hex(inputs.ExpectedSource.Md5, 32) && Hex(inputs.ExpectedSource.Crc, 8, allowZero: true) &&
            inputs.MaximumOutputBytes == (real ? 8L << 30 : checked(inputs.ExpectedSource.Size * 2)), "strict source tuple and explicit authorized output cap");
        var tool = ChdmanCandidate.FromBuildRecord(inputs.ToolDirectory);
        var native = ChdNativeCandidate.FromBuildRecord(inputs.NativeDirectory);
        Check.That(OperatingSystem.IsLinux() && tool.ExpectedIdentity.Rid == native.ExpectedIdentity.Rid &&
            tool.ExpectedIdentity.Rid == NativeRuntimeIdentifier.Current, "paired candidates built for this Linux RID");
        string[] protectedPaths = [tool.BinaryPath, tool.ManifestPath, native.BinaryPath, native.ManifestPath,
            configuration.InputIso, configuration.ProductionWorker, configPath];
        MediaProbePaths.Validate(protectedPaths);
        Check.That(!protectedPaths.Any(path => MediaProbePaths.Overlaps(path, configuration.DataDirectory) ||
            MediaProbePaths.Overlaps(path, reportPath)), "frozen CHD inputs/config isolated from managed data and report");
    }
    private static bool Hex(string? value, int length, bool allowZero = false) => value?.Length == length &&
        value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f') && (allowZero || value.Any(c => c != '0'));
    private static async Task<Digests> ChdOriginalAsync(string path, long length, CancellationToken ct)
    {
        using var handle = NativeFileLease.OpenRegularRead(path);
        await using var stream = new FileStream(handle, FileAccess.Read);
        Check.That(length > 0 && length <= (8L << 30) && stream.Length == length, "bounded regular CHD source/artifact input");
        var result = await Digests.StreamAsync(stream, ct, length);
        Check.That(result.Size == length && stream.Length == length && new FileInfo(path).Length == length, "complete stable-length CHD source/artifact measurement");
        return result;
    }
    private static async Task<LiveToolWitness?> ObserveLiveToolAsync(Configuration configuration,
        MediaProcessingRuntime.MediaProcessingReservation processing, int workerPid, ChdmanCandidate candidate,
        string sourcePath, string outputPath, Stopwatch deadline, CancellationToken ct)
    {
        var inputs = configuration.Chd ?? throw new InvalidDataException("Missing explicit CHD input facts.");
        // Enumerate task-specific children: the tool may be spawned on a CLR threadpool thread.
        string[] tasks = Directory.EnumerateDirectories($"/proc/{workerPid}/task").Take(129).ToArray();
        Check.That(tasks.Length is > 0 and <= 128, "bounded Linux worker task census");
        var children = new HashSet<int>();
        foreach (string task in tasks)
            foreach (string word in Encoding.ASCII.GetString(ReadProcBytes(task + "/children", 4096)).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            {
                Check.That(int.TryParse(word, NumberStyles.None, CultureInfo.InvariantCulture, out int pid) && pid > 1,
                    "strict actual Linux child PID");
                children.Add(pid);
                Check.That(children.Count <= 128, "bounded direct tool children");
            }
        var record = Capture(configuration).Record;
        var worker = ReadLiveStat(workerPid);
        if (worker is null || record.Identity != worker.Identity || record.Phase != "permitted" || worker.Identity.GroupId != workerPid)
            return null;
        var admission = processing.NativeLease ?? throw new InvalidDataException("Missing still-owned native admission description.");
        var parentFd = ReadFdInfo($"/proc/self/fdinfo/{admission.Descriptor}");
        var originalLease = Witness(LeasePath(configuration)); // Existing no-follow regular-file guard is unchanged.
        if (originalLease.FileIdentity != record.Admission || admission.Identity != record.Admission)
            return null;
        using var leaseHandle = NativeFileLease.OpenRegularRead(LeasePath(configuration));
        byte[] nonce = new byte[32];
        if (RandomAccess.GetLength(leaseHandle) != nonce.Length || RandomAccess.Read(leaseHandle, nonce, 0) != nonce.Length ||
            Encoding.ASCII.GetString(nonce) != record.RunId)
            return null;
        foreach (int pid in children.Order())
        {
            ct.ThrowIfCancellationRequested();
            if (deadline.Elapsed.TotalSeconds >= configuration.CheckpointWaitSeconds)
                throw new TimeoutException("Live exact tool checkpoint expired.");
            try
            {
                var before = ReadLiveStat(pid);
                if (before is null || before.ParentId != workerPid || before.Identity.GroupId != workerPid || before.SessionId != worker.SessionId ||
                    before.Identity.Boot != worker.Identity.Boot || before.Identity.PidNamespace != worker.Identity.PidNamespace || before.Nice != worker.Nice ||
                    before.Nice is < 10 or > 19)
                    continue;
                string? imageTarget = new FileInfo($"/proc/{pid}/exe").LinkTarget;
                if (imageTarget != candidate.BinaryPath)
                    continue;
                using var image = File.OpenRead($"/proc/{pid}/exe"); // Explicit kernel image observation, never generic regular-file guard relaxation.
                if (image.Length != candidate.ExpectedIdentity.BinaryBytes)
                    continue;
                string imageHash = await ImageHashAsync(image, candidate.ExpectedIdentity.BinaryBytes, deadline,
                    configuration.CheckpointWaitSeconds, ct);
                if (imageHash != candidate.ExpectedIdentity.BinarySha256)
                    continue;
                byte[] command = ReadProcBytes($"/proc/{pid}/cmdline", 16384);
                string[] argv = Encoding.UTF8.GetString(command).Split('\0', StringSplitOptions.RemoveEmptyEntries);
                string[] expected = [candidate.BinaryPath, .. ChdDvdCanonicalRecipe.ChdmanArguments(sourcePath, outputPath)];
                if (!argv.SequenceEqual(expected) || ReadProcBytes($"/proc/{pid}/environ", 4096).Length != 0)
                    continue;
                var fd = ReadFdInfo($"/proc/{pid}/fdinfo/3");
                string? target = new FileInfo($"/proc/{pid}/fd/3").LinkTarget;
                if (target != LeasePath(configuration) || fd.Inode != record.Admission!.Inode || fd.Inode != parentFd.Inode ||
                    fd.MountId != parentFd.MountId || (fd.Flags & 0x80000) != 0 || (fd.Flags & 3) != (parentFd.Flags & 3) ||
                    fd.Locks.Length == 0 || !fd.Locks.All(line => line.Contains(" FLOCK ", StringComparison.Ordinal) &&
                        line.Contains(" WRITE ", StringComparison.Ordinal)) || !fd.Locks.SequenceEqual(parentFd.Locks))
                    continue;
                string toolLimits = Encoding.ASCII.GetString(ReadProcBytes($"/proc/{pid}/limits", 8192));
                string workerLimits = Encoding.ASCII.GetString(ReadProcBytes($"/proc/{workerPid}/limits", 8192));
                string size = inputs.MaximumOutputBytes.ToString(CultureInfo.InvariantCulture);
                if (LimitPair(toolLimits, "Max file size") != size + " " + size ||
                    new[] { "Max address space", "Max data size", "Max cpu time", "Max core file size" }
                        .Any(name => LimitPair(toolLimits, name) != LimitPair(workerLimits, name)))
                    continue;
                var after = ReadLiveStat(pid);
                var workerAfter = ReadLiveStat(workerPid);
                if (!SameProcess(after, before) || !SameProcess(workerAfter, worker) ||
                    new FileInfo($"/proc/{pid}/exe").LinkTarget != imageTarget || new FileInfo($"/proc/{pid}/fd/3").LinkTarget != target ||
                    processing.NativeLease != admission || admission.Identity != record.Admission ||
                    new FileInfo(outputPath).Length is <= 0 || new FileInfo(outputPath).Length > inputs.MaximumOutputBytes)
                    continue;
                return new(DateTimeOffset.UtcNow, before, after!, worker, workerAfter!, imageTarget!, candidate.ExpectedIdentity.BinaryBytes, imageHash, argv, fd, parentFd,
                    originalLease, Encoding.ASCII.GetString(nonce), toolLimits, workerLimits, EmptyEnvironment: true,
                    RootMustFreshlyReconfirmExactLiveToolBeforeKill: true);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            { /* A disappearing/unreadable exact child cannot establish readiness. */ }
        }
        return null;
    }
    private static async Task<string> ImageHashAsync(FileStream image, long bytes, Stopwatch deadline, int maximumSeconds, CancellationToken ct)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[64 << 10];
        long remaining = bytes;
        while (remaining > 0)
        {
            ct.ThrowIfCancellationRequested();
            if (deadline.Elapsed.TotalSeconds >= maximumSeconds)
                throw new TimeoutException("Live image observation expired.");
            int count = await image.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), ct);
            if (count == 0)
                throw new EndOfStreamException("Observed tool image shrank.");
            sha.AppendData(buffer, 0, count);
            remaining -= count;
        }
        if (image.Length != bytes || image.ReadByte() != -1)
            throw new InvalidDataException("Observed tool image changed length.");
        return Convert.ToHexStringLower(sha.GetHashAndReset());
    }
    private static byte[] ReadProcBytes(string path, int maximum)
    {
        // Kernel pseudo-files only, separately bounded. Do not use this for managed source/output/lease paths.
        using var input = File.OpenRead(path);
        byte[] bytes = new byte[maximum + 1];
        int count = input.ReadAtLeast(bytes, bytes.Length, throwOnEndOfStream: false);
        if (count > maximum)
            throw new InvalidDataException("Oversized Linux custody observation.");
        return bytes[..count];
    }
    private static LiveStat? ReadLiveStat(int pid)
    {
        string raw = Encoding.ASCII.GetString(ReadProcBytes($"/proc/{pid}/stat", 4096));
        int close = raw.LastIndexOf(')');
        if (close < 0)
            throw new InvalidDataException("Malformed live Linux process stat.");
        string[] fields = raw[(close + 2)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length < 20)
            throw new InvalidDataException("Incomplete live Linux process stat.");
        if (fields[0] is not ("R" or "S" or "D" or "T" or "t" or "I"))
            return null;
        var identity = MediaProcessIdentity.Read(pid);
        if (identity is null || identity.Birth != fields[19] || identity.GroupId != int.Parse(fields[2], CultureInfo.InvariantCulture))
            return null;
        return new(identity, int.Parse(fields[1], CultureInfo.InvariantCulture), int.Parse(fields[3], CultureInfo.InvariantCulture),
            int.Parse(fields[16], CultureInfo.InvariantCulture), raw);
    }
    private static FdWitness ReadFdInfo(string path)
    {
        string raw = Encoding.ASCII.GetString(ReadProcBytes(path, 8192));
        string[] lines = raw.Split('\n');
        string Field(string name) => lines.Single(line => line.StartsWith(name + ":", StringComparison.Ordinal))[(name.Length + 1)..].Trim();
        return new(ulong.Parse(Field("ino"), CultureInfo.InvariantCulture), ulong.Parse(Field("mnt_id"), CultureInfo.InvariantCulture),
            Convert.ToUInt64(Field("flags"), 8), lines.Where(line => line.StartsWith("lock:", StringComparison.Ordinal)).ToArray(), raw);
    }
    private static string LimitPair(string raw, string name)
    {
        string line = raw.Split('\n').Single(value => value.StartsWith(name + " ", StringComparison.Ordinal));
        string[] fields = line[name.Length..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length != 3)
            throw new InvalidDataException("Malformed Linux limit observation.");
        return fields[0] + " " + fields[1];
    }
    private static bool SameProcess(LiveStat? actual, LiveStat expected) => actual is not null && actual.Identity == expected.Identity &&
        actual.ParentId == expected.ParentId && actual.SessionId == expected.SessionId && actual.Nice == expected.Nice;
    private sealed record LiveStat(MediaProcessIdentity Identity, int ParentId, int SessionId, int Nice, string RawStat);
    private sealed record FdWitness(ulong Inode, ulong MountId, ulong Flags, string[] Locks, string Raw);
    private sealed record LiveToolWitness(DateTimeOffset ObservedUtc, LiveStat ProcessBefore, LiveStat ProcessAfter,
        LiveStat WorkerBefore, LiveStat WorkerAfter, string ImageTarget, long ImageBytes, string ImageSha256,
        string[] Arguments, FdWitness ToolAdmission, FdWitness ParentAdmission, FileWitness OriginalAdmission, string RunNonce,
        string ToolLimits, string WorkerLimits, bool EmptyEnvironment, bool RootMustFreshlyReconfirmExactLiveToolBeforeKill);
    private sealed record Snapshot(MediaRunRecord Record, MediaProcessIdentity? Identity, string RecordSha256,
        string? IdentitySha256, string AccountingSha256, FileWitness[] Files);
    private static bool SameSnapshot(Snapshot first, Snapshot second) => first.RecordSha256 == second.RecordSha256 &&
        first.IdentitySha256 == second.IdentitySha256 && first.AccountingSha256 == second.AccountingSha256 &&
        first.Files.SequenceEqual(second.Files);
    private static string OtherNamespace(MediaProcessIdentity here, MediaProcessIdentity old) =>
        new[] { "pid:[1]", "pid:[2]", "pid:[3]" }.First(value => value != here.PidNamespace && value != old.PidNamespace);
    internal sealed record FileWitness(string Path, bool Exists, long Bytes, MediaAdmissionIdentity? FileIdentity, string? PrefixSha256);
    private sealed record ChildObservation(int ProcessId, string KernelState, bool Live, bool Finished);
    private sealed class Options(string root) : IRomdOptions
    {
        public string DataDirectory => root;
        public string? DefaultAdminEmail => null;
        public string? DefaultAdminPassword => null;
        public string JwtSecret => string.Empty;
        public long MaxUploadBytes => 16L << 30;
        public string[] AllowedImportPaths => [];
        public string LogsDirectory => root;
    }
    private sealed class CapturingSupervisor(MediaWorkerSupervisor inner) : IMediaWorkerSupervisor
    {
        public MediaWorkerResult? Last { get; private set; }
        public bool IsAvailable => inner.IsAvailable;
        public async Task<MediaWorkerResult> HandshakeAsync(CancellationToken ct = default) => Last = await inner.HandshakeAsync(ct);
        public async Task<MediaWorkerResult> InspectAsync(ITempFile source, CancellationToken ct = default) => Last = await inner.InspectAsync(source, ct);
        public async Task<MediaWorkerResult> InspectIsoAsync(ITempFile source, CancellationToken ct = default) => Last = await inner.InspectIsoAsync(source, ct);
        public async Task<MediaWorkerResult> EncodeAsync(ITempFile source, IWritableTempFile output, long logicalBytes, RomIsoDiscKind kind,
            CancellationToken ct = default) => Last = await inner.EncodeAsync(source, output, logicalBytes, kind, ct);
    }
    [DllImport("libc", EntryPoint = "geteuid")] private static extern uint GetEffectiveUserId();
    [DllImport("libc", EntryPoint = "kill", SetLastError = true)] private static extern int Kill(int processId, int signal);
    [DllImport("libc", EntryPoint = "open", SetLastError = true)] private static extern int Open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);
    [DllImport("libc", EntryPoint = "fstat", SetLastError = true)] private static extern int Fstat(int descriptor, [Out] byte[] stat);
    [DllImport("libc", EntryPoint = "fstat$INODE64", SetLastError = true)] private static extern int FstatDarwin64(int descriptor, [Out] byte[] stat);
}
