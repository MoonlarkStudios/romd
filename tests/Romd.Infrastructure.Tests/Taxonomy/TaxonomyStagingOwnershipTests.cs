using ErrorOr;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NSubstitute;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Taxonomy;
using Romd.Admin.Application.Taxonomy.ReadModels;
using Romd.Application.Common.Security;
using Romd.Domain.Taxonomy;
using Romd.Persistence;
using Romd.Persistence.Entities;
using Romd.Persistence.Repositories;
using Romd.PostgreSql.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Taxonomy;

public sealed class TaxonomyStagingOwnershipTests : IAsyncLifetime
{
    private readonly PostgreSqlTestDatabase _database = PostgreSqlTestDatabase.Create();

    public Task InitializeAsync() => Task.CompletedTask;
    public Task DisposeAsync() => _database.DisposeAsync().AsTask();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StageCreation_RequiresCallerTransactionAndNewRow_RejectsBeforeTracking(bool languages)
    {
        await using var db = _database.CreateContext();
        await Should.ThrowAsync<InvalidOperationException>(() => StageRowAsync(db, languages));
        await Should.ThrowAsync<InvalidOperationException>(() => StageAliasAsync(db, languages, 7, "atlantis"));
        await Should.ThrowAsync<InvalidOperationException>(() => SavepointAsync(db, languages, _ => Task.FromResult<int?>(null)));
        db.ChangeTracker.Entries().ShouldBeEmpty();
        int id = await SeedRowAsync(languages, "Existing");
        var work = new EfUnitOfWork(db);
        await using var transaction = await work.BeginTransactionAsync();

        if (languages)
        {
            var repository = new LanguageRepository(db);
            var existing = (await repository.GetByIdAsync(id))!;
            await Should.ThrowAsync<ArgumentException>(() => repository.AddStagedAsync(existing, "existing"));
        }
        else
        {
            var repository = new RegionRepository(db);
            var existing = (await repository.GetByIdAsync(id))!;
            await Should.ThrowAsync<ArgumentException>(() => repository.AddStagedAsync(existing, "existing"));
        }
        db.ChangeTracker.Entries().ShouldBeEmpty();
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task CreationReceipts_RequireOwnerFlushAndOriginalTransaction_ExpireAfterCommitOrRollback(bool languages, bool commit)
    {
        await using var db = _database.CreateContext();
        var work = new EfUnitOfWork(db);
        IStagedTaxonomyRow row;
        IStagedTaxonomyAlias alias;
        await using (var transaction = await work.BeginTransactionAsync())
        {
            row = await StageRowAsync(db, languages);
            Should.Throw<InvalidOperationException>(() => row.ReadIdAfterFlush());
            await using (var other = _database.CreateContext())
                await new EfUnitOfWork(other).FlushAsync();
            Should.Throw<InvalidOperationException>(() => row.ReadIdAfterFlush());
            await work.FlushAsync();
            int id = row.ReadIdAfterFlush();
            id.ShouldBeGreaterThan(0);
            alias = await StageAliasAsync(db, languages, id, "atlantis");
            Should.Throw<InvalidOperationException>(() => alias.ReadAfterFlush());
            await work.FlushAsync();
            var response = alias.ReadAfterFlush();
            (response.Alias, response.Ownership).ShouldBe(("atlantis", "Installation"));
            response.Id.ShouldBeGreaterThan(0);
            await AssertCountsAsync(0, 0, 0);
            if (commit)
                await transaction.CommitAsync();
            else
                await transaction.RollbackAsync();
        }
        Should.Throw<InvalidOperationException>(() => row.ReadIdAfterFlush());
        Should.Throw<InvalidOperationException>(() => alias.ReadAfterFlush());
        await using var replacement = await work.BeginTransactionAsync();
        Should.Throw<InvalidOperationException>(() => row.ReadIdAfterFlush());
        Should.Throw<InvalidOperationException>(() => alias.ReadAfterFlush());
        await AssertCountsAsync(commit ? 1 : 0, commit ? 1 : 0, 0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RefusedSavepoint_ExpiresReceiptsAndDetachesOnlyItsAttempt_PreservesPriorStagingAndAudit(bool languages)
    {
        await using var db = AuditedContext();
        var work = new EfUnitOfWork(db);
        await using var transaction = await work.BeginTransactionAsync();
        // Earlier ingest work must be flushed before the mint savepoint, then stay tracked and commit normally.
        var prior = new RegionEntity { Name = "Prior", CanonicalKey = "dat-prior" };
        db.Regions.Add(prior);
        await LockMintingAsync(db, languages);
        IStagedTaxonomyRow? row = null;
        IStagedTaxonomyAlias? alias = null;
        var boundary = db.Database.CurrentTransaction;

        var refused = await SavepointAsync(db, languages, async ct =>
        {
            db.Entry(prior).State.ShouldBe(EntityState.Unchanged);
            row = await StageRowAsync(db, languages);
            await work.FlushAsync(ct);
            alias = await StageAliasAsync(db, languages, row.ReadIdAfterFlush(), "atlantis");
            await work.FlushAsync(ct);
            alias.ReadAfterFlush().Alias.ShouldBe("atlantis");
            return null;
        });

        refused.ShouldBeNull();
        db.Database.CurrentTransaction.ShouldBeSameAs(boundary);
        db.Entry(prior).State.ShouldBe(EntityState.Unchanged);
        Should.Throw<InvalidOperationException>(() => row!.ReadIdAfterFlush());
        Should.Throw<InvalidOperationException>(() => alias!.ReadAfterFlush());
        db.ChangeTracker.Entries<AdminAuditEventEntity>().ShouldHaveSingleItem().Entity.TargetId.ShouldBe("dat-prior");
        await work.FlushAsync();
        await transaction.CommitAsync();
        await AssertCountsAsync(1, 0, 1);
        await using var observer = _database.CreateContext();
        (await observer.AdminAuditEvents.SingleAsync()).TargetId.ShouldBe("dat-prior");
    }

    [Theory]
    [InlineData(false, 1, false)]
    [InlineData(false, 1, true)]
    [InlineData(false, 2, false)]
    [InlineData(false, 2, true)]
    [InlineData(true, 1, false)]
    [InlineData(true, 1, true)]
    [InlineData(true, 2, false)]
    [InlineData(true, 2, true)]
    public async Task Resolve_FaultOrCancellationAfterRowOrAliasFlush_LeavesOuterRollbackToIngest(bool languages, int interruptAt, bool cancel)
    {
        await using var db = AuditedContext();
        var owner = new EfUnitOfWork(db);
        await using var ingest = await owner.BeginTransactionAsync();
        var boundary = db.Database.CurrentTransaction;
        using var cancellation = new CancellationTokenSource();
        var primary = new IOException("Injected mint flush fault.");
        var work = new ObservedUnitOfWork(owner)
        {
            AfterFlush = async (count, ct) =>
            {
                await AssertCountsAsync(0, 0, 0);
                db.Database.CurrentTransaction.ShouldBeSameAs(boundary);
                if (count != interruptAt)
                    return;
                if (cancel)
                { await cancellation.CancelAsync(); ct.ThrowIfCancellationRequested(); }
                throw primary;
            }
        };
        Exception? failure = null;
        try
        { await ResolveAsync(db, work, languages, cancellation.Token); }
        catch (Exception error) { failure = error; }

        if (cancel)
            failure.ShouldBeOfType<OperationCanceledException>().CancellationToken.ShouldBe(cancellation.Token);
        else
            failure.ShouldBeSameAs(primary);
        work.FlushCount.ShouldBe(interruptAt);
        work.Begins.ShouldBe(0);
        db.Database.CurrentTransaction.ShouldBeSameAs(boundary);
        await AssertCountsAsync(0, 0, 0);
        await ingest.RollbackAsync();
        db.ChangeTracker.Entries().ShouldBeEmpty();
        await owner.FlushAsync();
        await AssertCountsAsync(0, 0, 0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Resolve_CanonicalRowUniqueFailure_PropagatesInsteadOfAdoptingAnAliasWinner(bool languages)
    {
        await using var db = _database.CreateContext();
        var owner = new EfUnitOfWork(db);
        await using var ingest = await owner.BeginTransactionAsync();
        var work = new ObservedUnitOfWork(owner)
        {
            BeforeFlush = async (count, _) =>
            {
                if (count != 1)
                    return;
                // Inject the canonical-key backstop violation after allocation, before the owner row flush.
                // The alias winner makes a catch around the whole callback incorrectly look like success.
                int winner = await SeedRowAsync(languages, "Atlantis");
                await using var writer = _database.CreateContext();
                var unit = new EfUnitOfWork(writer);
                await using var transaction = await unit.BeginTransactionAsync();
                await StageAliasAsync(writer, languages, winner, "atlantis");
                await transaction.CommitAsync();
            }
        };

        var failure = await Should.ThrowAsync<DbUpdateException>(() => ResolveAsync(db, work, languages));

        failure.InnerException.ShouldBeOfType<PostgresException>().SqlState.ShouldBe(PostgresErrorCodes.UniqueViolation);
        failure.InnerException.ShouldBeOfType<PostgresException>().ConstraintName.ShouldBe(languages ? "IX_GameLanguages_CanonicalKey" : "IX_Regions_CanonicalKey");
        work.FlushCount.ShouldBe(1);
        work.Begins.ShouldBe(0);
        await ingest.RollbackAsync();
        db.ChangeTracker.Entries().ShouldBeEmpty();
        await AssertCountsAsync(1, 1, 0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Resolve_ConcurrentAliasWinner_RollsBackOnlyMintAndKeepsEarlierAuditAndTrackedObjects(bool languages)
    {
        int target = await SeedRowAsync(languages, "Target");
        await using var writer = _database.CreateContext();
        var writerWork = new EfUnitOfWork(writer);
        await using var writerTransaction = await writerWork.BeginTransactionAsync();
        await StageAliasAsync(writer, languages, target, "atlantis");
        await writerWork.FlushAsync();
        await using var db = AuditedContext();
        var owner = new EfUnitOfWork(db);
        await using var ingest = await owner.BeginTransactionAsync();
        var prior = new RegionEntity { Name = "Prior", CanonicalKey = "dat-prior" };
        db.Regions.Add(prior);
        var boundary = db.Database.CurrentTransaction;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var (resolve, waited) = await _database.StartWaitingForALockAsync(db,
            ct => ResolveAsync(db, owner, languages, ct), cancellation.Token);
        Exception? failure = null;
        try
        {
            waited.ShouldBeTrue();
            await writerTransaction.CommitAsync(cancellation.Token);
            (await resolve).ShouldHaveSingleItem().ShouldBe(target);
            db.Database.CurrentTransaction.ShouldBeSameAs(boundary);
            db.Entry(prior).State.ShouldBe(EntityState.Unchanged);
            await owner.FlushAsync(cancellation.Token);
            await ingest.CommitAsync(cancellation.Token);
        }
        catch (Exception error) { failure = error; throw; }
        finally { await PostgreSqlLockQueue.CancelAndJoinAsync(cancellation, failure, resolve); }

        await AssertCountsAsync(2, 1, 1);
        await using var observer = _database.CreateContext();
        (await observer.AdminAuditEvents.SingleAsync()).TargetId.ShouldBe("dat-prior");
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task AddAlias_FaultOrCancellationAfterRealFlush_RollsBackAliasAuditAndUnrelatedStaging(bool languages, bool cancel)
    {
        await SeedRowAsync(languages, "Target");
        await using var db = AuditedContext();
        var primary = new IOException("Injected alias flush fault.");
        using var cancellation = new CancellationTokenSource();
        var work = new ObservedUnitOfWork(new EfUnitOfWork(db))
        {
            AfterFlush = async (_, ct) =>
            {
                await AssertCountsAsync(1, 0, 0);
                if (cancel)
                { await cancellation.CancelAsync(); ct.ThrowIfCancellationRequested(); }
                throw primary;
            }
        };
        db.Regions.Add(new RegionEntity { Name = "Unrelated", CanonicalKey = "dat-unrelated" });
        Exception? failure = null;
        try
        { await AddAliasAsync(db, work, languages, cancellation.Token); }
        catch (Exception error) { failure = error; }

        if (cancel)
            failure.ShouldBeOfType<OperationCanceledException>().CancellationToken.ShouldBe(cancellation.Token);
        else
            failure.ShouldBeSameAs(primary);
        work.Begins.ShouldBe(1);
        work.FlushCount.ShouldBe(1);
        db.ChangeTracker.Entries().ShouldBeEmpty();
        await new EfUnitOfWork(db).FlushAsync();
        await AssertCountsAsync(1, 0, 0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AddAlias_ConcurrentAliasWinner_ReturnsDuplicateAndRollsBackOnlyItsOwnerStaging(bool languages)
    {
        int target = await SeedRowAsync(languages, "Target");
        await using var writer = _database.CreateContext();
        var writerWork = new EfUnitOfWork(writer);
        await using var writerTransaction = await writerWork.BeginTransactionAsync();
        await StageAliasAsync(writer, languages, target, "atlantis");
        await writerWork.FlushAsync();
        await using var db = AuditedContext();
        db.Regions.Add(new RegionEntity { Name = "Unrelated", CanonicalKey = "dat-unrelated" });
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var (add, waited) = await _database.StartWaitingForALockAsync(db,
            ct => AddAliasAsync(db, new EfUnitOfWork(db), languages, ct), cancellation.Token);
        Exception? failure = null;
        try
        {
            waited.ShouldBeTrue();
            await writerTransaction.CommitAsync(cancellation.Token);
            (await add).FirstError.Code.ShouldBe("Taxonomy.AliasAlreadyExists");
            db.ChangeTracker.Entries().ShouldBeEmpty();
            await new EfUnitOfWork(db).FlushAsync(cancellation.Token);
        }
        catch (Exception error) { failure = error; throw; }
        finally { await PostgreSqlLockQueue.CancelAndJoinAsync(cancellation, failure, add); }
        await AssertCountsAsync(1, 1, 0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AddAlias_ResponseWaitsForOwnerCommit_AndAnswersTheFlushedAlias(bool languages)
    {
        await SeedRowAsync(languages, "Target");
        await using var db = AuditedContext();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var committing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var work = new ObservedUnitOfWork(new EfUnitOfWork(db))
        {
            BeforeCommit = async ct =>
            {
                await AssertCountsAsync(1, 0, 0);
                committing.SetResult();
                await release.Task.WaitAsync(ct);
            }
        };
        var operation = AddAliasAsync(db, work, languages, cancellation.Token);
        Exception? failure = null;
        try
        {
            await committing.Task.WaitAsync(cancellation.Token);
            operation.IsCompleted.ShouldBeFalse();
            work.FlushCount.ShouldBe(1);
            release.SetResult();
            var response = (await operation).Value;
            (response.Alias, response.Ownership).ShouldBe(("atlantis", "Installation"));
            response.Id.ShouldBeGreaterThan(0);
            await using var observer = _database.CreateContext();
            int persistedId = languages ? (await observer.GameLanguageAliases.SingleAsync()).Id : (await observer.RegionAliases.SingleAsync()).Id;
            response.Id.ShouldBe(persistedId);
        }
        catch (Exception error) { failure = error; throw; }
        finally { await PostgreSqlLockQueue.CancelAndJoinAsync(cancellation, failure, operation); }
        await AssertCountsAsync(1, 1, 1);
    }

    private RomdDbContext AuditedContext()
    {
        var actor = Substitute.For<IAuditContext>();
        actor.ActorId.Returns(Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"));
        return new RomdDbContext(_database.CreateOptions(options => options.AddInterceptors(new AuditInterceptor(actor, TimeProvider.System))));
    }

    private async Task<int> SeedRowAsync(bool languages, string name)
    {
        await using var db = _database.CreateContext();
        EntityBase row = languages
            ? new GameLanguageEntity { Name = name, Code = name.ToLowerInvariant(), CanonicalKey = $"dat-{name.ToLowerInvariant()}" }
            : new RegionEntity { Name = name, CanonicalKey = $"dat-{name.ToLowerInvariant()}" };
        db.Add(row);
        await db.SaveChangesAsync();
        return row.Id;
    }

    private static Task<IStagedTaxonomyRow> StageRowAsync(RomdDbContext db, bool languages) => languages
        ? new LanguageRepository(db).AddStagedAsync(GameLanguage.CreateNew("Atlantis", "atlantis", isAutoCreated: true), "atlantis")
        : new RegionRepository(db).AddStagedAsync(Region.CreateNew("Atlantis", isAutoCreated: true), "atlantis");
    private static Task<IStagedTaxonomyAlias> StageAliasAsync(RomdDbContext db, bool languages, int id, string alias) => languages
        ? new LanguageRepository(db).AddAliasStagedAsync(id, alias)
        : new RegionRepository(db).AddAliasStagedAsync(id, alias);
    private static Task<int?> SavepointAsync(RomdDbContext db, bool languages, Func<CancellationToken, Task<int?>> operation) => languages
        ? new LanguageRepository(db).ExecuteMintSavepointAsync(operation)
        : new RegionRepository(db).ExecuteMintSavepointAsync(operation);
    private static Task LockMintingAsync(RomdDbContext db, bool languages) => languages
        ? new LanguageRepository(db).LockMintingAsync()
        : new RegionRepository(db).LockMintingAsync();
    private static Task<IReadOnlyList<int>> ResolveAsync(RomdDbContext db, IUnitOfWork work, bool languages, CancellationToken ct = default) => languages
        ? new TaxonomyResolver<GameLanguage>(new LanguageRepository(db), work, name => GameLanguage.CreateNew(name, name.ToLowerInvariant(), isAutoCreated: true)).ResolveAsync("Atlantis", ct)
        : new TaxonomyResolver<Region>(new RegionRepository(db), work, name => Region.CreateNew(name, isAutoCreated: true)).ResolveAsync("Atlantis", ct);
    private static Task<ErrorOr<TaxonomyAlias>> AddAliasAsync(RomdDbContext db, IUnitOfWork work, bool languages, CancellationToken ct) => languages
        ? new TaxonomyService<GameLanguage>(new LanguageRepository(db), work, Microsoft.Extensions.Logging.Abstractions.NullLogger<TaxonomyService<GameLanguage>>.Instance).AddAliasAsync("dat-target", "Atlantis", ct)
        : new TaxonomyService<Region>(new RegionRepository(db), work, Microsoft.Extensions.Logging.Abstractions.NullLogger<TaxonomyService<Region>>.Instance).AddAliasAsync("dat-target", "Atlantis", ct);

    private async Task AssertCountsAsync(int rows, int aliases, int audits)
    {
        await using var observer = _database.CreateContext();
        (await observer.Regions.CountAsync() + await observer.GameLanguages.CountAsync()).ShouldBe(rows);
        (await observer.RegionAliases.CountAsync() + await observer.GameLanguageAliases.CountAsync()).ShouldBe(aliases);
        (await observer.AdminAuditEvents.CountAsync()).ShouldBe(audits);
    }

    private sealed class ObservedUnitOfWork(IUnitOfWork inner) : IUnitOfWork
    {
        public Func<int, CancellationToken, Task>? BeforeFlush { get; init; }
        public Func<int, CancellationToken, Task>? AfterFlush { get; init; }
        public Func<CancellationToken, Task>? BeforeCommit { get; init; }
        public int FlushCount { get; private set; }
        public int Begins { get; private set; }
        public TransactionConflict ConflictOf(Exception error) => inner.ConflictOf(error);
        public async Task<ITransaction> BeginTransactionAsync(CancellationToken cancellationToken = default)
        {
            Begins++;
            var transaction = await inner.BeginTransactionAsync(cancellationToken);
            return BeforeCommit is null ? transaction : new ObservedTransaction(transaction, BeforeCommit);
        }
        public async Task FlushAsync(CancellationToken cancellationToken = default)
        {
            FlushCount++;
            if (BeforeFlush is not null)
                await BeforeFlush(FlushCount, cancellationToken);
            await inner.FlushAsync(cancellationToken);
            if (AfterFlush is not null)
                await AfterFlush(FlushCount, cancellationToken);
        }
        private sealed class ObservedTransaction(ITransaction transaction, Func<CancellationToken, Task> beforeCommit) : ITransaction
        {
            public async Task CommitAsync(CancellationToken cancellationToken = default)
            {
                await beforeCommit(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
            }
            public Task RollbackAsync(CancellationToken cancellationToken = default) => transaction.RollbackAsync(cancellationToken);
            public ValueTask DisposeAsync() => transaction.DisposeAsync();
        }
    }
}
