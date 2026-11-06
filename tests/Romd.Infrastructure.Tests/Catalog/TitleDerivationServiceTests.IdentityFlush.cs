using Microsoft.EntityFrameworkCore;
using Romd.Admin.Application.Catalog;
using Romd.Admin.Application.Common.Persistence;
using Romd.Domain.Catalog;
using Romd.Infrastructure.Catalog;
using Romd.Persistence;
using Romd.Persistence.Entities;
using Romd.Persistence.Repositories;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Catalog;

public sealed partial class TitleDerivationServiceTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Upsert_InterruptedAfterActualTitleIdentityFlush_PreservesCallerModeDurability(bool callerTransaction, bool cancel)
    {
        await using var db = CreateDb();
        var inner = new EfUnitOfWork(db);
        await using var transaction = callerTransaction ? await inner.BeginTransactionAsync() : null;
        using var cancellation = new CancellationTokenSource();
        var work = new ObserveTitleIdentityFlush(inner, db, cancel ? cancellation : null);
        var service = new TitleDerivationService(db, new TitleMatcher(new TitleRepository(db), TimeProvider.System), work);
        await using var assignments = service.UpsertAsync(SourceId,
            Stream([new TitleClaim("Interrupted", "Interrupted", PlatformId, false)]), cancellation.Token).GetAsyncEnumerator();

        if (cancel)
            (await Should.ThrowAsync<OperationCanceledException>(() => assignments.MoveNextAsync().AsTask()))
                .CancellationToken.ShouldBe(cancellation.Token);
        else
            await Should.ThrowAsync<IOException>(() => assignments.MoveNextAsync().AsTask());

        work.SawFlushedIdentityBeforeLinks.ShouldBeTrue();
        work.FlushCount.ShouldBe(1);
        if (transaction is not null)
        {
            await transaction.RollbackAsync();
            db.ChangeTracker.Entries().ShouldBeEmpty();
        }
        await using var verify = CreateDb();
        (await verify.SourceEntries.CountAsync()).ShouldBe(callerTransaction ? 0 : 1);
        (await verify.Titles.CountAsync()).ShouldBe(callerTransaction ? 0 : 1);
        (await verify.TitleSourceLinks.CountAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task Upsert_AllExistingTitleIdentities_DoesNotAddAnIdentityFlushOrTransaction()
    {
        await using var db = CreateDb();
        db.Titles.Add(new TitleEntity
        {
            PlatformId = PlatformId,
            Name = "Existing",
            NormalizedName = "existing",
            EnrichmentStatus = "None",
            CreatedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var work = new ObserveTitleIdentityFlush(new EfUnitOfWork(db), db);
        var service = new TitleDerivationService(db, new TitleMatcher(new TitleRepository(db), TimeProvider.System), work);

        var assignments = await CollectAsync(service.UpsertAsync(SourceId, Stream([new TitleClaim("Existing", "Existing", PlatformId, false)])));

        assignments.ShouldHaveSingleItem().Value.TitleWasCreated.ShouldBeFalse();
        work.FlushCount.ShouldBe(0);
        (await db.TitleSourceLinks.CountAsync()).ShouldBe(1);
        db.ChangeTracker.Entries().ShouldBeEmpty();
    }

    private sealed class ObserveTitleIdentityFlush(IUnitOfWork inner, RomdDbContext db,
        CancellationTokenSource? cancellation = null) : IUnitOfWork
    {
        public int FlushCount { get; private set; }
        public bool SawFlushedIdentityBeforeLinks { get; private set; }
        public TransactionConflict ConflictOf(Exception error) => inner.ConflictOf(error);
        public Task<ITransaction> BeginTransactionAsync(CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Derivation must not introduce a transaction around its standalone protocol.");
        public async Task FlushAsync(CancellationToken cancellationToken = default)
        {
            FlushCount++;
            await inner.FlushAsync(cancellationToken);
            SawFlushedIdentityBeforeLinks = (await db.Titles.SingleAsync(cancellationToken)).Id > 0
                && await db.SourceEntries.AnyAsync(cancellationToken) && !await db.TitleSourceLinks.AnyAsync(cancellationToken);
            SawFlushedIdentityBeforeLinks.ShouldBeTrue();
            if (cancellation is not null)
            {
                await cancellation.CancelAsync();
                cancellationToken.ThrowIfCancellationRequested();
            }
            throw new IOException("Interrupted after the existing derivation owner's title identity flush.");
        }
    }
}
