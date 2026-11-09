using Romd.Infrastructure.Tests.Helpers;
using Romd.Application.Common.Concurrency;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Romd.Admin.Application.MetadataProviders;
using Romd.Admin.Application.Search;
using Romd.Domain.Hashing;
using Romd.Persistence.Repositories;
using Romd.Application.Common.Ids;
using Romd.Contracts.Management.Artwork;
using Romd.Contracts.Management.MetadataProviders;
using Romd.Persistence.Artwork;
using Romd.Persistence.Entities;
using Romd.Persistence.MetadataProviders;
using Romd.PostgreSql.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Artwork;

public sealed class EnrichmentSchedulingTests : IDisposable
{
    private readonly PostgreSqlTestDatabase _database = PostgreSqlTestDatabase.Create();
    private readonly Guid _actor = Guid.NewGuid();

    [Fact]
    public async Task QueueScope_OverlappingTitles_SharesActiveOwnerWithoutRepeatingProviderWork()
    {
        await SeedAsync();
        await using var db = _database.CreateContext();
        var store = new ArtworkCompletionStore(db, clock: TimeProvider.System);
        var first = (await store.GetAsync((await store.AcceptCompletionAsync(db, Request([1, 2]), _actor, default)).Value, default)).Value;
        var second = (await store.GetAsync((await store.AcceptCompletionAsync(db, Request([2, 3]), _actor, default)).Value, default)).Value;

        second.Total.ShouldBe(2);
        second.Working.ShouldBe(2);
        var shared = await db.Set<ArtworkCompletionItemEntity>().SingleAsync(x => x.JobId == second.JobId && x.TitleId == 2);
        shared.FollowUpJobId.ShouldBe(first.JobId);
        shared.CompletedAt.ShouldNotBeNull();
        (await store.NextAsync(second.JobId, default))!.TitleId.ShouldBe(3);
        (await store.ItemsAsync(second.JobId, 0, null, default)).Value.Items.ShouldAllBe(x => x.Completion == ArtworkCompletionState.Working);
        (await db.Set<ArtworkCompletionItemEntity>().CountAsync(x => x.TitleId == 2 && x.CompletedAt == null)).ShouldBe(1);
        var recent = await store.RecentAsync(default);
        recent.Select(x => x.JobId).ShouldBe([second.JobId, first.JobId]);
        recent.ShouldAllBe(x => x.Working == 2 && x.Failed == 0 && x.Unfinished == 0);
        recent.Single(x => x.JobId == first.JobId).Processed.ShouldBe(0);
        // Processed is the job row's history: acceptance records the shared item as already processed.
        recent.Single(x => x.JobId == second.JobId).Processed.ShouldBe(1);
        second.Processed.ShouldBe(1);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task QueueScope_CompatibleSelectedScopes_InEitherAcceptanceOrder_KeepOneOwnerPerTitle(bool bulkFirst)
    {
        await SeedAsync();
        await using var db = _database.CreateContext();
        var store = new ArtworkCompletionStore(db, clock: TimeProvider.System);
        var bulk = Request([1, 2, 3]);
        var single = Request([3]);

        var first = (await store.GetAsync((await store.AcceptCompletionAsync(db, bulkFirst ? bulk : single, _actor, default)).Value, default)).Value;
        var second = (await store.GetAsync((await store.AcceptCompletionAsync(db, bulkFirst ? single : bulk, _actor, default)).Value, default)).Value;

        var owners = await db.Set<ArtworkCompletionItemEntity>().Where(x => x.CompletedAt == null && x.FollowUpJobId == null)
            .GroupBy(x => x.TitleId).Select(group => group.Count()).ToListAsync();
        owners.Count.ShouldBe(3);
        owners.ShouldAllBe(count => count == 1);
        var shared = await db.Set<ArtworkCompletionItemEntity>().SingleAsync(x => x.JobId == second.JobId && x.TitleId == 3);
        shared.FollowUpJobId.ShouldBe(first.JobId);
        shared.CompletedAt.ShouldNotBeNull();
    }

    [Theory]
    [InlineData("Enrich", "Artwork", true)]
    [InlineData("Artwork", "Enrich", false)]
    public async Task QueueScope_EnrichmentMode_OnlySharesWorkThatSatisfiesRequestedMode(string existingMode, string requestedMode, bool shares)
    {
        await SeedAsync();
        await using var db = _database.CreateContext();
        var store = new ArtworkCompletionStore(db, clock: TimeProvider.System);
        var first = (await store.GetAsync((await store.AcceptCompletionAsync(db, Request([1], existingMode), _actor, default)).Value, default)).Value;
        var second = (await store.GetAsync((await store.AcceptCompletionAsync(db, Request([1], requestedMode), _actor, default)).Value, default)).Value;
        var item = await db.Set<ArtworkCompletionItemEntity>().SingleAsync(x => x.JobId == second.JobId);

        item.FollowUpJobId.ShouldBe(shares ? first.JobId : null);
        (await store.NextAsync(second.JobId, default) is null).ShouldBe(shares);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task QueueScope_SelectedTitle_DoesNotShareTrackedOnlyOwnerThatMaySkip(bool untrackBeforeSelection)
    {
        await SeedAsync();
        await using var db = _database.CreateContext();
        db.TrackedTitles.Add(new TrackedTitleEntity { TitleId = 1, CreatedAt = DateTimeOffset.UtcNow });
        await db.PublishOriginalRomsAsync();
        await db.SaveChangesAsync();
        var store = new ArtworkCompletionStore(db, clock: TimeProvider.System);
        await store.AcceptCompletionAsync(db, new(Guid.NewGuid(), Scope: "AllTracked", Mode: "Enrich"), _actor, default);
        // Even while currently tracked, tracked-only work may skip if tracking changes
        // before execution; it cannot satisfy an unconditional Selected request.
        if (untrackBeforeSelection)
            await db.TrackedTitles.ExecuteDeleteAsync();
        var selected = (await store.GetAsync((await store.AcceptCompletionAsync(db, Request([1]), _actor, default)).Value, default)).Value;

        (await store.NextAsync(selected.JobId, default))!.RequireTracked.ShouldBeFalse();
        (await db.Set<ArtworkCompletionItemEntity>().SingleAsync(x => x.JobId == selected.JobId)).FollowUpJobId.ShouldBeNull();
    }

    [Fact]
    public async Task QueueScope_SelectedTitle_DoesNotReuseTrackedContinuationWithMatchingScopeSignature()
    {
        await SeedAsync();
        await using var db = _database.CreateContext();
        var store = new ArtworkCompletionStore(db, clock: TimeProvider.System);
        var first = (await store.GetAsync((await store.AcceptCompletionAsync(db, Request([1]), _actor, default)).Value, default)).Value;
        // Identity continuations carry a Selected signature while retaining their tracked-only origin.
        await db.Set<ArtworkCompletionItemEntity>().Where(x => x.JobId == first.JobId)
            .ExecuteUpdateAsync(x => x.SetProperty(i => i.RequireTracked, true));

        var selected = (await store.GetAsync((await store.AcceptCompletionAsync(db, Request([1]), _actor, default)).Value, default)).Value;

        selected.JobId.ShouldNotBe(first.JobId);
        (await store.NextAsync(selected.JobId, default))!.RequireTracked.ShouldBeFalse();
        (await db.Set<ArtworkCompletionItemEntity>().SingleAsync(x => x.JobId == selected.JobId)).FollowUpJobId.ShouldBeNull();
    }

    [Fact]
    public async Task ConfirmMatch_WithoutHistoricalTask_CreatesDurableTitleWorkAndCoalescesPendingFollowUps()
    {
        await SeedAsync();
        await using var db = _database.CreateContext();
        var adapter = Substitute.For<IProviderIdentityAdapter>();
        adapter.Id.Returns("test");
        adapter.Name.Returns("Test");
        adapter.Capabilities.Returns(new[] { "metadata" });
        adapter.GetAvailabilityAsync(Arg.Any<CancellationToken>()).Returns(new ProviderAvailability(true, true));
        db.TitleExternalIds.Add(TitleExternalIdEntity.FromDomain(Romd.Domain.Catalog.TitleExternalId.CreateNew(1, "test", "42", .2f, createdAt: DateTimeOffset.UnixEpoch)));
        await db.PublishOriginalRomsAsync();
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var store = new ArtworkCompletionStore(db, clock: TimeProvider.System);
        var matches = ProviderMatchTestServices.Create(db, [adapter], completion: store);

        var revision = (await matches.GetAsync(1, default)).Value.Single().Revision;
        (await matches.ConfirmAsync(1, "test", RevisionPrecondition.For(revision.ToString()), default)).IsError.ShouldBeFalse();
        db.ChangeTracker.Clear();
        var first = await db.Set<ArtworkCompletionJobEntity>().SingleAsync();
        (await store.NextAsync(first.Id, default))!.Mode.ShouldBe("Enrich");
        (await db.JobDispatches.CountAsync()).ShouldBe(1);
        revision = (await matches.GetAsync(1, default)).Value.Single().Revision;
        (await matches.ConfirmAsync(1, "test", RevisionPrecondition.For(revision.ToString()), default)).IsError.ShouldBeFalse();
        db.ChangeTracker.Clear();
        (await db.Set<ArtworkCompletionJobEntity>().CountAsync()).ShouldBe(1);

        await db.Set<ArtworkCompletionJobEntity>().Where(x => x.Id == first.Id).ExecuteUpdateAsync(x => x.SetProperty(j => j.Phase, "Enriching"));
        revision = (await matches.GetAsync(1, default)).Value.Single().Revision;
        (await matches.ConfirmAsync(1, "test", RevisionPrecondition.For(revision.ToString()), default)).IsError.ShouldBeFalse();
        db.ChangeTracker.Clear();

        (await db.Set<ArtworkCompletionJobEntity>().CountAsync()).ShouldBe(2);
        (await db.Set<ArtworkCompletionJobEntity>().CountAsync(x => x.Phase == "Pending")).ShouldBe(1);
        (await db.JobDispatches.CountAsync()).ShouldBe(2);
        (await db.Set<EnrichmentJobEntity>().CountAsync()).ShouldBe(0);
        var followUp = await db.Set<ArtworkCompletionJobEntity>().SingleAsync(x => x.Phase == "Pending");
        var nextRequest = (await store.GetAsync((await store.AcceptCompletionAsync(db, Request([1, 2]), _actor, default)).Value, default)).Value;
        (await db.Set<ArtworkCompletionItemEntity>().SingleAsync(x => x.JobId == nextRequest.JobId && x.TitleId == 1))
            .FollowUpJobId.ShouldBe(followUp.Id);
    }

    [Fact]
    public async Task QueueScope_ConcurrentOverlappingRequests_HaveOneOwnerPerTitle()
    {
        await SeedAsync();
        await using var firstDb = _database.CreateContext();
        await using var secondDb = _database.CreateContext();
        var runs = await Task.WhenAll(
            new ArtworkCompletionStore(firstDb, clock: TimeProvider.System).AcceptCompletionAsync(firstDb, Request([1, 2]), _actor, default),
            new ArtworkCompletionStore(secondDb, clock: TimeProvider.System).AcceptCompletionAsync(secondDb, Request([2, 3]), _actor, default));

        runs.ShouldAllBe(x => !x.IsError);
        await using var verification = _database.CreateContext();
        (await verification.Set<ArtworkCompletionItemEntity>().CountAsync(x => x.TitleId == 2 && x.CompletedAt == null)).ShouldBe(1);
        (await verification.Set<ArtworkCompletionItemEntity>().CountAsync(x => x.TitleId == 2 && x.FollowUpJobId != null)).ShouldBe(1);
    }

    [Fact]
    public async Task QueueScope_DynamicTrackingChanges_AddsNewTitlesWhileSharingExistingWork()
    {
        await SeedAsync();
        await using var db = _database.CreateContext();
        db.TrackedTitles.Add(new TrackedTitleEntity { TitleId = 1, CreatedAt = DateTimeOffset.UtcNow });
        await db.PublishOriginalRomsAsync();
        await db.SaveChangesAsync();
        var store = new ArtworkCompletionStore(db, clock: TimeProvider.System);
        var first = (await store.GetAsync((await store.AcceptCompletionAsync(db, new(Guid.NewGuid(), Scope: "AllTracked", Mode: "Enrich"), _actor, default)).Value, default)).Value;
        db.TrackedTitles.Add(new TrackedTitleEntity { TitleId = 2, CreatedAt = DateTimeOffset.UtcNow });
        await db.PublishOriginalRomsAsync();
        await db.SaveChangesAsync();

        var second = (await store.GetAsync((await store.AcceptCompletionAsync(db, new(Guid.NewGuid(), Scope: "AllTracked", Mode: "Enrich"), _actor, default)).Value, default)).Value;

        second.JobId.ShouldNotBe(first.JobId);
        second.Total.ShouldBe(2);
        (await store.NextAsync(second.JobId, default))!.TitleId.ShouldBe(2);
        (await db.Set<ArtworkCompletionItemEntity>().SingleAsync(x => x.JobId == second.JobId && x.TitleId == 1)).FollowUpJobId.ShouldBe(first.JobId);
    }

    [Fact]
    public async Task QueueScope_SharedOwnerFails_ProjectsFailureWithoutRewritingEitherRun()
    {
        await SeedAsync();
        await using var db = _database.CreateContext();
        var store = new ArtworkCompletionStore(db, clock: TimeProvider.System);
        var first = (await store.GetAsync((await store.AcceptCompletionAsync(db, Request([1]), _actor, default)).Value, default)).Value;
        var second = (await store.GetAsync((await store.AcceptCompletionAsync(db, Request([1, 2]), _actor, default)).Value, default)).Value;
        await db.Set<ArtworkCompletionItemEntity>().Where(x => x.JobId == first.JobId)
            .ExecuteUpdateAsync(x => x.SetProperty(i => i.CompletedAt, DateTimeOffset.UtcNow));
        foreach (var role in Enum.GetValues<Romd.Domain.Catalog.ArtworkRole>())
            db.Add(new ArtworkCompletionResultEntity { JobId = first.JobId, TitleId = 1, Role = role, Outcome = Romd.Domain.Catalog.ArtworkCompletionOutcome.Failed });
        await db.PublishOriginalRomsAsync();
        await db.SaveChangesAsync();

        var projected = (await store.ItemsAsync(second.JobId, 0, null, default)).Value.Items.Single(x => x.TitleId == IdCoder.Encode(1));

        projected.Completion.ShouldBe(ArtworkCompletionState.Done);
        projected.Roles.ShouldAllBe(x => x.Status == "Failed");
        (await store.ItemsAsync(second.JobId, 0, "Failed", default)).Value.Items.Single().TitleId.ShouldBe(IdCoder.Encode(1));
        (await db.Set<ArtworkCompletionResultEntity>().CountAsync(x => x.JobId == second.JobId)).ShouldBe(0);
    }

    [Theory]
    [InlineData("metadata", true)]
    [InlineData("artwork", false)]
    public async Task ProviderPresentation_LinkWithoutMetadataLayer_RequestsRefreshOnlyForMetadataProvider(string capability, bool needsRefresh)
    {
        await SeedAsync();
        await using var db = _database.CreateContext();
        db.TitleExternalIds.Add(TitleExternalIdEntity.FromDomain(Romd.Domain.Catalog.TitleExternalId.CreateNew(1, "test", "42", 1, createdAt: DateTimeOffset.UnixEpoch)));
        await db.PublishOriginalRomsAsync();
        await db.SaveChangesAsync();
        var adapter = Substitute.For<IProviderIdentityAdapter>();
        adapter.Id.Returns("test");
        adapter.Name.Returns("Test");
        adapter.Capabilities.Returns(new[] { capability });
        adapter.GetAvailabilityAsync(Arg.Any<CancellationToken>()).Returns(new ProviderAvailability(true, true));

        var result = (await ProviderMatchTestServices.Create(db, [adapter]).GetAsync(1, default)).Value.Single();

        result.MetadataNeedsRefresh.ShouldBe(needsRefresh);
    }

    [Fact]
    public async Task QueueScope_Automatic_UsesCurrentTrackedEligibilityAndCatalogFilters()
    {
        await SeedAsync();
        await using var db = _database.CreateContext();
        // Deployment-provided credentials may configure the adapter without a stored database secret.
        var adapter = Substitute.For<IProviderIdentityAdapter>();
        adapter.Id.Returns("igdb");
        adapter.GetAvailabilityAsync(Arg.Any<CancellationToken>()).Returns(new ProviderAvailability(true, true));
        db.TrackedTitles.AddRange(new TrackedTitleEntity { TitleId = 1, CreatedAt = DateTimeOffset.UtcNow },
            new TrackedTitleEntity { TitleId = 2, CreatedAt = DateTimeOffset.UtcNow });
        await db.PublishOriginalRomsAsync();
        await db.SaveChangesAsync();
        var store = new ArtworkCompletionStore(db, clock: TimeProvider.System, identityAdapters: [adapter]);
        var service = new Romd.Admin.Application.Artwork.ArtworkCompletionService(store);
        var request = new ArtworkCompletionRequest(Guid.NewGuid(), Scope: "Automatic", Filters: new(Query: "Game 2"), Mode: "Enrich");

        (await service.PreviewScopeAsync(request with { Mode = "Artwork" }, default)).IsError.ShouldBeTrue();
        (await service.PreviewScopeAsync(request, default)).Value.Titles.ShouldBe(1);
        var queued = (await service.GetAsync((await store.AcceptCompletionAsync(db, request, _actor, default)).Value, default)).Value;

        queued.Total.ShouldBe(1);
        var work = (await store.NextAsync(queued.JobId, default))!;
        work.TitleId.ShouldBe(2);
        work.RequireTracked.ShouldBeTrue();
        (await service.PreviewScopeAsync(new(Guid.NewGuid(), Scope: "Automatic", Mode: "Enrich"), default)).Value.Titles.ShouldBe(1);
        adapter.GetAvailabilityAsync(Arg.Any<CancellationToken>()).Returns(new ProviderAvailability(true, false));
        (await service.PreviewScopeAsync(new(Guid.NewGuid(), Scope: "Automatic", Mode: "Enrich"), default)).Value.Titles.ShouldBe(0);
        (await service.GetAsync((await store.AcceptCompletionAsync(db, request, _actor, default)).Value, default)).Value.JobId.ShouldBe(queued.JobId);
    }

    [Fact]
    public async Task QueueScope_RunAcceptedBeforeEnrichmentFilter_ReadsRetriesAndDedupes()
    {
        await SeedAsync();
        await using var db = _database.CreateContext();
        var store = new ArtworkCompletionStore(db, clock: TimeProvider.System);
        var service = new Romd.Admin.Application.Artwork.ArtworkCompletionService(store);
        var original = new ArtworkCompletionRequest(Guid.NewGuid(), Scope: "Filtered", Filters: new(Query: "Game"), Mode: "Enrich");
        // Signature persisted before ArtworkCompletionFilter.Enrichment existed.
        const string preChangeScope = """{"RequestId":"00000000-0000-0000-0000-000000000000","TitleIds":null,"Scope":"Filtered","Filters":{"Query":"Game","SystemKey":null,"Genre":null,"EnrichmentStatus":null,"Tracked":"all","ReleaseCompleteness":"all","ArtworkStatus":null},"RetryJobId":null,"RetryStatus":null,"Mode":"Enrich"}""";
        System.Text.Json.JsonSerializer.Serialize(original with { RequestId = Guid.Empty }).ShouldBe(preChangeScope);
        System.Text.Json.JsonSerializer.Deserialize<ArtworkCompletionRequest>(preChangeScope)!.Filters.ShouldNotBeNull().Enrichment.ShouldBeNull();
        var accepted = (await service.GetAsync((await store.AcceptCompletionAsync(db, original, _actor, default)).Value, default)).Value;
        accepted.Total.ShouldBe(3);
        await db.Set<ArtworkCompletionJobEntity>().Where(x => x.Id == accepted.JobId).ExecuteUpdateAsync(s => s
            .SetProperty(x => x.ScopeJson, preChangeScope).SetProperty(x => x.Phase, "CompletedWithErrors"));
        await db.Set<ArtworkCompletionItemEntity>().Where(x => x.JobId == accepted.JobId)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.CompletedAt, DateTimeOffset.UtcNow));
        db.Add(new ArtworkCompletionResultEntity
        {
            JobId = accepted.JobId,
            TitleId = 2,
            Role = Romd.Domain.Catalog.ArtworkRole.Poster,
            Outcome = Romd.Domain.Catalog.ArtworkCompletionOutcome.Failed
        });
        await db.PublishOriginalRomsAsync();
        await db.SaveChangesAsync();

        (await store.GetAsync(accepted.JobId, default)).Value.Mode.ShouldBe("Enrich");
        (await store.RecentAsync(default)).Single(x => x.JobId == accepted.JobId).Mode.ShouldBe("Enrich");
        (await service.GetAsync((await store.AcceptCompletionAsync(db, original, _actor, default)).Value, default)).Value.JobId.ShouldBe(accepted.JobId);
        var retryRequest = new ArtworkCompletionRequest(Guid.NewGuid(), Scope: "Retry", RetryJobId: accepted.JobId, RetryStatus: "Failed");
        var retry = (await service.GetAsync((await store.AcceptCompletionAsync(db, retryRequest, _actor, default)).Value, default)).Value;

        retry.JobId.ShouldNotBe(accepted.JobId);
        retry.Mode.ShouldBe("Enrich");
        retry.Total.ShouldBe(1);
        var work = (await store.NextAsync(retry.JobId, default)).ShouldNotBeNull();
        work.TitleId.ShouldBe(2);
        work.Mode.ShouldBe("Enrich");
        (await service.GetAsync((await store.AcceptCompletionAsync(db, retryRequest, _actor, default)).Value, default)).Value.JobId.ShouldBe(retry.JobId);
        (await db.Set<ArtworkCompletionJobEntity>().CountAsync()).ShouldBe(2);
    }

    [Fact]
    public async Task GetLinkRevision_WithinPublicationTransaction_ReadsTrustWithoutOpeningIdentitySnapshot()
    {
        await SeedAsync();
        await using var db = _database.CreateContext();
        var link = TitleExternalIdEntity.FromDomain(Romd.Domain.Catalog.TitleExternalId.CreateNew(1, "test", "42", 1, createdAt: DateTimeOffset.UnixEpoch));
        link.IsConfirmed = true;
        db.TitleExternalIds.Add(link);
        await db.PublishOriginalRomsAsync();
        await db.SaveChangesAsync();
        var identities = Substitute.For<IProviderIdentityStore>();
        var service = ProviderMatchTestServices.Create(db, [], identityStore: identities);

        await using var publication = await db.Database.BeginTransactionAsync();
        var revision = await service.GetLinkRevisionAsync(1, "test", "42", default);

        revision.ShouldNotBeNull();
        (await service.GetLinkRevisionAsync(1, "test", "43", default)).ShouldBeNull();
        await identities.DidNotReceive().ReadAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
        await publication.CommitAsync();
    }

    [Theory]
    [InlineData("Filtered", ReleaseCompletenessFilter.Complete, new[] { 1 })]
    [InlineData("Filtered", ReleaseCompletenessFilter.Partial, new[] { 2 })]
    [InlineData("Filtered", ReleaseCompletenessFilter.None, new[] { 3 })]
    [InlineData("Filtered", ReleaseCompletenessFilter.All, new[] { 1, 2, 3, 4 })]
    [InlineData("Automatic", ReleaseCompletenessFilter.Complete, new[] { 1 })]
    [InlineData("Automatic", ReleaseCompletenessFilter.Partial, new[] { 2 })]
    [InlineData("Automatic", ReleaseCompletenessFilter.None, new[] { 3 })]
    [InlineData("Automatic", ReleaseCompletenessFilter.All, new[] { 1, 2, 3, 4 })]
    public async Task QueueScope_ReleaseCompleteness_MatchesCatalogIncludingInactiveSourcesAndImportOnlyTitles(
        string scope, ReleaseCompletenessFilter completeness, int[] expected)
    {
        await SeedAsync();
        await using var db = _database.CreateContext();
        db.Titles.Add(new TitleEntity
        {
            Id = 4,
            PlatformId = 1,
            Name = "Import only",
            NormalizedName = "import only",
            EnrichmentStatus = "None",
            HasLocalPayload = true
        });
        var identity = MediaFactFixtures.Identity(new byte[] { 1 });
        db.Files.Add(new FileEntityPersistence { Id = 1, Sha256 = identity.Sha256, Size = identity.Size, SizeOnDisk = identity.Size });
        db.StageOriginalRom(new OriginalRomFixture
        {
            Id = 1,
            FileId = 1,
            OriginalFilename = "game.rom",
            ContentSha256 = identity.Sha256,
            ContentSize = identity.Size,
            Sha1 = identity.Sha1,
            Md5 = identity.Md5,
            Crc32 = identity.Crc32
        });
        db.DatSources.AddRange(
            new DatSourceEntity { Id = 1, CatalogSource = new CatalogSourceEntity { Id = 1, Kind = "Dat", Status = "Active" } },
            new DatSourceEntity { Id = 2, CatalogSource = new CatalogSourceEntity { Id = 2, Kind = "Dat", Status = "Paused" } });
        for (var source = 1; source <= 2; source++)
            db.DatFiles.Add(new DatFileEntity
            {
                Id = source,
                DatSourceId = source,
                Name = $"DAT {source}",
                Description = "Test",
                Type = "NoIntro",
                PlatformId = 1,
                OriginalFilename = $"test{source}.dat",
                FileId = 1
            });
        // Title 1 is complete, title 2 partial, title 3 absent. The paused missing release
        // must not make title 1 partial; title 4's imported payload is outside DAT buckets.
        var titleIds = new[] { 1, 2, 2, 3, 1 };
        for (var index = 0; index < titleIds.Length; index++)
        {
            var id = index + 1;
            var source = id == 5 ? 2 : 1;
            db.SourceEntries.Add(new SourceEntryEntity { Id = id, CatalogSourceId = source, EntryKey = $"release-{id}", Name = $"Release {id}", PlatformId = 1 });
            db.DatGames.Add(new DatGameEntity { Id = id, DatFileId = source, SourceEntryId = id, Name = $"Release {id}" });
            db.DatRoms.Add(new DatRomEntity { Id = id, DatGameId = id, Name = $"release-{id}.rom", Size = 1, RomFileId = id <= 2 ? 1 : null });
            db.TitleSourceLinks.Add(new TitleSourceLinkEntity { SourceEntryId = id, TitleId = titleIds[index] });
        }
        for (var id = 1; id <= 4; id++)
            db.TrackedTitles.Add(new TrackedTitleEntity { TitleId = id });
        await db.PublishOriginalRomsAsync();
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var adapter = Substitute.For<IProviderIdentityAdapter>();
        adapter.Id.Returns("igdb");
        adapter.GetAvailabilityAsync(Arg.Any<CancellationToken>()).Returns(new ProviderAvailability(true, true));
        var store = new ArtworkCompletionStore(db, clock: TimeProvider.System, identityAdapters: [adapter]);
        var request = new ArtworkCompletionRequest(Guid.NewGuid(), Scope: scope,
            Filters: new(ReleaseCompleteness: completeness.ToString()), Mode: "Enrich");
        var catalog = (await new SearchRepository(db).SearchTitlesAsync(null, new(ReleaseCompleteness: completeness),
            TitleSortField.Name, null, 50, null)).Value;

        var preview = (await store.PreviewScopeAsync(request, default)).Value;
        var job = (await store.GetAsync((await store.AcceptCompletionAsync(db, request, _actor, default)).Value, default)).Value;
        var queued = await db.Set<ArtworkCompletionItemEntity>().Where(x => x.JobId == job.JobId).OrderBy(x => x.TitleId).Select(x => x.TitleId).ToArrayAsync();

        catalog.Items.Select(x => x.Id).Order().ShouldBe(expected);
        preview.Titles.ShouldBe(expected.Length);
        queued.ShouldBe(expected);
    }

    [Fact]
    public async Task PreviewScope_Automatic_AggregatesAllRolesInOneEligibilityQuery()
    {
        await SeedAsync();
        var recorder = new CommandRecorder();
        await using var db = new Romd.Persistence.RomdDbContext(_database.CreateOptions(x => x.AddInterceptors(recorder)));
        for (var id = 1; id <= 3; id++)
            db.TrackedTitles.Add(new TrackedTitleEntity { TitleId = id });
        db.ArtworkSelections.Add(new ArtworkSelectionEntity
        {
            TitleId = 1,
            Role = Romd.Domain.Catalog.ArtworkRole.Hero,
            Mode = Romd.Domain.Catalog.ArtworkSelectionMode.Empty
        });
        db.ArtworkSelections.Add(new ArtworkSelectionEntity
        {
            TitleId = 2,
            Role = Romd.Domain.Catalog.ArtworkRole.Logo,
            Mode = Romd.Domain.Catalog.ArtworkSelectionMode.Automatic,
            PendingRequestId = Guid.NewGuid()
        });
        await db.PublishOriginalRomsAsync();
        await db.SaveChangesAsync();
        var adapter = Substitute.For<IProviderIdentityAdapter>();
        adapter.Id.Returns("igdb");
        adapter.GetAvailabilityAsync(Arg.Any<CancellationToken>()).Returns(new ProviderAvailability(true, true));
        var store = new ArtworkCompletionStore(db, clock: TimeProvider.System, identityAdapters: [adapter]);
        recorder.Commands.Clear();

        var preview = (await store.PreviewScopeAsync(new(Guid.NewGuid(), Scope: "Automatic", Mode: "Enrich"), default)).Value;

        preview.Titles.ShouldBe(3);
        preview.Roles.Select(x => x.Count).ShouldBe([3, 2, 2, 3]);
        recorder.Commands.Count.ShouldBe(1);
        await db.TrackedTitles.ExecuteDeleteAsync();
        var empty = (await store.PreviewScopeAsync(new(Guid.NewGuid(), Scope: "Automatic", Mode: "Enrich"), default)).Value;
        empty.Titles.ShouldBe(0);
        empty.Roles.ShouldAllBe(x => x.Count == 0);
    }

    [Fact]
    public async Task History_LargeRunAndDeepContinuation_FiltersBeforePagingAndAggregatesRecordedOutcomes()
    {
        await SeedAsync();
        var recorder = new CommandRecorder();
        await using var db = new Romd.Persistence.RomdDbContext(_database.CreateOptions(x => x.AddInterceptors(recorder)));
        for (var id = 4; id <= 125; id++)
            db.Titles.Add(new TitleEntity { Id = id, PlatformId = 1, Name = $"Game {id}", NormalizedName = $"game {id}", EnrichmentStatus = "None" });
        var root = AddHistoryJob(db, 125);
        var prior = root;
        for (var depth = 0; depth < 8; depth++)
        {
            var child = AddHistoryJob(db, 1, depth + 1);
            db.Add(new ArtworkCompletionItemEntity
            {
                JobId = prior.Id,
                TitleId = 125,
                TitleName = "Original title",
                CompletedAt = DateTimeOffset.UtcNow,
                FollowUpJobId = child.Id
            });
            prior = child;
        }
        db.Add(new ArtworkCompletionItemEntity { JobId = prior.Id, TitleId = 125, TitleName = "Later title", CompletedAt = DateTimeOffset.UtcNow });
        db.Add(new ArtworkCompletionResultEntity
        {
            JobId = prior.Id,
            TitleId = 125,
            Role = Romd.Domain.Catalog.ArtworkRole.Poster,
            Outcome = Romd.Domain.Catalog.ArtworkCompletionOutcome.Failed
        });
        db.ArtworkSelections.Add(new ArtworkSelectionEntity
        {
            TitleId = 125,
            Role = Romd.Domain.Catalog.ArtworkRole.Poster,
            Mode = Romd.Domain.Catalog.ArtworkSelectionMode.Empty
        });
        for (var id = 1; id < 125; id++)
        {
            db.Add(new ArtworkCompletionItemEntity { JobId = root.Id, TitleId = id, TitleName = $"Game {id}", CompletedAt = DateTimeOffset.UtcNow });
            db.Add(new ArtworkCompletionResultEntity
            {
                JobId = root.Id,
                TitleId = id,
                Role = Romd.Domain.Catalog.ArtworkRole.Poster,
                Outcome = id <= 60 ? Romd.Domain.Catalog.ArtworkCompletionOutcome.Updated : Romd.Domain.Catalog.ArtworkCompletionOutcome.Failed
            });
        }
        await db.PublishOriginalRomsAsync();
        await db.SaveChangesAsync();
        // Execution history remains readable after catalog removal.
        await db.Titles.Where(x => x.Id == 124).ExecuteDeleteAsync();
        var store = new ArtworkCompletionStore(db, clock: TimeProvider.System);
        recorder.Commands.Clear();

        var page = (await store.ItemsAsync(root.Id, 0, "Failed", default)).Value;

        page.Items.Select(x => Sqid.Parse(x.TitleId, null).Value).ShouldBe(Enumerable.Range(61, 50));
        page.NextCursor.ShouldBe(IdCoder.Encode(110));
        // Existence + a bounded page + current flags for the page's titles only, independent of continuation depth.
        recorder.Commands.Count.ShouldBe(3);
        recorder.Commands[1].ShouldContain("LIMIT");
        recorder.Commands[1].ShouldContain("FROM matching");
        recorder.Commands[1].ShouldNotContain("enrichment_requirement_facts_set");
        recorder.Commands[2].ShouldContain("enrichment_requirement_facts_set");
        var tail = (await store.ItemsAsync(root.Id, 110, "Failed", default)).Value;
        tail.Items.Count.ShouldBe(14); // the removed title 124 keeps its history but is not Failed
        tail.NextCursor.ShouldBeNull();
        // The leaf's recorded outcome stays history; a later curator decision does not rewrite it.
        var resolved = (await store.ItemsAsync(root.Id, 124, null, default)).Value.Items.Single();
        resolved.TitleName.ShouldBe("Original title");
        resolved.Completion.ShouldBe(ArtworkCompletionState.Done);
        resolved.Roles.Single().Status.ShouldBe("Failed");
        resolved.MetadataOutcome.ShouldBeNull();
        resolved.Now.ShouldNotBeNull();
        var removed = (await store.ItemsAsync(root.Id, 123, null, default)).Value.Items[0];
        (removed.TitleId, removed.Roles.Single().Status).ShouldBe((IdCoder.Encode(124), "Failed"));
        removed.Now.ShouldBeNull();
        var summary = (await store.GetAsync(root.Id, default)).Value;
        summary.Failed.ShouldBe(64);
        summary.Unfinished.ShouldBe(0);
        summary.Working.ShouldBe(0);
        summary.Processed.ShouldBe(125);
        summary.Roles.Single(x => x.Status == "Failed").Count.ShouldBe(65); // raw recorded outcomes include removed titles
        recorder.Commands.Clear();
        var recent = (await store.RecentAsync(default)).Single();
        (recent.Review, recent.Failed, recent.Unfinished, recent.Working).ShouldBe((summary.Review, summary.Failed, summary.Unfinished, summary.Working));
        recent.Roles.ShouldBe(summary.Roles);
        // Recent roots; the counts statement composes EF's assessment command outside EF's pipeline (like the
        // workbench page), so the recorder cannot see it. No per-job/item hydration either way.
        recorder.Commands.Count.ShouldBe(1);
        recorder.Commands.Clear();
        var retry = (await store.GetAsync((await store.AcceptCompletionAsync(db, new(Guid.NewGuid(), Scope: "Retry", RetryJobId: root.Id, RetryStatus: "Failed"), _actor, default)).Value, default)).Value;
        retry.Total.ShouldBe(summary.Failed); // retry membership is exactly the Failed list
        recorder.Commands.Count(x => x.Contains("UPDATE romd.\"ArtworkCompletionItems\"", StringComparison.Ordinal)).ShouldBe(1);
        (await db.Set<ArtworkCompletionItemEntity>().CountAsync(x => x.JobId == root.Id && x.FollowUpJobId == retry.JobId)).ShouldBe(63);
        (await store.ItemsAsync(root.Id, 0, "Failed", default)).Value.Items.ShouldBeEmpty();
        (await store.ItemsAsync(root.Id, 123, null, default)).Value.Items[0].TitleId.ShouldBe(IdCoder.Encode(124));
    }

    [Fact]
    public async Task History_CyclesMissingReferencesAndLegacyOutcomes_ReadsRecordedLeafAndTerminates()
    {
        await SeedAsync();
        await using var db = _database.CreateContext();
        var root = AddHistoryJob(db, 3);
        var child = AddHistoryJob(db, 1, 1, "Enrich");
        db.AddRange(
            new ArtworkCompletionItemEntity { JobId = root.Id, TitleId = 1, TitleName = "Game 1", CompletedAt = DateTimeOffset.UtcNow, FollowUpJobId = child.Id },
            new ArtworkCompletionItemEntity
            {
                JobId = child.Id,
                TitleId = 1,
                TitleName = "Child",
                CompletedAt = DateTimeOffset.UtcNow,
                FollowUpJobId = root.Id,
                MetadataOutcome = Romd.Domain.Catalog.MetadataExecutionOutcome.Failed
            },
            new ArtworkCompletionItemEntity { JobId = root.Id, TitleId = 2, TitleName = "Game 2", CompletedAt = DateTimeOffset.UtcNow, FollowUpJobId = Guid.NewGuid() },
            new ArtworkCompletionItemEntity { JobId = root.Id, TitleId = 3, TitleName = "Game 3" });
        // A current identity failure is not recorded history: title 2 has unknown (legacy) metadata history.
        db.Add(new TitleProviderMatchStateEntity { TitleId = 2, ProviderId = "test", DecisionJson = "{\"State\":\"Unavailable\"}" });
        db.Add(new ArtworkCompletionResultEntity
        {
            JobId = root.Id,
            TitleId = 2,
            Role = Romd.Domain.Catalog.ArtworkRole.Poster,
            Outcome = Romd.Domain.Catalog.ArtworkCompletionOutcome.NeedsMatch
        });
        await db.PublishOriginalRomsAsync();
        await db.SaveChangesAsync();
        var store = new ArtworkCompletionStore(db, clock: TimeProvider.System);

        var page = (await store.ItemsAsync(root.Id, 0, null, default)).Value;

        page.Items.Count.ShouldBe(3);
        page.Items[0].MetadataOutcome.ShouldBe(ArtworkCompletionMetadataOutcome.Failed);
        page.Items[1].MetadataOutcome.ShouldBeNull();
        page.Items[1].Roles.Single().Status.ShouldBe("NeedsMatch");
        page.Items[2].Completion.ShouldBe(ArtworkCompletionState.Unfinished);
        (await store.ItemsAsync(root.Id, 0, "Failed", default)).Value.Items.Single().TitleId.ShouldBe(IdCoder.Encode(1));
        (await store.ItemsAsync(root.Id, 0, "Unfinished", default)).Value.Items.Single().TitleId.ShouldBe(IdCoder.Encode(3));
        var summary = (await store.GetAsync(root.Id, default)).Value;
        (summary.Failed, summary.Unfinished, summary.Working).ShouldBe((1, 1, 0));
        (await store.PreviewScopeAsync(new(Guid.NewGuid(), Scope: "Retry", RetryJobId: root.Id, RetryStatus: "Failed"), default)).Value.Titles.ShouldBe(1);
        (await store.PreviewScopeAsync(new(Guid.NewGuid(), Scope: "Retry", RetryJobId: root.Id, RetryStatus: "Unfinished"), default)).Value.Titles.ShouldBe(1);
    }

    private ArtworkCompletionJobEntity AddHistoryJob(Romd.Persistence.RomdDbContext db, int total, int seconds = 0, string mode = "Artwork")
    {
        var row = ArtworkCompletionJobEntity.FromDomain(Romd.Domain.Jobs.ArtworkCompletionJob.Create(Guid.NewGuid(), total, _actor, createdAt: DateTimeOffset.UtcNow));
        row.Phase = "Completed";
        row.Processed = total;
        row.CreatedAt = DateTimeOffset.UtcNow.AddSeconds(seconds);
        row.ScopeJson = System.Text.Json.JsonSerializer.Serialize(new ArtworkCompletionRequest(Guid.Empty, Mode: mode));
        db.Add(row);
        return row;
    }

    private sealed class CommandRecorder : Microsoft.EntityFrameworkCore.Diagnostics.DbCommandInterceptor
    {
        public List<string> Commands { get; } = [];
        public override ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int>> NonQueryExecutingAsync(
            System.Data.Common.DbCommand command, Microsoft.EntityFrameworkCore.Diagnostics.CommandEventData eventData,
            Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            Commands.Add(command.CommandText);
            return ValueTask.FromResult(result);
        }
        public override ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<System.Data.Common.DbDataReader>> ReaderExecutingAsync(
            System.Data.Common.DbCommand command, Microsoft.EntityFrameworkCore.Diagnostics.CommandEventData eventData,
            Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<System.Data.Common.DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Commands.Add(command.CommandText);
            return ValueTask.FromResult(result);
        }
    }

    private static ArtworkCompletionRequest Request(int[] ids, string mode = "Enrich") =>
        new(Guid.NewGuid(), ids.Select(IdCoder.Encode).ToArray(), Mode: mode);

    private async Task SeedAsync()
    {
        await using var db = _database.CreateContext();
        db.Users.Add(new Romd.Persistence.Identity.RomdUser { Id = _actor, UserName = "curator", CreatedAt = DateTimeOffset.UtcNow });
        db.Platforms.Add(new PlatformEntity
        {
            Id = 1,
            Name = "Test",
            Ownership = Romd.Domain.ReferenceData.ReferenceOwnership.Romd,
            BuiltInVersion = 1,
            BaseName = "Test",
            BaseCompactLabel = "Test",
            CanonicalKey = "test",
            ShortName = "test",
            Manufacturer = "Test",
            CreatedAt = DateTimeOffset.UtcNow
        });
        for (var id = 1; id <= 3; id++)
            db.Titles.Add(new TitleEntity
            {
                Id = id,
                PlatformId = 1,
                Name = $"Game {id}",
                NormalizedName = $"game {id}",
                EnrichmentStatus = "None",
                FieldProvenanceJson = "{}",
                FieldSourceOverridesJson = "{}",
                ScreenshotPrefsJson = "{}",
                CreatedAt = DateTimeOffset.UtcNow
            });
        await db.PublishOriginalRomsAsync();
        await db.SaveChangesAsync();
    }

    public void Dispose() => _database.Dispose();
}
