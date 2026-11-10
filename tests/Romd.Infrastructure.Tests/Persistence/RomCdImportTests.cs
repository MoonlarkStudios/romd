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
using Romd.Application.Common.Configuration;
using Romd.Application.Common.Content;
using Romd.Domain.Hashing;
using Romd.Domain.Jobs;
using Romd.Domain.Source.Rom;
using Romd.Infrastructure.Realtime;
using Romd.Infrastructure.Storage;
using Romd.Infrastructure.Tests.Storage;
using Romd.Persistence;
using Romd.Persistence.MediaFacts;
using Romd.Persistence.Repositories;
using Romd.PostgreSql.TestSupport;
using Romd.Storage;
using Romd.Storage.FileSystem;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Persistence;

/// <summary>Trusted synthetic observations establish publication behavior; supervised native cases qualify byte measurement separately.</summary>
public sealed class RomCdImportTests : IAsyncLifetime
{
    private readonly PostgreSqlTestDatabase database = PostgreSqlTestDatabase.Create();
    private readonly string root = Directory.CreateTempSubdirectory("romd-cd-import-").FullName;
    private readonly List<ServiceProvider> providers = [];
    private static RomCdStorageRecipe Recipe => RomMediaPolicyComposition.Create().CdRecipes.ForKind("ps1-cd-set") ?? throw new InvalidOperationException("Missing registered CD recipe.");
    private static RomChdDecoderBuild Decoder(char pin) => new("linux-x64", Sha1.Parse(new string(pin, 40)), 1);
    private static RomCdIngestIntent Intent(bool archiveOnly, UploadItemContext? upload) => new("disc.chd", archiveOnly, false, upload);
    public Task InitializeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Ingest_UnclassifiedMode1Cd_SaturnCatalogNarrowsCandidatesWithoutInventingBootFacts()
    {
        var composition = RomMediaPolicyComposition.Create();
        var recipe = composition.CdRecipes.ForPlatform("saturn").ShouldNotBeNull();
        var temp = Temp();
        await using var input = await MediaInfrastructureTestServices.MeasuredCdInputAsync(temp, recipe, Decoder('a'), true, 0, 2, null, mode1: true);
        await using var db = database.CreateContext();
        await MediaInfrastructureTestServices.SeedCdCatalogAsync(db, input, true, "saturn");
        var services = MediaInfrastructureTestServices.CdImport(db, Cas(), temp, recipe, new AdminRealtimeOutbox(db, TimeProvider.System),
            false, new RomCdCueGenerator(temp), composition.CdRecipes);

        var result = await services.Ingestor.IngestAsync(input.Prepared, Intent(true, null), default);

        result.IsError.ShouldBeFalse(result.IsError ? string.Join(';', result.Errors) : "");
        var graph = (await new MediaFactReader(db).ReadAsync([result.Value.Archive.Id]))[result.Value.Archive.Id];
        graph.CdBootInspections.ShouldBeEmpty();
        var output = graph.Outputs[0];
        var content = new RomContentDescriptor(output.RomId!.Value, output.Id, output.Identity, "track.bin", [new(graph, output.Id, true)], [], []);
        MediaKindResolver.Resolve(composition.Kinds, content, []).Outcome.ShouldBe(MediaKindOutcome.Ambiguous);
        MediaKindResolver.Resolve(composition.Kinds, content, ["saturn"]).Unique!.Kind.ShouldBe("saturn-cd-set");
        MediaKindResolver.Resolve(composition.Kinds, content, ["psx"]).Unique!.Kind.ShouldBe("ps1-cd-set");
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task Ingest_SaturnObservation_BindsOnlyItsPlatformAndRetainsDistinctBootFacts(bool exactCue, bool wrongPlatform)
    {
        var composition = RomMediaPolicyComposition.Create();
        var recipe = composition.CdRecipes.ForPlatform("saturn").ShouldNotBeNull();
        var temp = Temp();
        var boot = new RomSaturnCdBoot(RomSaturnCdBoot.CurrentProfile, 0, "T-00001", "JUE", 4096, Sha256.Parse(new string('1', 64)));
        await using var input = await MediaInfrastructureTestServices.MeasuredCdInputAsync(temp, recipe, Decoder('a'), true, 0, 2, boot);
        await using var db = database.CreateContext();
        await MediaInfrastructureTestServices.SeedCdCatalogAsync(db, input, exactCue, wrongPlatform ? "psx" : "saturn");
        var services = MediaInfrastructureTestServices.CdImport(db, Cas(), temp, recipe, new AdminRealtimeOutbox(db, TimeProvider.System),
            false, new RomCdCueGenerator(temp), composition.CdRecipes);

        var result = await services.Ingestor.IngestAsync(input.Prepared, Intent(true, null), default);

        result.IsError.ShouldBe(wrongPlatform, result.IsError ? string.Join(';', result.Errors) : "");
        if (wrongPlatform)
        {
            (await db.RomFiles.CountAsync()).ShouldBe(0);
            (await CountAsync(db, "CatalogCdLayoutBindings")).ShouldBe(0);
            return;
        }
        result.Value.Archive.CdBootInspections.ShouldHaveSingleItem().Boot.ShouldBe(boot);
        result.Value.Archive.Creations.ShouldBeEmpty();
        result.Value.Archive.Outputs.Count.ShouldBe(2);
    }

    [Theory]
    [InlineData(false, false, 1)]
    [InlineData(false, true, 1)]
    [InlineData(true, false, 1)]
    [InlineData(true, true, 1)]
    [InlineData(false, false, 2)]
    [InlineData(false, true, 2)]
    [InlineData(true, false, 2)]
    [InlineData(true, true, 2)]
    public async Task Ingest_CompleteObservedDisc_PublishesActualOwnersAndTruthfulCueWithoutEncoding(bool exactCue, bool canonical, int trackCount)
    {
        var temp = Temp();
        await using var input = await MediaInfrastructureTestServices.MeasuredCdInputAsync(temp, Recipe, Decoder('a'), canonical, 0, trackCount, boot: null);
        await using var db = database.CreateContext();
        var expected = await MediaInfrastructureTestServices.SeedCdCatalogAsync(db, input, exactCue);
        var cas = Cas();
        var services = Services(db, cas, temp, tracked: false, new AdminRealtimeOutbox(db, TimeProvider.System), new RomCdCueGenerator(temp));
        var result = await services.Ingestor.IngestAsync(input.Prepared, Intent(archiveOnly: true, upload: null), default);

        result.IsError.ShouldBeFalse(result.IsError ? string.Join(';', result.Errors) : "");
        var receipt = result.Value;
        receipt.IsNew.ShouldBeTrue();
        receipt.MatchedTitleIds.ShouldBe([1]);
        receipt.PlatformId.ShouldBe(1);
        receipt.Archive.Outputs.Count.ShouldBe(trackCount);
        receipt.Archive.Outputs.Select(output => output.RomId).Distinct().Count().ShouldBe(trackCount);
        receipt.Archive.Outputs.ShouldAllBe(output => output.Id > 0 && output.RomId > 0);
        receipt.Archive.Verifications.Single().Id.ShouldBe(input.Prepared.Inspection.Id);
        receipt.Archive.Creations.ShouldBeEmpty();
        receipt.Archive.CdBootInspections.ShouldBeEmpty();
        (await db.RomFiles.CountAsync()).ShouldBe(trackCount + (exactCue ? 1 : 0));
        (await db.RomRepresentations.CountAsync()).ShouldBe(exactCue ? 2 : 1);
        if (exactCue)
            receipt.OriginalCue.ShouldNotBeNull().Outputs.Single().Identity.ShouldBe(expected);
        else
            receipt.OriginalCue.ShouldBeNull();
        var descriptor = receipt.Archive.Descriptors.Single();
        descriptor.Sha256.ShouldBe(MediaFactFixtures.Identity(input.CueBytes).Sha256);
        (await BindingAsync(db)).Id.ShouldBeGreaterThan(0);
        (await CountAsync(db, "RepresentationCreations")).ShouldBe(0);
        (await CountAsync(db, "MediaReconciliationJobs")).ShouldBe(1);
        (await db.AdminRealtimeOutboxEvents.CountAsync()).ShouldBeGreaterThan(0);
        await services.Tracking.DidNotReceive().TrackByTitleIdsAsync(Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<CancellationToken>());
        (await db.Files.SingleAsync(row => row.Id == receipt.Archive.FileId)).IsCompressed.ShouldBeFalse();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Ingest_SameArtifactAcrossDecoderBuilds_RetainsEveryBuildAndStableOwnedIds(bool exactCue)
    {
        var temp = Temp();
        await using var first = await MediaInfrastructureTestServices.MeasuredCdInputAsync(temp, Recipe, Decoder('a'), true, 0, trackCount: 1, null);
        await using var second = await MediaInfrastructureTestServices.MeasuredCdInputAsync(temp, Recipe, Decoder('b'), true, 0, trackCount: 1, null);
        await using var repeated = await MediaInfrastructureTestServices.MeasuredCdInputAsync(temp, Recipe, Decoder('b'), true, 0, trackCount: 1, null);
        await using var db = database.CreateContext();
        await MediaInfrastructureTestServices.SeedCdCatalogAsync(db, first, exactCue);
        var services = Services(db, Cas(), temp, false, new AdminRealtimeOutbox(db, TimeProvider.System), new RomCdCueGenerator(temp));
        var one = (await services.Ingestor.IngestAsync(first.Prepared, Intent(true, null), default)).Value;
        var binding = await BindingAsync(db);
        var two = (await services.Ingestor.IngestAsync(second.Prepared, Intent(true, null), default)).Value;
        var three = (await services.Ingestor.IngestAsync(repeated.Prepared, Intent(true, null), default)).Value;

        two.IsNew.ShouldBeFalse();
        three.IsNew.ShouldBeFalse();
        two.Archive.Id.ShouldBe(one.Archive.Id);
        three.Archive.Id.ShouldBe(one.Archive.Id);
        three.Archive.Outputs.ShouldBe(one.Archive.Outputs);
        three.Archive.Verifications.Select(row => row.Id).Order().ShouldBe(new[] { first.Prepared.Inspection.Id, second.Prepared.Inspection.Id }.Order());
        three.Archive.Verifications.ShouldNotContain(row => row.Id == repeated.Prepared.Inspection.Id);
        (await BindingAsync(db)).ShouldBe(binding);
        (await db.RomRepresentations.CountAsync()).ShouldBe(exactCue ? 2 : 1);
        (await CountAsync(db, "RepresentationCreations")).ShouldBe(0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Ingest_DifferentBytesWithSameLayout_KeepsExistingCanonicalWithoutRelabelingIncomingProof(bool incomingCanonical)
    {
        var temp = Temp();
        await using var first = await MediaInfrastructureTestServices.MeasuredCdInputAsync(temp, Recipe, Decoder('a'), true, 0, trackCount: 1, null);
        await using var incoming = await MediaInfrastructureTestServices.MeasuredCdInputAsync(temp, Recipe, Decoder('b'), incomingCanonical, 1, trackCount: 1, null);
        await using var db = database.CreateContext();
        await MediaInfrastructureTestServices.SeedCdCatalogAsync(db, first, false);
        var services = Services(db, Cas(), temp, false, new AdminRealtimeOutbox(db, TimeProvider.System), new RomCdCueGenerator(temp));
        var one = (await services.Ingestor.IngestAsync(first.Prepared, Intent(true, null), default)).Value;
        var binding = await BindingAsync(db);
        var two = (await services.Ingestor.IngestAsync(incoming.Prepared, Intent(true, null), default)).Value;

        two.IsNew.ShouldBeFalse();
        two.Archive.Id.ShouldBe(one.Archive.Id);
        two.Archive.ArtifactSha256.ShouldBe(first.Prepared.Artifact.FileSha256);
        two.Archive.ArtifactSha256.ShouldNotBe(incoming.Prepared.Artifact.FileSha256);
        two.Archive.Verifications.Select(proof => proof.Id).ShouldBe([first.Prepared.Inspection.Id]);
        (await BindingAsync(db)).ShouldBe(binding);
        (await db.Files.CountAsync()).ShouldBe(2);
        (await CountAsync(db, "RepresentationCreations")).ShouldBe(0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Ingest_CanonicalIncomingReplacesNoncanonical_ChangesBindingAndReleasesOnlyUnownedGraph(bool exactCue)
    {
        var temp = Temp();
        await using var first = await MediaInfrastructureTestServices.MeasuredCdInputAsync(temp, Recipe, Decoder('a'), false, 0, trackCount: 1, null);
        await using var incoming = await MediaInfrastructureTestServices.MeasuredCdInputAsync(temp, Recipe, Decoder('b'), true, 1, trackCount: 1, null);
        await using var db = database.CreateContext();
        await MediaInfrastructureTestServices.SeedCdCatalogAsync(db, first, exactCue);
        var services = Services(db, Cas(), temp, false, new AdminRealtimeOutbox(db, TimeProvider.System), new RomCdCueGenerator(temp));
        var one = (await services.Ingestor.IngestAsync(first.Prepared, Intent(true, null), default)).Value;
        var binding = await BindingAsync(db);
        var two = (await services.Ingestor.IngestAsync(incoming.Prepared, Intent(true, null), default)).Value;

        two.IsNew.ShouldBeFalse(); // New encoding bytes do not create another logical original.
        two.Archive.Id.ShouldNotBe(one.Archive.Id);
        two.Archive.ArtifactSha256.ShouldBe(incoming.Prepared.Artifact.FileSha256);
        two.Archive.Outputs.Select(row => row.RomId).ShouldBe(one.Archive.Outputs.Select(row => row.RomId));
        (await BindingAsync(db)).Revision.ShouldNotBe(binding.Revision);
        var prior = (await new MediaFactReader(db).ReadAsync([one.Archive.Id]))[one.Archive.Id];
        prior.Outputs.ShouldAllBe(row => row.RomId == null);
        prior.Verifications.Single().Id.ShouldBe(first.Prepared.Inspection.Id);
        (await new MediaRepresentationCleanup(db).CollectAsync(256)).ShouldBe(1);
        (await new MediaFactReader(db).ReadAsync([two.Archive.Id])).ShouldContainKey(two.Archive.Id);
        (await CountAsync(db, "RepresentationCreations")).ShouldBe(0);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Ingest_InjectedOutboxFailure_RollsBackOwnersFactsBindingsDemandOutcomeAndEvents(bool exactCue, bool withUpload)
    {
        var temp = Temp();
        await using var input = await MediaInfrastructureTestServices.MeasuredCdInputAsync(temp, Recipe, Decoder('a'), true, 0, trackCount: 2, null);
        await using var db = database.CreateContext();
        await MediaInfrastructureTestServices.SeedCdCatalogAsync(db, input, exactCue);
        var claim = withUpload ? await ClaimAsync(db) : null;
        using var fence = claim is null ? null : JobExecutionFenceScope.Enter(claim.Job.Id, claim.FenceToken, TimeProvider.System);
        var failed = Substitute.For<IAdminEventOutbox>();
        failed.EnqueueAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(_ => throw new IOException("Injected CD outbox failure after fact publication."));
        var services = Services(db, Cas(), temp, false, failed, new RomCdCueGenerator(temp));
        var error = await Should.ThrowAsync<IOException>(() => services.Ingestor.IngestAsync(input.Prepared,
            Intent(true, claim is null ? null : new(claim.Job.Id, "disc.chd")), default));

        error.Message.ShouldBe("Injected CD outbox failure after fact publication.");
        await using var observer = database.CreateContext();
        (await observer.RomFiles.CountAsync()).ShouldBe(0);
        (await observer.RomRepresentations.CountAsync()).ShouldBe(0);
        (await observer.Files.CountAsync()).ShouldBe(0);
        (await observer.JobItems.CountAsync()).ShouldBe(0);
        (await observer.AdminRealtimeOutboxEvents.CountAsync()).ShouldBe(0);
        (await CountAsync(observer, "CatalogCdLayoutBindings")).ShouldBe(0);
        (await CountAsync(observer, "Verifications")).ShouldBe(0);
        (await CountAsync(observer, "MediaReconciliationJobs")).ShouldBe(0);
        File.Exists(input.Prepared.Artifact.Path).ShouldBeTrue();
        (await services.Files.DeleteOrphanAsync(input.Prepared.Artifact.FileSha256, default)).ShouldBeTrue();
        (await services.Files.DeleteOrphanAsync(MediaFactFixtures.Identity(input.CueBytes).Sha256, default)).ShouldBeTrue();
    }

    [Fact]
    public async Task Ingest_TrackedOnlyWithoutIntent_RefusesBeforeCasOrOwnershipWrites()
    {
        var temp = Temp();
        await using var input = await MediaInfrastructureTestServices.MeasuredCdInputAsync(temp, Recipe, Decoder('a'), true, 0, trackCount: 1, null);
        await using var db = database.CreateContext();
        await MediaInfrastructureTestServices.SeedCdCatalogAsync(db, input, false);
        var services = Services(db, Cas(), temp, false, new AdminRealtimeOutbox(db, TimeProvider.System), new RomCdCueGenerator(temp));
        var result = await services.Ingestor.IngestAsync(input.Prepared, new("disc.chd", false, true, null), default);

        result.FirstError.Code.ShouldBe("Library.UntrackedRom");
        (await db.Files.CountAsync()).ShouldBe(0);
        (await db.RomFiles.CountAsync()).ShouldBe(0);
        (await CountAsync(db, "CatalogCdLayoutBindings")).ShouldBe(0);
        await services.Tracking.DidNotReceive().TrackByTitleIdsAsync(Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Ingest_ConcurrentBuildObservations_SerializeToOneGraphWithBothProofs()
    {
        var temp = Temp();
        await using var first = await MediaInfrastructureTestServices.MeasuredCdInputAsync(temp, Recipe, Decoder('a'), true, 0, trackCount: 2, null);
        await using var second = await MediaInfrastructureTestServices.MeasuredCdInputAsync(temp, Recipe, Decoder('b'), true, 0, trackCount: 2, null);
        await using (var seed = database.CreateContext())
            await MediaInfrastructureTestServices.SeedCdCatalogAsync(seed, first, false);
        var cas = Cas();
        await using var left = database.CreateContext();
        await using var right = database.CreateContext();
        var a = Services(left, cas, temp, false, new AdminRealtimeOutbox(left, TimeProvider.System), new RomCdCueGenerator(temp));
        var b = Services(right, cas, temp, false, new AdminRealtimeOutbox(right, TimeProvider.System), new RomCdCueGenerator(temp));
        var results = await Task.WhenAll(a.Ingestor.IngestAsync(first.Prepared, Intent(true, null), default), b.Ingestor.IngestAsync(second.Prepared, Intent(true, null), default));

        results.ShouldAllBe(result => !result.IsError);
        results[0].Value.Archive.Id.ShouldBe(results[1].Value.Archive.Id);
        await using var observer = database.CreateContext();
        var graph = (await new MediaFactReader(observer).ReadAsync([results[0].Value.Archive.Id])).Values.Single();
        graph.Verifications.Select(proof => proof.Id).Order().ShouldBe(new[] { first.Prepared.Inspection.Id, second.Prepared.Inspection.Id }.Order());
        (await observer.RomFiles.CountAsync()).ShouldBe(2);
        graph.Outputs.Count.ShouldBe(2);
        (await observer.RomRepresentations.CountAsync()).ShouldBe(1);
        (await CountAsync(observer, "CatalogCdLayoutBindings")).ShouldBe(1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Ingest_PositiveBootThenAbsentOrConflictingObservation_PreservesKnownFactsAtomically(bool conflict)
    {
        var boot = new RomPs1CdBoot(RomPs1CdBoot.CurrentProfile, 0, 64, 20, 2048, "PSX.EXE", 24, 4096, 0x80010000, 0x80010000, 2048);
        var changed = conflict ? new RomPs1CdBoot(RomPs1CdBoot.CurrentProfile, 0, 64, 20, 2048, "ALT.EXE", 24, 4096, 0x80010000, 0x80010000, 2048) : null;
        var temp = Temp();
        await using var first = await MediaInfrastructureTestServices.MeasuredCdInputAsync(temp, Recipe, Decoder('a'), true, 0, trackCount: 1, boot);
        await using var second = await MediaInfrastructureTestServices.MeasuredCdInputAsync(temp, Recipe, Decoder('b'), true, 0, trackCount: 1, changed);
        await using var db = database.CreateContext();
        await MediaInfrastructureTestServices.SeedCdCatalogAsync(db, first, false);
        var services = Services(db, Cas(), temp, false, new AdminRealtimeOutbox(db, TimeProvider.System), new RomCdCueGenerator(temp));
        var one = (await services.Ingestor.IngestAsync(first.Prepared, Intent(true, null), default)).Value;
        if (conflict)
            await Should.ThrowAsync<InvalidDataException>(() => services.Ingestor.IngestAsync(second.Prepared, Intent(true, null), default));
        else
            (await services.Ingestor.IngestAsync(second.Prepared, Intent(true, null), default)).IsError.ShouldBeFalse();

        await using var observer = database.CreateContext();
        var retained = (await new MediaFactReader(observer).ReadAsync([one.Archive.Id])).Values.Single();
        retained.CdBootInspections.Single().Boot.ShouldBe(boot);
        retained.CdBootInspections.Single().Id.ShouldBe(one.Archive.CdBootInspections.Single().Id);
        retained.Verifications.Count.ShouldBe(conflict ? 1 : 2);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Ingest_FencedUploadSuccess_CommitsOutcomeWithoutInventingASingleBinId(bool exactCue)
    {
        var temp = Temp();
        await using var input = await MediaInfrastructureTestServices.MeasuredCdInputAsync(temp, Recipe, Decoder('a'), true, 0, trackCount: 1, null);
        await using var db = database.CreateContext();
        await MediaInfrastructureTestServices.SeedCdCatalogAsync(db, input, exactCue);
        var claim = await ClaimAsync(db);
        using var fence = JobExecutionFenceScope.Enter(claim.Job.Id, claim.FenceToken, TimeProvider.System);
        var services = Services(db, Cas(), temp, false, new AdminRealtimeOutbox(db, TimeProvider.System), new RomCdCueGenerator(temp));
        var result = await services.Ingestor.IngestAsync(input.Prepared, Intent(true, new(claim.Job.Id, "disc.chd")), default);

        result.IsError.ShouldBeFalse(result.IsError ? string.Join(';', result.Errors) : "");
        await using var observer = database.CreateContext();
        var outcome = (await new JobItemRepository(observer).GetOutcomesAsync(claim.Job.Id)).ShouldHaveSingleItem();
        outcome.RomFileId.ShouldBeNull();
        outcome.Outcome.ShouldBe(JobItemOutcome.Ingested);
        outcome.MatchedTitleIds.ShouldBe([1]);
        outcome.PlatformId.ShouldBe(1);
        outcome.SizeBytes.ShouldBe(input.Prepared.Artifact.FileSize);
        (await new MediaFactReader(observer).ReadAsync([result.Value.Archive.Id])).ShouldContainKey(result.Value.Archive.Id);
        (await CountAsync(observer, "CatalogCdLayoutBindings")).ShouldBe(1);
        (await observer.AdminRealtimeOutboxEvents.CountAsync()).ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task Ingest_WithoutMatchingUploadFence_RollsBackPublishedFactsAndOutcome()
    {
        var temp = Temp();
        await using var input = await MediaInfrastructureTestServices.MeasuredCdInputAsync(temp, Recipe, Decoder('a'), true, 0, trackCount: 1, null);
        await using var db = database.CreateContext();
        await MediaInfrastructureTestServices.SeedCdCatalogAsync(db, input, false);
        var claim = await ClaimAsync(db); // Intentionally no matching execution-fence scope.
        var services = Services(db, Cas(), temp, false, new AdminRealtimeOutbox(db, TimeProvider.System), new RomCdCueGenerator(temp));
        await Should.ThrowAsync<JobExecutionOwnershipLostException>(() => services.Ingestor.IngestAsync(input.Prepared, Intent(true, new(claim.Job.Id, "disc.chd")), default));

        await using var observer = database.CreateContext();
        (await observer.RomFiles.CountAsync()).ShouldBe(0);
        (await observer.RomRepresentations.CountAsync()).ShouldBe(0);
        (await observer.JobItems.CountAsync()).ShouldBe(0);
        (await observer.Files.CountAsync()).ShouldBe(0);
        (await observer.AdminRealtimeOutboxEvents.CountAsync()).ShouldBe(0);
        (await CountAsync(observer, "CatalogCdLayoutBindings")).ShouldBe(0);
    }

    [Fact]
    public async Task Ingest_CatalogChangesAfterAdvisoryBinding_RefusesBeforeOwnershipOrLedgerPublication()
    {
        var temp = Temp();
        await using var input = await MediaInfrastructureTestServices.MeasuredCdInputAsync(temp, Recipe, Decoder('a'), true, 0, trackCount: 1, null);
        await using var db = database.CreateContext();
        await MediaInfrastructureTestServices.SeedCdCatalogAsync(db, input, false);
        var actual = new RomCdCueGenerator(temp);
        var changing = Substitute.For<IRomCdCueGenerator>();
        changing.GenerateAsync(Arg.Any<IReadOnlyList<RomCdInspectedTrack>>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>()).Returns(async call =>
        {
            var generated = await actual.GenerateAsync(call.Arg<IReadOnlyList<RomCdInspectedTrack>>(), call.Arg<IReadOnlyList<string>>(), call.Arg<CancellationToken>());
            await db.CatalogReleaseFiles.Where(row => row.Id == 1).ExecuteUpdateAsync(set => set.SetProperty(row => row.FileFingerprint, "bin-v2"));
            return generated;
        });
        var services = Services(db, Cas(), temp, false, new AdminRealtimeOutbox(db, TimeProvider.System), changing);
        var result = await services.Ingestor.IngestAsync(input.Prepared, Intent(true, null), default);

        result.FirstError.Code.ShouldBe(RomPublicationErrors.RetryRequiredCode);
        (await db.RomFiles.CountAsync()).ShouldBe(0);
        (await db.Files.CountAsync()).ShouldBe(0);
        (await CountAsync(db, "CatalogCdLayoutBindings")).ShouldBe(0);
        (await db.AdminRealtimeOutboxEvents.CountAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task Ingest_WithdrawnLogicalBin_RestoresActualOwnerWithoutLosingPriorMeasurementsOrBindingRevision()
    {
        var temp = Temp();
        await using var first = await MediaInfrastructureTestServices.MeasuredCdInputAsync(temp, Recipe, Decoder('a'), true, 0, trackCount: 1, null);
        await using var second = await MediaInfrastructureTestServices.MeasuredCdInputAsync(temp, Recipe, Decoder('b'), true, 0, trackCount: 1, null);
        await using var db = database.CreateContext();
        await MediaInfrastructureTestServices.SeedCdCatalogAsync(db, first, false);
        var services = Services(db, Cas(), temp, false, new AdminRealtimeOutbox(db, TimeProvider.System), new RomCdCueGenerator(temp));
        var one = (await services.Ingestor.IngestAsync(first.Prepared, Intent(true, null), default)).Value;
        var binding = await BindingAsync(db);
        int oldRom = one.Archive.Outputs.Single().RomId!.Value;
        await db.RomFiles.Where(row => row.Id == oldRom).ExecuteDeleteAsync();
        var two = (await services.Ingestor.IngestAsync(second.Prepared, Intent(true, null), default)).Value;

        two.IsNew.ShouldBeTrue();
        two.Archive.Id.ShouldBe(one.Archive.Id);
        two.Archive.Outputs.Single().Id.ShouldBe(one.Archive.Outputs.Single().Id);
        two.Archive.Outputs.Single().RomId.ShouldNotBe(oldRom);
        two.Archive.Verifications.Select(row => row.Id).Order().ShouldBe(new[] { first.Prepared.Inspection.Id, second.Prepared.Inspection.Id }.Order());
        (await BindingAsync(db)).ShouldBe(binding);
        (await db.RomFiles.CountAsync()).ShouldBe(1);
        (await db.Files.CountAsync()).ShouldBe(2);
    }

    [Fact]
    public async Task Ingest_SameArtifactWithContradictoryCapturedHeader_RefusesWithoutRelabelingRetainedFacts()
    {
        var temp = Temp();
        await using var input = await MediaInfrastructureTestServices.MeasuredCdInputAsync(temp, Recipe, Decoder('a'), true, 0, trackCount: 1, null);
        await using var db = database.CreateContext();
        await MediaInfrastructureTestServices.SeedCdCatalogAsync(db, input, false);
        var services = Services(db, Cas(), temp, false, new AdminRealtimeOutbox(db, TimeProvider.System), new RomCdCueGenerator(temp));
        var one = (await services.Ingestor.IngestAsync(input.Prepared, Intent(true, null), default)).Value;
        var observed = input.Prepared.Inspection;
        var conflictingContainer = MediaFactFixtures.Chd(observed.Container.LogicalBytes, 2448, 2448, observed.Container.Codecs,
            observed.Container.StoredRawSha1, observed.Container.StoredOverallSha1, observed.Container.Metadata);
        await using var contradictory = new PreparedRom.Cd(input.Prepared.Artifact, new RomCdInspection(Guid.NewGuid(), observed.ArtifactIdentity,
            conflictingContainer, Decoder('b'), observed.CheckedProfile, observed.ProjectionProfile, observed.VerifierBuild, observed.ObservedAt,
            observed.ComputedRawSha1, observed.ComputedOverallSha1, observed.Tracks, boot: null));
        await Should.ThrowAsync<InvalidDataException>(() => services.Ingestor.IngestAsync(contradictory, Intent(true, null), default));

        await using var observer = database.CreateContext();
        var retained = (await new MediaFactReader(observer).ReadAsync([one.Archive.Id])).Values.Single();
        retained.Container.Fingerprint.ShouldBe(observed.Container.Fingerprint);
        retained.Verifications.Select(row => row.Id).ShouldBe([observed.Id]);
        (await observer.RomRepresentations.CountAsync()).ShouldBe(1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Ingest_CatalogFilesReplaced_ReusesActualArchiveAndRetainsEveryDecoderBuild(bool exactCue)
    {
        var temp = Temp();
        await using var first = await MediaInfrastructureTestServices.MeasuredCdInputAsync(temp, Recipe, Decoder('a'), true, 0, trackCount: 2, null);
        await using var second = await MediaInfrastructureTestServices.MeasuredCdInputAsync(temp, Recipe, Decoder('b'), true, 0, trackCount: 2, null);
        await using var db = database.CreateContext();
        await MediaInfrastructureTestServices.SeedCdCatalogAsync(db, first, exactCue);
        var services = Services(db, Cas(), temp, false, new AdminRealtimeOutbox(db, TimeProvider.System), new RomCdCueGenerator(temp));
        var one = (await services.Ingestor.IngestAsync(first.Prepared, Intent(true, null), default)).Value;
        var prior = await BindingAsync(db);

        // A normal projection rebuild replaces file rows even when every requirement is unchanged.
        await ReplaceCatalogFilesAsync(db);
        (await CountAsync(db, "CatalogCdLayoutBindings")).ShouldBe(0);
        var claim = await ClaimAsync(db);
        using var fence = JobExecutionFenceScope.Enter(claim.Job.Id, claim.FenceToken, TimeProvider.System);
        var result = await services.Ingestor.IngestAsync(second.Prepared, Intent(true, new(claim.Job.Id, "disc.chd")), default);
        result.IsError.ShouldBeFalse(result.IsError ? string.Join(';', result.Errors) : "");
        var two = result.Value;
        two.IsNew.ShouldBeFalse();
        var outcome = (await new JobItemRepository(db).GetOutcomesAsync(claim.Job.Id)).ShouldHaveSingleItem();
        outcome.Outcome.ShouldBe(JobItemOutcome.Deduplicated);
        outcome.RomFileId.ShouldBeNull();

        two.Archive.Id.ShouldBe(one.Archive.Id);
        two.Archive.FileId.ShouldBe(one.Archive.FileId);
        two.Archive.Outputs.ShouldBe(one.Archive.Outputs);
        two.Archive.Verifications.Select(row => row.Id).Order().ShouldBe(new[] { first.Prepared.Inspection.Id, second.Prepared.Inspection.Id }.Order());
        (await BindingAsync(db)).Id.ShouldNotBe(prior.Id);
        (await db.RomRepresentations.CountAsync()).ShouldBe(exactCue ? 2 : 1);
        (await db.Files.CountAsync()).ShouldBe(2);
        (await CountAsync(db, "RepresentationCreations")).ShouldBe(0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Ingest_CatalogFilesReplaced_KeepsCanonicalBytesWithoutRelabelingNoncanonicalProof(bool exactCue)
    {
        var temp = Temp();
        await using var first = await MediaInfrastructureTestServices.MeasuredCdInputAsync(temp, Recipe, Decoder('a'), true, 0, trackCount: 2, null);
        await using var incoming = await MediaInfrastructureTestServices.MeasuredCdInputAsync(temp, Recipe, Decoder('b'), false, 1, trackCount: 2, null);
        await using var db = database.CreateContext();
        await MediaInfrastructureTestServices.SeedCdCatalogAsync(db, first, exactCue);
        var cas = Cas();
        var services = Services(db, cas, temp, false, new AdminRealtimeOutbox(db, TimeProvider.System), new RomCdCueGenerator(temp));
        var one = (await services.Ingestor.IngestAsync(first.Prepared, Intent(true, null), default)).Value;
        await ReplaceCatalogFilesAsync(db);
        (await CountAsync(db, "CatalogCdLayoutBindings")).ShouldBe(0);
        var claim = await ClaimAsync(db);
        using var fence = JobExecutionFenceScope.Enter(claim.Job.Id, claim.FenceToken, TimeProvider.System);
        var result = await services.Ingestor.IngestAsync(incoming.Prepared, Intent(true, new(claim.Job.Id, "disc.chd")), default);
        result.IsError.ShouldBeFalse(result.IsError ? string.Join(';', result.Errors) : "");
        var two = result.Value;
        two.IsNew.ShouldBeFalse();
        var outcome = (await new JobItemRepository(db).GetOutcomesAsync(claim.Job.Id)).ShouldHaveSingleItem();
        outcome.Outcome.ShouldBe(JobItemOutcome.Deduplicated);
        outcome.RomFileId.ShouldBeNull();

        two.Archive.Id.ShouldBe(one.Archive.Id);
        two.Archive.ArtifactSha256.ShouldBe(first.Prepared.Artifact.FileSha256);
        two.Archive.ArtifactSha256.ShouldNotBe(incoming.Prepared.Artifact.FileSha256);
        two.Archive.Verifications.Select(row => row.Id).ShouldBe([first.Prepared.Inspection.Id]);
        two.Archive.Outputs.ShouldBe(one.Archive.Outputs);
        (await db.RomRepresentations.CountAsync()).ShouldBe(exactCue ? 2 : 1);
        (await db.Files.CountAsync()).ShouldBe(2);
        (await CountAsync(db, "CatalogCdLayoutBindings")).ShouldBe(1);
        // Keep never publishes incoming bytes; disposing its staging must need no later orphan deletion.
        (await cas.ExistsAsync(StorageKey.FromHash(incoming.Prepared.Artifact.FileSha256))).ShouldBeFalse();
        (await cas.ExistsAsync(StorageKey.FromHash(first.Prepared.Artifact.FileSha256))).ShouldBeTrue();
        File.Exists(incoming.Prepared.Artifact.Path).ShouldBeTrue();
    }

    private static async Task ReplaceCatalogFilesAsync(RomdDbContext db)
    {
        await using var transaction = await db.Database.BeginTransactionAsync();
        await CatalogTopologyFence.AcquireAsync(db, default);
        var files = await db.CatalogReleaseFiles.ToArrayAsync();
        await db.CatalogReleaseFiles.ExecuteDeleteAsync();
        db.ChangeTracker.Clear();
        foreach (var file in files)
        { file.Id += 100; db.CatalogReleaseFiles.Add(file); }
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
        db.ChangeTracker.Clear();
    }

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
        services.AddContentAddressableStorage(options => { options.RootPath = Path.Combine(root, "cas"); options.WritePolicy = ContentWritePolicy.Raw; options.MaxContentSize = 1 << 20; });
        var provider = services.BuildServiceProvider();
        providers.Add(provider);
        return provider.GetRequiredService<IContentAddressableStore>();
    }
    private static MediaInfrastructureTestServices.CdImportServices Services(RomdDbContext db, IContentAddressableStore cas, LocalTempFileFactory temp,
        bool tracked, IAdminEventOutbox outbox, IRomCdCueGenerator generator) => MediaInfrastructureTestServices.CdImport(db, cas, temp, Recipe, outbox, tracked, generator);
    private sealed record BindingRow(int Id, Guid Revision);
    private static Task<BindingRow> BindingAsync(RomdDbContext db) => db.Database.SqlQueryRaw<BindingRow>("""SELECT "Id","Revision" FROM romd."CatalogCdLayoutBindings" """).SingleAsync();
    private static Task<int> CountAsync(RomdDbContext db, string table)
    {
        string sql = table switch
        {
            "RepresentationCreations" => """SELECT count(*)::int AS "Value" FROM romd."RepresentationCreations" """,
            "MediaReconciliationJobs" => """SELECT count(*)::int AS "Value" FROM romd."MediaReconciliationJobs" """,
            "CatalogCdLayoutBindings" => """SELECT count(*)::int AS "Value" FROM romd."CatalogCdLayoutBindings" """,
            "Verifications" => """SELECT count(*)::int AS "Value" FROM romd."Verifications" """,
            _ => throw new ArgumentOutOfRangeException(nameof(table))
        };
        return db.Database.SqlQueryRaw<int>(sql).SingleAsync();
    }
    private static async Task<JobExecutionClaim<UploadJob>> ClaimAsync(RomdDbContext db)
    {
        var job = UploadJob.Create("fixture", createdAt: DateTimeOffset.UtcNow);
        job.Start("fixture", startedAt: DateTimeOffset.UtcNow);
        job.BeginClassification();
        job.SetDiscoveredFiles(0, 1);
        job.BeginDatIngestion();
        job.BeginRomIngestion();
        var repository = new UploadJobRepository(db, TimeProvider.System);
        await using (var fixtureAcceptance = await new EfUnitOfWork(db).BeginTransactionAsync())
        {
            await repository.AddStagedAsync(job);
            await fixtureAcceptance.CommitAsync();
        }
        db.ChangeTracker.Clear();
        return (await new ClaimedJobRepository<UploadJob, UploadJobRepository>(repository, db, TimeProvider.System, new()).TryClaimExecutionAsync(job.Id, "fixture"))!;
    }
    public async Task DisposeAsync()
    {
        foreach (var provider in providers)
            await provider.DisposeAsync();
        await database.DisposeAsync();
        Directory.Delete(root, true);
    }
}
