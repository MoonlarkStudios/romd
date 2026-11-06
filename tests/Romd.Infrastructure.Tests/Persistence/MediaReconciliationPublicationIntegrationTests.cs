using System.Collections.Concurrent;
using System.IO.Abstractions;
using ErrorOr;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using NSubstitute;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Admin.Application.Source.Rom;
using Romd.Admin.Application.Source.Rom.Reconciliation;
using Romd.Admin.Application.Storage.Files;
using Romd.Application.Common.Configuration;
using Romd.Application.Common.Content;
using Romd.Domain.Jobs;
using Romd.Domain.Source.Rom;
using Romd.Infrastructure.Storage;
using Romd.Infrastructure.Tests.Storage;
using Romd.Infrastructure.Tests.Helpers;
using Romd.Media.Inspection;
using Romd.Media.TestSupport;
using Romd.Persistence;
using Romd.Persistence.Entities;
using Romd.Persistence.MediaFacts;
using Romd.Persistence.Repositories;
using Romd.Persistence.Repositories.Reconciliation;
using Romd.PostgreSql.TestSupport;
using Romd.Storage;
using Shouldly;
using Xunit;
using Xunit.Abstractions;

namespace Romd.Infrastructure.Tests.Persistence;

/// <summary>Real stopped child, measured RVZ, CAS and PostgreSQL; disposable generated discs, never private-corpus or image qualification.</summary>
[Collection(NativeMediaCodecWorkloads.Name)]
public sealed partial class MediaReconciliationPublicationIntegrationTests(ITestOutputHelper output) : IDisposable
{
    private readonly PostgreSqlTestDatabase database = PostgreSqlTestDatabase.Create();
    public void Dispose() => database.Dispose();

    [Theory]
    [InlineData("commit")]
    [InlineData("rollback")]
    [InlineData("lost-response")]
    [InlineData("deadlock")]
    [InlineData("cancel")]
    [InlineData("identity-flush-fault")]
    [InlineData("identity-flush-cancel")]
    [InlineData("identity-flush-cancel-and-cleanup")]
    public async Task Whole_RealEncodingAndPublication_PreservesAtomicEvidenceBytesAndCleanupAcrossCommitOutcomes(string interruption)
        => await WholePublicationAsync(interruption, psp: false);

    [LinuxEncodingTheory]
    [InlineData("commit")]
    [InlineData("rollback")]
    [InlineData("lost-response")]
    [InlineData("deadlock")]
    [InlineData("cancel")]
    public async Task Psp_ImportDemandWithoutExplicitIntent_PublishesExactChdOrRetainsOriginalAcrossCommitOutcomes(string interruption)
        => await WholePublicationAsync(interruption, psp: true);

    private async Task WholePublicationAsync(string interruption, bool psp)
    {
        await using var db = database.CreateContext();
        var work = new EfUnitOfWork(db);
        var native = psp ? new NativeToolOptions { LibchdrDirectory = NativeTestBuilds.LibchdrDirectory, ChdmanDirectory = NativeTestBuilds.ChdmanDirectory }
            : new NativeToolOptions();
        await using var media = new PublicationMedia(db, work, native);
        byte[] original = psp ? SyntheticPspUmd.CodecImage(modern: true) : SyntheticNintendoIso.GameCube();
        var identity = MediaFactFixtures.Identity(original);
        RomRepresentationFacts source;
        await using (var temp = await media.Staging.CreateAsync(new MemoryStream(original)))
        await using (var staged = await media.Files.StageAsync(temp))
        await using (var transaction = await work.BeginTransactionAsync())
        {
            var file = await media.Files.PublishAndFlushAsync(db, staged);
            var facts = new MediaFactPublisher(db);
            int rom = await facts.CreateLogicalAsync("disc.iso", identity, Guid.Empty, DateTimeOffset.UnixEpoch);
            var hasher = MediaFactFixtures.WholeMeasurement(Guid.NewGuid(), identity.Sha256, identity.Size, identity, RomContainerFacts.Identity.Instance,
                new RomDecoderIdentity.Managed("import-hasher/v1"), "complete-import/v1", "fixture-importer", DateTimeOffset.UnixEpoch, null, null);
            source = await facts.PublishAsync(file.File.Id, RomContainerFacts.Identity.Instance, RomReconstructionManifest.WholeFile.Instance, hasher, [new(RomOutputSelector.Whole.Instance, rom, true)], []);
            await transaction.CommitAsync();
        }
        Guid job;
        await using (var transaction = await work.BeginTransactionAsync())
        {
            var accepted = await new MediaReconciliationRequests(db, TimeProvider.System).StageSubjectAsync(new(Guid.NewGuid(),
                new MediaReconciliationSubject.LogicalRom(source.Outputs.Single().RomId!.Value), false, MediaReconciliationCause.Import, null, null));
            accepted.IsError.ShouldBeFalse();
            job = accepted.Value.Job.Id;
            await transaction.CommitAsync();
        }
        var repository = new MediaReconciliationJobRepository(db, TimeProvider.System);
        var claim = (await new ClaimedJobRepository<MediaReconciliationJob, MediaReconciliationJobRepository>(repository, db, TimeProvider.System, new())
            .TryClaimExecutionAsync(job, "actual-publication-fixture")).ShouldNotBeNull();
        var decision = new MediaReconciliationDecision(media.Composition.Kinds, media.Composition.Reconciliation, media.Capabilities, media.Profile);
        var snapshots = new MediaReconciliationSnapshotReader(database.CreateOptions(), media.Composition.Kinds, Substitute.For<IRomCdDescriptorIdentityReader>());
        var planning = new MediaReconciliationPlanning(snapshots, decision);
        var execution = new MediaReconciliationExecution(db, database.CreateOptions(), work, media.Runtime, TimeProvider.System, media.Composition.Kinds, decision);
        var interrupted = new CommitInterruption(work, interruption);
        using var identityCancellation = new CancellationTokenSource();
        bool cleanupFailure = interruption == "identity-flush-cancel-and-cleanup";
        var identityFlush = new FileIdentityFlushInterruption(work, db, interruption == "identity-flush-cancel" || cleanupFailure, identityCancellation);
        var observedFlush = new ObservedPublicationFlush(identityFlush);
        bool interruptIdentity = interruption is "identity-flush-fault" or "identity-flush-cancel" || cleanupFailure;
        var ownedStages = new List<DisposalFaultStage>();
        var publicationFiles = cleanupFailure ? WithStagingDisposalFailures(media.Files, ownedStages) : media.Files;
        var publisher = new MediaReconciliationPublication(publicationFiles, interruptIdentity ? observedFlush : interrupted, execution, execution, media.Runtime);
        MediaReconciliationEncoded.Whole encoded;
        string artifactPath;
        using (JobExecutionFenceScope.Enter(job, claim.FenceToken, TimeProvider.System))
        {
            var wanted = (await execution.ReadDemandAsync(job)).ShouldNotBeNull();
            var inspect = (await planning.NextAsync(job, wanted)).Value.ShouldBeOfType<MediaReconciliationDirective.Inspect>();
            var inspectAttempt = await execution.CaptureAsync(inspect.Capture);
            inspectAttempt.IsError.ShouldBeFalse();
            var acquired = await media.Originals.AcquireAsync(job, inspect.Source, (RomDecoderIdentity.Managed)inspect.Capture.Decoder);
            acquired.IsError.ShouldBeFalse();
            await using (var lease = acquired.Value)
            {
                var measured = await lease.InspectAsync();
                measured.IsError.ShouldBeFalse();
                lease.ProcessTreeStopped.ShouldBeTrue();
                (await lease.ReleaseProcessingAsync()).IsError.ShouldBeFalse();
                (await execution.RecordVerificationAsync(inspectAttempt.Value, measured.Value, true)).IsError.ShouldBeFalse();
            }
            wanted = (await execution.ReadDemandAsync(job)).ShouldNotBeNull();
            wanted.Version.ShouldBe(2);
            var encode = (await planning.NextAsync(job, wanted)).Value.ShouldBeOfType<MediaReconciliationDirective.Encode>();
            var attempt = await execution.CaptureAsync(encode.Capture);
            attempt.IsError.ShouldBeFalse();
            var admission = await media.Encoding.AcquireAsync(attempt.Value);
            admission.IsError.ShouldBeFalse();
            await using (var lease = admission.Value)
            {
                int comparisons = 0;
                var measured = await lease.EncodeAndVerifyAsync(_ => { comparisons++; return Task.CompletedTask; });
                measured.IsError.ShouldBeFalse(measured.IsError ? measured.FirstError.ToString() : null);
                comparisons.ShouldBe(1);
                encoded = measured.Value.ShouldBeOfType<MediaReconciliationEncoded.Whole>();
                artifactPath = encoded.Artifact.Path;
                lease.ProcessTreeStopped.ShouldBeTrue();
                (await lease.ReleaseProcessingAsync()).IsError.ShouldBeFalse();
                db.Database.CurrentTransaction.ShouldBeNull();
                File.Exists(artifactPath).ShouldBeTrue();
                if (interruptIdentity)
                {
                    if (cleanupFailure)
                    {
                        Exception? observed = null;
                        try
                        {
                            await publisher.PublishAsync(attempt.Value, encoded, true, identityCancellation.Token);
                        }
                        catch (Exception error)
                        {
                            observed = error;
                        }
                        observed.ShouldBeSameAs(observedFlush.Failure.ShouldNotBeNull());
                        observed.ShouldBeOfType<OperationCanceledException>().CancellationToken.ShouldBe(identityCancellation.Token);
                        ownedStages.ShouldHaveSingleItem().Disposals.ShouldBe(1);
                        File.Exists(artifactPath).ShouldBeTrue();
                    }
                    else if (interruption == "identity-flush-cancel")
                        (await Should.ThrowAsync<OperationCanceledException>(() => publisher.PublishAsync(attempt.Value, encoded, true, identityCancellation.Token)))
                            .CancellationToken.ShouldBe(identityCancellation.Token);
                    else
                        await Should.ThrowAsync<IOException>(() => publisher.PublishAsync(attempt.Value, encoded, true, identityCancellation.Token));
                    identityFlush.GeneratedFileIds.ShouldHaveSingleItem();
                    db.ChangeTracker.Entries().ShouldBeEmpty();
                    await db.SaveChangesAsync();
                    (await execution.ReadAttemptOutcomeAsync(job, attempt.Value.Number)).ShouldBeNull();
                    (await db.Files.CountAsync()).ShouldBe(1);
                    (await db.RomRepresentations.CountAsync()).ShouldBe(1);
                    (await db.RomFiles.SingleAsync()).PreferredOutputId.ShouldBe(source.Outputs.Single().Id);
                    (await db.Set<MediaReconciliationPinEntity>().CountAsync()).ShouldBe(1);
                    (await media.Files.DeleteOrphanAsync(encoded.Artifact.FileSha256, CancellationToken.None)).ShouldBeTrue();
                    (await media.Files.DeleteOrphanAsync(source.ArtifactSha256, CancellationToken.None)).ShouldBeFalse();
                    if (cleanupFailure)
                    {
                        // Reuse the same stopped, measured invocation after real rollback. A failed
                        // staging cleanup neither changes cancellation nor consumes its caller's payload.
                        var retry = new MediaReconciliationPublication(media.Files, work, execution, execution, media.Runtime);
                        (await retry.PublishAsync(attempt.Value, encoded, true)).IsError.ShouldBeFalse();
                        (await execution.ReadAttemptOutcomeAsync(job, attempt.Value.Number)).ShouldNotBeNull()
                            .Disposition.ShouldBe(MediaReconciliationAttemptDisposition.Recorded);
                        (await db.Set<MediaReconciliationPinEntity>().CountAsync()).ShouldBe(0);
                        wanted = (await execution.ReadDemandAsync(job)).ShouldNotBeNull();
                        (await planning.NextAsync(job, wanted)).Value.ShouldBeOfType<MediaReconciliationDirective.Settle>()
                            .Outcome.ShouldBe(MediaReconciliationOutcomeKind.Completed);
                        (await execution.FinishAsync(job, wanted.Version, MediaReconciliationOutcomeKind.Completed, "canonical", true))
                            .IsError.ShouldBeFalse();
                        (await retry.PublishAsync(attempt.Value, null!, false)).IsError.ShouldBeFalse();
                        File.Exists(artifactPath).ShouldBeTrue();
                    }
                }
                else if (interruption == "rollback")
                {
                    await Should.ThrowAsync<IOException>(() => publisher.PublishAsync(attempt.Value, encoded, true));
                    (await execution.ReadAttemptOutcomeAsync(job, attempt.Value.Number)).ShouldBeNull();
                    (await db.Set<MediaReconciliationPinEntity>().CountAsync()).ShouldBe(1);
                    (await db.RomFiles.SingleAsync()).PreferredOutputId.ShouldBe(source.Outputs.Single().Id);
                    (await db.Files.CountAsync()).ShouldBe(1);
                    (await media.Files.DeleteIfUnreferencedAsync(source.FileId, CancellationToken.None)).ShouldBeFalse();
                    (await media.Files.DeleteOrphanAsync(encoded.Artifact.FileSha256, CancellationToken.None)).ShouldBeTrue();
                    (await media.Files.DeleteOrphanAsync(source.ArtifactSha256, CancellationToken.None)).ShouldBeFalse();
                }
                else if (interruption == "cancel")
                {
                    // An independent operator cancellation arrives after the real encoder stops but before publication.
                    await using (var cancellation = database.CreateContext())
                        await cancellation.Set<MediaReconciliationDefinitionEntity>().Where(row => row.JobId == job)
                            .ExecuteUpdateAsync(set => set.SetProperty(row => row.CancelRequested, true));
                    (await publisher.PublishAsync(attempt.Value, encoded, true)).FirstError.ShouldBe(MediaReconciliationErrors.Changed);
                    (await execution.ReadAttemptOutcomeAsync(job, attempt.Value.Number)).ShouldBeNull();
                    (await db.Files.CountAsync()).ShouldBe(1);
                    (await db.Set<MediaReconciliationPinEntity>().CountAsync()).ShouldBe(1);
                    wanted = (await execution.ReadDemandAsync(job)).ShouldNotBeNull();
                    wanted.CancelRequested.ShouldBeTrue();
                    var cancelled = await execution.FinishAsync(job, wanted.Version, MediaReconciliationOutcomeKind.Cancelled, "cancelled", true);
                    cancelled.IsError.ShouldBeFalse();
                    cancelled.Value.Kind.ShouldBe(MediaReconciliationOutcomeKind.Cancelled);
                    (await db.Set<MediaReconciliationPinEntity>().CountAsync()).ShouldBe(0);
                    (await execution.ReadAttemptOutcomeAsync(job, attempt.Value.Number)).ShouldNotBeNull().Disposition.ShouldBe(MediaReconciliationAttemptDisposition.Cancelled);
                    (await publisher.PublishAsync(attempt.Value, null!, false)).IsError.ShouldBeFalse();
                }
                else
                {
                    (await publisher.PublishAsync(attempt.Value, encoded, true)).IsError.ShouldBeFalse();
                    var journal = (await execution.ReadAttemptOutcomeAsync(job, attempt.Value.Number)).ShouldNotBeNull();
                    journal.Disposition.ShouldBe(MediaReconciliationAttemptDisposition.Recorded);
                    journal.VerificationId.ShouldBe(encoded.Measured.Measurement.Id);
                    var retained = (await new MediaFactReader(db).ReadAsync([journal.RepresentationId!.Value]))[journal.RepresentationId.Value];
                    retained.Creations.ShouldHaveSingleItem().Sources.Single().VerificationId.ShouldBe(source.Verifications.Single().Id);
                    retained.Creations.Single().EncoderBuild.ShouldBe(encoded.EncoderBuild);
                    retained.Creations.Single().ComparedAt.ShouldBe(encoded.ComparedAt);
                    (await db.Set<MediaReconciliationPinEntity>().CountAsync()).ShouldBe(0);
                    wanted = (await execution.ReadDemandAsync(job)).ShouldNotBeNull();
                    (await planning.NextAsync(job, wanted)).Value.ShouldBeOfType<MediaReconciliationDirective.Settle>().Outcome.ShouldBe(MediaReconciliationOutcomeKind.Completed);
                    var finished = await execution.FinishAsync(job, wanted.Version, MediaReconciliationOutcomeKind.Completed, "canonical", true);
                    finished.IsError.ShouldBeFalse();
                    finished.Value.AcknowledgedVersion.ShouldBe(wanted.Version);
                    (await publisher.PublishAsync(attempt.Value, null!, false)).IsError.ShouldBeFalse();
                }
                interrupted.Commits.ShouldBe(interruption == "deadlock" ? 2 : interruption == "cancel" || interruptIdentity ? 0 : 1);
                File.Exists(artifactPath).ShouldBeTrue(); // Publisher never disposes the encoding lease's payload.
                await Should.ThrowAsync<InvalidOperationException>(() => lease.EncodeAndVerifyAsync(_ => Task.CompletedTask));
            }
        }
        File.Exists(artifactPath).ShouldBeFalse();
        var originalGraph = (await new MediaFactReader(db).ReadAsync([source.Id]))[source.Id];
        originalGraph.Verifications.Count.ShouldBe(2);
        originalGraph.Verifications.ShouldContain(proof => proof.Decoder.Equals(media.Profile.OriginalInspectionDecoder));
        if (interruption is "commit" or "deadlock" or "lost-response" or "identity-flush-cancel-and-cleanup")
        {
            var descriptor = (await MediaTestServices.CreateContentCatalog(db).FindAsync(identity.Sha256)).ShouldNotBeNull();
            descriptor.Encoding.ShouldBe(psp ? RomRepresentationEncoding.Chd : RomRepresentationEncoding.Rvz);
            descriptor.Artifacts.ShouldHaveSingleItem();
            if (psp)
            {
                descriptor.Inspections.ShouldContain(inspection => inspection.Layout is RomPspLayoutFacts);
                var formats = RomDeliveryPlanner.Plan(descriptor, "disc.iso", media.Composition.DeliveryPolicy,
                    MediaKindResolver.Resolve(media.Composition.Kinds, descriptor, ["psp"]), media.Admission);
                formats.Select(format => format.Format).ShouldBe([RomDeliveryFormat.Original, RomDeliveryFormat.Chd]);
                formats.ShouldAllBe(format => format.State == RomDeliveryState.Available && format.RuntimeApplicability.Count == 0);
            }
            await using var decoded = (await media.Reader.OpenVerifiedByRomIdAsync(descriptor.RomId)).ShouldNotBeNull();
            using var bytes = new MemoryStream();
            await decoded.CopyToAsync(bytes);
            bytes.ToArray().ShouldBe(original);
            (await new MediaRepresentationCleanup(db).CollectAsync(256)).ShouldBe(1);
            (await media.Files.DeleteIfUnreferencedAsync(source.FileId, CancellationToken.None)).ShouldBeTrue();
            (await media.Files.ExistsAsync(descriptor.FileId, CancellationToken.None)).ShouldBeTrue();
        }
        else
        {
            await using var bytes = (await media.Files.RetrieveByIdAsync(source.FileId, CancellationToken.None)).ShouldNotBeNull();
            using var copied = new MemoryStream();
            await bytes.CopyToAsync(copied);
            copied.ToArray().ShouldBe(original);
        }
    }

    private sealed class PublicationMedia : IAsyncDisposable
    {
        private readonly ServiceProvider services;
        private readonly MediaWorkerTestFixture child = new();
        private readonly string root = Directory.CreateTempSubdirectory("romd-publication-integration-").FullName;
        private readonly ConcurrentQueue<string> childTrace = new();
        internal string ChildTrace => string.Join("\n", childTrace);
        private readonly RvzVerifier verifier;
        private readonly RomRepresentationDecoder decoder;
        private readonly ChdMediaAdapter? chdAdapter;
        internal LocalTempFileFactory Staging { get; }
        internal IFileStorageService Files { get; }
        internal MediaProcessingRuntime Runtime { get; }
        internal RomMediaComposition Composition { get; } = RomMediaPolicyComposition.Create();
        internal IMediaHostCapabilities Capabilities { get; } = Substitute.For<IMediaHostCapabilities>();
        internal MediaReconciliationMediaProfile Profile { get; }
        internal RomOriginalMediaInspection Originals { get; }
        internal MediaReconciliationEncoding Encoding { get; }
        internal RomContentReader Reader { get; }
        internal ChdDeliveryAdmission Admission { get; }
        internal PublicationMedia(RomdDbContext db, IUnitOfWork work, NativeToolOptions native)
        {
            var options = Substitute.For<IRomdOptions>();
            options.DataDirectory.Returns(root);
            options.MaxUploadBytes.Returns(64L << 20);
            var temporary = new TempStorageOptions { MaximumFiles = 32, MaximumBytes = 256L << 20, MinimumFreeBytes = 0, AdmissionTimeoutSeconds = 1 };
            child.Options.MaxArtifactBytes = 64L << 20;
            child.Options.MaxLogicalBytes = 64L << 20;
            services = new ServiceCollection().AddContentAddressableStorage(options => options.RootPath = Path.Combine(root, "cas")).BuildServiceProvider();
            var cas = services.GetRequiredService<IContentAddressableStore>();
            Files = MediaInfrastructureTestServices.CreateFiles(db, cas, work, new FileMutationLock(db), TimeProvider.System);
            Staging = new(options, new FileSystem(), temporary);
            Runtime = new(options, child.Options);
            var deployment = new ChdDeploymentRuntime(native, true);
            if (deployment.Assets is not null)
            {
                deployment.Verify();
                Capabilities.Decoder(RomRepresentationEncoding.Chd).Returns(ChdBuildEvidence.Decoder(deployment.Assets.Native.ExpectedIdentity));
                Capabilities.Qualifies(Arg.Any<string>(), Arg.Any<string>()).Returns(true);
            }
            Capabilities.CanDecode(Arg.Any<string>()).Returns(true);
            Capabilities.CanEncode(Arg.Any<string>()).Returns(true);
            Capabilities.ManagedDecoderBuild.Returns(MediaInspectionService.DecoderBuild);
            var readLimits = new ReadDecoderOptions();
            var cache = new Romd.Media.MediaBlockCache(readLimits.CacheBytes);
            if (deployment.Assets is { } ready)
                chdAdapter = new(ready.Native, cache, readLimits);
            decoder = new(chdAdapter is null ? new RomMediaAdapterRegistry() : new RomMediaAdapterRegistry(chdAdapter), readLimits, cache);
            Profile = new(child.Options, temporary, options, deployment, Composition, Capabilities);
            var supervisor = new RecordingMediaWorkerSupervisor(
                new MediaWorkerSupervisor(child.Options, () => new MediaWorkerProcessLifetime(), runtime: Runtime, chdCandidate: deployment.Assets?.Native), childTrace.Enqueue);
            verifier = new(child.Options, supervisor, Runtime);
            var identifier = new RomOriginalMediaIdentifier(verifier);
            var facts = new MediaFactReader(db);
            Originals = new(Staging, facts, Files, Runtime, child.Options, identifier);
            Admission = new(deployment, Capabilities, Composition.Kinds);
            Reader = new(MediaTestServices.CreateContentCatalog(db), cas, decoder, Admission);
            var whole = new RomWholeReconciliationEncoding(Staging, Files, Runtime, child.Options, identifier, () => verifier,
                () => new ChdVerifier(child.Options, supervisor, Runtime, deployment.Assets!.Native),
                () => new ChdArtifactEncoder(supervisor, child.Options, deployment.Assets!.Tool!), Profile, TimeProvider.System);
            var cd = new RomCdReconciliationEncoding(Staging, Files, Reader, supervisor, Runtime, deployment, TimeProvider.System);
            Encoding = new(Composition, Capabilities, Profile, facts, whole, cd);
        }
        public async ValueTask DisposeAsync()
        {
            verifier.Dispose();
            await decoder.DisposeAsync();
            child.Dispose();
            await services.DisposeAsync();
            if (chdAdapter is not null)
                await chdAdapter.DisposeAsync();
            if (Directory.Exists(root))
                Directory.Delete(root, true);
        }
    }
    private sealed class CommitInterruption : IUnitOfWork
    {
        private readonly IUnitOfWork inner;
        private readonly string mode;
        internal CommitInterruption(IUnitOfWork inner, string mode) { this.inner = inner; this.mode = mode; }
        internal int Commits { get; private set; }
        public Task FlushAsync(CancellationToken cancellationToken = default) => inner.FlushAsync(cancellationToken);
        public TransactionConflict ConflictOf(Exception error) => inner.ConflictOf(error);
        public async Task<ITransaction> BeginTransactionAsync(CancellationToken cancellationToken = default) => new Transaction(await inner.BeginTransactionAsync(cancellationToken), this);
        private sealed class Transaction(ITransaction inner, CommitInterruption owner) : ITransaction
        {
            public async Task CommitAsync(CancellationToken cancellationToken = default)
            {
                owner.Commits++;
                if (owner.Commits == 1 && owner.mode is "rollback" or "deadlock")
                {
                    await inner.RollbackAsync(CancellationToken.None);
                    if (owner.mode == "deadlock")
                        throw new PostgresException("Injected database-aborted deadlock", "ERROR", "ERROR", PostgresErrorCodes.DeadlockDetected);
                    throw new IOException("Injected response before database commit");
                }
                await inner.CommitAsync(cancellationToken);
                if (owner.Commits == 1 && owner.mode == "lost-response")
                    throw new IOException("Injected lost response after database commit");
            }
            public Task RollbackAsync(CancellationToken cancellationToken = default) => inner.RollbackAsync(cancellationToken);
            public ValueTask DisposeAsync() => inner.DisposeAsync();
        }
    }
}
