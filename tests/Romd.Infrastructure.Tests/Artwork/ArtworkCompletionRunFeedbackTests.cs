using System.Data;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Romd.Admin.Application.Artwork;
using Romd.Admin.Application.Configuration;
using Romd.Admin.Application.Dashboard;
using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Admin.Application.Libraries;
using Romd.Admin.Application.MetadataProviders;
using Romd.Admin.Application.Storage.Files;
using Romd.Admin.Application.Titles.Enrichment;
using Romd.Application.Common.Configuration;
using Romd.Application.Common.Ids;
using Romd.Application.Common.Security;
using Romd.Contracts.Management.Artwork;
using Romd.Contracts.Management.MetadataProviders;
using Romd.Domain.Catalog;
using Romd.Domain.Hashing;
using Romd.Domain.Jobs;
using Romd.Domain.Source.Platform;
using Romd.Infrastructure.Jobs;
using Romd.Infrastructure.Jobs.Executors;
using Romd.Infrastructure.Tests.Helpers;
using Romd.Persistence;
using Romd.Persistence.Artwork;
using Romd.Persistence.Entities;
using Romd.Persistence.Enrichment;
using Romd.Persistence.Repositories;
using Romd.PostgreSql.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Artwork;

/// <summary>
///     Run feedback keeps recorded execution (Failed, Unfinished, Working) apart from the current assessment
///     (Review). Execution cases run through the production runner, fence and owned mutations.
/// </summary>
public sealed class ArtworkCompletionRunFeedbackTests : IDisposable
{
    private const int Review = 1, FailedRole = 2, FailedMetadata = 3, Cancelled = 4, Deferred = 5, Working = 6,
        LogoUnavailable = 7, Legacy = 8, ReviewAndFailed = 9, Removed = 10;
    private static readonly int[] Extra = Enumerable.Range(101, 60).ToArray();
    private static readonly JobExecutionClaimOptions ClaimOptions = new();
    private static readonly (ArtworkRole Role, string Status)[] AllUpdated = Enum.GetValues<ArtworkRole>().Select(role => (role, "Updated")).ToArray();

    private readonly PostgreSqlTestDatabase database = PostgreSqlTestDatabase.Create();
    private readonly Guid actor = Guid.NewGuid();
    // Millisecond-aligned: persisted timestamps round-trip at unix-millisecond precision.
    private readonly ManualTimeProvider clock = new(DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
    private readonly List<(string Phase, DateTimeOffset? NextAttemptAt)> pushes = [];
    private readonly IProviderIdentityAdapter[] adapters = [Provider("igdb"), Provider("steamgriddb")];
    private int assetId;

    public void Dispose() => database.Dispose();

    [Fact]
    public async Task Counts_EveryRunFilter_EqualItsListUnderOneSnapshot_AndLogoUnavailableIsInNone()
    {
        await SeedAsync([.. Enumerable.Range(1, 10), .. Extra]);
        var run = await SeedMixedRunAsync();
        await using var db = database.CreateContext();
        var store = new ArtworkCompletionStore(db, clock: TimeProvider.System, identityAdapters: adapters);
        var workbench = new EnrichmentWorkbench(db, TimeProvider.System, new ArtworkReader(db), adapters: adapters);
        var needsReviewNow = new List<int>();
        foreach (var id in Enumerable.Range(1, 9).Concat(Extra))
            if ((await workbench.GetAsync(id, default)).Value.NeedsReview)
                needsReviewNow.Add(id);
        // A poster search that found nothing needs a decision; a logo that found nothing falls back to text.
        needsReviewNow.ShouldBe([Review, ReviewAndFailed]);

        await using (var snapshot = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead))
        {
            var job = (await store.GetAsync(run, default)).Value;
            await ChangeTitlesAndHistoryConcurrentlyAsync(run);
            var lists = new Dictionary<string, int[]>();
            foreach (var status in new[] { "Review", "Failed", "Unfinished", "Working" })
                lists[status] = (await PageAllAsync(store, run, status)).Select(x => Sqid.Parse(x.TitleId, null).Value).ToArray();

            (job.Review, job.Failed, job.Unfinished, job.Working)
                .ShouldBe((lists["Review"].Length, lists["Failed"].Length, lists["Unfinished"].Length, lists["Working"].Length));
            lists["Review"].ShouldBe([Review, ReviewAndFailed]);
            lists["Failed"].ShouldBe([FailedRole, FailedMetadata, ReviewAndFailed, .. Extra]);
            lists["Unfinished"].ShouldBe([Cancelled, Deferred]);
            lists["Working"].ShouldBe([Working]);
            foreach (var list in lists.Values)
                list.ShouldNotContain(LogoUnavailable);
            foreach (var list in lists.Values)
                list.ShouldNotContain(Legacy);
            foreach (var list in lists.Values)
                list.ShouldNotContain(Removed);
            var all = await PageAllAsync(store, run, null);
            all.Count.ShouldBe(job.Total);
            // Recorded history stays visible beside the current flags.
            all.Single(x => x.TitleId == IdCoder.Encode(LogoUnavailable)).Roles.ShouldContain(new ArtworkRoleCountDto("Logo", "Unavailable", 1));
            all.Single(x => x.TitleId == IdCoder.Encode(Legacy)).MetadataOutcome.ShouldBeNull();
            all.Single(x => x.TitleId == IdCoder.Encode(FailedMetadata)).MetadataOutcome.ShouldBe(ArtworkCompletionMetadataOutcome.Failed);
            all.Single(x => x.TitleId == IdCoder.Encode(Removed)).Now.ShouldBeNull();
            var working = all.Single(x => x.TitleId == IdCoder.Encode(Working));
            working.Completion.ShouldBe(ArtworkCompletionState.Working);
            working.Roles.ShouldContain(new ArtworkRoleCountDto("Poster", "Failed", 1));
            // Retry membership is exactly the recorded lists: removed titles and Working items are never retried.
            (await store.PreviewScopeAsync(new(Guid.NewGuid(), Scope: "Retry", RetryJobId: run, RetryStatus: "Failed"), default)).Value.Titles.ShouldBe(job.Failed);
            (await store.PreviewScopeAsync(new(Guid.NewGuid(), Scope: "Retry", RetryJobId: run, RetryStatus: "Unfinished"), default)).Value.Titles.ShouldBe(job.Unfinished);
            await snapshot.RollbackAsync();
        }

        var live = (await store.GetAsync(run, default)).Value;
        (live.Review, live.Unfinished).ShouldBe((3, 1));
    }

    [Fact]
    public async Task RemovedTitles_KeepHistory_AreNeitherFailedNorUnfinished_AndRetryingOnlyThemIsRejected()
    {
        await SeedAsync([1, 2, 3]);
        await using var db = database.CreateContext();
        var run = Guid.NewGuid();
        AddJob(db, run, "Cancelled", total: 3, processed: 2, Scope());
        db.AddRange(
            new ArtworkCompletionItemEntity
            {
                JobId = run,
                TitleId = 1,
                TitleName = "Failed then removed",
                CompletedAt = DateTimeOffset.UtcNow,
                MetadataOutcome = MetadataExecutionOutcome.Failed
            },
            new ArtworkCompletionItemEntity { JobId = run, TitleId = 2, TitleName = "Unfinished then removed" },
            new ArtworkCompletionItemEntity
            {
                JobId = run,
                TitleId = 3,
                TitleName = "Title 3",
                CompletedAt = DateTimeOffset.UtcNow,
                MetadataOutcome = MetadataExecutionOutcome.Succeeded
            });
        AddResults(db, run, 1, (ArtworkRole.Poster, "Failed"));
        AddResults(db, run, 3, AllUpdated);
        await db.SaveChangesAsync();
        await db.Titles.Where(x => x.Id != 3).ExecuteDeleteAsync();
        db.ChangeTracker.Clear();
        var store = new ArtworkCompletionStore(db, clock: TimeProvider.System);
        var service = new ArtworkCompletionService(store);

        var job = (await store.GetAsync(run, default)).Value;
        (job.Failed, job.Unfinished).ShouldBe((0, 0));
        (await store.ItemsAsync(run, 0, "Failed", default)).Value.Items.ShouldBeEmpty();
        (await store.ItemsAsync(run, 0, "Unfinished", default)).Value.Items.ShouldBeEmpty();
        var history = (await store.ItemsAsync(run, 0, null, default)).Value.Items;
        history.Select(x => x.TitleName).ShouldBe(["Failed then removed", "Unfinished then removed", "Title 3"]);
        history.Take(2).ShouldAllBe(x => x.Now == null);
        (history[0].MetadataOutcome, history[0].Roles.Single().Status).ShouldBe((ArtworkCompletionMetadataOutcome.Failed, "Failed"));
        history[1].Completion.ShouldBe(ArtworkCompletionState.Unfinished);

        foreach (var status in new[] { "Failed", "Unfinished" })
            (await store.AcceptCompletionAsync(db, new(Guid.NewGuid(), Scope: "Retry", RetryJobId: run, RetryStatus: status), actor, default))
                .FirstError.Code.ShouldBe(ArtworkCurationErrors.InvalidRequest().Code, status);
        (await db.Set<ArtworkCompletionJobEntity>().CountAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task Execution_CancelledAfterMetadataCommit_IsUnfinished_PreservesWrites_AndRetryKeepsHistory()
    {
        await SeedAsync([1, 2]);
        var run = await AcceptAsync([1, 2]);

        await RunAsync(run, _ => OrchestrationResult.Found(1), async (title, context, acquisition, ct) =>
        {
            if (title.Id == 1)
            { await RecordAsync(acquisition, context, 1, ct, AllUpdated); return; }
            await RecordAsync(acquisition, context, 2, ct, (ArtworkRole.Poster, "Updated"));
            throw new OperationCanceledException();
        });

        await using var db = database.CreateContext();
        var store = new ArtworkCompletionStore(db, clock: TimeProvider.System);
        var cancelled = (await store.GetAsync(run, default)).Value;
        (cancelled.Phase, cancelled.Processed, cancelled.Unfinished, cancelled.Failed, cancelled.Working).ShouldBe(("Cancelled", 1, 1, 0, 0));
        var unfinished = (await store.ItemsAsync(run, 0, "Unfinished", default)).Value.Items.ShouldHaveSingleItem();
        unfinished.TitleId.ShouldBe(IdCoder.Encode(2));
        unfinished.Completion.ShouldBe(ArtworkCompletionState.Unfinished);
        unfinished.MetadataOutcome.ShouldBe(ArtworkCompletionMetadataOutcome.Succeeded);
        unfinished.Roles.ShouldBe([new ArtworkRoleCountDto("Poster", "Updated", 1)]);
        (await db.Titles.SingleAsync(x => x.Id == 2)).EnrichmentStatus.ShouldBe("Completed");

        var retry = (await store.GetAsync((await store.AcceptCompletionAsync(db, new(Guid.NewGuid(), Scope: "Retry", RetryJobId: run, RetryStatus: "Unfinished"), actor, default)).Value, default)).Value;
        (retry.Total, retry.Mode).ShouldBe((1, "Enrich"));
        await RunAsync(retry.JobId, _ => OrchestrationResult.Found(1), (title, context, acquisition, ct) => RecordAsync(acquisition, context, title.Id, ct, AllUpdated));

        var original = (await store.GetAsync(run, default)).Value;
        (original.Phase, original.Processed, original.Unfinished, original.Failed).ShouldBe(("Cancelled", 1, 0, 0));
        var continued = (await store.ItemsAsync(run, 1, null, default)).Value.Items.ShouldHaveSingleItem();
        (continued.Completion, continued.FollowUpJobId, continued.MetadataOutcome).ShouldBe((ArtworkCompletionState.Done, retry.JobId, ArtworkCompletionMetadataOutcome.Succeeded));
        continued.Roles.Count.ShouldBe(4);
        (await store.GetAsync(retry.JobId, default)).Value.Phase.ShouldBe("Completed");
    }

    [Fact]
    public async Task Execution_DeferralAndResume_RaisesRecordedMetadata_AndPublishesNextAttempt()
    {
        await SeedAsync([1, 2]);
        var run = await AcceptAsync([1, 2]);
        var attempt = 1;
        DateTimeOffset? deferredUntil = null;
        OrchestrationResult Metadata(Title title) => (attempt, title.Id) is (1, 1) or (3, 2) ? OrchestrationResult.Failed : OrchestrationResult.Found(1);
        Task Artwork(Title title, JobContext context, ArtworkAcquisitionStore acquisition, CancellationToken ct)
        {
            if ((attempt, title.Id) is not ((1, 1) or (2, 2)))
                return RecordAsync(acquisition, context, title.Id, ct, AllUpdated);
            deferredUntil = clock.GetUtcNow().AddMinutes(2);
            context.RequestDeferral(deferredUntil.Value);
            return Task.CompletedTask;
        }
        await using var db = database.CreateContext();
        var store = new ArtworkCompletionStore(db, clock: TimeProvider.System);

        await RunAsync(run, Metadata, Artwork);

        // The deferred job is published with its schedule; earlier pushes had none.
        pushes[^1].ShouldBe(("Enriching", deferredUntil));
        pushes.SkipLast(1).ShouldAllBe(push => push.NextAttemptAt == null);
        var paused = (await store.GetAsync(run, default)).Value;
        (paused.Phase, paused.NextAttemptAt, paused.Working, paused.Failed, paused.Processed).ShouldBe(("Enriching", deferredUntil, 2, 0, 0));
        // A Working item's recorded failure is provisional: it is neither Failed nor retried.
        (await Outcome(run, 1)).ShouldBe(MetadataExecutionOutcome.Failed);
        (await store.ItemsAsync(run, 0, "Failed", default)).Value.Items.ShouldBeEmpty();
        (await store.PreviewScopeAsync(new(Guid.NewGuid(), Scope: "Retry", RetryJobId: run, RetryStatus: "Failed"), default)).Value.Titles.ShouldBe(0);

        clock.Advance(TimeSpan.FromMinutes(3));
        attempt = 2;
        await RunAsync(run, Metadata, Artwork);
        (await Outcome(run, 1)).ShouldBe(MetadataExecutionOutcome.Succeeded);
        (await store.GetAsync(run, default)).Value.NextAttemptAt.ShouldBe(deferredUntil);

        clock.Advance(TimeSpan.FromMinutes(3));
        attempt = 3;
        await RunAsync(run, Metadata, Artwork);

        // Succeeded is sticky within an item; the title's current status follows the latest attempt.
        (await Outcome(run, 2)).ShouldBe(MetadataExecutionOutcome.Succeeded);
        (await db.Titles.SingleAsync(x => x.Id == 2)).EnrichmentStatus.ShouldBe("Failed");
        var finished = (await store.GetAsync(run, default)).Value;
        (finished.Phase, finished.Processed, finished.Failed, finished.Working).ShouldBe(("Completed", 2, 0, 0));
        finished.NextAttemptAt.ShouldBeNull();
    }

    [Fact]
    public async Task Retry_FailedMetadataStep_NewLeafIsAuthoritative_AndOriginalHistoryStays()
    {
        await SeedAsync([1]);
        var run = await AcceptAsync([1]);

        await RunAsync(run, _ => throw new InvalidOperationException("No enabled, configured metadata providers are available."),
            (_, _, _, _) => Task.CompletedTask);

        await using var db = database.CreateContext();
        var store = new ArtworkCompletionStore(db, clock: TimeProvider.System);
        var failed = (await store.GetAsync(run, default)).Value;
        (failed.Phase, failed.Processed, failed.Failed).ShouldBe(("CompletedWithErrors", 1, 1));
        var item = (await store.ItemsAsync(run, 0, "Failed", default)).Value.Items.ShouldHaveSingleItem();
        item.MetadataOutcome.ShouldBe(ArtworkCompletionMetadataOutcome.Failed);
        item.Roles.ShouldAllBe(x => x.Status == "Failed");

        var retry = (await store.GetAsync((await store.AcceptCompletionAsync(db, new(Guid.NewGuid(), Scope: "Retry", RetryJobId: run, RetryStatus: "Failed"), actor, default)).Value, default)).Value;
        await RunAsync(retry.JobId, _ => OrchestrationResult.Found(1), (title, context, acquisition, ct) => RecordAsync(acquisition, context, title.Id, ct, AllUpdated));

        var original = (await store.GetAsync(run, default)).Value;
        (original.Phase, original.Processed, original.Failed).ShouldBe(("CompletedWithErrors", 1, 0));
        var leaf = (await store.ItemsAsync(run, 0, null, default)).Value.Items.ShouldHaveSingleItem();
        (leaf.FollowUpJobId, leaf.MetadataOutcome).ShouldBe((retry.JobId, ArtworkCompletionMetadataOutcome.Succeeded));
        leaf.Roles.ShouldAllBe(x => x.Status == "Updated");
        (await Outcome(run, 1)).ShouldBe(MetadataExecutionOutcome.Failed);
        (await db.Set<ArtworkCompletionResultEntity>().Where(x => x.JobId == run).ToListAsync()).ShouldAllBe(x => x.Outcome == ArtworkCompletionOutcome.Failed);
        (await store.PreviewScopeAsync(new(Guid.NewGuid(), Scope: "Retry", RetryJobId: run, RetryStatus: "Failed"), default)).Value.Titles.ShouldBe(0);
    }

    [Fact]
    public async Task Execution_MetadataOnlyFailure_EndsRunCompletedWithErrors()
    {
        await SeedAsync([1]);
        var run = await AcceptAsync([1]);

        await RunAsync(run, _ => OrchestrationResult.Failed, (title, context, acquisition, ct) => RecordAsync(acquisition, context, title.Id, ct, AllUpdated));

        // The execution line and the recorded Failed count agree when only the metadata step failed.
        await using var db = database.CreateContext();
        var store = new ArtworkCompletionStore(db, clock: TimeProvider.System);
        var job = (await store.GetAsync(run, default)).Value;
        (job.Phase, job.Processed, job.Failed).ShouldBe(("CompletedWithErrors", 1, 1));
        var item = (await store.ItemsAsync(run, 0, "Failed", default)).Value.Items.ShouldHaveSingleItem();
        item.MetadataOutcome.ShouldBe(ArtworkCompletionMetadataOutcome.Failed);
        item.Roles.ShouldAllBe(x => x.Status == "Updated");
    }

    [Fact]
    public async Task QueueScope_ResponseCounts_EvaluateCurrentFactsOnlyAfterAcceptanceLocksAreReleased()
    {
        await SeedAsync([1]);
        var heldDuringFacts = new List<long>();
        // Selected acceptance reads provider availability only for the response counts, right before the facts.
        var provider = Substitute.For<IProviderIdentityAdapter>();
        provider.Id.Returns("igdb");
        provider.GetAvailabilityAsync(Arg.Any<CancellationToken>()).Returns(async _ =>
        {
            await using var observer = database.CreateContext();
            heldDuringFacts.Add(await observer.Database.SqlQueryRaw<long>("""
                SELECT count(*) AS "Value" FROM pg_locks WHERE locktype = 'advisory' AND classid IN (146, 148)
                    AND database = (SELECT oid FROM pg_database WHERE datname = current_database())
                """).SingleAsync());
            return new ProviderAvailability(true, true);
        });
        await using var db = database.CreateContext();
        var store = new ArtworkCompletionStore(db, clock: TimeProvider.System, identityAdapters: [provider]);
        var request = new ArtworkCompletionRequest(Guid.NewGuid(), [IdCoder.Encode(1)], Mode: "Enrich");

        var accepted = (await store.GetAsync((await store.AcceptCompletionAsync(db, request, actor, default)).Value, default)).Value;
        var replayed = (await store.GetAsync((await store.AcceptCompletionAsync(db, request, actor, default)).Value, default)).Value;
        var reused = (await store.GetAsync((await store.AcceptCompletionAsync(db, request with { RequestId = Guid.NewGuid() }, actor, default)).Value, default)).Value;

        // New acceptance, idempotent replay and active-run reuse all count after the locks are released.
        new[] { replayed.JobId, reused.JobId }.ShouldAllBe(id => id == accepted.JobId);
        heldDuringFacts.ShouldBe([0, 0, 0]);
    }

    [Fact]
    public async Task MetadataOutcome_StaleFence_CannotWrite_WhileCurrentOwnerCan()
    {
        await SeedAsync([1]);
        var run = await AcceptAsync([1]);
        await using var claimDb = database.CreateContext();
        var claims = new ClaimedJobRepository<ArtworkCompletionJob, ArtworkCompletionJobRepository>(new(claimDb), claimDb, clock, ClaimOptions);
        var stale = (await claims.TryClaimExecutionAsync(run, "stale")).ShouldNotBeNull();
        clock.Advance(ClaimOptions.LeaseDuration + TimeSpan.FromSeconds(1));
        var current = (await claims.TryClaimExecutionAsync(run, "current")).ShouldNotBeNull();
        await using var db = database.CreateContext();
        var store = new ArtworkCompletionStore(db, clock: TimeProvider.System);
        var pipeline = Pipeline(db, store, _ => OrchestrationResult.Found(1), Substitute.For<IAutomaticArtworkService>());
        JobContext Owned(Guid fence) => new(null, _ => Task.CompletedTask, CancellationToken.None, ownedMutationDelegate: (mutation, ct) =>
            new JobExecutionMutationGuard(db, new EfUnitOfWork(db), clock, ClaimOptions).ExecuteAsync(run, fence, mutation, ct), jobId: run);

        await Should.ThrowAsync<JobExecutionOwnershipLostException>(() => pipeline.EnrichAsync(1, 1, Owned(stale.FenceToken)));
        await Should.ThrowAsync<JobExecutionOwnershipLostException>(() => store.CompleteAsync(run, 1,
            AllUpdated.ToDictionary(x => x.Role, _ => ArtworkCompletionOutcome.Failed), MetadataExecutionOutcome.Failed, Owned(stale.FenceToken), default));

        (await Outcome(run, 1)).ShouldBeNull();
        (await db.Set<ArtworkCompletionItemEntity>().SingleAsync(x => x.JobId == run)).CompletedAt.ShouldBeNull();
        (await db.Titles.SingleAsync(x => x.Id == 1)).EnrichmentStatus.ShouldBe("None");
        await pipeline.EnrichAsync(1, 1, Owned(current.FenceToken));
        (await Outcome(run, 1)).ShouldBe(MetadataExecutionOutcome.Succeeded);
    }

    [Fact]
    public async Task EnrichmentJobExecutor_SharedPipeline_NeverWritesCompletionRows()
    {
        await SeedAsync([1]);
        var run = await AcceptAsync([1]);
        await using var db = database.CreateContext();
        var enrichment = EnrichmentJob.Create("Title 1", 1, 1, clock.GetUtcNow());
        await new EnrichmentJobRepository(db, clock).CommitCreationAsync(db, enrichment);
        db.ChangeTracker.Clear();
        var claim = (await new EnrichmentJobRepository(db, clock, ClaimOptions).TryClaimExecutionAsync(enrichment.Id, "enrichment")).ShouldNotBeNull();
        var context = new JobContext(null, _ => Task.CompletedTask, CancellationToken.None, ownedMutationDelegate: (mutation, ct) =>
            new JobExecutionMutationGuard(db, new EfUnitOfWork(db), clock, ClaimOptions).ExecuteAsync(enrichment.Id, claim.FenceToken, mutation, ct), jobId: enrichment.Id);

        await new EnrichmentJobExecutor(Pipeline(db, new ArtworkCompletionStore(db, clock: TimeProvider.System), _ => OrchestrationResult.Found(1), Substitute.For<IAutomaticArtworkService>()))
            .ExecuteAsync(claim.Job, context);

        (await db.Titles.SingleAsync(x => x.Id == 1)).EnrichmentStatus.ShouldBe("Completed");
        var item = await db.Set<ArtworkCompletionItemEntity>().SingleAsync(x => x.JobId == run);
        item.MetadataOutcome.ShouldBeNull();
        item.CompletedAt.ShouldBeNull();
        (await db.Set<ArtworkCompletionResultEntity>().CountAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task MetadataOutcome_MergeRank_OnlyRaisesUnfinishedItemsOfTheOwningJob()
    {
        await SeedAsync([1, 2, 3]);
        var run = await AcceptAsync([1, 2, 3]);
        await using var db = database.CreateContext();
        var store = new ArtworkCompletionStore(db, clock: TimeProvider.System);
        var owned = new JobContext(null, _ => Task.CompletedTask, CancellationToken.None, ownedMutationDelegate: async (mutation, ct) =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            await mutation(ct);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            db.ChangeTracker.Clear();
        }, jobId: run);
        async Task<MetadataExecutionOutcome?> Record(Guid jobId, int titleId, MetadataExecutionOutcome outcome)
        {
            await owned.ExecuteOwnedMutationAsync(ct => store.RecordMetadataOutcomeAsync(jobId, titleId, outcome, ct));
            return await Outcome(run, titleId);
        }
        var failed = AllUpdated.ToDictionary(x => x.Role, _ => ArtworkCompletionOutcome.Failed);

        (await Record(run, 1, MetadataExecutionOutcome.Failed)).ShouldBe(MetadataExecutionOutcome.Failed);
        (await Record(run, 1, MetadataExecutionOutcome.NotAttempted)).ShouldBe(MetadataExecutionOutcome.Failed);
        (await Record(run, 1, MetadataExecutionOutcome.Succeeded)).ShouldBe(MetadataExecutionOutcome.Succeeded);
        (await Record(run, 1, MetadataExecutionOutcome.Failed)).ShouldBe(MetadataExecutionOutcome.Succeeded);
        await store.CompleteAsync(run, 1, failed, MetadataExecutionOutcome.Failed, owned, default);
        (await Outcome(run, 1)).ShouldBe(MetadataExecutionOutcome.Succeeded);
        (await Record(run, 2, MetadataExecutionOutcome.NotAttempted)).ShouldBe(MetadataExecutionOutcome.NotAttempted);
        (await Record(run, 2, MetadataExecutionOutcome.Failed)).ShouldBe(MetadataExecutionOutcome.Failed);
        // Completion fills unknown history once; a finished item is immutable afterwards.
        await store.CompleteAsync(run, 3, failed, MetadataExecutionOutcome.NotAttempted, owned, default);
        (await Record(run, 3, MetadataExecutionOutcome.Succeeded)).ShouldBe(MetadataExecutionOutcome.NotAttempted);
        // Jobs without an item for the title write nothing; writes require the caller's owned transaction.
        (await Record(Guid.NewGuid(), 2, MetadataExecutionOutcome.Succeeded)).ShouldBe(MetadataExecutionOutcome.Failed);
        await Should.ThrowAsync<InvalidOperationException>(async () => await store.RecordMetadataOutcomeAsync(run, 2, MetadataExecutionOutcome.Succeeded, default));
    }

    [Fact]
    public async Task Retry_PreUpgradeRun_ReadsModeFromScopeJson_ShowsUnknownOutcomes_AndRetriesUnfinished()
    {
        await SeedAsync([1, 2, 3]);
        await using var db = database.CreateContext();
        var old = Guid.NewGuid();
        var oldRetry = Guid.NewGuid();
        // Rows and signatures written before schema 44: no metadata outcomes, and a retry scoped by the retired status.
        AddJob(db, old, "Cancelled", total: 2, processed: 1,
            $$"""{"RequestId":"00000000-0000-0000-0000-000000000000","TitleIds":["{{IdCoder.Encode(1)}}","{{IdCoder.Encode(2)}}"],"Scope":"Selected","Filters":null,"RetryJobId":null,"RetryStatus":null,"Mode":"Enrich"}""");
        AddJob(db, oldRetry, "Completed", total: 1, processed: 1,
            $$"""{"RequestId":"00000000-0000-0000-0000-000000000000","TitleIds":null,"Scope":"Retry","Filters":null,"RetryJobId":"{{Guid.NewGuid()}}","RetryStatus":"Unresolved","Mode":"Artwork"}""");
        db.AddRange(
            new ArtworkCompletionItemEntity { JobId = old, TitleId = 1, TitleName = "Title 1", CompletedAt = DateTimeOffset.UtcNow },
            new ArtworkCompletionItemEntity { JobId = old, TitleId = 2, TitleName = "Title 2" },
            new ArtworkCompletionItemEntity { JobId = oldRetry, TitleId = 3, TitleName = "Title 3", CompletedAt = DateTimeOffset.UtcNow });
        AddResults(db, old, 1, AllUpdated);
        AddResults(db, oldRetry, 3, (ArtworkRole.Poster, "Failed"));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var store = new ArtworkCompletionStore(db, clock: TimeProvider.System);

        var legacy = (await store.GetAsync(old, default)).Value;
        (legacy.Mode, legacy.Unfinished, legacy.Failed).ShouldBe(("Enrich", 1, 0));
        (await store.ItemsAsync(old, 0, null, default)).Value.Items.ShouldAllBe(x => x.MetadataOutcome == null);
        var legacyRetry = (await store.GetAsync(oldRetry, default)).Value;
        (legacyRetry.Mode, legacyRetry.Failed).ShouldBe(("Artwork", 1));

        var request = new ArtworkCompletionRequest(Guid.NewGuid(), Scope: "Retry", RetryJobId: old, RetryStatus: "Unfinished");
        var retry = (await store.GetAsync((await store.AcceptCompletionAsync(db, request, actor, default)).Value, default)).Value;
        (retry.Total, retry.Mode).ShouldBe((1, "Enrich"));
        (await store.NextAsync(retry.JobId, default)).ShouldBe(new ArtworkCompletionWork(2, "Title 2", false, "Enrich"));
        (await store.GetAsync((await store.AcceptCompletionAsync(db, request, actor, default)).Value, default)).Value.JobId.ShouldBe(retry.JobId);
        (await store.PreviewScopeAsync(request with { RetryStatus = "Failed" }, default)).Value.Titles.ShouldBe(0);
    }

    private async Task SeedAsync(IEnumerable<int> titleIds)
    {
        await using var db = database.CreateContext();
        db.Users.Add(new Romd.Persistence.Identity.RomdUser { Id = actor, UserName = "curator", CreatedAt = DateTimeOffset.UtcNow });
        db.Platforms.Add(new() { Id = 1, Name = "System", ShortName = "system" });
        db.Files.Add(new FileEntityPersistence { Id = 1, Sha256 = Sha256.FromSpan(Enumerable.Repeat((byte)1, 32).ToArray()) });
        foreach (var id in titleIds)
            db.Titles.Add(new() { Id = id, PlatformId = 1, Name = $"Title {id}", NormalizedName = $"TITLE {id}", EnrichmentStatus = "None" });
        await db.SaveChangesAsync();
        await db.Set<ArtworkEnrichmentSettingsEntity>().ExecuteUpdateAsync(s => s.SetProperty(x => x.FillPosters, true)
            .SetProperty(x => x.FillHeroes, true).SetProperty(x => x.FillLogos, true).SetProperty(x => x.FillBackdrops, true)
            .SetProperty(x => x.ReviewBackdrops, false));
    }

    // One cancelled run whose items cover every filter, overlaps, continuation leaves and legacy history.
    private async Task<Guid> SeedMixedRunAsync()
    {
        await using var db = database.CreateContext();
        var run = Guid.NewGuid();
        var paused = Guid.NewGuid();
        var active = Guid.NewGuid();
        var titles = Enumerable.Range(1, 10).Concat(Extra).ToArray();
        await db.Titles.Where(x => titles.Contains(x.Id)).ExecuteUpdateAsync(s => s.SetProperty(x => x.Description, "Curated")
            .SetProperty(x => x.Genre, "Puzzle").SetProperty(x => x.ReleaseDate, new DateOnly(1991, 6, 1)).SetProperty(x => x.EnrichmentStatus, "Completed"));
        AddJob(db, run, "Cancelled", titles.Length, titles.Length - 2, Scope());
        // Cancelled while paused by a provider deferral, and a continuation that is still running.
        AddJob(db, paused, "Cancelled", 1, 0, Scope()).NextAttemptAtUtc = DateTimeOffset.UtcNow.AddMinutes(-5);
        AddJob(db, active, "Enriching", 1, 0, Scope());
        foreach (var id in titles.Where(id => id is not Removed))
            Settle(db, id, poster: id is not (Review or ReviewAndFailed), logo: id is not LogoUnavailable);
        foreach (var id in titles)
        {
            var completed = id is not Cancelled;
            Guid? followUp = id switch { Deferred => paused, Working => active, _ => null };
            db.Add(new ArtworkCompletionItemEntity
            {
                JobId = run,
                TitleId = id,
                TitleName = $"Title {id}",
                FollowUpJobId = followUp,
                CompletedAt = completed ? DateTimeOffset.UtcNow : null,
                MetadataOutcome = id switch { FailedMetadata => MetadataExecutionOutcome.Failed, Legacy => null, _ => MetadataExecutionOutcome.Succeeded }
            });
            if (followUp is null)
                AddResults(db, run, id, id switch
                {
                    Cancelled => [(ArtworkRole.Poster, "Updated")],
                    FailedRole => [(ArtworkRole.Poster, "Failed"), (ArtworkRole.Hero, "Updated"), (ArtworkRole.Logo, "Updated"), (ArtworkRole.Backdrop, "Updated")],
                    ReviewAndFailed => [(ArtworkRole.Poster, "Unavailable"), (ArtworkRole.Hero, "Failed"), (ArtworkRole.Logo, "Updated"), (ArtworkRole.Backdrop, "Updated")],
                    Review => [(ArtworkRole.Poster, "Unavailable"), (ArtworkRole.Hero, "Updated"), (ArtworkRole.Logo, "Updated"), (ArtworkRole.Backdrop, "Updated")],
                    LogoUnavailable => [(ArtworkRole.Poster, "Updated"), (ArtworkRole.Hero, "Updated"), (ArtworkRole.Logo, "Unavailable"), (ArtworkRole.Backdrop, "Updated")],
                    Removed => [(ArtworkRole.Backdrop, "Failed")],
                    _ when Extra.Contains(id) => [(ArtworkRole.Poster, "Failed")],
                    _ => AllUpdated
                });
        }
        db.Add(new ArtworkCompletionItemEntity { JobId = paused, TitleId = Deferred, TitleName = "Paused", MetadataOutcome = MetadataExecutionOutcome.Succeeded });
        AddResults(db, paused, Deferred, (ArtworkRole.Poster, "Updated"));
        db.Add(new ArtworkCompletionItemEntity { JobId = active, TitleId = Working, TitleName = "Running", MetadataOutcome = MetadataExecutionOutcome.Failed });
        AddResults(db, active, Working, (ArtworkRole.Poster, "Failed"));
        await db.SaveChangesAsync();
        // Run history outlives catalog removal.
        await db.Titles.Where(x => x.Id == Removed).ExecuteDeleteAsync();
        return run;
    }

    // Commits outside the snapshot: title 7 now needs review and the cancelled item is completed.
    private async Task ChangeTitlesAndHistoryConcurrentlyAsync(Guid run)
    {
        await using var other = database.CreateContext();
        await other.ArtworkAssets.Where(x => x.TitleId == LogoUnavailable && x.Role == ArtworkRole.Poster).ExecuteDeleteAsync();
        other.Set<ArtworkAcquisitionEntity>().Add(new() { TitleId = LogoUnavailable, Role = ArtworkRole.Poster, Status = "Unavailable", UpdatedAt = DateTimeOffset.UtcNow });
        await other.SaveChangesAsync();
        await other.Set<ArtworkCompletionItemEntity>().Where(x => x.JobId == run && x.TitleId == Cancelled)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.CompletedAt, DateTimeOffset.UtcNow));
    }

    private static async Task<List<ArtworkCompletionItemDto>> PageAllAsync(ArtworkCompletionStore store, Guid run, string? status)
    {
        var items = new List<ArtworkCompletionItemDto>();
        var after = 0;
        while (true)
        {
            var page = (await store.ItemsAsync(run, after, status, default)).Value;
            items.AddRange(page.Items);
            if (page.NextCursor is null)
                return items;
            after = Sqid.Parse(page.NextCursor, null).Value;
        }
    }

    // Trusted identities, core metadata and present artwork; a missing poster or logo was searched and found nothing.
    private void Settle(RomdDbContext db, int titleId, bool poster, bool logo)
    {
        db.TitleExternalIds.AddRange(new TitleExternalIdEntity { TitleId = titleId, Provider = "igdb", ExternalId = $"{titleId}", IsConfirmed = true },
            new TitleExternalIdEntity { TitleId = titleId, Provider = "steamgriddb", ExternalId = $"{titleId}", IsConfirmed = true });
        foreach (var role in new[] { ArtworkRole.Hero, ArtworkRole.Backdrop }.Concat(poster ? [ArtworkRole.Poster] : []).Concat(logo ? [ArtworkRole.Logo] : []))
            db.ArtworkAssets.Add(new()
            {
                Id = ++assetId,
                TitleId = titleId,
                Role = role,
                SourceId = "steamgriddb",
                OriginalFileId = 1,
                ContentVersion = "v1",
                ContentType = "image/webp",
                Width = 600,
                Height = 900,
                IsEligible = true,
                Variants = [new() { Name = "large", FileId = 1, ContentVersion = "large-v1", ContentType = "image/webp", Width = 600, Height = 900 }]
            });
        if (!poster)
            db.Set<ArtworkAcquisitionEntity>().Add(new() { TitleId = titleId, Role = ArtworkRole.Poster, Status = "Unavailable", UpdatedAt = DateTimeOffset.UtcNow });
        if (!logo)
            db.Set<ArtworkAcquisitionEntity>().Add(new() { TitleId = titleId, Role = ArtworkRole.Logo, Status = "Unavailable", UpdatedAt = DateTimeOffset.UtcNow });
    }

    private ArtworkCompletionJobEntity AddJob(RomdDbContext db, Guid id, string phase, int total, int processed, string scope)
    {
        var row = ArtworkCompletionJobEntity.FromDomain(ArtworkCompletionJob.Create(id, total, actor, createdAt: clock.GetUtcNow()));
        (row.Phase, row.Processed, row.ScopeJson) = (phase, processed, scope);
        db.Add(row);
        return row;
    }

    private static string Scope() => JsonSerializer.Serialize(new ArtworkCompletionRequest(Guid.Empty, Scope: "AllTracked", Mode: "Enrich"));

    private static void AddResults(RomdDbContext db, Guid jobId, int titleId, params (ArtworkRole Role, string Status)[] outcomes)
    {
        foreach (var (role, status) in outcomes)
            db.Add(new ArtworkCompletionResultEntity { JobId = jobId, TitleId = titleId, Role = role, Outcome = Enum.Parse<ArtworkCompletionOutcome>(status) });
    }

    private async Task<Guid> AcceptAsync(int[] titleIds)
    {
        await using var db = database.CreateContext();
        return (await new ArtworkCompletionStore(db, clock: TimeProvider.System).GetAsync((await new ArtworkCompletionStore(db, clock: TimeProvider.System).AcceptCompletionAsync(db, new(Guid.NewGuid(), titleIds.Select(IdCoder.Encode).ToArray(), Mode: "Enrich"), actor, default)).Value, default)).Value.JobId;
    }

    private async Task<MetadataExecutionOutcome?> Outcome(Guid jobId, int titleId)
    {
        await using var db = database.CreateContext();
        return (await db.Set<ArtworkCompletionItemEntity>().SingleAsync(x => x.JobId == jobId && x.TitleId == titleId)).MetadataOutcome;
    }

    private static async Task RecordAsync(ArtworkAcquisitionStore acquisition, JobContext context, int titleId, CancellationToken ct,
        params (ArtworkRole Role, string Status)[] outcomes)
    {
        foreach (var (role, status) in outcomes)
            await context.ExecuteOwnedMutationAsync(token => acquisition.RecordAsync(titleId, role, status, "steamgriddb", token, context.JobId), ct);
    }

    private TitleEnrichmentPipeline Pipeline(RomdDbContext db, IArtworkCompletionStore store, Func<Title, OrchestrationResult> metadata, IAutomaticArtworkService artwork)
    {
        var orchestrator = Substitute.For<IEnrichmentOrchestrator>();
        orchestrator.EnrichTitleAsync(default!, default!, default).ReturnsForAnyArgs(call => Task.FromResult(metadata(call.Arg<Title>())));
        return new TitleEnrichmentPipeline(new TitleRepository(db), new TitleRepository(db), new TitleRepository(db), new PlatformRepository(db), orchestrator, Substitute.For<IFileStorageService>(), new EfUnitOfWork(db),
            Substitute.For<IStatsNotifier>(), Substitute.For<ILibraryMaterializationService>(), Options.Create(new EnrichmentOptions()),
            NullLogger<TitleEnrichmentPipeline>.Instance, artwork, store, clock: TimeProvider.System);
    }

    // One delivery through the production runner, claim, fence and owned mutations, with scripted providers.
    private async Task RunAsync(Guid jobId, Func<Title, OrchestrationResult> metadata,
        Func<Title, JobContext, ArtworkAcquisitionStore, CancellationToken, Task> artwork)
    {
        await using var provider = new ServiceCollection()
            .AddDbContext<RomdDbContext>(options => options.UseNpgsql(database.ConnectionString).UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking))
            .AddScoped<IJobRepository<ArtworkCompletionJob>>(sp =>
            {
                var context = sp.GetRequiredService<RomdDbContext>();
                return new ClaimedJobRepository<ArtworkCompletionJob, ArtworkCompletionJobRepository>(new(context), context, clock, ClaimOptions);
            })
            .AddScoped<JobAuditContext>()
            .AddScoped<IAuditContext>(sp => sp.GetRequiredService<JobAuditContext>())
            .AddRomdJobLifecyclePolicies()
            .BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<RomdDbContext>();
        var store = new ArtworkCompletionStore(db, clock: TimeProvider.System);
        var acquisition = new ArtworkAcquisitionStore(db, Substitute.For<IArtworkCurationRepository>(), clock);
        var fill = new ScriptedArtwork((title, context, ct) => artwork(title, context, acquisition, ct));
        var notifier = Substitute.For<IJobNotifier>();
        notifier.When(x => x.NotifyJobUpdatedAsync(Arg.Any<Job>(), Arg.Any<CancellationToken>()))
            .Do(call => pushes.Add((call.Arg<Job>().Phase, call.Arg<Job>().NextAttemptAt)));
        var romd = Substitute.For<IRomdOptions>();
        romd.DataDirectory.Returns(Path.GetTempPath());
        var executor = new ArtworkCompletionJobExecutor(store, new TitleRepository(db), new PlatformRepository(db), fill,
            NullLogger<ArtworkCompletionJobExecutor>.Instance, clock: TimeProvider.System, Pipeline(db, store, metadata, fill));
        await new JobRunner<ArtworkCompletionJob>(provider.GetRequiredService<IServiceScopeFactory>(), executor, notifier, romd, clock,
                ClaimOptions, new JobExecutionMutationGuard(db, new EfUnitOfWork(db), clock, ClaimOptions), NullLogger<JobRunner<ArtworkCompletionJob>>.Instance)
            .RunAsync(jobId, $"delivery-{Guid.NewGuid():N}", CancellationToken.None);
    }

    private static IProviderIdentityAdapter Provider(string id)
    {
        var provider = Substitute.For<IProviderIdentityAdapter>();
        provider.Id.Returns(id);
        provider.GetAvailabilityAsync(Arg.Any<CancellationToken>()).Returns(new ProviderAvailability(true, true));
        return provider;
    }

    private sealed class ScriptedArtwork(Func<Title, JobContext, CancellationToken, Task> fill) : IAutomaticArtworkService
    {
        public Task FillAsync(Title title, Platform platform, JobContext context, CancellationToken ct,
            IReadOnlyList<ProviderArtworkResult>? results = null) => fill(title, context, ct);
    }
}
