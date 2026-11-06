using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Titles.Commands.MoveGame;
using Romd.Infrastructure.Tests.Helpers;
using Romd.Persistence;
using Romd.Persistence.ReferenceData;
using Romd.Persistence.Repositories;
using Romd.PostgreSql.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests;

public sealed partial class AdminMutationOutboxAtomicityTests
{
    private static readonly DateTimeOffset SplitCreatedAt = new DateTimeOffset(2026, 11, 4, 12, 34, 56, TimeSpan.Zero).AddTicks(1234560);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MoveGame_NewTitle_CommitOutcome_KeepsCreationAssignmentAndFlagsAtomic(bool failCommit)
    {
        await using var database = await TestDatabase.CreateAsync(failCommit ? 1 : null);
        await SeedCatalogMutationGraphAsync(database.Context, routed: true);
        var handler = NewTitleMoveHandler(database, database.UnitOfWork);
        if (failCommit)
            await Should.ThrowAsync<InvalidOperationException>(() => handler.HandleAsync(new MoveGameCommand(101, 40, null, "Split")));
        else
        {
            var result = await handler.HandleAsync(new MoveGameCommand(101, 40, null, "Split"));
            result.IsError.ShouldBeFalse();
            result.Value.TitleCreated.ShouldBeTrue();
        }

        await using var read = database.CreateReadContext();
        var created = await read.Titles.SingleOrDefaultAsync(row => row.NormalizedName == "split");
        int assignment = (await read.TitleSourceLinks.SingleAsync()).TitleId;
        if (failCommit)
        {
            created.ShouldBeNull();
            assignment.ShouldBe(101);
        }
        else
        {
            created.ShouldNotBeNull();
            created.CreatedAt.ShouldBe(DateTimeOffset.FromUnixTimeMilliseconds(SplitCreatedAt.ToUnixTimeMilliseconds()));
            assignment.ShouldBe(created.Id);
        }
        (await read.Titles.AnyAsync(row => row.Id == 101)).ShouldBe(failCommit);
        (await read.Jobs.CountAsync()).ShouldBe(0);
        (await read.JobDispatches.CountAsync()).ShouldBe(0);
        (await read.AdminRealtimeOutboxEvents.CountAsync()).ShouldBe(0);
        await AssertCatalogEffectsAsync(read, failCommit);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MoveGame_NewTitle_InterruptedAfterIdentityFlush_RollsBackCreationAndOriginalAssignment(bool cancel)
    {
        await using var database = await TestDatabase.CreateAsync();
        await SeedCatalogMutationGraphAsync(database.Context, routed: true);
        using var cancellation = new CancellationTokenSource();
        var work = new InterruptTitleCreationFlush(database.UnitOfWork, database.Context, cancellation, cancel);
        var operation = () => NewTitleMoveHandler(database, work)
            .HandleAsync(new MoveGameCommand(101, 40, null, "Split"), cancellation.Token);
        if (cancel)
        {
            var error = await Should.ThrowAsync<OperationCanceledException>(operation);
            error.CancellationToken.ShouldBe(cancellation.Token);
        }
        else
            await Should.ThrowAsync<InvalidOperationException>(operation);

        work.GeneratedId.ShouldBeGreaterThan(0);
        work.FlushCount.ShouldBe(1);
        database.Context.ChangeTracker.Entries().ShouldBeEmpty();
        await database.Context.SaveChangesAsync();
        await using var read = database.CreateReadContext();
        (await read.Titles.AnyAsync(row => row.NormalizedName == "split")).ShouldBeFalse();
        (await read.Titles.AnyAsync(row => row.Id == 101)).ShouldBeTrue();
        (await read.TitleSourceLinks.SingleAsync()).TitleId.ShouldBe(101);
        (await read.Jobs.CountAsync()).ShouldBe(0);
        (await read.JobDispatches.CountAsync()).ShouldBe(0);
        (await read.AdminRealtimeOutboxEvents.CountAsync()).ShouldBe(0);
        await AssertCatalogEffectsAsync(read, rolledBack: true);
    }

    private static MoveGameCommandHandler NewTitleMoveHandler(TestDatabase database, IUnitOfWork work) =>
        new(new ReferenceCatalogService(database.Context, TestReferenceBlobs.For(database.Context)),
            new TitleRepository(database.Context), new TitleOrphanRepository(database.Context),
            new TitleReadRepository(database.Context), new TitleRepository(database.Context),
            new DatRepository(database.Context, TimeProvider.System), work,
            new LibraryRepository(database.Context), new RebuildSuppressingCatalogProjection(RealProjection(database)),
            NullLogger<MoveGameCommandHandler>.Instance, new ManualTimeProvider(SplitCreatedAt));

    private sealed class InterruptTitleCreationFlush(
        IUnitOfWork inner, RomdDbContext context, CancellationTokenSource cancellation, bool cancel) : IUnitOfWork
    {
        public int GeneratedId { get; private set; }
        public int FlushCount { get; private set; }
        public TransactionConflict ConflictOf(Exception error) => inner.ConflictOf(error);
        public Task<ITransaction> BeginTransactionAsync(CancellationToken cancellationToken = default) =>
            inner.BeginTransactionAsync(cancellationToken);

        public async Task FlushAsync(CancellationToken cancellationToken = default)
        {
            await inner.FlushAsync(cancellationToken);
            FlushCount++;
            GeneratedId = await context.Titles.Where(row => row.NormalizedName == "split")
                .Select(row => row.Id).SingleAsync(cancellationToken);
            if (cancel)
            {
                await cancellation.CancelAsync();
                cancellationToken.ThrowIfCancellationRequested();
            }
            throw new InvalidOperationException("Injected failure after title identity flush.");
        }
    }
}
