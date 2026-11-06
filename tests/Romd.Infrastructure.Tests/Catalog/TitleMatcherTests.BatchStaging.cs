using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Romd.Admin.Application.Catalog;
using Romd.Admin.Application.Titles;
using Romd.Domain.Catalog;
using Romd.Infrastructure.Catalog;
using Romd.Infrastructure;
using Romd.Persistence;
using Romd.Persistence.Entities;
using Romd.Persistence.Repositories;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Catalog;

public sealed partial class TitleMatcherTests
{
    [Fact]
    public void Composition_ExistingCurationRegistration_ResolvesDerivationWithItsScopedFlushOwner()
    {
        var services = new ServiceCollection();
        services.AddSingleton(TimeProvider.System);
        services.AddRomdTestingPersistence(_connection.ConnectionString);
        services.AddRomdAdminCurationServices();
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        scope.ServiceProvider.GetRequiredService<ITitleDerivationService>().ShouldBeOfType<TitleDerivationService>();
        scope.ServiceProvider.GetRequiredService<Romd.Admin.Application.Common.Persistence.IUnitOfWork>()
            .ShouldBeOfType<EfUnitOfWork>();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AddBatchStaged_IdentityFlushRespectsCallerModeAndOwnsRowsInInputOrder(bool callerTransaction)
    {
        await using var db = CreateDb();
        var work = new EfUnitOfWork(db);
        await using var tx = callerTransaction ? await work.BeginTransactionAsync() : null;
        var now = DateTimeOffset.UnixEpoch.AddTicks(1234567);
        var titles = new List<Title>
        {
            Title.CreateNew(PlatformId, "Zeta", "zeta", now),
            Title.CreateNew(PlatformId, "Alpha", "alpha", now.AddTicks(1))
        };
        var unrelated = new RegionEntity { Name = "Pending", CanonicalKey = "dat-pending" };
        db.Regions.Add(unrelated);
        var staged = await new TitleRepository(db).AddBatchStagedAsync(titles);
        titles.Clear();

        staged.RequiresFlush.ShouldBeTrue();
        Should.Throw<InvalidOperationException>(() => staged.ReadIdsAfterFlush());
        (await db.Titles.CountAsync()).ShouldBe(1);
        (await db.Regions.CountAsync()).ShouldBe(0);
        db.Entry(unrelated).State.ShouldBe(EntityState.Added);
        await work.FlushAsync();
        staged.RequiresFlush.ShouldBeFalse();
        var ids = staged.ReadIdsAfterFlush();
        ids.Count.ShouldBe(2);
        ids.ShouldAllBe(id => id > 0 && id != 101);
        ids[0].ShouldNotBe(ids[1]);
        (await db.Titles.SingleAsync(row => row.Id == ids[0])).Name.ShouldBe("Zeta");
        (await db.Titles.SingleAsync(row => row.Id == ids[1])).Name.ShouldBe("Alpha");
        (await db.Titles.SingleAsync(row => row.Id == ids[0])).CreatedAt.ShouldBe(DateTimeOffset.FromUnixTimeMilliseconds(now.ToUnixTimeMilliseconds()));
        await using (var outside = CreateDb())
        {
            (await outside.Titles.CountAsync()).ShouldBe(callerTransaction ? 1 : 3);
            (await outside.Regions.CountAsync()).ShouldBe(callerTransaction ? 0 : 1);
        }
        if (tx is not null)
        {
            await tx.CommitAsync();
            Should.Throw<InvalidOperationException>(() => staged.ReadIdsAfterFlush());
        }
        else
        {
            db.ChangeTracker.Clear();
            Should.Throw<InvalidOperationException>(() => staged.ReadIdsAfterFlush());
        }
    }

    [Fact]
    public async Task AddBatchStaged_EmptyCancellationAndExistingIdentityGuards_DoNotStagePartialInput()
    {
        await using var db = CreateDb();
        var repository = new TitleRepository(db);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        var empty = await repository.AddBatchStagedAsync([], cancellation.Token);
        empty.RequiresFlush.ShouldBeFalse();
        empty.ReadIdsAfterFlush().ShouldBeEmpty();
        var fresh = Title.CreateNew(PlatformId, "Fresh", "fresh", DateTimeOffset.UtcNow);
        var existing = (await db.Titles.SingleAsync(row => row.Id == 101)).ToDomain();
        db.ChangeTracker.Clear();

        await Should.ThrowAsync<ArgumentException>(() => repository.AddBatchStagedAsync([fresh, existing]));
        (await Should.ThrowAsync<OperationCanceledException>(() => repository.AddBatchStagedAsync([fresh], cancellation.Token)))
            .CancellationToken.ShouldBe(cancellation.Token);
        // Single-title command creation keeps its existing transaction precondition.
        await Should.ThrowAsync<InvalidOperationException>(() => repository.AddStagedAsync(fresh));
        db.ChangeTracker.Entries().ShouldBeEmpty();
    }

    [Fact]
    public async Task AddBatchStaged_StandaloneReceiptCannotMoveIntoAnotherTransaction()
    {
        await using var db = CreateDb();
        var work = new EfUnitOfWork(db);
        var staged = await new TitleRepository(db).AddBatchStagedAsync([
            Title.CreateNew(PlatformId, "Durable", "durable", DateTimeOffset.UtcNow)]);
        await work.FlushAsync();
        int durableId = staged.ReadIdsAfterFlush().Single();
        await using (var next = await work.BeginTransactionAsync())
        {
            Should.Throw<InvalidOperationException>(() => _ = staged.RequiresFlush);
            Should.Throw<InvalidOperationException>(() => staged.ReadIdsAfterFlush());
        }
        Should.Throw<InvalidOperationException>(() => staged.ReadIdsAfterFlush());
        await using var verify = CreateDb();
        (await verify.Titles.SingleAsync(row => row.Id == durableId)).Name.ShouldBe("Durable");
    }

    [Fact]
    public async Task AddBatchStaged_FlushedRollbackAndSameContextRetry_InvalidatesOriginalIds()
    {
        await using var db = CreateDb();
        var work = new EfUnitOfWork(db);
        var repository = new TitleRepository(db);
        IStagedTitleCreationBatch failed;
        int failedId;
        await using (var transaction = await work.BeginTransactionAsync())
        {
            failed = await repository.AddBatchStagedAsync([Title.CreateNew(PlatformId, "Retry", "retry", DateTimeOffset.UtcNow)]);
            await work.FlushAsync();
            failedId = failed.ReadIdsAfterFlush().Single();
        }
        db.ChangeTracker.Entries().ShouldBeEmpty();
        await using var retry = await work.BeginTransactionAsync();
        Should.Throw<InvalidOperationException>(() => _ = failed.RequiresFlush);
        var staged = await repository.AddBatchStagedAsync([Title.CreateNew(PlatformId, "Retry", "retry", DateTimeOffset.UtcNow)]);
        await work.FlushAsync();
        staged.ReadIdsAfterFlush().Single().ShouldNotBe(failedId);
        await retry.CommitAsync();
        await using var verify = CreateDb();
        (await verify.Titles.CountAsync(row => row.NormalizedName == "retry")).ShouldBe(1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MatchOrStage_CompetingMissingName_UsesExistingUniqueConstraintOutcomeWithoutRetry(bool callerTransaction)
    {
        await using var firstDb = CreateDb();
        await using var secondDb = CreateDb();
        var firstWork = new EfUnitOfWork(firstDb);
        var secondWork = new EfUnitOfWork(secondDb);
        await using var firstTx = callerTransaction ? await firstWork.BeginTransactionAsync() : null;
        await using var secondTx = callerTransaction ? await secondWork.BeginTransactionAsync() : null;
        // Both match decisions observe absence before either insertion is flushed.
        var first = await new TitleMatcher(new TitleRepository(firstDb), TimeProvider.System).MatchOrStageBatchAsync(PlatformId, ["Collision"]);
        var second = await new TitleMatcher(new TitleRepository(secondDb), TimeProvider.System).MatchOrStageBatchAsync(PlatformId, ["Collision"]);
        first.RequiresFlush.ShouldBeTrue();
        second.RequiresFlush.ShouldBeTrue();
        await firstWork.FlushAsync();
        int winner = first.ReadAfterFlush().Single().TitleId;
        if (firstTx is not null)
            await firstTx.CommitAsync();

        var error = await Should.ThrowAsync<DbUpdateException>(() => secondWork.FlushAsync());

        error.InnerException.ShouldBeOfType<PostgresException>().SqlState.ShouldBe(PostgresErrorCodes.UniqueViolation);
        Should.Throw<InvalidOperationException>(() => second.ReadAfterFlush());
        await using var verify = CreateDb();
        (await verify.Titles.SingleAsync(row => row.NormalizedName == "collision")).Id.ShouldBe(winner);
    }
}
