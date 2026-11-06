using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Romd.Admin.Application.Collections.Commands.CreateCollection;
using Romd.Admin.Application.Common.Persistence;
using Romd.Application.Common.Ids;
using Romd.Consumer.Application.Browse;
using Romd.Domain.Collections;
using Romd.Persistence;
using Romd.Persistence.Entities;
using Romd.Persistence.Queries;
using Romd.Persistence.ReferenceData;
using Romd.Persistence.Repositories;
using Romd.PostgreSql.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Persistence;

public sealed class CollectionCreationTransactionTests : IDisposable
{
    private readonly PostgreSqlTestDatabase _database = PostgreSqlTestDatabase.Create();

    public void Dispose() => _database.Dispose();

    [Fact]
    public async Task CreateCollection_ConcurrentSameName_ReturnsEachCreationsGeneratedIdentity()
    {
        await using var firstContext = _database.CreateContext();
        await using var secondContext = _database.CreateContext();
        var results = await Task.WhenAll(
            Handler(firstContext).HandleAsync(new CreateCollectionCommand(" Same name ", " first ", null, null)),
            Handler(secondContext).HandleAsync(new CreateCollectionCommand(" Same name ", " second ", null, null)));

        await using var assertion = _database.CreateContext();
        var rows = await assertion.Collections.AsNoTracking().ToListAsync();
        rows.Count.ShouldBe(2);
        results.ShouldAllBe(result => !result.IsError);
        results.Select(result => result.Value.Id).Distinct().Count().ShouldBe(2);
        for (int i = 0; i < results.Length; i++)
        {
            var summary = results[i].Value;
            var row = rows.Single(row => IdCoder.Encode(row.Id) == summary.Id);
            row.Id.ShouldBeGreaterThan(0);
            row.Description.ShouldBe(i == 0 ? "first" : "second");
            summary.Name.ShouldBe("Same name");
            summary.Description.ShouldBe(row.Description);
            summary.ItemCount.ShouldBe(0);
            summary.IsSystem.ShouldBeFalse();
            summary.SortOrder.ShouldBe(0);
            summary.SystemKey.ShouldBeNull();
            summary.CoverUrl.ShouldBeNull();
            summary.CreatedAt.ToUnixTimeMilliseconds().ShouldBe(row.CreatedAt.ToUnixTimeMilliseconds());
        }
    }

    [Fact]
    public async Task AddStagedAsync_WithoutTransaction_RejectsCreation()
    {
        await using var context = _database.CreateContext();

        await Should.ThrowAsync<InvalidOperationException>(() =>
            Repository(context).AddStagedAsync(Collection.CreateNew("Unprotected", DateTimeOffset.UnixEpoch)));

        context.ChangeTracker.Entries().ShouldBeEmpty();
    }

    [Fact]
    public async Task AddStagedAsync_ExistingCollection_RejectsBeforeTracking()
    {
        await using var context = _database.CreateContext();
        context.Collections.Add(new CollectionEntity { Id = 42, Name = "Existing", CreatedAt = DateTimeOffset.UtcNow });
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
        var repository = Repository(context);
        var existing = await repository.GetByIdAsync(42);
        existing.ShouldNotBeNull();
        await using var transaction = await new EfUnitOfWork(context).BeginTransactionAsync();

        var exception = await Should.ThrowAsync<ArgumentException>(() => repository.AddStagedAsync(existing));

        exception.ParamName.ShouldBe("collection");
        context.ChangeTracker.Entries().ShouldBeEmpty();
    }

    [Fact]
    public async Task AddStagedAsync_FlushThenRollback_DiscardsCreationAndDoesNotLeakIntoLaterFlush()
    {
        await using var context = _database.CreateContext();
        var work = new EfUnitOfWork(context);
        await using var transaction = await work.BeginTransactionAsync();
        var creation = await Repository(context).AddStagedAsync(Collection.CreateNew("Rolled back", DateTimeOffset.UnixEpoch));
        Should.Throw<InvalidOperationException>(() => creation.ReadAfterFlush());

        await work.FlushAsync();

        creation.ReadAfterFlush().Id.ShouldBeGreaterThan(0);
        (await context.Collections.CountAsync()).ShouldBe(1);
        await using (var beforeCommit = _database.CreateContext())
            (await beforeCommit.Collections.CountAsync()).ShouldBe(0);
        await transaction.RollbackAsync();
        await work.FlushAsync();

        await using var assertion = _database.CreateContext();
        (await assertion.Collections.CountAsync()).ShouldBe(0);
        context.ChangeTracker.Entries().ShouldBeEmpty();
    }

    [Fact]
    public async Task CreateCollection_CommitFailsAfterFlush_RollsBackCreationAndClearsStaging()
    {
        var failure = new FailingCommitInterceptor();
        await using var context = new RomdDbContext(_database.CreateOptions(builder => builder.AddInterceptors(failure)));

        var exception = await Should.ThrowAsync<InvalidOperationException>(() =>
            Handler(context).HandleAsync(new CreateCollectionCommand("Failed commit", null, null, null)));

        exception.Message.ShouldBe("Simulated commit failure.");
        failure.SawFlushedCreation.ShouldBeTrue();
        context.ChangeTracker.Entries().ShouldBeEmpty();
        await context.SaveChangesAsync();
        await using var assertion = _database.CreateContext();
        (await assertion.Collections.CountAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task CreateCollection_CancelledAfterFlush_RollsBackCreationAndPreservesCancellation()
    {
        await using var context = _database.CreateContext();
        using var cancellation = new CancellationTokenSource();
        var work = new CancelAfterFlushUnitOfWork(new EfUnitOfWork(context), cancellation);

        var exception = await Should.ThrowAsync<OperationCanceledException>(() =>
            Handler(context, work).HandleAsync(new CreateCollectionCommand("Cancelled", null, null, null), cancellation.Token));

        exception.CancellationToken.ShouldBe(cancellation.Token);
        work.Flushed.ShouldBeTrue();
        context.ChangeTracker.Entries().ShouldBeEmpty();
        await context.SaveChangesAsync();
        await using var assertion = _database.CreateContext();
        (await assertion.Collections.CountAsync()).ShouldBe(0);
    }

    private static CollectionRepository Repository(RomdDbContext context) =>
        new(context, Substitute.For<IConsumerReleaseSelector>());

    private static CreateCollectionCommandHandler Handler(RomdDbContext context, IUnitOfWork? work = null) =>
        new(new ReferenceCatalogService(context, TestReferenceBlobs.For(context)), Repository(context),
            new CollectionCoverMediaReader(context), work ?? new EfUnitOfWork(context), TimeProvider.System,
            NullLogger<CreateCollectionCommandHandler>.Instance);

    private sealed class FailingCommitInterceptor : DbTransactionInterceptor
    {
        public bool SawFlushedCreation { get; private set; }

        public override async ValueTask<InterceptionResult> TransactionCommittingAsync(
            DbTransaction transaction,
            TransactionEventData eventData,
            InterceptionResult result,
            CancellationToken cancellationToken = default)
        {
            var context = (RomdDbContext)eventData.Context!;
            SawFlushedCreation = await context.Collections.AnyAsync(cancellationToken);
            throw new InvalidOperationException("Simulated commit failure.");
        }
    }

    private sealed class CancelAfterFlushUnitOfWork(IUnitOfWork inner, CancellationTokenSource cancellation) : IUnitOfWork
    {
        public bool Flushed { get; private set; }

        public async Task FlushAsync(CancellationToken cancellationToken = default)
        {
            await inner.FlushAsync(cancellationToken);
            Flushed = true;
            cancellation.Cancel();
        }

        public Task<ITransaction> BeginTransactionAsync(CancellationToken cancellationToken = default) =>
            inner.BeginTransactionAsync(cancellationToken);

        public TransactionConflict ConflictOf(Exception error) => inner.ConflictOf(error);
    }
}
