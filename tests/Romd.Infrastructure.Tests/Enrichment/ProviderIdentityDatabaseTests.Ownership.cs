using System.Text.Json;
using System.Data;
using ErrorOr;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Admin.Application.MetadataProviders;
using Romd.Admin.Application.Titles.Enrichment;
using Romd.Contracts.Management.MetadataProviders;
using Romd.Domain.Catalog;
using Romd.Domain.Jobs;
using Romd.Infrastructure.Jobs;
using Romd.Infrastructure.Tests.Helpers;
using Romd.Persistence;
using Romd.Persistence.Entities;
using Romd.Persistence.MetadataProviders;
using Romd.Persistence.Repositories;
using Romd.PostgreSql.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Enrichment;

public sealed partial class ProviderIdentityDatabaseTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Discovery_ActualOwnerFaultOrCancellationAfterFlush_RollsBackDecisionAndUnrelatedStaging(bool owned, bool cancel)
    {
        await using var db = _database.CreateContext();
        await Seed(db);
        var clock = new ManualTimeProvider(new(2030, 1, 2, 3, 4, 5, TimeSpan.Zero));
        var claim = await IdentityClaimAsync(db, clock);
        using var fence = JobExecutionFenceScope.Enter(claim.Job.Id, claim.FenceToken, clock);
        var store = IdentityStore(db, clock);
        var unrelated = TitleExternalIdEntity.FromDomain(TitleExternalId.CreateNew(1, "unrelated", "retained", 1, clock.GetUtcNow()));
        db.Add(unrelated);
        var failure = cancel ? (Exception)new OperationCanceledException("Injected identity cancellation after flush.")
            : new IOException("Injected identity failure after flush.");
        var work = new IdentityCommitProbe(new EfUnitOfWork(db), async () =>
        {
            (await db.TitleExternalIds.CountAsync()).ShouldBe(2);
            (await db.Set<TitleProviderMatchStateEntity>().CountAsync()).ShouldBe(1);
            await using var observer = _database.CreateContext();
            (await observer.TitleExternalIds.CountAsync()).ShouldBe(0);
            (await observer.Set<TitleProviderMatchStateEntity>().CountAsync()).ShouldBe(0);
            throw failure;
        });
        var service = new ProviderIdentityService(store, [IdentityAdapter("a")], [], clock, work, NullLogger<ProviderIdentityService>.Instance);
        var owner = owned ? IdentityOwner(db, work, clock, claim) : null;
        Exception? observed = null;
        try
        { await service.DiscoverAsync(1, owner, default); }
        catch (Exception error) { observed = error; }
        observed.ShouldBeSameAs(failure);
        work.CommitAttempts.ShouldBe(1);
        db.ChangeTracker.Entries().ShouldBeEmpty();
        await using var verify = _database.CreateContext();
        (await verify.TitleExternalIds.CountAsync()).ShouldBe(0);
        (await verify.Set<TitleProviderMatchStateEntity>().CountAsync()).ShouldBe(0);
        // The same context can retry after the real owner's rollback; the receipt retained no poison.
        var retryWork = new EfUnitOfWork(db);
        var retry = new ProviderIdentityService(store, [IdentityAdapter("a")], [], clock, retryWork, NullLogger<ProviderIdentityService>.Instance);
        await retry.DiscoverAsync(1, owned ? IdentityOwner(db, retryWork, clock, claim) : null, default);
        (await store.ReadTrustedAsync(1, "a", default)).ShouldBe("42");
        db.ChangeTracker.Entries().ShouldBeEmpty();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Discovery_StaleEvidenceAppliedFalse_StillCommitsTheActualOwnerWithoutIdentityWrites(bool owned)
    {
        await using var db = _database.CreateContext();
        await Seed(db);
        var clock = new ManualTimeProvider(new(2030, 1, 2, 3, 4, 5, TimeSpan.Zero));
        var claim = await IdentityClaimAsync(db, clock);
        using var fence = JobExecutionFenceScope.Enter(claim.Job.Id, claim.FenceToken, clock);
        var adapter = IdentityAdapter("a");
        bool changed = false;
        adapter.SearchAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(async _ =>
        {
            db.Database.CurrentTransaction.ShouldBeNull();
            if (!changed)
            {
                changed = true;
                await using var writer = _database.CreateContext();
                await writer.Titles.ExecuteUpdateAsync(update => update.SetProperty(title => title.Revision, Guid.NewGuid()));
            }
            return (ErrorOr<ProviderGameSearchResultDto>)IdentitySearch();
        });
        var store = new IdentityStoreProbe(IdentityStore(db, clock));
        var work = new IdentityCommitProbe(new EfUnitOfWork(db));
        var service = new ProviderIdentityService(store, [adapter], [], clock, work, NullLogger<ProviderIdentityService>.Instance);

        await service.DiscoverAsync(1, owned ? IdentityOwner(db, work, clock, claim) : null, default);

        store.Applied.ShouldBe(false);
        work.CommitAttempts.ShouldBe(1);
        (await db.TitleExternalIds.CountAsync()).ShouldBe(0);
        (await db.Set<TitleProviderMatchStateEntity>().CountAsync()).ShouldBe(0);
        db.ChangeTracker.Entries().ShouldBeEmpty();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Discovery_ClaimChangesDuringProviderIo_ActualOwnerRejectsStaleExecutionBeforePublication(bool owned)
    {
        await using var db = _database.CreateContext();
        await Seed(db);
        var clock = new ManualTimeProvider(new(2030, 1, 2, 3, 4, 5, TimeSpan.Zero));
        var claim = await IdentityClaimAsync(db, clock);
        using var fence = JobExecutionFenceScope.Enter(claim.Job.Id, claim.FenceToken, clock);
        var adapter = IdentityAdapter("a");
        bool replaced = false;
        adapter.SearchAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(async _ =>
        {
            db.Database.CurrentTransaction.ShouldBeNull();
            if (!replaced)
            {
                replaced = true;
                clock.Advance(new JobExecutionClaimOptions().LeaseDuration + TimeSpan.FromSeconds(1));
                await using var writer = _database.CreateContext();
                var claims = new ClaimedJobRepository<EnrichmentJob, EnrichmentJobRepository>(
                    new(writer, clock), writer, clock, new());
                var winner = (await claims.TryClaimExecutionAsync(claim.Job.Id, "replacement")).ShouldNotBeNull();
                winner.FenceToken.ShouldNotBe(claim.FenceToken);
            }
            return (ErrorOr<ProviderGameSearchResultDto>)IdentitySearch();
        });
        var work = new EfUnitOfWork(db);
        var service = new ProviderIdentityService(IdentityStore(db, clock), [adapter], [], clock, work, NullLogger<ProviderIdentityService>.Instance);

        await Should.ThrowAsync<JobExecutionOwnershipLostException>(() =>
            service.DiscoverAsync(1, owned ? IdentityOwner(db, work, clock, claim) : null, default));

        replaced.ShouldBeTrue();
        (await db.TitleExternalIds.CountAsync()).ShouldBe(0);
        (await db.Set<TitleProviderMatchStateEntity>().CountAsync()).ShouldBe(0);
        db.ChangeTracker.Entries().ShouldBeEmpty();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Discovery_CuratorWinsTitleLock_RevalidatesAfterWaitingAndPreservesConfirmedAssociation(bool owned)
    {
        await using var db = _database.CreateContext();
        await Seed(db);
        var clock = new ManualTimeProvider(new(2030, 1, 2, 3, 4, 5, TimeSpan.Zero));
        var claim = await IdentityClaimAsync(db, clock);
        using var fence = JobExecutionFenceScope.Enter(claim.Job.Id, claim.FenceToken, clock);
        await using var writer = _database.CreateContext();
        await using var write = await writer.Database.BeginTransactionAsync();
        await writer.Titles.ExecuteUpdateAsync(update => update.SetProperty(title => title.Revision, Guid.NewGuid()));
        var curated = TitleExternalIdEntity.FromDomain(TitleExternalId.CreateNew(1, "a", "curated", 1, clock.GetUtcNow()));
        curated.IsConfirmed = true;
        writer.TitleExternalIds.Add(curated);
        await writer.SaveChangesAsync();
        var store = new IdentityStoreProbe(IdentityStore(db, clock));
        var work = new IdentityCommitProbe(new EfUnitOfWork(db));
        var service = new ProviderIdentityService(store, [IdentityAdapter("a")], [], clock, work, NullLogger<ProviderIdentityService>.Instance);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var pending = await _database.StartWaitingForALockAsync(db, async token =>
        {
            await service.DiscoverAsync(1, owned ? IdentityOwner(db, work, clock, claim) : null, token);
            return true;
        }, timeout.Token);
        Exception? primary = null;
        try
        {
            pending.Waited.ShouldBeTrue();
            await write.CommitAsync(timeout.Token);
            (await pending.Pending.WaitAsync(timeout.Token)).ShouldBeTrue();
            store.Applied.ShouldBe(false);
            work.CommitAttempts.ShouldBe(1);
            var saved = await db.TitleExternalIds.SingleAsync(timeout.Token);
            saved.ExternalId.ShouldBe("curated");
            saved.IsConfirmed.ShouldBeTrue();
            (await db.Set<TitleProviderMatchStateEntity>().CountAsync(timeout.Token)).ShouldBe(0);
        }
        catch (Exception error) { primary = error; throw; }
        finally
        {
            await write.DisposeAsync();
            await PostgreSqlLockQueue.CancelAndJoinAsync(timeout, primary, pending.Pending);
        }
    }

    [Fact]
    public async Task Discovery_OwnerCallbackUsesAnotherContext_RejectsInsteadOfPublishingOutsideItsBoundary()
    {
        await using var db = _database.CreateContext();
        await Seed(db);
        var clock = new ManualTimeProvider(new(2030, 1, 2, 3, 4, 5, TimeSpan.Zero));
        var claim = await IdentityClaimAsync(db, clock);
        using var fence = JobExecutionFenceScope.Enter(claim.Job.Id, claim.FenceToken, clock);
        await using var other = _database.CreateContext();
        var service = new ProviderIdentityService(IdentityStore(db, clock), [IdentityAdapter("a")], [], clock,
            new EfUnitOfWork(db), NullLogger<ProviderIdentityService>.Instance);
        var owner = IdentityOwner(other, new EfUnitOfWork(other), clock, claim);

        await Should.ThrowAsync<InvalidOperationException>(() => service.DiscoverAsync(1, owner, default));

        (await db.TitleExternalIds.CountAsync()).ShouldBe(0);
        (await db.Set<TitleProviderMatchStateEntity>().CountAsync()).ShouldBe(0);
        db.ChangeTracker.Entries().ShouldBeEmpty();
    }

    [Fact]
    public async Task StageDecision_ReceiptSurvivesFlushAndRollback_ReleasesOnlyAcquiredRows()
    {
        await using var db = _database.CreateContext();
        await Seed(db);
        var store = IdentityStore(db, TimeProvider.System);
        var snapshot = (await store.ReadAsync(1, default)).ShouldNotBeNull();
        var unrelated = TitleExternalIdEntity.FromDomain(TitleExternalId.CreateNew(1, "unrelated", "retained", 1, DateTimeOffset.UnixEpoch));
        db.Add(unrelated);
        IStagedProviderIdentityDecision receipt;
        await using (var boundary = await db.Database.BeginTransactionAsync())
        {
            receipt = await store.StageDecisionAsync(snapshot, "a", Guid.Empty, IdentityDecision(snapshot), IdentityGame(), default);
            receipt.Applied.ShouldBeTrue();
            db.Entry(unrelated).State.ShouldBe(EntityState.Added);
            (await db.TitleExternalIds.CountAsync()).ShouldBe(0);
            await db.SaveChangesAsync();
            (await db.TitleExternalIds.CountAsync()).ShouldBe(2);
            await using var observer = _database.CreateContext();
            (await observer.TitleExternalIds.CountAsync()).ShouldBe(0);
            await boundary.RollbackAsync();
        }
        receipt.Dispose();
        db.ChangeTracker.Entries().ShouldHaveSingleItem().Entity.ShouldBeSameAs(unrelated);
        (await db.TitleExternalIds.CountAsync()).ShouldBe(0);
    }

    [Theory]
    [InlineData("revision")]
    [InlineData("fingerprint")]
    [InlineData("bridge")]
    [InlineData("confirmation")]
    [InlineData("suppression")]
    [InlineData("match-revision")]
    [InlineData("conflict-review")]
    public async Task StageDecision_RevalidatesLiveEvidenceAndProtectsCuratedOrConflictingAssociations(string change)
    {
        await using var db = _database.CreateContext();
        await Seed(db);
        if (change == "bridge")
            await AddAutomatic(db, "source", "source-game");
        if (change is "confirmation" or "conflict-review")
            await AddAutomatic(db, "a", "old-game");
        if (change is "suppression" or "match-revision")
        {
            db.Add(new TitleProviderMatchStateEntity { TitleId = 1, ProviderId = "a", Revision = Guid.NewGuid() });
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();
        }
        var store = IdentityStore(db, TimeProvider.System);
        var snapshot = (await store.ReadAsync(1, default)).ShouldNotBeNull();
        var expected = snapshot.Links.SingleOrDefault(link => link.ProviderId == "a")?.Revision ?? Guid.Empty;
        await using (var writer = _database.CreateContext())
        {
            switch (change)
            {
                case "revision":
                    await writer.Titles.ExecuteUpdateAsync(update => update.SetProperty(title => title.Revision, Guid.NewGuid()));
                    break;
                case "fingerprint":
                    await writer.CatalogSources.ExecuteUpdateAsync(update => update.SetProperty(source => source.Status, "Paused"));
                    break;
                case "bridge":
                    await writer.Set<TitleProviderMatchStateEntity>().Where(state => state.ProviderId == "source")
                    .ExecuteUpdateAsync(update => update.SetProperty(state => state.Revision, Guid.NewGuid()));
                    break;
                case "confirmation":
                    await writer.TitleExternalIds.ExecuteUpdateAsync(update => update.SetProperty(link => link.IsConfirmed, true));
                    break;
                case "suppression":
                    await writer.Set<TitleProviderMatchStateEntity>().ExecuteUpdateAsync(update => update.SetProperty(state => state.SuppressAutomaticMatch, true));
                    break;
                case "match-revision":
                    await writer.Set<TitleProviderMatchStateEntity>().ExecuteUpdateAsync(update => update.SetProperty(state => state.Revision, Guid.NewGuid()));
                    break;
            }
        }
        IStagedProviderIdentityDecision receipt;
        await using (var boundary = await new EfUnitOfWork(db).BeginTransactionAsync())
        {
            receipt = await store.StageDecisionAsync(snapshot, "a", expected, IdentityDecision(snapshot), IdentityGame(), default);
            receipt.Applied.ShouldBe(change == "conflict-review");
            await boundary.CommitAsync();
        }
        receipt.Dispose();
        db.ChangeTracker.Entries().ShouldBeEmpty();
        if (change == "conflict-review")
        {
            (await db.TitleExternalIds.SingleAsync()).ExternalId.ShouldBe("old-game");
            var state = await db.Set<TitleProviderMatchStateEntity>().SingleAsync();
            state.AutomaticallyTrusted.ShouldBeFalse();
            JsonSerializer.Deserialize<ProviderIdentityDecisionDto>(state.DecisionJson!)!.State.ShouldBe("NeedsReview");
        }
        else if (change is not ("confirmation" or "bridge"))
            (await db.TitleExternalIds.CountAsync()).ShouldBe(0);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("repeatable-read")]
    [InlineData("unowned-fence")]
    public async Task StageDecision_WithoutRequiredBoundary_RejectsBeforeTracking(string boundaryKind)
    {
        await using var db = _database.CreateContext();
        await Seed(db);
        var clock = new ManualTimeProvider(new(2030, 1, 2, 3, 4, 5, TimeSpan.Zero));
        var claim = await IdentityClaimAsync(db, clock);
        var store = IdentityStore(db, clock);
        var snapshot = (await store.ReadAsync(1, default)).ShouldNotBeNull();
        if (boundaryKind == "missing")
            await Should.ThrowAsync<InvalidOperationException>(() => store.StageDecisionAsync(snapshot, "a", Guid.Empty, IdentityDecision(snapshot), IdentityGame(), default));
        else
        {
            await using var boundary = await db.Database.BeginTransactionAsync(boundaryKind == "repeatable-read" ? IsolationLevel.RepeatableRead : IsolationLevel.ReadCommitted);
            using var fence = boundaryKind == "unowned-fence" ? JobExecutionFenceScope.Enter(claim.Job.Id, claim.FenceToken, clock) : null;
            if (boundaryKind == "unowned-fence")
                await Should.ThrowAsync<JobExecutionOwnershipLostException>(() => store.StageDecisionAsync(snapshot, "a", Guid.Empty, IdentityDecision(snapshot), IdentityGame(), default));
            else
                await Should.ThrowAsync<InvalidOperationException>(() => store.StageDecisionAsync(snapshot, "a", Guid.Empty, IdentityDecision(snapshot), IdentityGame(), default));
        }
        db.ChangeTracker.Entries().ShouldBeEmpty();
    }

    private static ProviderIdentityStore IdentityStore(RomdDbContext db, TimeProvider clock)
    {
        var evidence = Substitute.For<ITitleEnrichmentEvidenceReader>();
        evidence.ReadAsync(1, Arg.Any<CancellationToken>()).Returns(Array.Empty<TitleEnrichmentEvidence>());
        return new(db, evidence, clock);
    }
    private static ProviderGameDto IdentityGame() => new("42", "Game", "https://example.test/game");
    private static ProviderGameSearchResultDto IdentitySearch() => new([IdentityGame()], ProviderGameSearchCompleteness.Complete, 500);
    private static ProviderIdentityDecisionDto IdentityDecision(ProviderIdentitySnapshot snapshot) => new("AutoAccepted", "test", "test", snapshot.Fingerprint, []);
    private static async Task<JobExecutionClaim<EnrichmentJob>> IdentityClaimAsync(RomdDbContext db, TimeProvider clock)
    {
        var job = EnrichmentJob.Create("Game", 1, 1, clock.GetUtcNow());
        var repository = new EnrichmentJobRepository(db, clock);
        await repository.CommitCreationAsync(db, job);
        var claim = (await repository.TryClaimExecutionAsync(job.Id, "identity-owner")).ShouldNotBeNull();
        db.ChangeTracker.Clear();
        return claim;
    }
    private static JobContext IdentityOwner(RomdDbContext db, IUnitOfWork work, TimeProvider clock, JobExecutionClaim<EnrichmentJob> claim)
    {
        var guard = new JobExecutionMutationGuard(db, work, clock, new());
        return new(null, _ => Task.CompletedTask, default,
            ownedMutationDelegate: (mutation, token) => guard.ExecuteAsync(claim.Job.Id, claim.FenceToken, mutation, token));
    }
    private sealed class IdentityStoreProbe(IProviderIdentityStore inner) : IProviderIdentityStore
    {
        public bool? Applied { get; private set; }
        public Task<ProviderIdentitySnapshot?> ReadAsync(int titleId, CancellationToken ct) => inner.ReadAsync(titleId, ct);
        public Task<string?> ReadTrustedAsync(int titleId, string providerId, CancellationToken ct) => inner.ReadTrustedAsync(titleId, providerId, ct);
        public async Task<IStagedProviderIdentityDecision> StageDecisionAsync(ProviderIdentitySnapshot snapshot, string providerId,
            Guid expectedRevision, ProviderIdentityDecisionDto decision, ProviderGameDto? accepted, CancellationToken ct)
        {
            var result = await inner.StageDecisionAsync(snapshot, providerId, expectedRevision, decision, accepted, ct);
            Applied = result.Applied;
            return result;
        }
    }
    private sealed class IdentityCommitProbe(IUnitOfWork inner, Func<Task>? afterFlush = null) : IUnitOfWork
    {
        public int CommitAttempts { get; private set; }
        public Task FlushAsync(CancellationToken cancellationToken = default) => inner.FlushAsync(cancellationToken);
        public TransactionConflict ConflictOf(Exception error) => inner.ConflictOf(error);
        public async Task<ITransaction> BeginTransactionAsync(CancellationToken cancellationToken = default) =>
            new Transaction(await inner.BeginTransactionAsync(cancellationToken), this);
        private async Task PrepareCommitAsync(CancellationToken cancellationToken)
        {
            CommitAttempts++;
            await inner.FlushAsync(cancellationToken);
            if (afterFlush is not null)
                await afterFlush();
        }
        private sealed class Transaction(ITransaction inner, IdentityCommitProbe owner) : ITransaction
        {
            public async Task CommitAsync(CancellationToken cancellationToken = default)
            {
                await owner.PrepareCommitAsync(cancellationToken);
                await inner.CommitAsync(cancellationToken);
            }
            public Task RollbackAsync(CancellationToken cancellationToken = default) => inner.RollbackAsync(cancellationToken);
            public ValueTask DisposeAsync() => inner.DisposeAsync();
        }
    }
}
