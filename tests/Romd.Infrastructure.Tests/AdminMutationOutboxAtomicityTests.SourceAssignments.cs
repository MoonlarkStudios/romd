using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Titles.Commands.MoveGame;
using Romd.Persistence;
using Romd.Persistence.ReferenceData;
using Romd.Persistence.Repositories;
using Romd.PostgreSql.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests;

public sealed partial class AdminMutationOutboxAtomicityTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task TitleAssignment_FailureAfterCallerFlush_RollsBackAssignmentsRevisionsAndIntent(
        bool mergeTitles, bool cancel)
    {
        await using var database = await TestDatabase.CreateAsync();
        await SeedCatalogMutationGraphAsync(database.Context, routed: true, secondSourceEntry: mergeTitles);
        var initialRevisions = await database.Context.Titles.OrderBy(row => row.Id)
            .Select(row => row.Revision).ToListAsync();
        using var cancellation = new CancellationTokenSource();
        var work = new InterruptAssignmentFlushUnitOfWork(database.UnitOfWork, database.Context, cancellation, cancel);

        async Task ActAsync()
        {
            if (mergeTitles)
            {
                await NewMergeHandler(database, work).HandleAsync(
                    await MergeCommandAsync(database.Context, 101, 202), cancellation.Token);
            }
            else
            {
                var handler = new MoveGameCommandHandler(
                    new ReferenceCatalogService(database.Context, TestReferenceBlobs.For(database.Context)),
                    new TitleRepository(database.Context), new TitleOrphanRepository(database.Context),
                    new TitleReadRepository(database.Context), new TitleRepository(database.Context),
                    new DatRepository(database.Context, TimeProvider.System), work,
                    new LibraryRepository(database.Context),
                    new RebuildSuppressingCatalogProjection(RealProjection(database)),
                    NullLogger<MoveGameCommandHandler>.Instance, TimeProvider.System);
                await handler.HandleAsync(new MoveGameCommand(101, 40, 202, null), cancellation.Token);
            }
        }

        if (cancel)
        {
            var error = await Should.ThrowAsync<OperationCanceledException>(ActAsync);
            error.CancellationToken.ShouldBe(cancellation.Token);
        }
        else
        {
            var error = await Should.ThrowAsync<InvalidOperationException>(ActAsync);
            error.Message.ShouldBe("Injected failure after assignment flush.");
        }

        work.SawUnflushedAssignments.ShouldBeTrue();
        work.SawFlushedAssignments.ShouldBeTrue();
        database.Context.ChangeTracker.Entries().ShouldBeEmpty();
        await database.Context.SaveChangesAsync();
        await using var read = database.CreateReadContext();
        (await read.TitleSourceLinks.AllAsync(link => link.TitleId == 101)).ShouldBeTrue();
        (await read.TitleSourceLinks.CountAsync()).ShouldBe(mergeTitles ? 2 : 1);
        (await read.Titles.OrderBy(row => row.Id).Select(row => row.Revision).ToListAsync())
            .ShouldBe(initialRevisions);
        await AssertCatalogEffectsAsync(read, rolledBack: true);
    }

    private sealed class InterruptAssignmentFlushUnitOfWork(
        IUnitOfWork inner, RomdDbContext context, CancellationTokenSource cancellation, bool cancel) : IUnitOfWork
    {
        public bool SawUnflushedAssignments { get; private set; }
        public bool SawFlushedAssignments { get; private set; }

        public TransactionConflict ConflictOf(Exception error) => inner.ConflictOf(error);

        public Task<ITransaction> BeginTransactionAsync(CancellationToken cancellationToken = default) =>
            inner.BeginTransactionAsync(cancellationToken);

        public async Task FlushAsync(CancellationToken cancellationToken = default)
        {
            SawUnflushedAssignments = await context.TitleSourceLinks.AllAsync(link => link.TitleId == 101, cancellationToken);
            await inner.FlushAsync(cancellationToken);
            SawFlushedAssignments = await context.TitleSourceLinks.AllAsync(link => link.TitleId == 202, cancellationToken);
            if (cancel)
            {
                await cancellation.CancelAsync();
                cancellationToken.ThrowIfCancellationRequested();
            }
            throw new InvalidOperationException("Injected failure after assignment flush.");
        }
    }
}
