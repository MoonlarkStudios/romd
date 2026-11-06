using Romd.Persistence;
using Microsoft.Extensions.Logging.Abstractions;
using Romd.Infrastructure.Tests.Helpers;
using Romd.Application.Common.Concurrency;
using Romd.Application.Common.Validation;
using ErrorOr;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Romd.Admin.Application.MetadataProviders;
using Romd.Contracts.Management.MetadataProviders;
using Romd.Persistence.Entities;
using Romd.Persistence.MetadataProviders;
using Romd.PostgreSql.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Artwork;

public sealed partial class ProviderMatchPersistenceTests : IDisposable
{
    private readonly PostgreSqlTestDatabase _database = PostgreSqlTestDatabase.Create();
    private readonly IProviderIdentityAdapter _adapter = Substitute.For<IProviderIdentityAdapter>();

    public ProviderMatchPersistenceTests()
    {
        _adapter.Id.Returns("test");
        _adapter.Name.Returns("Test provider");
        _adapter.Capabilities.Returns(new[] { "artwork", "search", "resolve" });
        _adapter.GetAvailabilityAsync(Arg.Any<CancellationToken>()).Returns(new ProviderAvailability(true, true));
        _adapter.ResolveAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(call =>
            Task.FromResult<ErrorOr<ProviderGameDto>>(new ProviderGameDto(call.Arg<string>(), "Game", "https://example.com/game")));
    }

    [Fact]
    public async Task Linking_ValidatesIdentityAndRejectsStaleRevision()
    {
        await Seed();
        await using var db = _database.CreateContext();
        var service = ProviderMatchTestServices.Create(db, [_adapter]);
        var revision = (await service.GetAsync(1, default)).Value.Single().Revision;
        (await service.SetAsync(1, "test", "42", RevisionPrecondition.For(revision.ToString()), default)).IsError.ShouldBeFalse();
        db.ChangeTracker.Clear();
        (await service.SetAsync(1, "test", "43", RevisionPrecondition.For(revision.ToString()), default)).FirstError.Code.ShouldBe("ProviderMatch.PreconditionFailed");
        db.ChangeTracker.Clear();
        (await db.TitleExternalIds.SingleAsync()).ExternalId.ShouldBe("42");
        (await db.TitleMetadataLayers.CountAsync()).ShouldBe(0);
        (await db.ArtworkAssets.CountAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task Set_ConcurrentWritersWithOneETag_TheSecondWaitsForTheTitleLockThenIs412()
    {
        await Seed();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        // The first writer pauses inside its transaction, holding the title lock, while it queues its follow-up.
        var holding = new TaskCompletionSource();
        var release = new TaskCompletionSource<Guid?>();
        var completion = Substitute.For<Romd.Admin.Application.Artwork.IArtworkCompletionStore>();
        completion.ResumeTitleStagedAsync(1, Arg.Any<CancellationToken>()).Returns(_ =>
        {
            holding.TrySetResult();
            return release.Task;
        });
        await using var first = _database.CreateContext();
        await using var second = _database.CreateContext();
        var shown = RevisionPrecondition.For(
            (await ProviderMatchTestServices.Create(first, [_adapter]).GetAsync(1, timeout.Token)).Value.Single().Revision.ToString());
        first.ChangeTracker.Clear();
        var winner = ProviderMatchTestServices.Create(first, [_adapter], completion: completion)
            .SetAsync(1, "test", "41", shown, timeout.Token);
        await holding.Task.WaitAsync(timeout.Token);

        var (pending, waited) = await _database.StartWaitingForALockAsync(second,
            ct => ProviderMatchTestServices.Create(second, [_adapter]).SetAsync(1, "test", "42", shown, ct), timeout.Token);
        release.SetResult(null);

        (await winner.WaitAsync(timeout.Token)).IsError.ShouldBeFalse();
        waited.ShouldBeTrue();
        (await pending.WaitAsync(timeout.Token)).FirstError.Code.ShouldBe("ProviderMatch.PreconditionFailed");
        await using var read = _database.CreateContext();
        (await read.TitleExternalIds.SingleAsync(timeout.Token)).ExternalId.ShouldBe("41");
    }

    [Fact]
    public async Task Unlink_PreservesDataAndSuppressesRematching()
    {
        await Seed();
        await using var db = _database.CreateContext();
        var service = ProviderMatchTestServices.Create(db, [_adapter]);
        var initial = (await service.GetAsync(1, default)).Value.Single();
        (await service.SetAsync(1, "test", "42", RevisionPrecondition.For(initial.Revision.ToString()), default)).IsError.ShouldBeFalse();
        db.ChangeTracker.Clear();
        var linked = (await service.GetAsync(1, default)).Value.Single();
        (await service.UnlinkAsync(1, "test", RevisionPrecondition.For(Guid.NewGuid().ToString()), default)).FirstError.Code
            .ShouldBe("ProviderMatch.PreconditionFailed");
        (await service.IsSuppressedAsync(1, "test", default)).ShouldBeFalse();
        (await service.UnlinkAsync(1, "test", RevisionPrecondition.For(linked.Revision.ToString()), default)).IsError.ShouldBeFalse();
        db.ChangeTracker.Clear();
        (await service.IsSuppressedAsync(1, "test", default)).ShouldBeTrue();
        (await service.GetLinkRevisionAsync(1, "test", "42", default)).ShouldBeNull();
        var unlinked = (await service.GetAsync(1, default)).Value.Single();
        (await service.SetAsync(1, "test", "43", RevisionPrecondition.For(unlinked.Revision.ToString()), default)).IsError.ShouldBeFalse();
        db.ChangeTracker.Clear();
        (await service.IsSuppressedAsync(1, "test", default)).ShouldBeFalse();
    }

    [Fact]
    public async Task LowConfidenceMatch_RequiresReviewBeforeArtworkBrowsing()
    {
        await Seed();
        await using var db = _database.CreateContext();
        db.TitleExternalIds.Add(TitleExternalIdEntity.FromDomain(Romd.Domain.Catalog.TitleExternalId.CreateNew(1, "test", "42", 0.2f, createdAt: DateTimeOffset.UnixEpoch)));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var service = ProviderMatchTestServices.Create(db, [_adapter]);
        var row = (await service.GetAsync(1, default)).Value.Single();
        row.State.ShouldBe("NeedsReview");
        (await service.GetLinkRevisionAsync(1, "test", "42", default)).ShouldBeNull();
        (await service.ConfirmAsync(1, "test", RevisionPrecondition.For(row.Revision.ToString()), default)).IsError.ShouldBeFalse();
        db.ChangeTracker.Clear();
        (await service.GetLinkRevisionAsync(1, "test", "42", default)).ShouldNotBeNull();
    }

    [Fact]
    public async Task Confirm_LowConfidenceTitle_TheFollowUpEnrichmentEndsCompleted_EvenWhenTheProviderScoresLow()
    {
        await Seed();
        await using var db = _database.CreateContext();
        await db.Titles.Where(x => x.Id == 1).ExecuteUpdateAsync(x =>
            x.SetProperty(t => t.EnrichmentStatus, nameof(Romd.Domain.Catalog.EnrichmentStatus.LowConfidence)));
        db.TitleExternalIds.Add(TitleExternalIdEntity.FromDomain(Romd.Domain.Catalog.TitleExternalId.CreateNew(1, "test", "42", 0.3f, createdAt: DateTimeOffset.UnixEpoch)));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var service = ProviderMatchTestServices.Create(db, [_adapter]);
        var shown = (await service.GetAsync(1, "test", default)).Value;

        var confirmed = (await service.ConfirmAsync(1, "test", RevisionPrecondition.For(shown.Revision.ToString()), default)).Value;
        db.ChangeTracker.Clear();
        // The follow-up runs as its job: claimed, fenced, with a provider that scores the confirmed game below the
        // automatic threshold.
        var jobId = confirmed.PendingJobId.ShouldNotBeNull();
        var options = new Romd.Admin.Application.Ingestion.Jobs.JobExecutionClaimOptions();
        var claim = (await new Romd.Persistence.Repositories.EnrichmentJobRepository(db, TimeProvider.System, options)
            .TryClaimExecutionAsync(jobId, "enrichment")).ShouldNotBeNull();
        var context = new Romd.Admin.Application.Ingestion.Jobs.JobContext(null, _ => Task.CompletedTask, CancellationToken.None,
            ownedMutationDelegate: (mutation, ct) => new Romd.Infrastructure.Jobs.JobExecutionMutationGuard(db,
                new Romd.Persistence.EfUnitOfWork(db), TimeProvider.System, options).ExecuteAsync(jobId, claim.FenceToken, mutation, ct),
            jobId: jobId);
        var orchestrator = Substitute.For<Romd.Admin.Application.Titles.Enrichment.IEnrichmentOrchestrator>();
        orchestrator.EnrichTitleAsync(default!, default!, default).ReturnsForAnyArgs(
            Romd.Admin.Application.Titles.Enrichment.OrchestrationResult.Found(0.1f));
        var titles = new Romd.Persistence.Repositories.TitleRepository(db);
        var pipeline = new Romd.Infrastructure.Jobs.Executors.TitleEnrichmentPipeline(titles, titles, titles,
            new Romd.Persistence.Repositories.PlatformRepository(db), orchestrator,
            Substitute.For<Romd.Admin.Application.Storage.Files.IFileStorageService>(),
            new Romd.Persistence.EfUnitOfWork(db),
            Substitute.For<Romd.Admin.Application.Dashboard.IStatsNotifier>(),
            Substitute.For<Romd.Admin.Application.Libraries.ILibraryMaterializationService>(),
            Microsoft.Extensions.Options.Options.Create(new Romd.Admin.Application.Configuration.EnrichmentOptions()),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<Romd.Infrastructure.Jobs.Executors.TitleEnrichmentPipeline>.Instance,
            Substitute.For<Romd.Admin.Application.Artwork.IAutomaticArtworkService>(),
            Substitute.For<Romd.Admin.Application.Artwork.IArtworkCompletionStore>(), TimeProvider.System);

        await new Romd.Infrastructure.Jobs.Executors.EnrichmentJobExecutor(pipeline).ExecuteAsync(claim.Job, context);

        db.ChangeTracker.Clear();
        (await db.Titles.SingleAsync(x => x.Id == 1)).EnrichmentStatus
            .ShouldBe(nameof(Romd.Domain.Catalog.EnrichmentStatus.Completed));
        (await db.TitleExternalIds.SingleAsync()).IsConfirmed.ShouldBeTrue();
    }

    [Fact]
    public async Task DisabledProvider_IsHiddenWithoutRemovingLink()
    {
        await Seed();
        await using var db = _database.CreateContext();
        var service = ProviderMatchTestServices.Create(db, [_adapter]);
        var initial = (await service.GetAsync(1, default)).Value.Single();
        (await service.SetAsync(1, "test", "42", RevisionPrecondition.For(initial.Revision.ToString()), default)).IsError.ShouldBeFalse();
        _adapter.GetAvailabilityAsync(Arg.Any<CancellationToken>()).Returns(new ProviderAvailability(false, true));
        (await service.GetAsync(1, default)).Value.ShouldBeEmpty();
        (await db.TitleExternalIds.CountAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task InvalidIdentity_DoesNotCreateAssociation()
    {
        await Seed();
        await using var db = _database.CreateContext();
        var service = ProviderMatchTestServices.Create(db, [_adapter]);
        var initial = (await service.GetAsync(1, default)).Value.Single();
        _adapter.ResolveAsync("bad", Arg.Any<CancellationToken>()).Returns(Task.FromResult<ErrorOr<ProviderGameDto>>(ProviderMatchErrors.NotFound));
        (await service.SetAsync(1, "test", "bad", RevisionPrecondition.For(initial.Revision.ToString()), default)).IsError.ShouldBeTrue();
        (await db.TitleExternalIds.CountAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task InputTheProviderRejects_IsKeyedByTheRequestFieldThatCarriedIt()
    {
        await Seed();
        await using var db = _database.CreateContext();
        var service = ProviderMatchTestServices.Create(db, [_adapter]);
        var shown = RevisionPrecondition.For((await service.GetAsync(1, default)).Value.Single().Revision.ToString());
        _adapter.ResolveAsync("bad", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ErrorOr<ProviderGameDto>>(ProviderMatchErrors.InvalidInput));
        _adapter.ResolveAsync("404", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ErrorOr<ProviderGameDto>>(ProviderMatchErrors.NotFound));
        _adapter.SearchAsync("bad", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ErrorOr<ProviderGameSearchResultDto>>(ProviderMatchErrors.InvalidInput));

        var rejected = new[]
        {
            (await service.SetAsync(1, "test", "bad", shown, default)).Errors,
            (await service.SetAsync(1, "test", "404", shown, default)).Errors,
            (await service.SearchAsync("test", "bad", default)).Errors,
            (await service.ResolveAsync("test", "404", default)).Errors
        };

        rejected.Select(errors => (errors.Single().Code, FieldErrors.FieldOf(errors.Single()))).ShouldBe([
            ("ProviderMatch.InvalidInput", "externalId"), ("ProviderMatch.Unresolvable", "externalId"),
            ("ProviderMatch.InvalidInput", "query"), ("ProviderMatch.Unresolvable", "input")
        ]);
        rejected.ShouldAllBe(errors => errors.Single().Type == ErrorType.Validation);
    }

    [Fact]
    public async Task ConfirmingMatch_QueuesMetadataAndArtworkAndCoalescesPendingWork()
    {
        await Seed();
        await using var db = _database.CreateContext();
        var service = ProviderMatchTestServices.Create(db, [_adapter]);
        var row = (await service.GetAsync(1, default)).Value.Single();
        var linked = (await service.SetAsync(1, "test", "42", RevisionPrecondition.For(row.Revision.ToString()), default)).Value;
        db.ChangeTracker.Clear();
        row = (await service.GetAsync(1, default)).Value.Single();
        // The write answers the match as it wrote it: the revision a read then returns.
        (linked.Revision, linked.State, linked.ExternalId).ShouldBe((row.Revision, "Confirmed", "42"));
        row.PendingJobId.ShouldBeNull();
        var confirmed = (await service.ConfirmAsync(1, "test", RevisionPrecondition.For(row.Revision.ToString()), default)).Value;
        var pending = await db.Set<EnrichmentJobEntity>().SingleAsync();
        pending.ArtworkOnly.ShouldBeFalse();
        (linked.PendingJobId, confirmed.PendingJobId).ShouldBe((pending.Id, pending.Id));
        (await db.JobDispatches.AnyAsync(x => x.JobId == pending.Id)).ShouldBeTrue();
        await db.Set<EnrichmentJobEntity>().Where(x => x.Id == pending.Id)
            .ExecuteUpdateAsync(x => x.SetProperty(j => j.Phase, "Enriching"));
        db.ChangeTracker.Clear();
        row = (await service.GetAsync(1, default)).Value.Single();
        var relinked = (await service.SetAsync(1, "test", "43", RevisionPrecondition.For(row.Revision.ToString()), default)).Value;
        (await db.Set<EnrichmentJobEntity>().CountAsync()).ShouldBe(2);
        (await db.JobDispatches.CountAsync()).ShouldBe(2);
        relinked.PendingJobId.ShouldNotBe(pending.Id);
        (await db.Set<EnrichmentJobEntity>().AnyAsync(x => x.Id == relinked.PendingJobId)).ShouldBeTrue();
    }

    [Fact]
    public async Task ArtworkCompletion_AcceptanceIsIdempotentAndDurablyDispatched()
    {
        await Seed();
        await using var db = _database.CreateContext();
        var actor = Guid.NewGuid();
        db.Users.Add(new Romd.Persistence.Identity.RomdUser { Id = actor, UserName = "curator", CreatedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();
        var store = new Romd.Persistence.Artwork.ArtworkCompletionStore(db, clock: TimeProvider.System);
        var preview = await store.PreviewAsync([1], default);
        preview.Value.Roles.Count.ShouldBe(4);
        preview.Value.Roles.All(x => x.Count == 1).ShouldBeTrue();
        var id = Guid.NewGuid();
        var first = await store.AcceptCompletionAsync(db, id, [1], actor, default);
        first.IsError.ShouldBeFalse();
        db.ChangeTracker.Clear();
        (await store.GetAsync((await store.AcceptCompletionAsync(db, id, [1], actor, default)).Value, default)).Value.JobId.ShouldBe(id);
        (await store.GetAsync((await store.AcceptCompletionAsync(db, Guid.NewGuid(), [1], actor, default)).Value, default)).Value.JobId.ShouldBe(id);
        (await db.Set<ArtworkCompletionJobEntity>().CountAsync()).ShouldBe(1);
        (await db.JobDispatches.SingleAsync()).JobId.ShouldBe(id);
    }

    [Fact]
    public async Task Completion_AllTrackedSnapshotsBeyondOnePageAndExcludesLaterTracking()
    {
        await Seed();
        await using var db = _database.CreateContext();
        var actor = Guid.NewGuid();
        db.Users.Add(new Romd.Persistence.Identity.RomdUser { Id = actor, UserName = "completion", CreatedAt = DateTimeOffset.UtcNow });
        for (var id = 2; id <= 252; id++)
            db.Titles.Add(new TitleEntity { Id = id, PlatformId = 1, Name = $"Game {id}", NormalizedName = $"game {id}", EnrichmentStatus = "None", FieldProvenanceJson = "{}", FieldSourceOverridesJson = "{}", ScreenshotPrefsJson = "{}", CreatedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();
        for (var id = 1; id <= 251; id++)
            db.TrackedTitles.Add(new TrackedTitleEntity { TitleId = id, CreatedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var store = new Romd.Persistence.Artwork.ArtworkCompletionStore(db, clock: TimeProvider.System);
        var request = new Romd.Contracts.Management.Artwork.ArtworkCompletionRequest(Guid.NewGuid(), Scope: "AllTracked");
        (await store.PreviewScopeAsync(request, default)).Value.Titles.ShouldBe(251);
        var run = (await store.GetAsync((await store.AcceptCompletionAsync(db, request, actor, default)).Value, default)).Value;
        run.Total.ShouldBe(251);
        db.TrackedTitles.Add(new TrackedTitleEntity { TitleId = 252, CreatedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        (await db.Set<ArtworkCompletionItemEntity>().CountAsync(x => x.JobId == run.JobId)).ShouldBe(251);
        (await db.Set<ArtworkCompletionItemEntity>().AllAsync(x => x.RequireTracked)).ShouldBeTrue();
        var first = (await store.ItemsAsync(run.JobId, 0, null, default)).Value;
        first.Items.Count.ShouldBe(50);
        first.NextCursor.ShouldNotBeNull();
        (await store.GetAsync((await store.AcceptCompletionAsync(db, request, actor, default)).Value, default)).Value.Total.ShouldBe(251);
        (await store.RecentAsync(default)).Single().JobId.ShouldBe(run.JobId);
    }

    [Fact]
    public async Task Identity_ProviderDatesCannotAuthorizeMatch_AndOldScoresAreNotTrusted()
    {
        await Seed();
        await using var db = _database.CreateContext();
        await db.Titles.ExecuteUpdateAsync(x => x.SetProperty(t => t.ReleaseDate, new DateOnly(1994, 1, 1)));
        db.TitleExternalIds.Add(TitleExternalIdEntity.FromDomain(Romd.Domain.Catalog.TitleExternalId.CreateNew(1, "test", "42", .99f, createdAt: DateTimeOffset.UnixEpoch)));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var evidence = Substitute.For<Romd.Admin.Application.Titles.Enrichment.ITitleEnrichmentEvidenceReader>();
        evidence.ReadAsync(1, default).ReturnsForAnyArgs(Array.Empty<Romd.Admin.Application.Titles.Enrichment.TitleEnrichmentEvidence>());
        var store = new ProviderIdentityStore(db, evidence, TimeProvider.System);
        _adapter.SearchAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(ErrorOrFactory.From(new ProviderGameSearchResultDto([new("42", "Game", "https://example.com/42", Year: 1994)], ProviderGameSearchCompleteness.Complete, 500)));
        var identity = new ProviderIdentityService(store, [_adapter], [], TimeProvider.System, new EfUnitOfWork(db), NullLogger<ProviderIdentityService>.Instance);
        (await identity.ResolveTrustedAsync(1, "test", default)).ShouldBeNull();
        var snapshot = (await store.ReadAsync(1, default))!;
        snapshot.Facts.Years.ShouldBeEmpty();
        snapshot.Links.Single().Decision!.State.ShouldBe("NeedsReview");
        await identity.DiscoverAsync(1, null, default);
        await _adapter.Received(1).SearchAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Completion_LedgerIsIdempotent_ConfirmationResumesOnce_AndRetryPreservesTracking()
    {
        await Seed();
        await using var db = _database.CreateContext();
        var actor = Guid.NewGuid();
        db.Users.Add(new Romd.Persistence.Identity.RomdUser { Id = actor, UserName = "curator", CreatedAt = DateTimeOffset.UtcNow });
        db.TrackedTitles.Add(new TrackedTitleEntity { TitleId = 1, CreatedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var store = new Romd.Persistence.Artwork.ArtworkCompletionStore(db, clock: TimeProvider.System);
        var jobId = (await store.GetAsync((await store.AcceptCompletionAsync(db, new(Guid.NewGuid(), Scope: "AllTracked"), actor, default)).Value, default)).Value.JobId;
        var context = new Romd.Admin.Application.Ingestion.Jobs.JobContext(null, _ => Task.CompletedTask, default,
            ownedMutationDelegate: async (mutation, ct) =>
            {
                await using var tx = await db.Database.BeginTransactionAsync(ct);
                await mutation(ct);
                await db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
                db.ChangeTracker.Clear();
            });
        var outcomes = Enum.GetValues<Romd.Domain.Catalog.ArtworkRole>().ToDictionary(x => x, _ => Romd.Domain.Catalog.ArtworkCompletionOutcome.NeedsMatch);
        await store.CompleteAsync(jobId, 1, outcomes, Romd.Domain.Catalog.MetadataExecutionOutcome.NotAttempted, context, default);
        await store.CompleteAsync(jobId, 1, outcomes, Romd.Domain.Catalog.MetadataExecutionOutcome.Failed, context, default);
        // The second completion is a no-op: a finished item's recorded history is immutable.
        (await db.Set<ArtworkCompletionItemEntity>().SingleAsync(x => x.JobId == jobId)).MetadataOutcome.ShouldBe(Romd.Domain.Catalog.MetadataExecutionOutcome.NotAttempted);
        (await store.CompletedCountAsync(jobId, default)).ShouldBe(1);
        (await db.Set<ArtworkCompletionResultEntity>().CountAsync()).ShouldBe(4);
        (await store.NextAsync(jobId, default)).ShouldBeNull();
        await db.Set<ArtworkCompletionJobEntity>().ExecuteUpdateAsync(x => x.SetProperty(j => j.Phase, "Completed"));
        var matches = ProviderMatchTestServices.Create(db, [_adapter], completion: store);
        var revision = (await matches.GetAsync(1, default)).Value.Single().Revision;
        (await matches.SetAsync(1, "test", "42", RevisionPrecondition.For(revision.ToString()), default)).IsError.ShouldBeFalse();
        db.ChangeTracker.Clear();
        var parent = await db.Set<ArtworkCompletionItemEntity>().SingleAsync(x => x.JobId == jobId);
        parent.FollowUpJobId.ShouldNotBeNull();
        (await db.Set<ArtworkCompletionItemEntity>().SingleAsync(x => x.JobId == parent.FollowUpJobId)).RequireTracked.ShouldBeTrue();
        (await db.JobDispatches.CountAsync()).ShouldBe(2);
        (await store.GetAsync(jobId, default)).Value.Working.ShouldBe(1);
        (await store.ItemsAsync(jobId, 0, "Failed", default)).Value.Items.ShouldBeEmpty();
        (await store.ItemsAsync(jobId, 0, "Unfinished", default)).Value.Items.ShouldBeEmpty();
        (await store.PreviewScopeAsync(new(Guid.NewGuid(), Scope: "Retry", RetryJobId: jobId, RetryStatus: "Failed"), default)).Value.Titles.ShouldBe(0);
        (await store.PreviewScopeAsync(new(Guid.NewGuid(), Scope: "Retry", RetryJobId: jobId), default)).IsError.ShouldBeTrue();
        await store.CompleteAsync(parent.FollowUpJobId!.Value, 1, outcomes.ToDictionary(x => x.Key, _ => Romd.Domain.Catalog.ArtworkCompletionOutcome.Failed),
            Romd.Domain.Catalog.MetadataExecutionOutcome.NotAttempted, context, default);
        var retry = (await store.GetAsync((await store.AcceptCompletionAsync(db, new(Guid.NewGuid(), Scope: "Retry", RetryJobId: jobId, RetryStatus: "Failed"), actor, default)).Value, default)).Value;
        retry.Total.ShouldBe(1);
        (await db.Set<ArtworkCompletionItemEntity>().SingleAsync(x => x.JobId == retry.JobId)).RequireTracked.ShouldBeTrue();
        await db.TrackedTitles.ExecuteDeleteAsync();
        (await store.IsTrackedAsync(1, default)).ShouldBeFalse();
    }

    [Fact]
    public async Task Completion_ProjectsContinuationAndCurrentReview_WithoutRewritingHistory()
    {
        await Seed();
        await using var db = _database.CreateContext();
        var actor = Guid.NewGuid();
        db.Users.Add(new Romd.Persistence.Identity.RomdUser { Id = actor, UserName = "reviewer", CreatedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        await db.Set<ArtworkEnrichmentSettingsEntity>().ExecuteUpdateAsync(x => x.SetProperty(s => s.FillPosters, true));
        var store = new Romd.Persistence.Artwork.ArtworkCompletionStore(db, clock: TimeProvider.System, identityAdapters: [_adapter]);
        var parent = (await store.GetAsync((await store.AcceptCompletionAsync(db, new(Guid.NewGuid(), [Romd.Application.Common.Ids.IdCoder.Encode(1)], Mode: "Enrich"), actor, default)).Value, default)).Value.JobId;
        var context = new Romd.Admin.Application.Ingestion.Jobs.JobContext(null, _ => Task.CompletedTask, default,
            ownedMutationDelegate: async (mutation, ct) =>
            {
                await using var tx = await db.Database.BeginTransactionAsync(ct);
                await mutation(ct);
                await db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
                db.ChangeTracker.Clear();
            });
        var blocked = Enum.GetValues<Romd.Domain.Catalog.ArtworkRole>().ToDictionary(x => x, _ => Romd.Domain.Catalog.ArtworkCompletionOutcome.NeedsMatch);
        await store.CompleteAsync(parent, 1, blocked, Romd.Domain.Catalog.MetadataExecutionOutcome.Succeeded, context, default);
        var completed = (await store.GetAsync(parent, default)).Value;
        (completed.Failed, completed.Unfinished, completed.Working).ShouldBe((0, 0, 0));
        await using (var tx = await db.Database.BeginTransactionAsync())
        {
            (await store.ResumeTitleStagedAsync(1, default)).ShouldNotBeNull();
            await db.SaveChangesAsync();
            await tx.CommitAsync();
            db.ChangeTracker.Clear();
        }
        var child = (await db.Set<ArtworkCompletionItemEntity>().SingleAsync(x => x.JobId == parent)).FollowUpJobId!.Value;
        (await store.NextAsync(child, default))!.Mode.ShouldBe("Enrich");
        (await store.GetAsync(parent, default)).Value.Working.ShouldBe(1);
        (await store.ItemsAsync(parent, 0, "Working", default)).Value.Items.Single().Completion.ShouldBe(Romd.Contracts.Management.Artwork.ArtworkCompletionState.Working);
        (await store.ItemsAsync(parent, 0, "Failed", default)).Value.Items.ShouldBeEmpty();
        (await store.RecentAsync(default)).Select(x => x.JobId).ShouldBe([parent]);
        await store.CompleteAsync(child, 1, blocked.ToDictionary(x => x.Key, _ => Romd.Domain.Catalog.ArtworkCompletionOutcome.NeedsReview),
            Romd.Domain.Catalog.MetadataExecutionOutcome.NotAttempted, context, default);
        await db.Titles.Where(x => x.Id == 1).ExecuteUpdateAsync(x => x.SetProperty(t => t.EnrichmentStatus, "Completed"));
        foreach (var role in Enum.GetValues<Romd.Domain.Catalog.ArtworkRole>())
            db.ArtworkSelections.Add(new ArtworkSelectionEntity { TitleId = 1, Role = role, Mode = Romd.Domain.Catalog.ArtworkSelectionMode.Empty });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var resolved = (await store.GetAsync(parent, default)).Value;
        (resolved.Failed, resolved.Unfinished, resolved.Working).ShouldBe((0, 0, 0));
        // Recorded results are the continuation leaf's raw outcomes; curator selections never rewrite them.
        var item = (await store.ItemsAsync(parent, 0, null, default)).Value.Items.Single();
        item.Roles.ShouldAllBe(x => x.Status == "NeedsReview");
        item.MetadataOutcome.ShouldBe(Romd.Contracts.Management.Artwork.ArtworkCompletionMetadataOutcome.NotAttempted);
        (await db.Set<ArtworkCompletionResultEntity>().Where(x => x.JobId == parent).ToListAsync()).ShouldAllBe(x => x.Outcome == Romd.Domain.Catalog.ArtworkCompletionOutcome.NeedsMatch);
        // Review follows the title's current assessment: a trusted poster search that found nothing needs a decision now.
        await db.ArtworkSelections.Where(x => x.Role == Romd.Domain.Catalog.ArtworkRole.Poster).ExecuteDeleteAsync();
        db.TitleExternalIds.Add(new TitleExternalIdEntity { TitleId = 1, Provider = "test", ExternalId = "42", IsConfirmed = true });
        db.Set<ArtworkAcquisitionEntity>().Add(new ArtworkAcquisitionEntity { TitleId = 1, Role = Romd.Domain.Catalog.ArtworkRole.Poster, Status = "Unavailable", UpdatedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var current = (await new Romd.Persistence.Enrichment.EnrichmentWorkbench(db, TimeProvider.System, new Romd.Persistence.Repositories.ArtworkReader(db), adapters: [_adapter]).GetAsync(1, default)).Value;
        current.NeedsReview.ShouldBeTrue();
        (await store.GetAsync(parent, default)).Value.Review.ShouldBe(1);
        var review = (await store.ItemsAsync(parent, 0, "Review", default)).Value.Items.Single();
        review.Now.ShouldNotBeNull().NeedsReview.ShouldBeTrue();
        review.Roles.ShouldAllBe(x => x.Status == "NeedsReview");
    }

    [Fact]
    public async Task Identity_LocalEvidenceChangeInvalidatesAutomaticTrust_ButConfirmationSurvives()
    {
        await Seed();
        await using var db = _database.CreateContext();
        var evidence = Substitute.For<Romd.Admin.Application.Titles.Enrichment.ITitleEnrichmentEvidenceReader>();
        evidence.ReadAsync(1, default).ReturnsForAnyArgs(Array.Empty<Romd.Admin.Application.Titles.Enrichment.TitleEnrichmentEvidence>());
        var store = new ProviderIdentityStore(db, evidence, TimeProvider.System);
        _adapter.SearchAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(ErrorOrFactory.From(new ProviderGameSearchResultDto([new("42", "Game", "https://example.com/42", Platforms: ["Test"])], ProviderGameSearchCompleteness.Complete, 500)));
        var identities = new ProviderIdentityService(store, [_adapter], [], TimeProvider.System, new EfUnitOfWork(db), NullLogger<ProviderIdentityService>.Instance);
        (await identities.ResolveTrustedAsync(1, "test", default)).ShouldBe("42");
        await db.Titles.ExecuteUpdateAsync(x => x.SetProperty(t => t.Name, "Game Remastered"));
        (await identities.GetTrustedAsync(1, "test", default)).ShouldBeNull();
        var matches = ProviderMatchTestServices.Create(db, [_adapter], identities, store);
        var revision = (await matches.GetAsync(1, default)).Value.Single().Revision;
        (await matches.ConfirmAsync(1, "test", RevisionPrecondition.For(revision.ToString()), default)).IsError.ShouldBeFalse();
        db.ChangeTracker.Clear();
        (await identities.GetTrustedAsync(1, "test", default)).ShouldBe("42");
        await identities.DiscoverAsync(1, null, default);
        await _adapter.Received(1).SearchAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    private async Task Seed()
    {
        await using var db = _database.CreateContext();
        db.Platforms.Add(new PlatformEntity { Id = 1, Name = "Test", Ownership = Romd.Domain.ReferenceData.ReferenceOwnership.Romd, BuiltInVersion = 1, BaseName = "Test", BaseCompactLabel = "Test", CanonicalKey = "test", ShortName = "test", Manufacturer = "Test", CreatedAt = DateTimeOffset.UtcNow });
        db.Titles.Add(new TitleEntity
        {
            Id = 1,
            PlatformId = 1,
            Name = "Game",
            NormalizedName = "game",
            EnrichmentStatus = "None",
            FieldProvenanceJson = "{}",
            FieldSourceOverridesJson = "{}",
            ScreenshotPrefsJson = "{}",
            CreatedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Discovery_PersistsStrongEvidenceButNeverAuthorizesSameNameAlternatives(bool ambiguous)
    {
        await Seed();
        await using var db = _database.CreateContext();
        await db.Titles.Where(x => x.Id == 1).ExecuteUpdateAsync(x => x.SetProperty(t => t.ReleaseDate, new DateOnly(1994, 1, 1)));
        IReadOnlyList<ProviderGameDto> games = ambiguous
            ? [new("42", "Game", "https://example.com/42", Year: 1994, Platforms: ["Test"]), new("43", "Game", "https://example.com/43", Year: 2001, Platforms: ["Test"])]
            : [new("42", "Game", "https://example.com/42", Year: 1994, Platforms: ["Test"])];
        _adapter.SearchAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(ErrorOrFactory.From(new ProviderGameSearchResultDto(games, ProviderGameSearchCompleteness.Complete, 500)));
        var evidence = Substitute.For<Romd.Admin.Application.Titles.Enrichment.ITitleEnrichmentEvidenceReader>();
        evidence.ReadAsync(1, default).ReturnsForAnyArgs(Array.Empty<Romd.Admin.Application.Titles.Enrichment.TitleEnrichmentEvidence>());
        var store = new ProviderIdentityStore(db, evidence, TimeProvider.System);
        var identity = new ProviderIdentityService(store, [_adapter], [], TimeProvider.System, new EfUnitOfWork(db), NullLogger<ProviderIdentityService>.Instance);
        var service = ProviderMatchTestServices.Create(db, [_adapter], identity, store);
        var context = new Romd.Admin.Application.Ingestion.Jobs.JobContext(null, _ => Task.CompletedTask, default,
            ownedMutationDelegate: async (mutation, ct) =>
            {
                await using var transaction = await db.Database.BeginTransactionAsync(ct);
                await mutation(ct);
                await db.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);
            });
        await service.DiscoverAsync(1, context, default);
        db.ChangeTracker.Clear();
        var match = (await service.GetAsync(1, default)).Value.Single();
        match.State.ShouldBe(ambiguous ? "NeedsReview" : "AutoMatched");
        match.MatcherVersion.ShouldBe(Romd.Domain.Catalog.ProviderIdentityPolicy.Version);
        match.Evidence.ShouldNotBeNullOrEmpty();
        var revision = await service.GetLinkRevisionAsync(1, "test", "42", default);
        if (ambiguous)
            revision.ShouldBeNull();
        else
        {
            revision.ShouldNotBeNull();
            (await db.TitleExternalIds.SingleAsync()).IsConfirmed.ShouldBeFalse();
            // Evidence for one game must never authorize a different persisted association.
            await db.TitleExternalIds.ExecuteUpdateAsync(x => x.SetProperty(t => t.ExternalId, "43"));
            (await service.GetLinkRevisionAsync(1, "test", "43", default)).ShouldBeNull();
        }
    }

    public void Dispose() => _database.Dispose();
}
