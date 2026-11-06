using System.IO.Abstractions;
using System.Text;
using ErrorOr;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Romd.Admin.Application.Common.Realtime;
using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Admin.Application.Source.Rom;
using Romd.Admin.Application.Source.Rom.Compressed;
using Romd.Admin.Application.Source.Rom.Reconciliation;
using Romd.Admin.Application.Storage.Files;
using Romd.Application.Common.Configuration;
using Romd.Application.Common.Content;
using Romd.Domain.Hashing;
using Romd.Domain.Jobs;
using Romd.Domain.Source.Rom;
using Romd.Infrastructure.Storage;
using Romd.Infrastructure.Jobs.Executors;
using Romd.Infrastructure.Tests.Storage;
using Romd.Persistence;
using Romd.Persistence.Entities;
using Romd.Persistence.MediaFacts;
using Romd.Persistence.Repositories;
using Romd.Persistence.Repositories.Reconciliation;
using Romd.PostgreSql.TestSupport;
using Romd.Storage;
using Romd.Storage.FileSystem;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Persistence;

/// <summary>Actual intake, ordinary-CUE publication and PostgreSQL source fences; synthetic observations are not native qualification.</summary>
public sealed class RomCdAdoptionCaptureTests : IAsyncLifetime
{
    private readonly PostgreSqlTestDatabase database = PostgreSqlTestDatabase.Create();
    private readonly string root = Directory.CreateTempSubdirectory("romd-cd-adoption-").FullName;
    private readonly List<ServiceProvider> providers = [];
    public Task InitializeAsync() => Task.CompletedTask;

    [Fact]
    public Task Adopt_ByteIdenticalOriginalCue_RetainsRawProofAndPublishesTheFreshComparisonPurpose()
        => Reconcile_CanonicalImportedArchiveAndAuthenticCue_FencesScopeAndPublishesOnlyMeasuredOriginals("exact-cue");

    [Theory]
    [InlineData("unchanged")]
    [InlineData("before-capture")]
    [InlineData("before-publication")]
    [InlineData("publish")]
    [InlineData("concurrent")]
    [InlineData("execute")]
    [InlineData("execute-cancel")]
    [InlineData("rollback")]
    [InlineData("forged-raw-receipt")]
    [InlineData("wrong-profile")]
    public async Task Reconcile_CanonicalImportedArchiveAndAuthenticCue_FencesScopeAndPublishesOnlyMeasuredOriginals(string change)
    {
        var composition = RomMediaPolicyComposition.Create();
        var recipe = composition.CdRecipe.ShouldNotBeNull();
        var decoder = new RomChdDecoderBuild("linux-x64", Sha1.Parse(new string('a', 40)), 2);
        var temp = Temp();
        await using var input = await MediaInfrastructureTestServices.MeasuredCdInputAsync(temp, recipe, decoder, true, 0, trackCount: 2, boot: null);
        await using var db = database.CreateContext();
        await MediaInfrastructureTestServices.SeedCdCatalogAsync(db, input, exactCue: change == "exact-cue");
        var services = MediaInfrastructureTestServices.CdImport(db, Cas(), temp, recipe, Substitute.For<IAdminEventOutbox>(), false, new RomCdCueGenerator(temp));
        var imported = (await services.Ingestor.IngestAsync(input.Prepared, new("disc.chd", true, false, null), default)).Value;
        var archive = imported.Archive;
        var firstProof = archive.Verifications.Single();
        var earlierDecoder = new RomChdDecoderBuild("linux-x64", Sha1.Parse(new string('b', 40)), 2);
        var earlierProof = new RomRepresentationVerification(Guid.NewGuid(), archive.ArtifactSha256, archive.ArtifactSize, new RomDecoderIdentity.Native(earlierDecoder),
            firstProof.CheckedProfile, "synthetic-earlier-decoder", DateTimeOffset.UnixEpoch, firstProof.Outputs, archive.Container, firstProof.ComputedRawSha1, firstProof.ComputedOverallSha1);
        await using (var transaction = await services.Work.BeginTransactionAsync())
        { await new MediaFactPublisher(db).AppendVerificationAsync(archive.Id, earlierProof); await transaction.CommitAsync(); }
        Guid[] retainedProofs = [input.Prepared.Inspection.Id, earlierProof.Id];

        // The exact case uses the ordinary CUE independently published by intake. Other cases supply later original LF bytes.
        byte[] cueBytes = change == "exact-cue" ? input.CueBytes
            : Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(input.CueBytes).Replace("\r\n", "\n", StringComparison.Ordinal));
        var cue = MediaFactFixtures.Identity(cueBytes);
        int cueFileId;
        int cueRomId;
        await using (var stream = new MemoryStream(cueBytes))
        await using (var staged = await services.Files.StageAsync(stream))
        await using (var transaction = await services.Work.BeginTransactionAsync())
        {
            var stored = await services.Files.PublishAndFlushAsync(db, staged);
            cueFileId = stored.File.Id;
            if (change == "exact-cue")
                cueRomId = await db.RomFiles.Where(rom => rom.ContentSha256 == cue.Sha256).Select(rom => rom.Id).SingleAsync();
            else
            {
                var original = new OriginalRomFixture
                {
                    FileId = cueFileId,
                    OriginalFilename = "disc.cue",
                    Sha1 = cue.Sha1,
                    Md5 = cue.Md5,
                    Crc32 = cue.Crc32,
                    ContentSha256 = cue.Sha256,
                    ContentSize = cue.Size,
                    CreatedAt = DateTimeOffset.UnixEpoch.AddDays(2),
                    CreatedByUserId = Guid.Empty
                };
                db.StageOriginalRom(original);
                await db.PublishOriginalRomsAsync();
                cueRomId = original.Id;
            }
            await transaction.CommitAsync();
        }
        cueRomId.ShouldBeGreaterThan(0);
        var manifest = (RomReconstructionManifest.CdTracks)archive.Manifest;
        var layout = new OriginalDiscLayoutEntity
        {
            SourceJobId = Guid.NewGuid(),
            SourceRelativePath = "disc.cue",
            EntryPoint = "disc.cue",
            DescriptorFileId = cueFileId,
            Revision = Guid.NewGuid(),
            State = "pending",
            DependencyPathsJson = System.Text.Json.JsonSerializer.Serialize(input.BinNames),
            Files = archive.Outputs.Select(output =>
                new OriginalDiscLayoutFileEntity
                {
                    Path = manifest.Members[((RomOutputSelector.Member)output.Selector).Ordinal].Path,
                    RomId = output.RomId!.Value,
                    FileId = archive.FileId,
                    BindingRevision = Guid.Empty
                }).ToList()
        };
        db.OriginalDiscLayouts.Add(layout);
        await db.SaveChangesAsync();
        var subject = new MediaReconciliationSubject.OriginalLayout(layout.Id);
        var snapshots = new MediaReconciliationSnapshotReader(database.CreateOptions(), composition.Kinds, new RomCdDescriptorIdentityReader(services.Files));
        var observed = (await snapshots.ReadAsync(subject)).Value.ShouldBeOfType<MediaReconciliationFacts.Layout>();
        var candidate = observed.AdoptionCandidate.ShouldNotBeNull();
        candidate.Graph.Id.ShouldBe(archive.Id);
        var witness = candidate.CatalogWitness.ShouldNotBeNull();
        witness.Requirement.CatalogFileId.ShouldBe(2);
        observed.Sources.Inputs.OfType<MediaReconciliationInput.Logical>().Count().ShouldBe(2);

        var caps = Substitute.For<IMediaHostCapabilities>();
        caps.CanDecode(Arg.Any<string>()).Returns(true);
        caps.CanEncode(Arg.Any<string>()).Returns(false);
        caps.Qualifies(Arg.Any<string>(), Arg.Any<string>()).Returns(true);
        caps.Decoder(RomRepresentationEncoding.Chd).Returns(decoder);
        caps.ManagedDecoderBuild.Returns("fixture-managed");
        var media = Substitute.For<IMediaReconciliationMediaProfile>();
        media.LimitsFingerprint.Returns(Sha256.Parse(new string('a', 64)));
        media.OriginalInspectionDecoder.Returns(new RomDecoderIdentity.Managed("fixture-original"));
        media.Encoder(Arg.Any<string>()).Returns(_ => throw new InvalidOperationException("Canonical adoption must not resolve an encoder."));
        var decision = new MediaReconciliationDecision(composition.Kinds, composition.Reconciliation, caps, media);
        Guid jobId;
        await using (var transaction = await services.Work.BeginTransactionAsync())
        {
            var staged = (await new MediaReconciliationRequests(db, TimeProvider.System).StageSubjectAsync(
                new(Guid.NewGuid(), subject, false, MediaReconciliationCause.Import, null, null))).Value;
            jobId = staged.Job.Id;
            await services.Work.FlushAsync();
            await transaction.CommitAsync();
        }
        var repository = new MediaReconciliationJobRepository(db, TimeProvider.System);
        var claim = (await new ClaimedJobRepository<MediaReconciliationJob, MediaReconciliationJobRepository>(repository, db, TimeProvider.System, new())
            .TryClaimExecutionAsync(jobId, "adoption-fixture")).ShouldNotBeNull();
        using var fence = JobExecutionFenceScope.Enter(jobId, claim.FenceToken, TimeProvider.System);
        var custody = Substitute.For<IMediaRunCustody>();
        custody.EnsureJobStoppedAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(Result.Success);
        var execution = new MediaReconciliationExecution(db, database.CreateOptions(), services.Work, custody, TimeProvider.System, composition.Kinds, decision);
        var demand = (await execution.ReadDemandAsync(jobId)).ShouldNotBeNull();
        demand.ConversionRequested.ShouldBeFalse();
        var directive = decision.Decide(jobId, demand, observed).Value.ShouldBeOfType<MediaReconciliationDirective.Adopt>();
        if (change == "before-capture")
            await ChangeCatalogAsync(db, witness.Requirement.CatalogFileId);
        var captured = await execution.CaptureAsync(directive.Capture);
        if (change == "before-capture")
        {
            captured.FirstError.ShouldBe(MediaReconciliationErrors.Changed);
            (await db.Set<MediaReconciliationPinEntity>().CountAsync(pin => pin.JobId == jobId)).ShouldBe(0);
        }
        else
        {
            captured.IsError.ShouldBeFalse(captured.IsError ? captured.FirstError.ToString() : null);
            var recorded = await db.Database.SqlQuery<WitnessRow>($"""
                SELECT "WitnessCatalogFileId" AS "CatalogFileId","WitnessBindingId" AS "BindingId","WitnessRevision" AS "Revision"
                FROM romd."MediaReconciliationInputs" WHERE "JobId"={jobId} AND "WitnessBindingId" IS NOT NULL
                """).SingleAsync();
            recorded.ShouldBe(new(witness.Requirement.CatalogFileId, witness.BindingId, witness.Revision));
            (await db.Set<MediaReconciliationPinEntity>().CountAsync(pin => pin.JobId == jobId)).ShouldBe(4);
            if (change is "exact-cue" or "publish" or "concurrent" or "execute" or "execute-cancel" or "rollback" or "forged-raw-receipt" or "wrong-profile")
            {
                if (change == "forged-raw-receipt")
                {
                    await using (var transaction = await services.Work.BeginTransactionAsync())
                    {
                        var error = await Should.ThrowAsync<Npgsql.PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync($"""
                            UPDATE romd."MediaReconciliationAttempts" SET "Outcome"='Recorded',"OutcomeCode"='forged',"Deterministic"=false,
                                "ResultRepresentationId"={archive.Id},"ResultVerificationId"={input.Prepared.Inspection.Id},
                                "CompletedAtUtcTicks"={DateTimeOffset.UtcNow.UtcTicks},"StoppedAtUtcTicks"={DateTimeOffset.UtcNow.UtcTicks}
                            WHERE "JobId"={jobId} AND "Number"={captured.Value.Number}
                            """));
                        error.SqlState.ShouldBe("23514");
                        error.MessageText.ShouldContain("Adoption completion requires");
                    }
                    (await execution.ReadAttemptOutcomeAsync(jobId, captured.Value.Number)).ShouldBeNull();
                }
                else
                {
                    await using var authentic = await temp.CreateAsync(new MemoryStream(cueBytes));
                    // Synthetic independent comparison facts exercise publication, not native qualification.
                    // The real-child comparison lease is covered separately by the native workflow batch.
                    var originalManifest = new RomReconstructionManifest.CdTracks(manifest.LogicalFrames, manifest.Tracks,
                        manifest.Members.Select<RomCdFileMember, RomCdFileMember>(member => member is RomCdStoredMember { Role: RomCdStoredRole.Cue }
                            ? new RomCdStoredMember(member.Path, new(cue.Size, cue.Sha256, cue.Sha1, cue.Md5, cue.Crc32), RomCdStoredRole.Cue) : member).ToArray());
                    var raw = input.Prepared.Inspection;
                    var measured = new RomMeasuredRepresentation(archive.Container, originalManifest, new(Guid.NewGuid(), archive.ArtifactSha256, archive.ArtifactSize,
                        new RomDecoderIdentity.Native(decoder), change == "wrong-profile" ? "chd-cd-retained-files/v2" : "chd-cd-whole-files/v1", "synthetic-independent-comparer", DateTimeOffset.UtcNow,
                        archive.Outputs.Select(output => new RomMeasuredOutput(output.Selector, output.Identity)).ToArray(), archive.Container, raw.ComputedRawSha1, raw.ComputedOverallSha1));
                    var adopted = new MediaReconciliationAdopted(input.Prepared.Artifact, new(measured, "synthetic-inspector", null,
                        new(input.Prepared.Artifact.FileSha256, input.Prepared.Artifact.DatHashes!.Md5, input.Prepared.Artifact.DatHashes.Sha1, input.Prepared.Artifact.FileSize)),
                        new Dictionary<string, ITempFile> { ["disc.cue"] = authentic });
                    IMediaReconciliationPublicationStore store = execution;
                    if (change == "rollback")
                    {
                        var injected = Substitute.For<IMediaReconciliationPublicationStore>();
                        injected.PrepareAsync(captured.Value, Arg.Any<IReadOnlyCollection<Sha256>>(), Arg.Any<CancellationToken>())
                            .Returns(call => execution.PrepareAsync(captured.Value, call.Arg<IReadOnlyCollection<Sha256>>(), call.Arg<CancellationToken>()));
                        injected.StageAdoptedAsync(captured.Value, adopted, archive.FileId, Arg.Any<IReadOnlyList<RomRetainedDescriptor>>(), Arg.Any<CancellationToken>())
                            .Returns(async call =>
                            {
                                var staged = await execution.StageAdoptedAsync(captured.Value, adopted, archive.FileId, call.Arg<IReadOnlyList<RomRetainedDescriptor>>(), call.Arg<CancellationToken>());
                                staged.IsError.ShouldBeFalse(staged.IsError ? staged.FirstError.ToString() : null);
                                return (ErrorOr<Success>)Error.Failure("Injected.BeforeCommit", "Rollback after staging graph, owner and journal.");
                            });
                        store = injected;
                    }
                    var publisher = new MediaReconciliationPublication(services.Files, services.Work, store, execution, custody);
                    ErrorOr<Success> result;
                    if (change is "execute" or "execute-cancel")
                    {
                        bool released = false;
                        var processing = Substitute.For<IMediaReconciliationAdoption>();
                        var lease = Substitute.For<IMediaReconciliationAdoptionLease>();
                        lease.ProcessTreeStopped.Returns(true);
                        lease.CompareOriginalsAsync(Arg.Any<CancellationToken>()).Returns(_ =>
                        {
                            db.Database.CurrentTransaction.ShouldBeNull();
                            claim.Job.PhaseEnum.ShouldBe(MediaReconciliationPhase.Verifying);
                            return Task.FromResult<ErrorOr<MediaReconciliationAdopted>>(adopted);
                        });
                        lease.ReleaseProcessingAsync(Arg.Any<CancellationToken>()).Returns(async _ =>
                        {
                            released = true;
                            if (change == "execute-cancel")
                            {
                                await using var transaction = await services.Work.BeginTransactionAsync();
                                await db.Set<MediaReconciliationDefinitionEntity>().Where(row => row.JobId == jobId)
                                    .ExecuteUpdateAsync(set => set.SetProperty(row => row.CancelRequested, true));
                                await transaction.CommitAsync();
                            }
                            return (ErrorOr<Success>)Result.Success;
                        });
                        processing.AcquireAsync(Arg.Any<MediaReconciliationAttempt>(), Arg.Any<CancellationToken>()).Returns(ErrorOrFactory.From(lease));
                        var encoding = Substitute.For<IMediaReconciliationEncoding>();
                        var phases = new List<MediaReconciliationPhase>();
                        var context = new JobContext(null, _ =>
                        {
                            phases.Add(claim.Job.PhaseEnum);
                            if (claim.Job.PhaseEnum == MediaReconciliationPhase.Publishing)
                                released.ShouldBeTrue();
                            return Task.CompletedTask;
                        }, CancellationToken.None);
                        var executor = new MediaReconciliationJobExecutor(execution, new MediaReconciliationPlanning(snapshots, decision), Substitute.For<IMediaReconciliationSelections>(),
                            Substitute.For<IRomOriginalMediaInspection>(), Substitute.For<IRomMediaVerification>(), encoding, processing, publisher, TimeProvider.System);
                        await executor.ExecuteAsync(claim.Job, context);
                        released.ShouldBeTrue();
                        claim.Job.IsTerminal.ShouldBeTrue();
                        claim.Job.Outcome.ShouldBe(change == "execute-cancel" ? MediaReconciliationOutcomeKind.Cancelled : MediaReconciliationOutcomeKind.Completed);
                        phases.ShouldContain(MediaReconciliationPhase.Verifying);
                        phases.ShouldNotContain(MediaReconciliationPhase.Encoding);
                        encoding.ReceivedCalls().ShouldBeEmpty();
                        await lease.Received(1).DisposeAsync();
                        if (change == "execute-cancel")
                        {
                            (await db.CdArchiveOwners.CountAsync()).ShouldBe(0);
                            (await db.RomRepresentations.CountAsync(graph => graph.FileId == archive.FileId)).ShouldBe(1);
                            (await db.Set<MediaReconciliationPinEntity>().CountAsync(pin => pin.JobId == jobId)).ShouldBe(0);
                            phases.ShouldNotContain(MediaReconciliationPhase.Publishing);
                            return;
                        }
                        result = Result.Success;
                    }
                    else if (change == "concurrent")
                    {
                        await using var secondDb = database.CreateContext();
                        var secondServices = MediaInfrastructureTestServices.CdImport(secondDb, Cas(), temp, recipe, Substitute.For<IAdminEventOutbox>(), false, new RomCdCueGenerator(temp));
                        var secondExecution = new MediaReconciliationExecution(secondDb, database.CreateOptions(), secondServices.Work, custody, TimeProvider.System, composition.Kinds, decision);
                        var secondPublisher = new MediaReconciliationPublication(secondServices.Files, secondServices.Work, secondExecution, secondExecution, custody);
                        var results = await Task.WhenAll(publisher.AdoptAsync(captured.Value, adopted, processTreeStopped: true),
                            secondPublisher.AdoptAsync(captured.Value, adopted, processTreeStopped: true));
                        results.Count(value => !value.IsError).ShouldBeGreaterThanOrEqualTo(1);
                        // A loser either observes the committed journal or reaches its now-obsolete authority check.
                        results.Where(value => value.IsError).ShouldAllBe(value => value.FirstError == MediaReconciliationErrors.Changed);
                        result = Result.Success;
                    }
                    else
                        result = await publisher.AdoptAsync(captured.Value, adopted, processTreeStopped: true);
                    result.IsError.ShouldBe(change is "rollback" or "wrong-profile", result.IsError ? result.FirstError.ToString() : null);
                    if (change is "exact-cue" or "publish" or "concurrent" or "execute")
                    {
                        var outcome = (await execution.ReadAttemptOutcomeAsync(jobId, captured.Value.Number)).ShouldNotBeNull();
                        outcome.Code.ShouldBe("adopted");
                        outcome.CreationId.ShouldBeNull();
                        outcome.RepresentationId.ShouldNotBe(archive.Id);
                        var actual = (await new MediaFactReader(db).ReadAsync([archive.Id, outcome.RepresentationId!.Value]));
                        var target = actual[outcome.RepresentationId.Value];
                        target.FileId.ShouldBe(archive.FileId);
                        target.Manifest.ShouldBeOfType<RomReconstructionManifest.CdTracks>().Members.OfType<RomCdStoredMember>().Single().Identity.Sha256.ShouldBe(cue.Sha256);
                        target.Descriptors.Single().FileId.ShouldBe(cueFileId);
                        target.Creations.ShouldBeEmpty();
                        var live = (await new RomCdArchiveDownloadCatalog(db, database.CreateOptions(), composition.DeliveryPolicy)
                            .FindForLiveLayoutAsync(layout.Id, witness.ReleaseId, CancellationToken.None)).ShouldNotBeNull();
                        live.Representation.Id.ShouldBe(target.Id);
                        await using (var content = (await services.Files.RetrieveByIdAsync(live.Representation.Descriptors.Single().FileId, CancellationToken.None)).ShouldNotBeNull())
                        {
                            using var bytes = new MemoryStream();
                            await content.CopyToAsync(bytes);
                            bytes.ToArray().ShouldBe(cueBytes);
                            if (change == "exact-cue")
                                bytes.ToArray().ShouldBe(input.CueBytes);
                            else
                                bytes.ToArray().ShouldNotBe(input.CueBytes);
                        }
                        var comparison = target.Verifications.ShouldHaveSingleItem();
                        comparison.CheckedProfile.ShouldBe("chd-cd-whole-files/v1");
                        comparison.Id.ShouldBe(measured.Measurement.Id);
                        outcome.VerificationId.ShouldBe(comparison.Id);
                        actual[archive.Id].Verifications.ShouldAllBe(proof => proof.CheckedProfile == firstProof.CheckedProfile);
                        var owner = await db.CdArchiveOwners.SingleAsync();
                        owner.RepresentationId.ShouldBe(target.Id);
                        owner.VerificationId.ShouldBe(outcome.VerificationId!.Value);
                        (await db.Set<MediaReconciliationPinEntity>().CountAsync(pin => pin.JobId == jobId)).ShouldBe(0);
                        (await db.Database.SqlQuery<int>($"SELECT \"RepresentationId\" AS \"Value\" FROM romd.\"CurrentCatalogCdLayoutBindings\" WHERE \"CatalogFileId\"={witness.Requirement.CatalogFileId}").SingleAsync()).ShouldBe(archive.Id);
                        actual[archive.Id].Verifications.Select(proof => proof.Id).ShouldBe(retainedProofs, ignoreOrder: true);
                        (await db.RomFiles.Where(rom => archive.Outputs.Select(output => output.RomId).Contains(rom.Id)).Select(rom => rom.PreferredOutputId).ToArrayAsync())
                            .ShouldBe(archive.Outputs.Select(output => (int?)output.Id), ignoreOrder: true);
                        // Journal replay reuses the same stopped measurement and adds no graph, owner or creation.
                        (await publisher.AdoptAsync(captured.Value, adopted, processTreeStopped: true)).IsError.ShouldBeFalse();
                        (await db.RomRepresentations.CountAsync(graph => graph.FileId == archive.FileId)).ShouldBe(2);
                        return;
                    }
                    if (change == "wrong-profile")
                    {
                        result.FirstError.ShouldBe(MediaReconciliationErrors.Measurement);
                        (await execution.ReadAttemptOutcomeAsync(jobId, captured.Value.Number)).ShouldBeNull();
                    }
                    else
                        result.FirstError.Code.ShouldBe("Injected.BeforeCommit");
                    (await db.RomRepresentations.CountAsync(graph => graph.FileId == archive.FileId)).ShouldBe(1);
                    (await execution.ReadAttemptOutcomeAsync(jobId, captured.Value.Number)).ShouldBeNull();
                    (await db.Set<MediaReconciliationPinEntity>().CountAsync(pin => pin.JobId == jobId)).ShouldBe(4);
                }
                (await execution.SupersedeAttemptAsync(jobId, "fixture-stopped", processTreeStopped: true)).IsError.ShouldBeFalse();
                (await db.Set<MediaReconciliationPinEntity>().CountAsync(pin => pin.JobId == jobId)).ShouldBe(0);
            }
            else if (change == "before-publication")
            {
                await ChangeCatalogAsync(db, witness.Requirement.CatalogFileId);
                await using (var transaction = await services.Work.BeginTransactionAsync())
                    (await execution.PrepareAsync(captured.Value, [])).IsError.ShouldBeTrue();
                (await execution.SupersedeAttemptAsync(jobId, "catalog-changed", processTreeStopped: true)).IsError.ShouldBeFalse();
                (await db.Set<MediaReconciliationPinEntity>().CountAsync(pin => pin.JobId == jobId)).ShouldBe(0);
            }
            else
            {
                await using var transaction = await services.Work.BeginTransactionAsync();
                (await execution.PrepareAsync(captured.Value, [])).IsError.ShouldBeFalse();
            }
        }
        (await db.CdArchiveOwners.CountAsync()).ShouldBe(0);
        var retained = (await new MediaFactReader(db).ReadAsync([archive.Id]))[archive.Id];
        retained.Verifications.Select(row => row.Id).ShouldBe(retainedProofs, ignoreOrder: true);
        retained.Creations.ShouldBeEmpty();
        retained.Outputs.ShouldBe(archive.Outputs);
    }

    private sealed record WitnessRow(int CatalogFileId, int BindingId, Guid Revision);
    private static Task<int> ChangeCatalogAsync(RomdDbContext db, int id) => db.CatalogReleaseFiles.Where(file => file.Id == id)
        .ExecuteUpdateAsync(set => set.SetProperty(file => file.FileFingerprint, "changed-cue-requirement"));
    private LocalTempFileFactory Temp()
    {
        var options = Substitute.For<IRomdOptions>();
        options.DataDirectory.Returns(root);
        options.MaxUploadBytes.Returns(1 << 20);
        return new(options, new FileSystem(), new TempStorageOptions { MaximumFiles = 16, MaximumBytes = 16 << 20, MinimumFreeBytes = 0, AdmissionTimeoutSeconds = 2 });
    }
    private IContentAddressableStore Cas()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddContentAddressableStorage(options =>
        { options.RootPath = Path.Combine(root, "cas"); options.WritePolicy = ContentWritePolicy.Raw; options.MaxContentSize = 1 << 20; });
        var provider = services.BuildServiceProvider();
        providers.Add(provider);
        return provider.GetRequiredService<IContentAddressableStore>();
    }
    public async Task DisposeAsync()
    {
        foreach (var provider in providers)
            await provider.DisposeAsync();
        await database.DisposeAsync();
        Directory.Delete(root, true);
    }
}
