using System.Diagnostics;
using System.ComponentModel;
using System.IO.Abstractions;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Admin.Application.Source.Rom;
using Romd.Admin.Application.Storage.Files;
using Romd.Application.Common.Configuration;
using Romd.Application.Common.Content;
using Romd.Domain.Hashing;
using Romd.Domain.Jobs;
using Romd.Domain.Source.Rom;
using Romd.Infrastructure.Hashing;
using Romd.Infrastructure.Jobs;
using Romd.Infrastructure.Jobs.Executors;
using Romd.Infrastructure.Realtime;
using Romd.Infrastructure.Storage;
using Romd.Media.Processes;
using Romd.Media.Inspection;
using Romd.Media.Rvz;
using Romd.Persistence;
using Romd.Persistence.MediaFacts;
using Romd.Persistence.Entities;
using Romd.Persistence.Repositories;
using Romd.Persistence.Repositories.Reconciliation;
using Romd.Admin.Application.Source.Rom.Reconciliation;
using Romd.Storage;

namespace Romd.Media.Probe;

/// <summary>Disposable PostgreSQL/CAS qualification across an externally killed production-image PID1.</summary>
internal static class ConvergenceRecoveryChecks
{
    private const string Version = "romd-media-reconciliation-recovery/v3";
    private const int MaximumReportBytes = 256 * 1024;
    private static readonly JsonSerializerOptions StrictJson = new() { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };

    internal static object CaptureJobState(MediaReconciliationJob job) => new
    {
        job.Id,
        job.Phase,
        job.Outcome,
        job.OutcomeCode,
        Errors = job.Errors.ToArray()
    };

    internal static (string DatabaseName, string Operation) ValidateArguments(string[] args)
    {
        var config = ReadConfiguration(args);
        return (config.DatabaseName, config.Operation);
    }

    private static Configuration ReadConfiguration(string[] args)
    {
        if (args.Length != 3 || !Path.IsPathFullyQualified(args[1]) || !Path.IsPathFullyQualified(args[2]))
            throw new ArgumentException("Usage: --convergence-recovery-probe <absolute configuration.json> <absolute new report.json>");
        var config = Read<Configuration>(args[1], 16384);
        Validate(config, args[1], args[2]);
        return config;
    }

    internal static async Task RunAsync(string[] args, DbContextOptions<RomdDbContext> suppliedOptions, CancellationToken ct)
    {
        var config = ReadConfiguration(args);
        await using (var db = new RomdDbContext(suppliedOptions))
        {
            Check.That(db.Database.ProviderName == "Npgsql.EntityFrameworkCore.PostgreSQL" &&
                db.Database.GetDbConnection().Database == config.DatabaseName &&
                await db.Database.SqlQueryRaw<string>("SELECT current_database() AS \"Value\"").SingleAsync(ct) == config.DatabaseName,
                "supplied options target the exact disposable PostgreSQL database");
            Check.That((await db.Database.GetAppliedMigrationsAsync(ct)).SequenceEqual(db.Database.GetMigrations()) &&
                db.Database.GetMigrations().Single().EndsWith("_FreshMediaBaseline", StringComparison.Ordinal),
                "caller provisioned the one current disposable fresh baseline");
        }
        await using var report = new FileStream(args[2], FileMode.CreateNew, FileAccess.Write, FileShare.Read, 4096, FileOptions.Asynchronous);
        var rows = new List<object>();
        var here = MediaProcessIdentity.Read(Environment.ProcessId);
        Check.That(here is { ProcessId: 1, Platform: "linux" } && GetEffectiveUserId() != 0,
            "qualification runs as non-root Linux container PID1");
        var assemblies = await CheckImageAsync(config.ProductionWorker, ct);
        var options = new Options(config.DataDirectory);
        var limits = new MediaProcessingOptions
        {
            WorkerPath = config.ProductionWorker,
            MaxConcurrent = 1,
            AdmissionTimeoutSeconds = 1,
            WallTimeoutSeconds = config.WallTimeoutSeconds,
            CpuTimeoutSeconds = config.CpuTimeoutSeconds
        };
        var stagingLimits = new TempStorageOptions
        {
            MaximumFiles = 3,
            MaximumBytes = config.MaximumStagingBytes,
            MinimumFreeBytes = config.MinimumFreeBytes,
            AdmissionTimeoutSeconds = 1
        };
        var claimLimits = new JobExecutionClaimOptions
        {
            LeaseDuration = TimeSpan.FromSeconds(config.LeaseSeconds),
            HeartbeatInterval = TimeSpan.FromSeconds(config.HeartbeatSeconds)
        };
        DateTimeOffset started = DateTimeOffset.UtcNow;
        SeedState? state = null;
        JobWitness? currentJob = null;
        object? lastExecution = null;
        object? checkpointFailure = null;
        string current = "initializing";
        await SaveAsync("in-progress", null, ct);
        try
        {
            await using var environment = new EnvironmentState(config, suppliedOptions, options, limits, stagingLimits, claimLimits);
            switch (config.Operation)
            {
                case "seed":
                    await SeedAsync(environment, ct);
                    break;
                case "start":
                    state = Read<SeedState>(config.StatePath, 32768);
                    CheckState(config, state, assemblies);
                    await AssertPinnedAsync(environment, state, requireAttemptPin: false, ct);
                    environment.Pause = PauseAsync;
                    await ExecuteAsync(environment, state, null, ct);
                    throw new InvalidDataException("External kill checkpoint was not reached.");
                case "assert-pinned":
                    state = Read<SeedState>(config.StatePath, 32768);
                    CheckState(config, state, assemblies);
                    var ready = Read<ReadyState>(config.ReadyPath, MaximumReportBytes);
                    CheckReady(config, state, ready, here!);
                    await AssertPinnedAsync(environment, state, requireAttemptPin: true, ct);
                    var held = Capture(config);
                    AssertCustody(held, state.JobId, config.Checkpoint, requireHeld: true);
                    Check.That(held.RecordSha256 == ready.Custody.RecordSha256 && held.AccountingSha256 == ready.Custody.AccountingSha256,
                        "read-only observer leaves media run and staging accounting unchanged");
                    Check.That(currentJob!.FenceToken == ready.Job.FenceToken && currentJob.LeaseExpiresAt > DateTimeOffset.UtcNow,
                        "old process continues renewing the genuine execution lease");
                    rows.Add(new
                    {
                        Step = "read-only-live-source-and-custody",
                        Job = currentJob,
                        Custody = held,
                        ObserverNamespace = here!.PidNamespace,
                        SourcePinHeld = true,
                        RecoveryInvoked = false
                    });
                    break;
                case "recover":
                    state = Read<SeedState>(config.StatePath, 32768);
                    CheckState(config, state, assemblies);
                    var old = Read<ReadyState>(config.ReadyPath, MaximumReportBytes);
                    CheckReady(config, state, old, here!);
                    var retained = Capture(config);
                    Check.That(retained.RecordSha256 == old.Custody.RecordSha256 && retained.IdentitySha256 == old.Custody.IdentitySha256 &&
                        retained.AccountingSha256 == old.Custody.AccountingSha256 && old.Custody.Files.All(previous => retained.Files.Any(file =>
                            file.Slot == previous.Slot && file.Path == previous.Path && file.ReservedBytes == previous.ReservedBytes && file.LeaseIdentity == previous.LeaseIdentity &&
                            (config.Checkpoint == "nonempty" && previous.Role == "output" ? file.Bytes >= previous.Bytes : file.Bytes == previous.Bytes))),
                        "external kill preserves exact original durable custody facts");
                    AssertCustody(retained, state.JobId, config.Checkpoint, requireHeld: false);
                    current = "waiting for actual lease expiry";
                    await SaveAsync("in-progress", null, ct);
                    long waitStarted = Stopwatch.GetTimestamp();
                    while (true)
                    {
                        await AssertPinnedAsync(environment, state, requireAttemptPin: true, ct);
                        Check.That(currentJob!.FenceToken == old.Job.FenceToken, "old execution fence unchanged before reclaim");
                        if (currentJob.LeaseExpiresAt <= DateTimeOffset.UtcNow)
                            break;
                        Check.That(Stopwatch.GetElapsedTime(waitStarted).TotalSeconds < config.LeaseExpiryWaitSeconds, "bounded genuine lease-expiry wait");
                        await Task.Delay(TimeSpan.FromSeconds(1), ct);
                    }
                    var foreignPidLookupAttempts = new List<int>();
                    var recovery = new MediaProcessingRuntime(Path.Combine(config.DataDirectory, "temp", "media-runs-v1"), limits, pid =>
                    {
                        foreignPidLookupAttempts.Add(pid);
                        throw new InvalidDataException("Foreign recovery attempted a numeric PID lookup.");
                    });
                    var stop = await recovery.EnsureJobStoppedAsync(state.JobId, ct);
                    Check.That(!stop.IsError, "independent production custody recovery certifies old payload stopped or extinct");
                    Check.That(foreignPidLookupAttempts.Count == 0, "production recovery attempted no old numeric PID lookup");
                    await AssertPinnedAsync(environment, state, requireAttemptPin: true, ct);
                    rows.Add(new
                    {
                        Step = "old-attempt-custody-recovered",
                        OldParent = old.Parent,
                        CurrentParent = here,
                        ActualExpiredLease = currentJob,
                        WaitSeconds = Stopwatch.GetElapsedTime(waitStarted).TotalSeconds,
                        OriginalCustody = retained,
                        SourcePinStillHeld = true,
                        ObservedForeignOsReap = false,
                        ForeignNumericPidLookupAttempts = foreignPidLookupAttempts.ToArray()
                    });
                    await SaveAsync("in-progress", null, ct);
                    await ExecuteAsync(environment, state, old.Job.FenceToken, ct);
                    await AssertPublishedAsync(environment, state, ct);
                    await ReadBothAsync(environment, state, ct);
                    await ReleaseOriginalAsync(environment, state, ct);
                    await ReplayAsync(environment, state, ct);
                    var empty = Capture(config);
                    Check.That(empty.Record is null && empty.Files.Length == 0 && !Directory.EnumerateFiles(StagingRoot(config), "*.quarantine").Any(),
                        "terminal attempt releases all confirmed processing and staging custody");
                    rows.Add(new { Step = "terminal-custody-released", Custody = empty });
                    break;
                case "replay":
                    state = Read<SeedState>(config.StatePath, 32768);
                    CheckState(config, state, assemblies);
                    await AssertPublishedAsync(environment, state, ct);
                    await ReplayAsync(environment, state, ct);
                    break;
            }
            await SaveAsync("passed", null, ct);
        }
        catch (Exception error)
        {
            await SupervisorChecks.PreserveFailureAsync(error, token => SaveAsync("failed", error.GetType().Name, token), CancellationToken.None);
        }

        async Task SeedAsync(EnvironmentState environment, CancellationToken token)
        {
            current = "independent read-only oracles and DAT identity";
            await SaveAsync("in-progress", null, token);
            await using (var db = environment.Context())
                Check.That(!await db.Jobs.AnyAsync(token) && !await db.RomFiles.AnyAsync(token) && !await db.Files.AnyAsync(token),
                    "seeding uses an empty disposable ROM/job/file ledger");
            var originals = await Digests.FileAsync(config.Fixture.Iso, token);
            var uploaded = await Digests.FileAsync(config.Fixture.UploadedRvz, token);
            var datHashes = await Digests.FileAsync(config.Fixture.Dat, token);
            byte[] dat = BoundedRuntimeFile.Read(config.Fixture.Dat, 64 * 1024 * 1024);
            using var datStream = new MemoryStream(dat, writable: false);
            var expected = XDocument.Load(datStream).Descendants("game").Single(game => (string?)game.Attribute("id") == config.Fixture.DatId)
                .Elements("rom").Single();
            Check.That(originals.Size == (long)expected.Attribute("size")! && originals.Crc == ((string?)expected.Attribute("crc"))?.ToLowerInvariant() &&
                originals.Md5 == ((string?)expected.Attribute("md5"))?.ToLowerInvariant() && originals.Sha1 == ((string?)expected.Attribute("sha1"))?.ToLowerInvariant(),
                "independent ISO matches all Redump DAT hashes and length");
            Check.That((await environment.Supervisor.HandshakeAsync(token)).IsSuccess, "packaged production media worker handshake");
            var preparation = ProbeMediaFacts.ManagedPreparation(new HashingService(), environment.Verifier, new ProbeMediaFacts.EmptyReferences());
            IRomPreparationAdmission? admission = null;
            await using var input = File.OpenRead(config.Fixture.Iso);
            string filename = Path.GetFileName(config.Fixture.Iso);
            try
            {
                await using var staged = await environment.Staging.CreateAsync(input, token,
                    prefix => preparation.RequiresDatHashes(prefix, filename),
                    async (prefix, admissionToken) => admission = await preparation.AdmitAsync(prefix, filename, admissionToken),
                    prefix => RomPreparationDetection.SelectPrefixLength(prefix, filename));
                Check.That(admission is not null, "real ISO inspection admitted before staging copy");
                var result = await admission!.PrepareAsync(staged, filename, token);
                admission.Dispose();
                admission = null;
                Check.That(!result.IsError, "production preparation accepts real ISO");
                await using var prepared = (Romd.Admin.Application.Source.Rom.PreparedRom.WholeFile)result.Value;
                AssertLogical(prepared.Verification, originals);
                Check.That(prepared.Encoding == RomRepresentationEncoding.Identity && prepared.Eligibility is
                { Status: RomIsoEligibilityStatus.Supported, DiscKind: not null }, "content inspection establishes supported original disc");
                if (config.Fixture.NintendoKind is not null)
                    Check.That(prepared.Eligibility!.DiscKind!.Value.ToString().Equals(config.Fixture.NintendoKind, StringComparison.OrdinalIgnoreCase) &&
                        prepared.Eligibility.StandardKeyPartitions == config.Fixture.StandardKeyPartitions &&
                        prepared.Eligibility.KoreanKeyPartitions == config.Fixture.KoreanKeyPartitions, "fixture expected layout and common-key indexes match actual inspection");
                await using var db = environment.Context();
                var publisher = new RomRepresentationPublisher(environment.Files(db), new EfUnitOfWork(db), Romd.PostgreSql.TestSupport.MediaTestServices.CreateRomRepository(db), environment.Policies,
                    environment.Encodings, new FileMutationLock(db), null, new MediaFactPublisher(db), ProbeMediaFacts.Observations(db), ProbeMediaFacts.Creations(db), ProbeMediaFacts.Audit, TimeProvider.System, new Romd.Persistence.Repositories.Reconciliation.MediaReconciliationRequests(db, TimeProvider.System), Romd.PostgreSql.TestSupport.MediaTestServices.CreatePs2Authority(db));
                var published = await publisher.PublishAsync(prepared, null, true, (_, _, publishToken) =>
                    { publishToken.ThrowIfCancellationRequested(); return Task.CompletedTask; }, _ => null, token);
                Check.That(!published.IsError, "production publication retains original and durable automatic intent");
                var subject = await db.Set<MediaReconciliationSubjectEntity>().SingleAsync(row => row.SubjectKind == "LogicalRom" && row.SubjectId == published.Value.Id, token);
                var content = await ProbeMediaFacts.DescribeAsync(db, published.Value.Id, token);
                Check.That(content.FileId == published.Value.FileId && content.Identity.Sha256.ToString() == originals.Sha256 &&
                    content.Preferred.ArtifactSha256.ToString() == originals.Sha256 &&
                    await db.JobDispatches.AnyAsync(dispatch => dispatch.JobId == subject.LatestJobId, token) &&
                    !await db.Set<MediaReconciliationPinEntity>().AnyAsync(token), "general demand and dispatch agree with retained ISO before worker source capture");
                state = new(Version, config.DatabaseName, config.DataDirectory, config.Fixture, subject.LatestJobId, content.RomId,
                    content.FileId, RvzCanonicalRecipe.Id, originals, uploaded, datHashes, Snapshot(content), prepared.Verification.Id,
                    here!, assemblies, DateTimeOffset.UtcNow);
                await WriteNewAsync(config.StatePath, state, 32768, token);
                rows.Add(new { Step = "seeded-real-original", State = state, Children = environment.Supervisor.Events.ToArray() });
            }
            finally { admission?.Dispose(); }
        }

        async Task PauseAsync(EnvironmentState environment, string checkpoint, int? childPid, CancellationToken token)
        {
            try
            { await PauseCoreAsync(environment, checkpoint, childPid, token); }
            catch (Exception error)
            {
                checkpointFailure = new
                {
                    Checkpoint = checkpoint,
                    FailureType = error.GetType().Name,
                    Message = error.Message[..Math.Min(error.Message.Length, 4096)]
                };
                throw;
            }
        }

        async Task PauseCoreAsync(EnvironmentState environment, string checkpoint, int? childPid, CancellationToken token)
        {
            Check.That(state is not null && config.Operation == "start" && checkpoint == config.Checkpoint, "exact externally killed checkpoint");
            await AssertPinnedAsync(environment, state!, requireAttemptPin: true, token);
            var custody = Capture(config);
            AssertCustody(custody, state!.JobId, checkpoint, requireHeld: true);
            Check.That(currentJob!.FenceToken is not null && currentJob.LeaseExpiresAt > DateTimeOffset.UtcNow,
                "durable phase checkpoint retains live real execution lease");
            Check.That(currentJob.Phase == (checkpoint == "nonempty" ? "Encoding" : UpperFirst(checkpoint)), "persisted checkpoint phase matches runtime pause");
            if (checkpoint == "nonempty")
                Check.That(custody.Record is { Phase: "permitted" } && custody.Record.Identity?.ProcessId == childPid &&
                    custody.Files.Any(file => file.Role == "output" && file.Bytes > 0), "permitted encoding child has written actual output before external kill");
            var evidence = new ReadyState(Version, config.Checkpoint, state.JobId, here!, currentJob, custody,
                environment.Supervisor.LastRun, environment.Supervisor.Events.ToArray(), environment.Supervisor.ChildEnvironments.ToArray(),
                DateTimeOffset.UtcNow);
            if (checkpoint == "publishing")
                Check.That(evidence.LastStoppedRun is { Phase: "stopped" } && evidence.LastStoppedRun.JobId == state.JobId &&
                    evidence.LastStoppedRun.Admission == custody.ProcessingIdentity, "publishing released the exact stopped processing run before its staged output is published");
            rows.Add(new { Step = "external-kill-ready", Ready = evidence, RootMustKillContainerExternally = true });
            current = "external kill ready at " + checkpoint;
            await SaveAsync("ready", null, token);
            await WriteNewAsync(config.ReadyPath, evidence, MaximumReportBytes, token);
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        }

        async Task ExecuteAsync(EnvironmentState environment, SeedState source, Guid? oldFence, CancellationToken token)
        {
            current = "claimed production convergence executor";
            await SaveAsync("in-progress", null, token);
            Check.That((await environment.Supervisor.HandshakeAsync(token)).IsSuccess, "production worker healthy before execution");
            await using var scope = environment.CreateExecutionScope();
            var db = scope.ServiceProvider.GetRequiredService<RomdDbContext>();
            var claim = await environment.Claims(db).TryClaimExecutionAsync(source.JobId, "linux-recovery-probe", token)
                ?? throw new InvalidDataException("Real execution claim unavailable.");
            Check.That(oldFence is null || claim.FenceToken != oldFence, "recovery obtains a new fenced claim after old lease expiry");
            var executor = scope.ServiceProvider.GetRequiredService<MediaReconciliationJobExecutor>();
            int before = environment.Supervisor.Events.Count;
            int checkpoints = 0, renewals = 0;
            using var run = CancellationTokenSource.CreateLinkedTokenSource(token);
            string? deferralReason = null;
            var context = new JobContext(null, async checkpointToken =>
            {
                await using var checkpointDb = environment.Context();
                Check.That(await environment.Claims(checkpointDb).TryUpdateClaimedAsync(claim.Job, claim.FenceToken, checkpointToken), "real fenced phase persisted");
                Interlocked.Increment(ref checkpoints);
                if (config.Operation == "start" && UpperFirst(config.Checkpoint) == claim.Job.Phase)
                    await PauseAsync(environment, config.Checkpoint, null, checkpointToken);
            }, run.Token, async ownershipToken =>
            {
                await using var ownership = environment.Context();
                return await environment.Claims(ownership).HasExecutionOwnershipAsync(source.JobId, claim.FenceToken, ownershipToken);
            }, jobId: source.JobId, onDeferral: (reason, _) => deferralReason = reason);
            Task heartbeat = HeartbeatAsync(run.Token);
            try
            {
                using var fence = JobExecutionFenceScope.Enter(source.JobId, claim.FenceToken, TimeProvider.System);
                await executor.ExecuteAsync(claim.Job, context);
            }
            finally
            {
                await run.CancelAsync();
                lastExecution = new
                {
                    Job = CaptureJobState(claim.Job),
                    claim.FenceToken,
                    context.DeferredUntil,
                    DeferralReason = deferralReason,
                    Children = environment.Supervisor.Events.Skip(before).ToArray(),
                    ChildEnvironmentKeys = environment.Supervisor.ChildEnvironments.ToArray()
                };
                try
                { await heartbeat; }
                catch (OperationCanceledException) when (run.IsCancellationRequested) { }
            }
            Check.That(context.DeferredUntil is null && claim.Job.Outcome == MediaReconciliationOutcomeKind.Completed,
                "real convergence completes without deferral or file failure");
            var children = environment.Supervisor.Events.Skip(before).ToArray();
            Check.That(children.Select(child => child.Operation).SequenceEqual(new[] { "inspect-iso", "encode", "inspect-rvz" }) &&
                children.All(child => child.Result.IsSuccess) && children.Select(child => child.Result.Execution!.ProcessId).Distinct().Count() == 3,
                "recovery inspects source, encodes and verifies output in three distinct stopped children");
            foreach (var child in children.Where(child => child.Operation != "encode"))
            {
                var inspection = child.Result.Reply!.Inspection!;
                Check.That(new Digests(inspection.LogicalBytes, inspection.Crc32, inspection.Md5, inspection.Sha1, inspection.LogicalSha256) == source.Original,
                    "source and output child proofs independently match complete Redump identity");
            }
            var encoding = children.Single(child => child.Operation == "encode").Result.Reply!.Encoding!;
            Check.That(encoding.RecipeVersion == source.RecipeVersion && encoding.PriorityKind == "unix-nice" && encoding.PriorityValue >= 10,
                "canonical encoder reports reduced Linux priority");
            rows.Add(new
            {
                Step = "reclaimed-and-converged",
                claim.FenceToken,
                Checkpoints = checkpoints,
                LeaseRenewals = renewals,
                Children = children,
                ChildEnvironmentKeys = environment.Supervisor.ChildEnvironments.ToArray()
            });

            async Task HeartbeatAsync(CancellationToken heartbeatToken)
            {
                try
                {
                    while (true)
                    {
                        heartbeatToken.ThrowIfCancellationRequested();
                        await using var lease = environment.Context();
                        if (!await environment.Claims(lease).RenewExecutionLeaseAsync(source.JobId, claim.FenceToken, heartbeatToken))
                        {
                            if (await lease.Set<MediaReconciliationOutcomeEntity>().AnyAsync(row => row.JobId == source.JobId, heartbeatToken))
                                return;
                            throw new JobExecutionOwnershipLostException();
                        }
                        Interlocked.Increment(ref renewals);
                        await Task.Delay(claimLimits.HeartbeatInterval, heartbeatToken);
                    }
                }
                catch { await run.CancelAsync(); throw; }
            }
        }

        async Task AssertPinnedAsync(EnvironmentState environment, SeedState source, bool requireAttemptPin, CancellationToken token)
        {
            await using var db = environment.Context();
            var preferred = await ProbeMediaFacts.DescribeAsync(db, source.RomId, token);
            var retained = preferred.Preferred;
            Check.That(Snapshot(preferred) == source.Source && !await db.Set<MediaReconciliationOutcomeEntity>().AnyAsync(row => row.JobId == source.JobId, token),
                "retained source identity is immutable before confirmed terminal publication");
            if (requireAttemptPin)
            {
                var pin = await db.Set<MediaReconciliationPinEntity>().SingleAsync(row => row.JobId == source.JobId, token);
                Check.That(pin.FileId == source.SourceFileId && pin.RepresentationId == source.Source.SourceRepresentationId &&
                    pin.OutputId == source.Source.SourceOutputId && pin.VerificationId == source.OriginalVerificationId,
                    "the actual captured attempt retains its exact interpretation, output and source proof");
            }
            else
                Check.That(!await db.Set<MediaReconciliationPinEntity>().AnyAsync(row => row.JobId == source.JobId, token), "import has no premature execution pin");
            Check.That(preferred.FileId == source.SourceFileId && retained.Encoding == RomRepresentationEncoding.Identity &&
                await db.Files.AnyAsync(row => row.Id == source.SourceFileId, token) &&
                await environment.Cas.ExistsAsync(StorageKey.FromHash(Sha256.Parse(source.Original.Sha256)), token), "owned or captured source retains original ledger and CAS bytes");
            var job = await db.Jobs.OfType<MediaReconciliationJobEntity>().SingleAsync(row => row.Id == source.JobId, token);
            currentJob = new(job.Id, job.Phase, job.ExecutionFenceToken, job.ExecutionLeaseExpiresAtUtc, job.CompletedAt);
        }

        async Task AssertPublishedAsync(EnvironmentState environment, SeedState source, CancellationToken token)
        {
            await using var db = environment.Context();
            var journal = await db.Set<MediaReconciliationOutcomeEntity>().SingleAsync(row => row.JobId == source.JobId, token);
            var job = await db.Jobs.OfType<MediaReconciliationJobEntity>().SingleAsync(row => row.Id == source.JobId, token);
            var retained = (await ProbeMediaFacts.DescribeAsync(db, source.RomId, token)).Preferred;
            var preferred = await ProbeMediaFacts.DescribeAsync(db, source.RomId, token);
            var creation = retained.Facts.Creations.Single();
            var sourceWitness = creation.Sources.Single();
            var identity = preferred.Identity;
            Check.That(journal.Kind == MediaReconciliationOutcomeKind.Completed.ToString() && retained.FileId == preferred.FileId &&
                retained.Encoding == RomRepresentationEncoding.Rvz && environment.Policies.Classify(retained.Container, environment.Policies.ResolveObservedKind(preferred)) == RomRepresentationProfileClass.Canonical &&
                job.Phase == "Completed" && job.CompletedAt is not null && job.ExecutionFenceToken is null && job.ExecutionLeaseExpiresAtUtc is null &&
                !await db.Set<MediaReconciliationPinEntity>().AnyAsync(row => row.JobId == source.JobId, token), "atomic immutable outcome, preferred single canonical RVZ and source-pin release");
            Check.That(sourceWitness.ArtifactSha256.ToString() == source.Source.ArtifactSha256 && sourceWitness.ArtifactSize == source.Source.ArtifactSize &&
                sourceWitness.FileId == source.SourceFileId && sourceWitness.RepresentationId == source.Source.SourceRepresentationId &&
                sourceWitness.OutputId == source.Source.SourceOutputId && sourceWitness.VerificationId == source.OriginalVerificationId &&
                identity.Sha256.ToString() == source.Original.Sha256 && identity.Size == source.Original.Size &&
                identity.Sha1.ToString() == source.Original.Sha1 && identity.Md5.ToString() == source.Original.Md5 && identity.Crc32.ToString() == source.Original.Crc &&
                retained.Facts.Verifications.Any(proof => proof.Id == creation.VerificationId) && creation.Recipe.Value == source.RecipeVersion &&
                retained.Facts.Verifications.Any(proof => proof.Id == creation.VerificationId && proof.CheckedProfile == RvzVerifier.Profile), "full source-equivalence and separate new-profile output observation survive source release");
            currentJob = new(job.Id, job.Phase, job.ExecutionFenceToken, job.ExecutionLeaseExpiresAtUtc, job.CompletedAt);
            var historical = Read<SeedState>(config.StatePath, 32768);
            Check.That(historical.Source == source.Source && historical.JobId == source.JobId && historical.Original == source.Original &&
                historical.OriginalVerificationId == source.OriginalVerificationId, "durable original snapshot survives source release");
            rows.Add(new
            {
                Step = "publication-journal",
                Journal = new { journal.JobId, Kind = journal.Kind.ToString(), OutputFileId = retained.FileId, OutputVerificationId = creation.VerificationId, CompletedAt = new DateTimeOffset(journal.CompletedAtUtcTicks, TimeSpan.Zero) },
                Creation = new
                {
                    creation.Id,
                    Source = sourceWitness,
                    OutputSha256 = retained.ArtifactSha256.ToString(),
                    retained.ArtifactSize,
                    Recipe = creation.Recipe.Value,
                    creation.EncoderBuild
                },
                HistoricalSeedSnapshotSurvives = true
            });
        }

        async Task ReadBothAsync(EnvironmentState environment, SeedState source, CancellationToken token)
        {
            var catalog = new ProbeCatalog(suppliedOptions);
            var descriptor = await catalog.FindByRomIdAsync(source.RomId, token) ?? throw new InvalidDataException("Retained catalog missing.");
            Check.That(descriptor.Artifacts is { Count: 1 } && descriptor.Artifacts[0].IsPreferred && descriptor.Artifacts[0].HasMeasuredEvidence,
                "production catalog projects one measured preferred artifact");
            await using var decoder = new RomRepresentationDecoder(new RomMediaAdapterRegistry(), new ReadDecoderOptions());
            var reader = new RomContentReader(catalog, environment.Cas, decoder, ProbeMediaFacts.ManagedAdmission);
            var targets = RomDeliveryPlanner.Plan(descriptor, "disc.iso", RomMediaPolicyComposition.CreateDeliveryPolicy(), chdAdmission: ProbeMediaFacts.ManagedAdmission);
            Check.That(targets.Count == 2, "production planner exposes both formats");
            foreach (var option in targets)
            {
                Check.That(option.State == RomDeliveryState.Available && option.Target is not null, "both formats available after restart");
                var target = option.Target!;
                long readStarted = Stopwatch.GetTimestamp();
                await using var stream = await reader.OpenAsync(target, token) ?? throw new InvalidDataException("Delivery missing.");
                var digest = await Digests.StreamAsync(stream, token, target.SizeBytes);
                Check.That(digest.Size == target.SizeBytes && digest.Sha256 == target.Sha256.ToString() && digest.Md5 == target.Md5?.ToString() &&
                    digest.Sha1 == target.Sha1?.ToString(), "every advertised checksum describes the delivered bytes");
                if (target.Format == RomDeliveryFormat.Original)
                    Check.That(digest == source.Original, "reconstructed full ISO equals independent Redump oracle");
                else
                {
                    await using var raw = await environment.Cas.OpenRandomAccessAsync(StorageKey.FromHash(target.ArtifactSha256), token)
                        ?? throw new InvalidDataException("Retained raw artifact missing.");
                    await using var rawStream = raw.AsStream(leaveOpen: true);
                    Check.That(digest == await Digests.StreamAsync(rawStream, token, target.SizeBytes), "full RVZ delivery equals exact raw CAS bytes");
                }
                rows.Add(new
                {
                    Step = "recovered-delivery",
                    Format = RomDeliveryFormats.ToWire(target.Format),
                    target.Filename,
                    Hashes = digest,
                    FullReadSeconds = Stopwatch.GetElapsedTime(readStarted).TotalSeconds
                });
            }
            Check.That(await Digests.FileAsync(source.Fixture.Iso, token) == source.Original &&
                await Digests.FileAsync(source.Fixture.UploadedRvz, token) == source.UploadedRvz &&
                await Digests.FileAsync(source.Fixture.Dat, token) == source.Dat, "read-only real fixtures unchanged after recovery");
        }

        async Task ReleaseOriginalAsync(EnvironmentState environment, SeedState source, CancellationToken token)
        {
            await using var db = environment.Context();
            Check.That(await new MediaRepresentationCleanup(db).CollectAsync(256, token) == 1, "released source representation is collected before its file");
            Check.That(await environment.Files(db).DeleteIfUnreferencedAsync(source.SourceFileId, token), "released source file is GC eligible after terminal journal");
            Check.That(!await db.Files.AnyAsync(row => row.Id == source.SourceFileId, token) &&
                !await environment.Cas.ExistsAsync(StorageKey.FromHash(Sha256.Parse(source.Original.Sha256)), token) &&
                await ProbeMediaFacts.CreationsAsync(db, source.RomId, token) == 1 &&
                await db.Set<MediaReconciliationOutcomeEntity>().CountAsync(row => row.JobId == source.JobId, token) == 1,
                "source equivalence and immutable journal survive original file/CAS GC");
            rows.Add(new
            {
                Step = "original-GC",
                OriginalFileId = source.SourceFileId,
                SourceSnapshot = source.Source,
                ReleasedOriginal = true,
                SourceEquivalenceSurvives = true
            });
        }

        async Task ReplayAsync(EnvironmentState environment, SeedState source, CancellationToken token)
        {
            await using var db = environment.Context();
            var before = await ReplayWitnessAsync(db, source, token);
            int children = environment.Supervisor.Events.Count;
            await using var scope = environment.CreateExecutionScope();
            var runner = new JobRunner<MediaReconciliationJob>(scope.ServiceProvider.GetRequiredService<IServiceScopeFactory>(),
                scope.ServiceProvider.GetRequiredService<MediaReconciliationJobExecutor>(), new NoNotification(), options, TimeProvider.System,
                claimLimits, NullLogger<JobRunner<MediaReconciliationJob>>.Instance);
            await runner.RunAsync(source.JobId, "completed-journal-redelivery", token);
            await using var afterDb = environment.Context();
            var after = await ReplayWitnessAsync(afterDb, source, token);
            Check.That(before == after && environment.Supervisor.Events.Count == children, "terminal redelivery creates no child, evidence, source pin, outcome or preferred-artifact change");
            rows.Add(new
            {
                Step = "terminal-journal-redelivery",
                Before = before,
                After = after,
                NewChildren = 0,
                ProductionRunner = true,
                ProductionClaimRepository = true,
                RealtimeNotificationsQualified = false
            });
        }

        async Task SaveAsync(string status, string? failureType, CancellationToken token)
        {
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(new
            {
                Version,
                Status = status,
                Current = current,
                StartedAt = started,
                RecordedAt = DateTimeOffset.UtcNow,
                config.Operation,
                config.Checkpoint,
                config.DatabaseName,
                config.DataDirectory,
                config.StatePath,
                config.ReadyPath,
                Parent = here,
                UserId = GetEffectiveUserId(),
                Runtime = RuntimeInformation.FrameworkDescription,
                Assemblies = assemblies,
                ProcessingLimits = limits,
                StagingLimits = stagingLimits,
                ExecutionClaimLimits = claimLimits,
                CurrentJob = currentJob,
                LastExecution = lastExecution,
                CheckpointFailure = checkpointFailure,
                Rows = rows,
                FailureType = failureType,
                Scope = "Disposable PostgreSQL/raw CAS, production-image Linux PID1 external kill/recreation, genuine DB lease expiry/fenced reclaim, " +
                    "durable original source pin, independent media custody recovery, real source/encode/output children, atomic journal/equivalence/GC and both full delivery formats. " +
                    "Foreign-namespace recovery establishes payload extinction, not observed OS reap. No HTTP, VFS mount, gameplay, sustained load or full-suite claim. Persistent scratch is always preserved for the caller."
            }, JsonOptions.Value);
            Check.That(bytes.Length <= MaximumReportBytes && rows.Count <= 32, "bounded recovery evidence report");
            report.Position = 0;
            report.SetLength(0);
            await report.WriteAsync(bytes, token);
            await report.FlushAsync(token);
            report.Flush(flushToDisk: true);
        }
    }

    private static void Validate(Configuration config, string configPath, string reportPath)
    {
        Check.That(config.Operation is "seed" or "start" or "recover" or "assert-pinned" or "replay" &&
            config.Checkpoint is "encoding" or "nonempty" or "verifying" or "publishing", "closed recovery operations and checkpoints");
        Check.That(config.DatabaseName is not null && Regex.IsMatch(config.DatabaseName, "^romd_gate9_[a-z0-9_]{1,48}$", RegexOptions.CultureInvariant),
            "only unmistakably disposable Gate 9 databases are permitted");
        if (config.Fixture is null)
            throw new InvalidDataException("Missing recovery qualification fixture.");
        var paths = new[] { configPath, reportPath, config.DataDirectory, config.StatePath, config.ReadyPath, config.ProductionWorker,
            config.Fixture.Iso, config.Fixture.UploadedRvz, config.Fixture.Dat };
        MediaProbePaths.Validate(paths);
        Check.That(MediaProbePaths.Contains(config.DataDirectory, config.StatePath) &&
            MediaProbePaths.Contains(config.DataDirectory, config.ReadyPath) &&
            !paths.Skip(6).Any(path => MediaProbePaths.Overlaps(config.DataDirectory, path) ||
                new[] { configPath, reportPath, config.StatePath, config.ReadyPath }.Any(control => MediaProbePaths.Overlaps(control, path))),
            "durable seed/ready evidence is isolated from immutable fixture paths");
        MediaProbePaths.OutsideManagedStorage(config.DataDirectory, configPath, reportPath, config.StatePath, config.ReadyPath);
        Check.That(config.ProductionWorker == "/app/media-worker/Romd.Media.Worker.dll" && config.LeaseSeconds is >= 10 and <= 120 &&
            config.HeartbeatSeconds is >= 1 and <= 30 && config.HeartbeatSeconds * 2 < config.LeaseSeconds &&
            config.CheckpointWaitSeconds is >= 1 and <= 60 && config.LeaseExpiryWaitSeconds >= config.LeaseSeconds && config.LeaseExpiryWaitSeconds <= 300 &&
            config.WallTimeoutSeconds is >= 30 and <= 3600 && config.CpuTimeoutSeconds is >= 30 and <= 3600 &&
            config.MaximumStagingBytes is >= (16L << 30) and <= (128L << 30) && config.MinimumFreeBytes is >= 0 and <= (16L << 30),
            "bounded qualification limits and untouched production child");
        Check.That(config.Operation != "seed" || !File.Exists(config.StatePath), "seeding never replaces an earlier durable source snapshot");
        Check.That(config.Operation is not ("seed" or "start") || !File.Exists(config.ReadyPath), "start never replaces earlier external-kill ready evidence");
    }

    private static void CheckState(Configuration config, SeedState state, AssemblyWitness[] assemblies) => Check.That(state.Version == Version && state.DatabaseName == config.DatabaseName &&
        state.DataDirectory == config.DataDirectory && state.Fixture == config.Fixture && state.JobId != Guid.Empty && state.RomId > 0 &&
        state.SourceFileId > 0 && state.RecipeVersion == RvzCanonicalRecipe.Id && state.Source.RomId == state.RomId &&
        state.Source.SourceRepresentationId > 0 && state.Source.SourceOutputId > 0 && state.Source.SourceFileId == state.SourceFileId && state.Source.ArtifactSha256 == state.Original.Sha256 &&
        state.Source.ArtifactSize == state.Original.Size && state.Source.ContentSha256 == state.Original.Sha256 &&
        state.Source.ContentSize == state.Original.Size && state.Assemblies.SequenceEqual(assemblies),
        "seed snapshot belongs to exact disposable database, source, unchanged production assemblies and recipe");

    private static void CheckReady(Configuration config, SeedState state, ReadyState ready, MediaProcessIdentity here) => Check.That(
        ready.Version == Version && ready.JobId == state.JobId && ready.Checkpoint == config.Checkpoint && ready.Job.JobId == state.JobId &&
        ready.Job.FenceToken is not null && ready.Job.LeaseExpiresAt is not null && ready.Parent.ProcessId == 1 &&
        MediaProcessingRuntime.DifferentNamespaceIncarnation(ready.Parent, here) &&
        (ready.Custody.Record ?? ready.LastStoppedRun) is { } run && run.Parent == ready.Parent && run.JobId == state.JobId,
        "ready evidence identifies old production PID1 on same boot and a different namespace incarnation");

    private static async Task<ReplayWitness> ReplayWitnessAsync(RomdDbContext db, SeedState source, CancellationToken ct)
    {
        var outcome = await db.Set<MediaReconciliationOutcomeEntity>().SingleAsync(row => row.JobId == source.JobId, ct);
        var retained = (await ProbeMediaFacts.DescribeAsync(db, source.RomId, ct)).Preferred;
        var artifact = await db.Files.SingleAsync(row => row.Id == retained.FileId, ct);
        var job = await db.Jobs.OfType<MediaReconciliationJobEntity>().SingleAsync(row => row.Id == source.JobId, ct);
        var creation = retained.Facts.Creations.Single();
        return new(outcome.Kind, retained.FileId, creation.VerificationId, new DateTimeOffset(outcome.CompletedAtUtcTicks, TimeSpan.Zero), retained.FileId, artifact.Sha256.ToString(),
            await ProbeMediaFacts.VerificationsAsync(db, source.RomId, ct),
            await ProbeMediaFacts.CreationsAsync(db, source.RomId, ct),
            await db.Set<MediaReconciliationPinEntity>().CountAsync(row => row.JobId == source.JobId, ct),
            await db.Set<MediaReconciliationDefinitionEntity>().CountAsync(row => row.SubjectKind == "LogicalRom" && row.SubjectId == source.RomId, ct),
            await db.JobDispatches.CountAsync(row => row.JobId == source.JobId, ct), await db.Files.CountAsync(ct),
            new(job.Id, job.Phase, job.ExecutionFenceToken, job.ExecutionLeaseExpiresAtUtc, job.CompletedAt), job.HangfireJobId);
    }

    private static CustodySnapshot Capture(Configuration config)
    {
        string runPath = Path.Combine(config.DataDirectory, "temp", "media-runs-v1", "run-0.json");
        var run = MediaRunRecord.Read(runPath, 0, 1);
        string identityPath = runPath + ".identity";
        string accountingPath = Path.Combine(StagingRoot(config), "reservations.json");
        byte[] accounting = BoundedRuntimeFile.Read(accountingPath, 32768);
        using var parsed = JsonDocument.Parse(accounting);
        Check.That(MediaRunRecord.Unique(parsed.RootElement), "unique staging accounting fields");
        var reservations = parsed.RootElement.GetProperty("Reservations");
        Check.That(reservations.EnumerateObject().Count() <= 3, "bounded complete aggregate accounting");
        var files = reservations.EnumerateObject().Select(entry =>
        {
            Check.That(int.TryParse(entry.Name, out int slot) && slot is >= 0 and <= 2 && entry.Value.GetInt64() > 0, "bounded staging reservation slot and charge");
            string lease = Path.Combine(StagingRoot(config), $"slot-{slot}.lease");
            string path = Path.Combine(StagingRoot(config), $"slot-{slot}.data");
            Check.That(File.Exists(lease), "original staging lease path retained");
            var binding = run?.Staging.SingleOrDefault(item => item.Slot == slot);
            Check.That(binding is null || binding.JobId == run!.JobId && binding.Path == path && binding.PoolRoot == StagingRoot(config),
                "media custody names only the configured aggregate staging pool");
            return new StagingWitness(slot, binding?.Role ?? (slot == 0 ? "source" : "output"), path, entry.Value.GetInt64(),
                File.Exists(path) ? new FileInfo(path).Length : 0, FileIdentity(lease));
        }).OrderBy(file => file.Slot).ToArray();
        string processing = Path.Combine(config.DataDirectory, "temp", "media-runs-v1", "processing-0.lease");
        return new(run, run is not null ? run.Identity ?? MediaProcessingRuntime.ReadIdentity(runPath, run.RunId, run.Admission) : null,
            run is not null ? Hash(BoundedRuntimeFile.Read(runPath, MediaRunRecord.MaximumBytes)) : null,
            File.Exists(identityPath) ? Hash(BoundedRuntimeFile.Read(identityPath, 2048)) : null, Hash(accounting),
            File.Exists(processing) ? FileIdentity(processing) : null, files);
    }

    private static void AssertCustody(CustodySnapshot custody, Guid jobId, string checkpoint, bool requireHeld)
    {
        Check.That(custody.Files.Length == 2 && custody.Files.All(file => file.Bytes <= file.ReservedBytes), "complete source/output staging remains charged before terminal outcome");
        if (checkpoint == "publishing")
            Check.That(custody.Record is null && custody.Identity is null, "publishing occurs after processing release while staging is held");
        else
            Check.That(custody.Record is { } run && run.JobId == jobId && run.Staging.Count == 2 &&
            run.Admission == custody.ProcessingIdentity, "original admission inode and complete aggregate binding agree");
        foreach (var staged in custody.Files)
        {
            using var lease = NativeFileLease.TryAcquire(Path.Combine(Path.GetDirectoryName(staged.Path)!, $"slot-{staged.Slot}.lease"));
            Check.That(requireHeld ? lease is null : lease is not null && lease.Identity == staged.LeaseIdentity,
                requireHeld ? "live parent retains every original staging lock" : "old staging lock is released on its original inode after external kill");
        }
        if (checkpoint != "publishing")
        {
            string path = Path.Combine(Path.GetDirectoryName(custody.Files[0].Path)!, "..", "media-runs-v1", "processing-0.lease");
            using var lease = NativeFileLease.TryAcquire(path);
            Check.That(requireHeld ? lease is null : lease is not null && lease.Identity == custody.ProcessingIdentity,
                requireHeld ? "live original processing OFD remains held" : "old processing OFD is free on the original inode");
        }
    }

    internal static MediaAdmissionIdentity FileIdentity(string path)
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            throw new PlatformNotSupportedException("Lease observation requires a POSIX host.");
        // A managed open attempts a shared flock and conflicts with the live owner's exclusive lease.
        // Read-only native observation neither acquires nor releases ownership, and never follows the final symlink.
        // Linux arm64 assigns a different O_NOFOLLOW bit from x64.
        int flags = OperatingSystem.IsMacOS() ? 0x01000100
            : RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? 0x00088000 : 0x000a0000;
        int descriptor = Open(path, flags);
        if (descriptor < 0)
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        try
        {
            byte[] stat = new byte[512];
            int result = OperatingSystem.IsMacOS() && RuntimeInformation.ProcessArchitecture == Architecture.X64
                ? FstatDarwin64(descriptor, stat) : Fstat(descriptor, stat);
            if (result != 0)
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            ulong device = OperatingSystem.IsMacOS() ? BitConverter.ToUInt32(stat, 0) : BitConverter.ToUInt64(stat, 0);
            ulong inode = BitConverter.ToUInt64(stat, 8);
            Check.That(inode > 0, "original lease device/inode independently observed");
            return new(OperatingSystem.IsMacOS() ? "darwin" : "linux", device, inode);
        }
        finally { NativeFileLease.Close(descriptor); }
    }

    private static SourceSnapshot Snapshot(RomContentDescriptor content) => new(content.RomId, content.FileId, content.RepresentationId, content.Preferred.Output.Id,
        content.OriginalFilename, RvzCanonicalRecipe.Id, content.Preferred.Encoding.ToString(), content.Preferred.ArtifactSha256.ToString(), content.Preferred.ArtifactSize,
        content.Identity.Sha256.ToString(), content.Identity.Size, content.Identity.Sha1.ToString(), content.Identity.Md5.ToString(), content.Identity.Crc32.ToString(),
        RomMediaPolicyComposition.CreateRegistry().ResolveObservedKind(content)?.ToString());
    private static void AssertLogical(RomVerification proof, Digests oracle) => Check.That(proof.ContentSha256.ToString() == oracle.Sha256 &&
        proof.ContentSize == oracle.Size && proof.Sha1.ToString() == oracle.Sha1 && proof.Md5.ToString() == oracle.Md5 && proof.Crc32.ToString() == oracle.Crc,
        "production logical observation equals complete independent Redump identity");
    private static string UpperFirst(string text) => char.ToUpperInvariant(text[0]) + text[1..];
    private static string StagingRoot(Configuration config) => Path.Combine(config.DataDirectory, "temp", "staging-v1");
    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static T Read<T>(string path, int maximum)
    {
        byte[] bytes = BoundedRuntimeFile.Read(path, maximum);
        using var parsed = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 12 });
        Check.That(MediaRunRecord.Unique(parsed.RootElement), "unique bounded configuration/evidence fields");
        return JsonSerializer.Deserialize<T>(bytes, StrictJson) ?? throw new InvalidDataException("Missing recovery configuration/evidence.");
    }
    private static async Task WriteNewAsync<T>(string path, T value, int maximum, CancellationToken ct)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions.Value);
        Check.That(bytes.Length <= maximum, "bounded new durable evidence document");
        await using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 4096, FileOptions.Asynchronous);
        await file.WriteAsync(bytes, ct);
        await file.FlushAsync(ct);
        file.Flush(flushToDisk: true);
    }
    private static async Task<AssemblyWitness[]> CheckImageAsync(string worker, CancellationToken ct)
    {
        var results = new List<AssemblyWitness>();
        foreach (var assembly in new[] { typeof(MediaProcessingRuntime).Assembly, typeof(MediaInspectionService).Assembly, typeof(RomdDbContext).Assembly,
            typeof(MediaReconciliationJob).Assembly, typeof(RomRepresentationPublisher).Assembly, typeof(IContentAddressableStore).Assembly })
        {
            var loaded = await Digests.FileAsync(assembly.Location, ct);
            var image = await Digests.FileAsync(Path.Combine("/app", Path.GetFileName(assembly.Location)), ct);
            Check.That(loaded.Size <= 64L << 20 && loaded == image, "loaded dependency equals untouched production image: " + assembly.GetName().Name);
            results.Add(new(assembly.GetName().Name!, assembly.ManifestModule.ModuleVersionId, loaded.Sha256));
        }
        results.Add(new("Romd.Media.Worker", null, (await Digests.FileAsync(worker, ct)).Sha256));
        return results.ToArray();
    }

    private sealed class EnvironmentState : IAsyncDisposable
    {
        private readonly DbContextOptions<RomdDbContext> _database;
        private readonly MediaProcessingOptions _limits;
        private readonly JobExecutionClaimOptions _claimLimits;
        private readonly ServiceProvider _services;
        private readonly ReconciliationProbeServices executionServices;
        public RomMediaPolicyRegistry Policies { get; } = RomMediaPolicyComposition.CreateRegistry();
        public RomEncodingRegistry Encodings { get; } = RomEncodingComposition.CreateRegistry();
        public IContentAddressableStore Cas { get; }
        public MediaProcessingRuntime Runtime { get; }
        public LocalTempFileFactory Staging { get; }
        public CapturingSupervisor Supervisor { get; }
        public RvzVerifier Verifier { get; }
        public Func<EnvironmentState, string, int?, CancellationToken, Task>? Pause { get; set; }
        public EnvironmentState(Configuration config, DbContextOptions<RomdDbContext> database, IRomdOptions options,
            MediaProcessingOptions limits, TempStorageOptions staging, JobExecutionClaimOptions claimLimits)
        {
            _database = database;
            _limits = limits;
            _claimLimits = claimLimits;
            _services = new ServiceCollection().AddContentAddressableStorage(storage =>
                { storage.RootPath = Path.Combine(options.DataDirectory, "cas"); storage.WritePolicy = ContentWritePolicy.Raw; storage.MaxContentSize = options.MaxUploadBytes; })
                .BuildServiceProvider();
            Cas = _services.GetRequiredService<IContentAddressableStore>();
            Runtime = new(options, limits);
            Staging = new(options, new FileSystem(), staging);
            Supervisor = new(new MediaWorkerSupervisor(limits, () => new MediaWorkerProcessLifetime(), setup: async (process, point, token) =>
            {
                if (point == MediaWorkerSetupPoint.Identified)
                    Supervisor!.ChildEnvironments.Add(ObserveEnvironment(process.Id));
                if (point != MediaWorkerSetupPoint.Draining)
                    return;
                if (config.Operation != "start" || config.Checkpoint != "nonempty" || Supervisor!.Operation != "encode")
                    return;
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(token);
                wait.CancelAfter(TimeSpan.FromSeconds(config.CheckpointWaitSeconds));
                string output = Supervisor.OutputPath ?? throw new InvalidDataException("Missing actual encoding destination.");
                while (!File.Exists(output) || new FileInfo(output).Length == 0)
                {
                    Check.That(IsChildLive(process.Id), "actual encoding output appears before child exits");
                    await Task.Delay(10, wait.Token);
                }
                Check.That(IsChildLive(process.Id), "nonempty checkpoint observes a live encoding child");
                await (Pause ?? throw new InvalidDataException("Missing external-kill checkpoint callback."))(this, "nonempty", process.Id, token);
            }, runtime: Runtime), config);
            Verifier = new(limits, Supervisor, Runtime);
            executionServices = new(database, options, limits, staging, Runtime, Staging, Supervisor, Cas, claimLimits);
        }
        public RomdDbContext Context() => new(_database);
        public FileStorageService Files(RomdDbContext db) => new(Cas, new FileRepository(db), new EfUnitOfWork(db), new FileMutationLock(db),
            new AdminRealtimeOutbox(db, TimeProvider.System), NullLogger<FileStorageService>.Instance, TimeProvider.System);
        public ClaimedJobRepository<MediaReconciliationJob, MediaReconciliationJobRepository> Claims(RomdDbContext db) =>
            new(new(db, TimeProvider.System), db, TimeProvider.System, _claimLimits);
        public AsyncServiceScope CreateExecutionScope() => executionServices.CreateScope();
        public async ValueTask DisposeAsync()
        {
            Verifier.Dispose();
            await executionServices.DisposeAsync();
            await _services.DisposeAsync();
        }
    }

    private static bool IsChildLive(int pid)
    {
        // Read only this attempt's direct child in the current namespace; native waitpid remains the sole reap owner.
        using var file = File.OpenRead($"/proc/{pid}/stat");
        byte[] bytes = new byte[4097];
        int count = file.ReadAtLeast(bytes, bytes.Length, throwOnEndOfStream: false);
        Check.That(count < bytes.Length, "bounded direct child kernel-state observation");
        string stat = Encoding.UTF8.GetString(bytes, 0, count);
        int end = stat.LastIndexOf(')');
        Check.That(end >= 0 && stat.Length > end + 2, "recognized direct child kernel-state record");
        return stat[end + 2] is 'R' or 'S' or 'D' or 'T' or 't' or 'I';
    }
    private static ChildEnvironmentWitness ObserveEnvironment(int pid)
    {
        using var file = File.OpenRead($"/proc/{pid}/environ");
        byte[] bytes = new byte[32769];
        int count = file.ReadAtLeast(bytes, bytes.Length, throwOnEndOfStream: false);
        Check.That(count < bytes.Length, "bounded child environment observation");
        var names = Encoding.UTF8.GetString(bytes, 0, count).Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .Select(entry => entry.Split('=', 2)[0]).Order(StringComparer.Ordinal).ToArray();
        Check.That(!names.Any(name => name == "ROMD_CONVERGENCE_PROBE_CONNECTION" || name.StartsWith("ConnectionStrings", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("PG", StringComparison.Ordinal) || name.Contains("DATABASE", StringComparison.OrdinalIgnoreCase)), "media child receives no database configuration");
        Check.That(names.SequenceEqual(new[] { "DOTNET_EnableDiagnostics", "DOTNET_GCHeapHardLimit", "DOTNET_PROCESSOR_COUNT", "DOTNET_gcServer" }
            .Order(StringComparer.Ordinal)), "child environment contains only the four production runtime limit settings");
        return new(pid, names);
    }
    private sealed class CapturingSupervisor(MediaWorkerSupervisor inner, Configuration config) : IMediaWorkerSupervisor
    {
        public List<ChildWitness> Events { get; } = [];
        public List<ChildEnvironmentWitness> ChildEnvironments { get; } = [];
        public MediaRunRecord? LastRun { get; private set; }
        public string? Operation { get; private set; }
        public string? OutputPath { get; private set; }
        public bool IsAvailable => inner.IsAvailable;
        public Task<MediaWorkerResult> HandshakeAsync(CancellationToken ct = default) => inner.HandshakeAsync(ct);
        public Task<MediaWorkerResult> InspectAsync(ITempFile source, CancellationToken ct = default) => RunAsync("inspect-rvz", () => inner.InspectAsync(source, ct), ct);
        public Task<MediaWorkerResult> InspectIsoAsync(ITempFile source, CancellationToken ct = default) => RunAsync("inspect-iso", () => inner.InspectIsoAsync(source, ct), ct);
        public Task<MediaWorkerResult> EncodeAsync(ITempFile source, IWritableTempFile output, long logicalBytes, RomIsoDiscKind kind, CancellationToken ct = default)
        { OutputPath = output.Path; return RunAsync("encode", () => inner.EncodeAsync(source, output, logicalBytes, kind, ct), ct); }
        private async Task<MediaWorkerResult> RunAsync(string operation, Func<Task<MediaWorkerResult>> run, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Operation = operation;
            long started = Stopwatch.GetTimestamp();
            var result = await run();
            LastRun = MediaRunRecord.Read(Path.Combine(config.DataDirectory, "temp", "media-runs-v1", "run-0.json"), 0, 1);
            Events.Add(new(operation, Stopwatch.GetElapsedTime(started).TotalSeconds, result));
            Check.That(Events.Count <= 8, "bounded child result collection");
            return result;
        }
    }
    private sealed class NoNotification : IJobNotifier
    {
        public Task NotifyJobUpdatedAsync(Job job, CancellationToken ct = default) { ct.ThrowIfCancellationRequested(); return Task.CompletedTask; }
        public Task NotifyTitleEnrichedAsync(int titleId, int platformId, int? coverMediaId = null, CancellationToken ct = default)
        { ct.ThrowIfCancellationRequested(); return Task.CompletedTask; }
    }
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
    private sealed record Configuration(string Operation, string Checkpoint, string DatabaseName, string DataDirectory,
        string StatePath, string ReadyPath, string ProductionWorker, Fixture Fixture)
    {
        public int LeaseSeconds { get; init; } = 30;
        public int HeartbeatSeconds { get; init; } = 5;
        public int CheckpointWaitSeconds { get; init; } = 60;
        public int LeaseExpiryWaitSeconds { get; init; } = 180;
        public int WallTimeoutSeconds { get; init; } = 600;
        public int CpuTimeoutSeconds { get; init; } = 600;
        public long MaximumStagingBytes { get; init; } = 16L << 30;
        public long MinimumFreeBytes { get; init; } = 1L << 30;
    }
    private sealed record SeedState(string Version, string DatabaseName, string DataDirectory, Fixture Fixture, Guid JobId, int RomId,
        int SourceFileId, string RecipeVersion, Digests Original, Digests UploadedRvz, Digests Dat, SourceSnapshot Source,
        Guid OriginalVerificationId, MediaProcessIdentity Parent, AssemblyWitness[] Assemblies, DateTimeOffset CreatedAt);
    private sealed record ReadyState(string Version, string Checkpoint, Guid JobId, MediaProcessIdentity Parent, JobWitness Job,
        CustodySnapshot Custody, MediaRunRecord? LastStoppedRun, ChildWitness[] Children, ChildEnvironmentWitness[] ChildEnvironments, DateTimeOffset CreatedAt);
    private sealed record SourceSnapshot(int RomId, int SourceFileId, int SourceRepresentationId, int SourceOutputId, string Filename, string RecipeVersion, string Encoding,
        string ArtifactSha256, long ArtifactSize, string ContentSha256, long ContentSize, string Sha1, string Md5, string Crc32, string? DiscKind);
    private sealed record JobWitness(Guid JobId, string Phase, Guid? FenceToken, DateTimeOffset? LeaseExpiresAt, DateTimeOffset? CompletedAt);
    private sealed record CustodySnapshot(MediaRunRecord? Record, MediaProcessIdentity? Identity, string? RecordSha256, string? IdentitySha256,
        string AccountingSha256, MediaAdmissionIdentity? ProcessingIdentity, StagingWitness[] Files);
    private sealed record StagingWitness(int Slot, string Role, string Path, long ReservedBytes, long Bytes, MediaAdmissionIdentity LeaseIdentity);
    private sealed record AssemblyWitness(string Name, Guid? ModuleVersionId, string Sha256);
    private sealed record ChildWitness(string Operation, double Seconds, MediaWorkerResult Result);
    private sealed record ChildEnvironmentWitness(int ProcessId, string[] Keys);
    private sealed record ReplayWitness(string Kind, int? OutputFileId, Guid? VerificationId, DateTimeOffset CreatedAt,
        int PreferredFileId, string ArtifactSha256, int VerificationCount, int EquivalenceCount, int SourcePinCount,
        int ConvergenceJobCount, int DispatchCount, int FileCount, JobWitness Job, string? DeliveryId);
    [DllImport("libc", EntryPoint = "geteuid")] private static extern uint GetEffectiveUserId();
    [DllImport("libc", EntryPoint = "open", SetLastError = true)] private static extern int Open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);
    [DllImport("libc", EntryPoint = "fstat", SetLastError = true)] private static extern int Fstat(int descriptor, [Out] byte[] stat);
    [DllImport("libc", EntryPoint = "fstat$INODE64", SetLastError = true)] private static extern int FstatDarwin64(int descriptor, [Out] byte[] stat);
}
