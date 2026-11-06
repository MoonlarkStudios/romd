using System.Diagnostics;
using System.IO.Abstractions;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml.Linq;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Admin.Application.Source.Rom;
using Romd.Admin.Application.Storage.Files;
using Romd.Application.Common.Configuration;
using Romd.Application.Common.Content;
using Romd.Domain.Jobs;
using Romd.Domain.Source.Rom;
using Romd.Infrastructure.Hashing;
using Romd.Infrastructure.Jobs.Executors;
using Romd.Infrastructure.Realtime;
using Romd.Infrastructure.Storage;
using Romd.Media.Rvz;
using Romd.Persistence;
using Romd.Persistence.MediaFacts;
using Romd.Persistence.Entities;
using Romd.Persistence.Repositories;
using Romd.Persistence.Repositories.Reconciliation;
using Romd.Admin.Application.Source.Rom.Reconciliation;
using Romd.PostgreSql.TestSupport;
using Romd.Storage;

namespace Romd.Media.Probe;

/// <summary>Real media, production publication/execution/read paths, and isolated PostgreSQL/CAS ownership.</summary>
internal static class ConvergenceChecks
{
    internal static async Task RunAsync(string[] args, CancellationToken ct, ConvergedLibraryCheck? onConvergedLibrary = null)
    {
        if (args.Length != 3 || !Path.IsPathFullyQualified(args[1]) || !Path.IsPathFullyQualified(args[2]))
            throw new ArgumentException("Usage: --convergence-probe <absolute fixtures.json> <absolute new results.json>");
        var fixtures = JsonSerializer.Deserialize<Fixture[]>(await File.ReadAllTextAsync(args[1], ct))
            ?? throw new InvalidDataException("Missing convergence fixtures.");
        Check.That(fixtures.Length > 0 && fixtures.Select(fixture => fixture.Name).Distinct().Count() == fixtures.Length,
            "nonempty uniquely named real fixtures");
        await using var output = new FileStream(args[2], FileMode.CreateNew, FileAccess.Write, FileShare.Read,
            4096, FileOptions.Asynchronous);
        var reports = new List<object>();
        var limits = new MediaProcessingOptions();
        var stagingLimits = new TempStorageOptions();
        var readLimits = new ReadDecoderOptions();
        var claimLimits = new JobExecutionClaimOptions();
        string scope = "Disposable PostgreSQL 18 and raw CAS; production supervised upload preparation, atomic publication, " +
            "durable dispatch/source dependency, claimed convergence executor, separate media children, original-source GC, " +
            "and production format planner/catalog/reader. Redump XML hashes compared against independent read-only ISO fixtures. " +
            "No HTTP endpoint, VFS mount, sustained-load, Linux limit, gameplay, or full-suite qualification.";
        string current = "initializing";
        ProbeEnvironment? active = null;
        DateTimeOffset started = DateTimeOffset.UtcNow;
        await SaveAsync("in-progress", null, ct);
        try
        {
            await using var shared = new ProbeEnvironment(limits, stagingLimits, readLimits, claimLimits);
            var library = new List<(RomContentDescriptor Content, string IsoPath, string SystemKey)>();
            foreach (var fixture in fixtures)
            {
                current = fixture.Name + ": independent fixture hashes";
                await SaveAsync("in-progress", null, ct);
                Check.That(new[] { fixture.Iso, fixture.UploadedRvz, fixture.Dat }.All(Path.IsPathFullyQualified),
                    "absolute read-only fixture paths");
                var identity = new FixtureIdentity(fixture);
                var oracle = await Digests.FileAsync(fixture.Iso, ct);
                var uploaded = await Digests.FileAsync(fixture.UploadedRvz, ct);
                var dat = XDocument.Load(fixture.Dat);
                var expected = dat.Descendants("game").Single(game => (string?)game.Attribute("id") == fixture.DatId)
                    .Elements("rom").Single();
                Check.That(oracle.Size == (long)expected.Attribute("size")! &&
                    oracle.Crc == ((string?)expected.Attribute("crc"))?.ToLowerInvariant() &&
                    oracle.Md5 == ((string?)expected.Attribute("md5"))?.ToLowerInvariant() &&
                    oracle.Sha1 == ((string?)expected.Attribute("sha1"))?.ToLowerInvariant(), "independent ISO matches complete Redump DAT identity");
                reports.Add(new
                {
                    fixture.Name,
                    Step = "independent-fixture-oracles",
                    fixture.Iso,
                    UploadedRvzPath = fixture.UploadedRvz,
                    fixture.Dat,
                    fixture.DatId,
                    Original = oracle,
                    UploadedRvz = uploaded
                });
                await SaveAsync("in-progress", null, ct);

                {
                    var environment = shared;
                    active = environment;
                    current = fixture.Name + ": production ISO upload";
                    await SaveAsync("in-progress", null, ct);
                    var original = await environment.UploadPathAsync(fixture.Iso, oracle, ct);
                    CheckPrepared(original, oracle);
                    Check.That(original.Encoding == RomRepresentationEncoding.Identity &&
                        original.Eligibility is { Status: RomIsoEligibilityStatus.Supported, DiscKind: not null }, "ISO inspection establishes encode eligibility");
                    if (fixture.NintendoKind is not null)
                        Check.That(original.Eligibility!.DiscKind!.Value.ToString().Equals(fixture.NintendoKind, StringComparison.OrdinalIgnoreCase) &&
                            original.Eligibility.StandardKeyPartitions == fixture.StandardKeyPartitions &&
                            original.Eligibility.KoreanKeyPartitions == fixture.KoreanKeyPartitions, "fixture disc layout and common-key indexes");
                    Guid jobId;
                    await using (var observer = environment.Database.CreateContext())
                    {
                        var subject = await observer.Set<MediaReconciliationSubjectEntity>().SingleAsync(row => row.SubjectKind == "LogicalRom" && row.SubjectId == original.RomId, ct);
                        jobId = subject.LatestJobId;
                        Check.That(subject.RequestedVersion > subject.AcknowledgedVersion && !subject.ConversionRequested,
                            "import records current fact demand without an implicit operator conversion request");
                        Check.That(await observer.Set<MediaReconciliationPinEntity>().CountAsync(ct) == 0,
                            "import acceptance does not pin an execution source before the worker captures it");
                        Check.That(await observer.JobDispatches.AnyAsync(dispatch => dispatch.JobId == jobId, ct), "durable reconciliation dispatch exists");
                        Check.That(await observer.Set<MediaReconciliationDefinitionEntity>().CountAsync(row => row.SubjectKind == "LogicalRom" && row.SubjectId == original.RomId, ct) == 1 &&
                            await ProbeMediaFacts.OwnedRepresentationsAsync(observer, original.RomId, ct) == 1, "ISO published once with one active subject");
                    }
                    reports.Add(new
                    {
                        fixture.Name,
                        Step = "ISO-upload-and-automatic-intent",
                        Upload = original,
                        JobId = jobId,
                        AutomaticDispatch = true,
                        SourceCaptureDeferredToWorker = true
                    });
                    await SaveAsync("in-progress", null, ct);

                    current = fixture.Name + ": claimed convergence children";
                    await SaveAsync("in-progress", null, ct);
                    var execution = await environment.ExecuteAsync(jobId, oracle, expectEncoding: true, ct);
                    reports.Add(new { fixture.Name, Step = "claimed-convergence", Execution = execution });
                    await SaveAsync("in-progress", null, ct);

                    current = fixture.Name + ": delivered formats and original GC";
                    await SaveAsync("in-progress", null, ct);
                    var delivered = await environment.ReadBothAsync(original.RomId, fixture.Iso, oracle, ct);
                    Check.That(delivered.Stored.Sha256 != uploaded.Sha256, "fixture RVZ is a different canonical artifact from managed output");
                    await using (var cleanup = environment.Database.CreateContext())
                    {
                        Check.That(await new MediaRepresentationCleanup(cleanup).CollectAsync(256, ct) == 1, "released source representation is collected before its file");
                        Check.That(await environment.Files(cleanup).DeleteIfUnreferencedAsync(original.FileId, ct), "released original file and CAS blob are GC eligible");
                        Check.That(!await cleanup.Files.AnyAsync(file => file.Id == original.FileId, ct) &&
                            await ProbeMediaFacts.CreationsAsync(cleanup, original.RomId, ct) == 1 &&
                            await cleanup.Set<MediaReconciliationOutcomeEntity>().CountAsync(row => row.JobId == jobId, ct) == 1, "source equivalence and journal survive original GC");
                        Check.That(!await environment.Cas.ExistsAsync(StorageKey.FromHash(Romd.Domain.Hashing.Sha256.Parse(original.ArtifactSha256)), ct), "original CAS bytes released");
                    }
                    reports.Add(new
                    {
                        fixture.Name,
                        Step = "delivery-and-original-GC",
                        Delivery = delivered,
                        OriginalFileReleased = true,
                        SourceEquivalenceRetained = true
                    });
                    await SaveAsync("in-progress", null, ct);

                    current = fixture.Name + ": verified discard and exact reupload ranking";
                    await SaveAsync("in-progress", null, ct);
                    int before = await environment.ProofCountAsync(original.RomId, ct);
                    var discardedIso = await environment.UploadPathAsync(fixture.Iso, oracle, ct);
                    CheckPrepared(discardedIso, oracle);
                    await environment.AssertRetainedAsync(original.RomId, delivered.Stored.Sha256, before, ct);
                    var discardedRvz = await environment.UploadPathAsync(fixture.UploadedRvz, oracle, ct);
                    CheckPrepared(discardedRvz, oracle);
                    Check.That(environment.Policies.Classify(discardedRvz.Container, null) ==
                        RomRepresentationProfileClass.Canonical, "different uploaded RVZ already matches recipe parameters");
                    await environment.AssertRetainedAsync(original.RomId, delivered.Stored.Sha256, before, ct);
                    await using (var artifact = await environment.Cas.OpenRandomAccessAsync(StorageKey.FromHash(Romd.Domain.Hashing.Sha256.Parse(delivered.Stored.Sha256)), ct)
                        ?? throw new InvalidOperationException("Retained artifact missing."))
                    await using (var stream = artifact.AsStream(leaveOpen: true))
                    {
                        var exact = await environment.UploadStreamAsync(stream, "exact-retained.rvz", oracle, ct);
                        CheckPrepared(exact, oracle);
                        await environment.AssertRetainedAsync(original.RomId, delivered.Stored.Sha256, before, ct);
                    }
                    reports.Add(new
                    {
                        fixture.Name,
                        Step = "ranking",
                        OriginalDiscardVerified = discardedIso,
                        DifferentCanonicalRvzDiscardVerified = discardedRvz,
                        DiscardedMeasurementsPersisted = 0,
                        EveryObservedDecoderBuildRetained = true
                    });
                    await SaveAsync("in-progress", null, ct);
                    var content = await new ProbeCatalog(shared.Database.CreateOptions()).FindByRomIdAsync(original.RomId, ct)
                        ?? throw new InvalidOperationException("Converged fixture missing from catalog.");
                    library.Add((content, fixture.Iso, original.Eligibility!.DiscKind == RomIsoDiscKind.GameCube ? "gc" : "wii"));
                }

                current = fixture.Name + ": direct canonical RVZ upload";
                await SaveAsync("in-progress", null, ct);
                await using (var direct = new ProbeEnvironment(limits, stagingLimits, readLimits, claimLimits))
                {
                    active = direct;
                    var retained = await direct.UploadPathAsync(fixture.UploadedRvz, oracle, ct);
                    CheckPrepared(retained, oracle);
                    Check.That(direct.Policies.Classify(retained.Container, null) == RomRepresentationProfileClass.Canonical,
                        "direct RVZ container parameters are canonical independently of producer");
                    await using var observer = direct.Database.CreateContext();
                    var subject = await observer.Set<MediaReconciliationSubjectEntity>().SingleAsync(row => row.SubjectKind == "LogicalRom" && row.SubjectId == retained.RomId, ct);
                    Check.That(await observer.Set<MediaReconciliationPinEntity>().CountAsync(ct) == 0 &&
                        await observer.JobDispatches.AnyAsync(row => row.JobId == subject.LatestJobId, ct), "canonical upload stages general demand without an execution pin");
                    await direct.ExecuteAsync(subject.LatestJobId, oracle, expectEncoding: false, ct);
                    var delivered = await direct.ReadBothAsync(retained.RomId, fixture.Iso, oracle, ct);
                    Check.That(delivered.Stored == uploaded, "direct upload retains exact original RVZ bytes and hashes");
                    reports.Add(new
                    {
                        fixture.Name,
                        Step = "direct-canonical-RVZ-upload",
                        Upload = retained,
                        Delivery = delivered,
                        GeneralDemandAcknowledged = true,
                        NoReencoding = true
                    });
                    await SaveAsync("in-progress", null, ct);
                }
                identity.AssertUnchanged();
                Check.That(await Digests.FileAsync(fixture.Iso, ct) == oracle && await Digests.FileAsync(fixture.UploadedRvz, ct) == uploaded,
                    "read-only fixture hashes preserved after qualification");
                reports.Add(new { fixture.Name, Step = "fixture-immutability", FullHashesUnchanged = true });
                await SaveAsync("in-progress", null, ct);
            }
            active = shared;
            await using (var observer = shared.Database.CreateContext())
                Check.That(await observer.RomFiles.CountAsync(ct) == fixtures.Length &&
                    await observer.RomRepresentations.CountAsync(ct) == fixtures.Length &&
                    await observer.Set<MediaReconciliationPinEntity>().CountAsync(ct) == 0,
                    "both real discs retained in one fully converged disposable library");
            if (onConvergedLibrary is not null)
            {
                current = "shared converged-library qualification callback";
                await SaveAsync("in-progress", null, ct);
                long callbackStarted = Stopwatch.GetTimestamp();
                var qualified = await onConvergedLibrary(shared.Cas, new ProbeCatalog(shared.Database.CreateOptions()), library,
                    shared.StagingScope, ct);
                double callbackSeconds = Stopwatch.GetElapsedTime(callbackStarted).TotalSeconds;
                Check.That(qualified.RequestedDuration > TimeSpan.Zero && callbackSeconds >= qualified.RequestedDuration.TotalSeconds &&
                    Path.IsPathFullyQualified(qualified.ResultsPath) && Path.GetFullPath(qualified.ResultsPath) != Path.GetFullPath(args[2]),
                    "callback ran requested duration and returned an independent result path");
                using (var callbackReport = JsonDocument.Parse(await File.ReadAllTextAsync(qualified.ResultsPath, ct)))
                    Check.That(callbackReport.RootElement.GetProperty("Status").GetString() == "passed", "external converged-library qualification passed");
                await using var callbackBytes = File.OpenRead(qualified.ResultsPath);
                string callbackHash = Convert.ToHexStringLower(await SHA256.HashDataAsync(callbackBytes, ct));
                reports.Add(new
                {
                    Step = "shared-converged-library-callback",
                    RequestedSeconds = qualified.RequestedDuration.TotalSeconds,
                    ElapsedSeconds = callbackSeconds,
                    qualified.ResultsPath,
                    ResultSha256 = callbackHash,
                    Discs = library.Select(item => new
                    {
                        item.Content.RomId,
                        item.IsoPath,
                        item.SystemKey,
                        ArtifactSha256 = item.Content.ArtifactSha256.ToString(),
                        item.Content.ArtifactSize
                    }).ToArray()
                });
                await SaveAsync("in-progress", null, ct);
            }
            current = "complete";
            await SaveAsync("passed", null, ct);
        }
        catch (Exception error)
        {
            await SaveAsync("failed", error.ToString(), CancellationToken.None);
            throw;
        }

        async Task SaveAsync(string status, string? error, CancellationToken token)
        {
            output.Position = 0;
            output.SetLength(0);
            await JsonSerializer.SerializeAsync(output, new
            {
                Status = status,
                Current = current,
                StartedAt = started,
                RecordedAt = DateTimeOffset.UtcNow,
                Error = error,
                Scope = scope,
                Environment = new
                {
                    RuntimeInformation.RuntimeIdentifier,
                    RuntimeInformation.FrameworkDescription,
                    RuntimeInformation.OSDescription,
                    ProcessArchitecture = RuntimeInformation.ProcessArchitecture.ToString(),
                    Recipe = RvzCanonicalRecipe.Id,
                    RvzCanonicalRecipe.Compression,
                    RvzCanonicalRecipe.CompressionLevel,
                    RvzCanonicalRecipe.ChunkSize,
                    PostgreSqlConfiguration.ExpectedSchemaVersion
                },
                MediaLimits = limits,
                StagingLimits = stagingLimits,
                ReadLimits = readLimits,
                ExecutionClaimLimits = claimLimits,
                CurrentChildren = active?.Children,
                CurrentExecution = active?.LastExecution,
                ScratchDirectory = active?.ScratchDirectory,
                Reports = reports
            }, JsonOptions.Value, token);
            await output.FlushAsync(token);
        }
    }

    private static void CheckPrepared(UploadResult result, Digests oracle) =>
        Check.That(result.Logical == oracle && result.VerificationProfile is RvzVerifier.Profile or RvzVerifier.IsoProfile,
            "production upload verifies complete logical hashes and DAT length");

    private sealed class ProbeEnvironment : IAsyncDisposable
    {
        private readonly string _root = Directory.CreateTempSubdirectory("romd-real-convergence-").FullName;
        private readonly ServiceProvider _services;
        private readonly ReconciliationProbeServices _executionServices;
        private readonly RvzVerifier _verifier;
        private readonly RomPreparationService _preparation;
        private readonly CapturingSupervisor _supervisor;
        private readonly LocalTempFileFactory _staging;
        private readonly MediaProcessingRuntime _runtime;
        private readonly MediaProcessingOptions _limits;
        private readonly JobExecutionClaimOptions _claimLimits;
        private readonly RomRepresentationDecoder _decoder;
        public PostgreSqlTestDatabase Database { get; } = PostgreSqlTestDatabase.Create();
        public RomMediaPolicyRegistry Policies { get; } = RomMediaPolicyComposition.CreateRegistry();
        public RomEncodingRegistry Encodings { get; } = RomEncodingComposition.CreateRegistry();
        public IContentAddressableStore Cas { get; }
        public ChildResult[] Children => _supervisor.Events.ToArray();
        public string ScratchDirectory => _root;
        public ReadResourceChecks.StagingScope StagingScope { get; }
        public object? LastExecution { get; private set; }

        public ProbeEnvironment(MediaProcessingOptions limits, TempStorageOptions stagingLimits,
            ReadDecoderOptions readLimits, JobExecutionClaimOptions claimLimits)
        {
            _limits = limits;
            _claimLimits = claimLimits;
            var options = Substitute.For<IRomdOptions>();
            options.DataDirectory.Returns(_root);
            options.MaxUploadBytes.Returns(limits.MaxArtifactBytes);
            _staging = new(options, new FileSystem(), stagingLimits);
            StagingScope = new(Path.GetFullPath(Path.Combine(_root, "temp", "staging-v1")),
                stagingLimits.MaximumFiles, stagingLimits.MaximumBytes, limits.MaxArtifactBytes, stagingLimits.MinimumFreeBytes);
            _runtime = new(options, limits);
            _supervisor = new(new(limits, _runtime));
            _verifier = new(limits, _supervisor, _runtime);
            _preparation = ProbeMediaFacts.ManagedPreparation(new HashingService(), _verifier, new ProbeMediaFacts.EmptyReferences());
            _services = new ServiceCollection().AddContentAddressableStorage(storage =>
                {
                    storage.RootPath = Path.Combine(_root, "cas");
                    storage.WritePolicy = ContentWritePolicy.Raw;
                    storage.MaxContentSize = new Romd.Host.Configuration.RomdOptions().MaxUploadBytes;
                }).BuildServiceProvider();
            Cas = _services.GetRequiredService<IContentAddressableStore>();
            _decoder = new(new RomMediaAdapterRegistry(), readLimits);
            _executionServices = new(Database.CreateOptions(), options, limits, stagingLimits, _runtime, _staging, _supervisor, Cas, claimLimits);
        }

        public FileStorageService Files(RomdDbContext db) => new(Cas, new FileRepository(db), new EfUnitOfWork(db),
            new FileMutationLock(db), new AdminRealtimeOutbox(db, TimeProvider.System), NullLogger<FileStorageService>.Instance, TimeProvider.System);
        private ClaimedJobRepository<MediaReconciliationJob, MediaReconciliationJobRepository> Claims(RomdDbContext db) =>
            new(new(db, TimeProvider.System), db, TimeProvider.System, _claimLimits);

        public async Task<UploadResult> UploadPathAsync(string path, Digests oracle, CancellationToken ct)
        {
            await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, FileOptions.Asynchronous);
            return await UploadStreamAsync(input, Path.GetFileName(path), oracle, ct);
        }

        public async Task<UploadResult> UploadStreamAsync(Stream input, string filename, Digests oracle, CancellationToken ct)
        {
            Check.That((await _supervisor.HandshakeAsync(ct)).IsSuccess, "production media child package handshake");
            long started = Stopwatch.GetTimestamp();
            IRomPreparationAdmission? admission = null;
            int eventsBefore = _supervisor.Events.Count;
            try
            {
                await using var staged = await _staging.CreateAsync(input, ct,
                    prefix => _preparation.RequiresDatHashes(prefix, filename),
                    async (prefix, token) => admission = await _preparation.AdmitAsync(prefix, filename, token),
                    prefix => RomPreparationDetection.SelectPrefixLength(prefix, filename));
                Check.That(admission is not null, "Nintendo upload reserves inspection admission before staging copy");
                var preparedResult = await admission!.PrepareAsync(staged, filename, ct);
                admission.Dispose();
                admission = null;
                Check.That(!preparedResult.IsError, "production media preparation: " + string.Join(",", (preparedResult.IsError ? preparedResult.Errors : []).Select(error => error.Code)));
                await using var prepared = (Romd.Admin.Application.Source.Rom.PreparedRom.WholeFile)preparedResult.Value;
                var logical = new Digests(prepared.Hashes.BytesRead, prepared.Hashes.Crc32.ToString(), prepared.Hashes.Md5.ToString(),
                    prepared.Hashes.Sha1.ToString(), prepared.ContentSha256.ToString());
                Check.That(logical == oracle, "DAT-matched logical output before publication");
                Check.That(ReferenceEquals(prepared.Artifact, staged), "upload preparation preserves artifact without encoding");
                await using var db = Database.CreateContext();
                var roms = Romd.PostgreSql.TestSupport.MediaTestServices.CreateRomRepository(db);
                var existing = await roms.GetBySha1Async(prepared.Hashes.Sha1, ct);
                var publisher = new RomRepresentationPublisher(Files(db), new EfUnitOfWork(db), roms, Policies, Encodings,
                    new FileMutationLock(db), null, new MediaFactPublisher(db), ProbeMediaFacts.Observations(db), ProbeMediaFacts.Creations(db), ProbeMediaFacts.Audit, TimeProvider.System, new Romd.Persistence.Repositories.Reconciliation.MediaReconciliationRequests(db, TimeProvider.System), Romd.PostgreSql.TestSupport.MediaTestServices.CreatePs2Authority(db));
                var published = await publisher.PublishAsync(prepared, existing, true, (_, _, token) =>
                    { token.ThrowIfCancellationRequested(); return Task.CompletedTask; }, _ => null, ct);
                Check.That(!published.IsError, "production publication: " + string.Join(",", (published.IsError ? published.Errors : []).Select(error => error.Code)));
                return new(published.Value.Id, published.Value.FileId, staged.FileSha256.ToString(), staged.FileSize,
                    prepared.Encoding, prepared.EncodingProfile, prepared.Container, logical, prepared.Verification.Id, prepared.Verification.Profile,
                    prepared.Verification.DecoderBuild, prepared.Eligibility, Stopwatch.GetElapsedTime(started).TotalSeconds,
                    _supervisor.Events.Skip(eventsBefore).ToArray());
            }
            finally { admission?.Dispose(); }
        }

        public async Task<object> ExecuteAsync(Guid jobId, Digests oracle, bool expectEncoding, CancellationToken ct)
        {
            await using var scope = _executionServices.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<RomdDbContext>();
            var executor = scope.ServiceProvider.GetRequiredService<MediaReconciliationJobExecutor>();
            var claim = await Claims(db).TryClaimExecutionAsync(jobId, "real-reconciliation-probe", ct)
                ?? throw new InvalidOperationException("Reconciliation execution claim unavailable.");
            var subject = (MediaReconciliationRequest.SubjectWork)claim.Job.Request;
            Check.That(subject.Subject is MediaReconciliationSubject.LogicalRom, "Nintendo probe operates on its logical-ROM subject");
            int romId = subject.Subject.Id;
            var original = (await ProbeMediaFacts.DescribeAsync(db, romId, ct)).Preferred;
            int eventsBefore = _supervisor.Events.Count;
            int checkpoints = 0, renewals = 0;
            string? deferralReason = null;
            long started = Stopwatch.GetTimestamp();
            using var run = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var context = new JobContext(null, async token =>
            {
                await using var checkpoint = Database.CreateContext();
                Check.That(await Claims(checkpoint).TryUpdateClaimedAsync(claim.Job, claim.FenceToken, token), "fenced durable phase checkpoint");
                Interlocked.Increment(ref checkpoints);
            }, run.Token, async token =>
            {
                await using var ownership = Database.CreateContext();
                return await Claims(ownership).HasExecutionOwnershipAsync(jobId, claim.FenceToken, token);
            }, jobId: jobId, onDeferral: (reason, _) => deferralReason = reason);
            Task heartbeat = HeartbeatAsync(run.Token);
            try
            {
                using var fence = JobExecutionFenceScope.Enter(jobId, claim.FenceToken, TimeProvider.System);
                await executor.ExecuteAsync(claim.Job, context);
            }
            finally
            {
                await run.CancelAsync();
                try
                { await heartbeat; }
                catch (OperationCanceledException) when (run.IsCancellationRequested) { }
            }
            LastExecution = new
            {
                JobId = jobId,
                claim.Job.Phase,
                claim.Job.Outcome,
                claim.Job.OutcomeCode,
                context.DeferredUntil,
                DeferralReason = deferralReason,
                Errors = claim.Job.Errors.ToArray(),
                Checkpoints = checkpoints,
                LeaseRenewals = renewals
            };
            Check.That(context.DeferredUntil is null && claim.Job.Outcome == MediaReconciliationOutcomeKind.Completed,
                "reconciliation completed without deferral or file failure: " + (deferralReason ?? claim.Job.OutcomeCode));
            Check.That(!(await _runtime.EnsureJobStoppedAsync(jobId, ct)).IsError, "durable custody confirms every child stopped");
            var children = _supervisor.Events.Skip(eventsBefore).ToArray();
            if (expectEncoding)
            {
                Check.That(children.Select(child => child.Operation).SequenceEqual(new[] { "inspect-iso", "encode", "inspect-rvz" }) &&
                    children.All(child => child.Result.IsSuccess) && children.Select(child => child.Result.Execution!.ProcessId).Distinct().Count() == 3,
                    "source inspection, encoding and complete verification use three fresh successful children");
                foreach (var child in children.Where(child => child.Operation != "encode"))
                {
                    var observed = child.Result.Reply!.Inspection!;
                    Check.That(new Digests(observed.LogicalBytes, observed.Crc32, observed.Md5, observed.Sha1, observed.LogicalSha256) == oracle,
                        "independent reconciliation child output matches complete Redump identity");
                }
                var encoded = children.Single(child => child.Operation == "encode").Result.Reply!.Encoding!;
                Check.That(encoded.RecipeVersion == RvzCanonicalRecipe.Id &&
                    (OperatingSystem.IsWindows() ? encoded.PriorityKind == "windows-priority" && encoded.PriorityValue == 0x4000
                        : encoded.PriorityKind == "unix-nice" && encoded.PriorityValue >= 10), "canonical encoding uses measured reduced process priority");
            }
            else
                Check.That(children.Length == 0, "current canonical upload needs no native work or reencoding");
            await using var observedDb = Database.CreateContext();
            var journal = await observedDb.Set<MediaReconciliationOutcomeEntity>().SingleAsync(row => row.JobId == jobId, ct);
            var persisted = await observedDb.Jobs.OfType<MediaReconciliationJobEntity>().SingleAsync(row => row.Id == jobId, ct);
            var retained = (await ProbeMediaFacts.DescribeAsync(observedDb, romId, ct)).Preferred;
            var identity = retained.Output.Identity;
            Check.That(journal.Kind == MediaReconciliationOutcomeKind.Completed.ToString() && persisted.CompletedAt is not null && persisted.ExecutionFenceToken is null &&
                await observedDb.Set<MediaReconciliationPinEntity>().CountAsync(row => row.JobId == jobId, ct) == 0 && retained.Encoding == RomRepresentationEncoding.Rvz,
                "terminal journal, preferred RVZ and released execution pins are durable");
            var creation = expectEncoding ? retained.Facts.Creations.Single() : null;
            var sourceWitness = creation?.Sources.Single();
            if (expectEncoding)
                Check.That(identity.Sha256.ToString() == oracle.Sha256 && sourceWitness!.ArtifactSha256.ToString() == oracle.Sha256 &&
                    sourceWitness.ArtifactSize == oracle.Size && sourceWitness.FileId == original.FileId &&
                    sourceWitness.RepresentationId == original.RepresentationId && sourceWitness.OutputId == original.Output.Id &&
                    sourceWitness.VerificationId != creation!.VerificationId && identity.Size == oracle.Size && identity.Sha1.ToString() == oracle.Sha1 &&
                    identity.Md5.ToString() == oracle.Md5 && identity.Crc32.ToString() == oracle.Crc && creation.Recipe.Value == RvzCanonicalRecipe.Id,
                    "source-equivalence creation witness survives released original interpretation");
            return new
            {
                JobId = jobId,
                DurationSeconds = Stopwatch.GetElapsedTime(started).TotalSeconds,
                Checkpoints = checkpoints,
                LeaseRenewals = renewals,
                Children = children,
                Outcome = journal.Kind,
                OutcomeCode = journal.Code,
                OutputFileId = retained.FileId,
                OutputVerificationId = creation?.VerificationId,
                SourceDependencyReleased = true,
                Creation = creation is null ? null : new { creation.Id, Source = sourceWitness, OutputSha256 = retained.ArtifactSha256.ToString(), Recipe = creation.Recipe.Value, creation.EncoderBuild }
            };

            async Task HeartbeatAsync(CancellationToken token)
            {
                try
                {
                    while (true)
                    {
                        token.ThrowIfCancellationRequested();
                        await using var lease = Database.CreateContext();
                        if (!await Claims(lease).RenewExecutionLeaseAsync(jobId, claim.FenceToken, token))
                        {
                            if (await lease.Set<MediaReconciliationOutcomeEntity>().AnyAsync(outcome => outcome.JobId == jobId, token))
                                return;
                            throw new JobExecutionOwnershipLostException();
                        }
                        Interlocked.Increment(ref renewals);
                        await Task.Delay(_claimLimits.HeartbeatInterval, token);
                    }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch { await run.CancelAsync(); throw; }
            }
        }

        public async Task<DeliveryResult> ReadBothAsync(int romId, string iso, Digests oracle, CancellationToken ct)
        {
            var catalog = new ProbeCatalog(Database.CreateOptions());
            var descriptor = await catalog.FindByRomIdAsync(romId, ct) ?? throw new InvalidOperationException("ROM catalog entry missing.");
            var artifacts = descriptor.Artifacts ?? throw new InvalidOperationException("Catalog artifact set missing.");
            Check.That(descriptor.Encoding == RomRepresentationEncoding.Rvz && artifacts.Count == 1 &&
                artifacts[0].IsPreferred && artifacts[0].HasMeasuredEvidence, "single measured preferred RVZ descriptor");
            var reader = new RomContentReader(catalog, Cas, _decoder, ProbeMediaFacts.ManagedAdmission);
            var options = RomDeliveryPlanner.Plan(descriptor, "disc.iso", RomMediaPolicyComposition.CreateDeliveryPolicy(), chdAdmission: ProbeMediaFacts.ManagedAdmission);
            var results = new List<object>();
            Digests? stored = null;
            foreach (var option in options)
            {
                Check.That(option.State == RomDeliveryState.Available && option.Target is not null, "both production delivery formats available");
                var target = option.Target!;
                long started = Stopwatch.GetTimestamp();
                await using var stream = await reader.OpenAsync(target, ct) ?? throw new InvalidOperationException("Delivery target unavailable.");
                var hashes = await Digests.StreamAsync(stream, ct, target.SizeBytes);
                Check.That(hashes.Size == target.SizeBytes && hashes.Sha256 == target.Sha256.ToString() &&
                    target.Md5?.ToString() == hashes.Md5 && target.Sha1?.ToString() == hashes.Sha1, "advertised hashes describe complete delivered bytes");
                if (target.Format == RomDeliveryFormat.Original)
                    Check.That(hashes == oracle, "delivered ISO matches independent original byte hashes");
                else
                    stored = hashes;
                double fullSeconds = Stopwatch.GetElapsedTime(started).TotalSeconds;
                var ranges = await CheckRangesAsync(reader, target, Cas, iso, ct);
                results.Add(new
                {
                    Format = RomDeliveryFormats.ToWire(target.Format),
                    target.Filename,
                    Delivered = hashes,
                    FullReadSeconds = fullSeconds,
                    Ranges = ranges
                });
            }
            Check.That(stored is not null && stored.Sha256 == descriptor.ArtifactSha256.ToString() && stored.Size == descriptor.ArtifactSize,
                "RVZ delivery is exact retained artifact");
            return new(stored!, results.ToArray(), new
            {
                descriptor.RomId,
                descriptor.FileId,
                LogicalSha256 = descriptor.ContentSha256.ToString(),
                descriptor.ContentSize,
                ArtifactSha256 = descriptor.ArtifactSha256.ToString(),
                descriptor.ArtifactSize,
                descriptor.EncodingProfile,
                Artifacts = artifacts.Select(artifact => new
                {
                    artifact.FileId,
                    Sha256 = artifact.ArtifactSha256.ToString(),
                    Size = artifact.ArtifactSize,
                    artifact.EncodingProfile,
                    Md5 = artifact.ArtifactMd5?.ToString(),
                    Sha1 = artifact.ArtifactSha1?.ToString(),
                    artifact.IsPreferred,
                    artifact.HasMeasuredEvidence
                }).ToArray()
            });
        }

        public async Task<int> ProofCountAsync(int romId, CancellationToken ct)
        {
            await using var db = Database.CreateContext();
            return await ProbeMediaFacts.VerificationsAsync(db, romId, ct);
        }

        public async Task AssertRetainedAsync(int romId, string sha256, int proofs, CancellationToken ct)
        {
            await using var db = Database.CreateContext();
            var descriptor = await Romd.PostgreSql.TestSupport.MediaTestServices.CreateContentCatalog(db).FindByRomIdAsync(romId, ct);
            Check.That(descriptor is not null && descriptor.ArtifactSha256.ToString() == sha256 &&
                await ProbeMediaFacts.OwnedRepresentationsAsync(db, romId, ct) == 1 &&
                await ProbeMediaFacts.VerificationsAsync(db, romId, ct) == proofs &&
                await db.Set<MediaReconciliationPinEntity>().CountAsync(ct) == 0 &&
                await db.Set<MediaReconciliationSubjectEntity>().CountAsync(row => row.SubjectKind == "LogicalRom" && row.SubjectId == romId, ct) == 1,
                "ranking preserves sole preferred RVZ and every decoder build while recording current general demand");
        }

        public async ValueTask DisposeAsync()
        {
            _verifier.Dispose();
            await _executionServices.DisposeAsync();
            await _decoder.DisposeAsync();
            await _services.DisposeAsync();
            await Database.DisposeAsync();
            if (Directory.Exists(_root))
            {
                bool quarantined = Directory.EnumerateFiles(_root, "*.quarantine", SearchOption.AllDirectories).Any();
                if (_supervisor.CustodyUncertain || quarantined)
                    Console.Error.WriteLine("Convergence probe preserved unresolved media custody at " + _root);
                else
                    Directory.Delete(_root, true);
            }
        }
    }

    private static async Task<object> CheckRangesAsync(RomContentReader reader, RomDeliveryTarget target,
        IContentAddressableStore cas, string iso, CancellationToken ct)
    {
        await using var actual = await reader.OpenSeekableAsync(target, ct) ?? throw new InvalidOperationException("Seekable target unavailable.");
        await using var raw = target.Format == RomDeliveryFormat.Rvz
            ? await cas.OpenRandomAccessAsync(StorageKey.FromHash(target.ArtifactSha256), ct) : null;
        using var original = target.Format == RomDeliveryFormat.Original ? File.OpenHandle(iso, FileMode.Open, FileAccess.Read, FileShare.Read) : null;
        var offsets = new List<long> { 0, target.SizeBytes - 1, Math.Max(0, target.SizeBytes - 8192) };
        foreach (long boundary in new long[] { 32768, 65536, 131072, 262144, 2097152, 0x40000, 0x50000, 0xf800000, 0xf820000 })
            foreach (int delta in new[] { -1, 0, 1 })
                if (boundary + delta >= 0 && boundary + delta < target.SizeBytes)
                    offsets.Add(boundary + delta);
        var random = new Random(90421);
        for (int index = 0; index < 64; index++)
            offsets.Add(random.NextInt64(target.SizeBytes));
        var durations = new List<double>();
        foreach (long offset in offsets.Distinct())
        {
            int length = (int)Math.Min(8192, target.SizeBytes - offset);
            byte[] expected = new byte[length], received = new byte[length];
            int read = original is not null ? await RandomAccess.ReadAsync(original, expected, offset, ct)
                : await raw!.ReadAtAsync(offset, expected, ct);
            Check.That(read == length, "fixture/CAS oracle positional read fills exact range");
            actual.Position = offset;
            long started = Stopwatch.GetTimestamp();
            await actual.ReadExactlyAsync(received, ct);
            durations.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            Check.That(expected.AsSpan().SequenceEqual(received), "production delivered positional bytes equal oracle at " + offset);
        }
        return new
        {
            Count = durations.Count,
            Seed = 90421,
            MaximumReadBytes = 8192,
            MedianMilliseconds = durations.Order().ElementAt(durations.Count / 2),
            MaximumMilliseconds = durations.Max(),
            Offsets = offsets.Distinct().ToArray()
        };
    }

    private sealed record UploadResult(int RomId, int FileId, string ArtifactSha256, long ArtifactSize,
        RomRepresentationEncoding Encoding, string EncodingProfile, [property: JsonIgnore] RomContainerFacts Container, Digests Logical, Guid VerificationId,
        string VerificationProfile, string DecoderBuild, [property: JsonIgnore] RomIsoEligibility? Eligibility, double Seconds, ChildResult[] Children)
    {
        public object? EligibilityFacts => Eligibility is not { } observed ? null : new
        {
            observed.Id,
            ContentSha256 = observed.ContentSha256.ToString(),
            observed.ContentSize,
            Status = observed.Status.ToString(),
            DiscKind = observed.DiscKind?.ToString(),
            observed.WiiPartitionCount,
            observed.StandardKeyPartitions,
            observed.KoreanKeyPartitions,
            observed.Profile,
            observed.InspectorBuild
        };
    }
    private sealed record DeliveryResult(Digests Stored, object[] Formats, object Descriptor);
    private sealed record ChildResult(string Operation, double Seconds, string InputSha256, long InputBytes, long? OutputBound, MediaWorkerResult Result);
    private sealed class CapturingSupervisor(MediaWorkerSupervisor inner) : IMediaWorkerSupervisor
    {
        public List<ChildResult> Events { get; } = [];
        public bool CustodyUncertain { get; private set; }
        public bool IsAvailable => inner.IsAvailable;
        public Task<MediaWorkerResult> HandshakeAsync(CancellationToken ct = default) => inner.HandshakeAsync(ct);
        public Task<MediaWorkerResult> InspectAsync(ITempFile source, CancellationToken ct = default) =>
            CaptureAsync("inspect-rvz", source, null, () => inner.InspectAsync(source, ct), ct);
        public Task<MediaWorkerResult> InspectIsoAsync(ITempFile source, CancellationToken ct = default) =>
            CaptureAsync("inspect-iso", source, null, () => inner.InspectIsoAsync(source, ct), ct);
        public Task<MediaWorkerResult> EncodeAsync(ITempFile source, IWritableTempFile output, long logicalBytes, RomIsoDiscKind discKind,
            CancellationToken ct = default) => CaptureAsync("encode", source, output.MaximumBytes,
                () => inner.EncodeAsync(source, output, logicalBytes, discKind, ct), ct);
        private async Task<MediaWorkerResult> CaptureAsync(string operation, ITempFile source, long? outputBound,
            Func<Task<MediaWorkerResult>> run, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            long started = Stopwatch.GetTimestamp();
            try
            {
                var result = await run();
                if (!result.ProcessTreeStopped)
                    CustodyUncertain = true;
                Events.Add(new(operation, Stopwatch.GetElapsedTime(started).TotalSeconds, source.FileSha256.ToString(), source.FileSize, outputBound, result));
                return result;
            }
            catch { CustodyUncertain = true; throw; }
        }
    }
    private sealed class FixtureIdentity(Fixture fixture)
    {
        private readonly (string Path, long Bytes, DateTime Modified)[] _facts = new[] { fixture.Iso, fixture.UploadedRvz, fixture.Dat }
            .Select(path => { var file = new FileInfo(path); return (path, file.Length, file.LastWriteTimeUtc); }).ToArray();
        public void AssertUnchanged()
        {
            foreach (var fact in _facts)
            {
                var current = new FileInfo(fact.Path);
                Check.That(current.Length == fact.Bytes && current.LastWriteTimeUtc == fact.Modified, "fixture files remain immutable: " + fact.Path);
            }
        }
    }
}

/// <summary>Production catalog adapter with a fresh EF context per lookup; safe for independent concurrent probe reads.</summary>
internal sealed class ProbeCatalog(DbContextOptions<RomdDbContext> options) : IRomContentCatalog
{
    public async Task<RomContentDescriptor?> FindAsync(Romd.Domain.Hashing.Sha256 hash, CancellationToken ct = default)
    {
        await using var context = new RomdDbContext(options);
        return await Romd.PostgreSql.TestSupport.MediaTestServices.CreateContentCatalog(context).FindAsync(hash, ct);
    }
    public async Task<RomContentDescriptor?> FindByRomIdAsync(int romId, CancellationToken ct = default)
    {
        await using var context = new RomdDbContext(options);
        return await Romd.PostgreSql.TestSupport.MediaTestServices.CreateContentCatalog(context).FindByRomIdAsync(romId, ct);
    }

    public async Task<IReadOnlyDictionary<int, RomContentDescriptor>> FindByRomIdsAsync(
        IReadOnlyCollection<int> romIds, CancellationToken ct = default)
    {
        await using var context = new RomdDbContext(options);
        return await Romd.PostgreSql.TestSupport.MediaTestServices.CreateContentCatalog(context).FindByRomIdsAsync(romIds, ct);
    }
}

internal delegate Task<ConvergedLibraryCallbackResult> ConvergedLibraryCheck(IContentAddressableStore cas,
    IRomContentCatalog catalog, IReadOnlyList<(RomContentDescriptor Content, string IsoPath, string SystemKey)> library,
    ReadResourceChecks.StagingScope stagingScope, CancellationToken ct);
internal sealed record ConvergedLibraryCallbackResult(TimeSpan RequestedDuration, string ResultsPath);
