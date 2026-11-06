using ErrorOr;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Titles;
using Romd.Admin.Application.Titles.Commands.SetEnrichmentOmission;
using Romd.Admin.Application.Titles.ReadModels;
using Romd.Application.Common.Concurrency;
using Romd.Application.Common.ReferenceCatalog;
using Romd.Application.Common.Systems;
using Romd.Persistence;
using Romd.Persistence.Enrichment;
using Romd.Persistence.Entities;
using Romd.Persistence.Queries;
using Romd.Persistence.Repositories;
using Romd.PostgreSql.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Persistence;

public sealed class SetEnrichmentOmissionAtomicityTests : IDisposable
{
    private readonly PostgreSqlTestDatabase _database = PostgreSqlTestDatabase.Create();
    private static readonly DateTimeOffset AcceptedAt = DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_123);
    public void Dispose() => _database.Dispose();

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SetOmissionAsync_StagesDecisionUntilCallerFlushAndRollbackRestoresRevision(bool accepted)
    {
        Guid revision = await SeedAsync(!accepted);
        await using var db = _database.CreateContext();
        var work = new EfUnitOfWork(db);
        await using (await work.BeginTransactionAsync())
        {
            var result = await Workbench(db).SetOmissionAsync(1, "Description", accepted, RevisionPrecondition.For(revision.ToString()), default);

            result.IsError.ShouldBeFalse();
            (await db.Set<EnrichmentOmissionEntity>().AnyAsync()).ShouldBe(!accepted);
            await work.FlushAsync();
            (await db.Set<EnrichmentOmissionEntity>().AnyAsync()).ShouldBe(accepted);
            (await db.Titles.Select(row => row.Revision).SingleAsync()).ShouldNotBe(revision);
        }

        await AssertOriginalAsync(revision, !accepted);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task HandleAsync_Success_FlushesDecisionBeforeResponseAndCommitsReturnedRevision(bool accepted)
    {
        Guid revision = await SeedAsync(!accepted);
        await using var db = _database.CreateContext();
        var details = Substitute.For<ITitleDetailReader>();
        details.GetDetailAsync(1, Arg.Any<CancellationToken>()).Returns(async call =>
        {
            (await db.Set<EnrichmentOmissionEntity>().AnyAsync(call.Arg<CancellationToken>())).ShouldBe(accepted);
            return await new TitleDetailReader(db).GetDetailAsync(1, call.Arg<CancellationToken>());
        });

        var result = await Handler(db, new EfUnitOfWork(db), details).HandleAsync(Command(accepted, revision));

        result.IsError.ShouldBeFalse();
        await using var read = _database.CreateContext();
        var current = await read.Titles.Select(row => row.Revision).SingleAsync();
        current.ShouldNotBe(revision);
        result.Value.Revision.ShouldBe(current.ToString());
        (await read.Set<EnrichmentOmissionEntity>().AnyAsync()).ShouldBe(accepted);
        if (accepted)
            (await read.Set<EnrichmentOmissionEntity>().SingleAsync()).AcceptedAt.ShouldBe(AcceptedAt);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public async Task HandleAsync_FaultOrCancellationAfterActualFlush_RollsBackDecisionAndRevision(bool accepted, bool cancel)
    {
        Guid revision = await SeedAsync(!accepted);
        await using var db = _database.CreateContext();
        using var cancellation = new CancellationTokenSource();
        var work = new AfterFlush(new EfUnitOfWork(db), async () =>
        {
            (await db.Set<EnrichmentOmissionEntity>().AnyAsync()).ShouldBe(accepted);
            (await db.Titles.Select(row => row.Revision).SingleAsync()).ShouldNotBe(revision);
            await AssertOriginalAsync(revision, !accepted);
            if (cancel)
                cancellation.Cancel();
            else
                throw new InvalidOperationException("Injected failure after omission flush.");
        });
        var handler = Handler(db, work);

        if (cancel)
            await Should.ThrowAsync<OperationCanceledException>(() => handler.HandleAsync(Command(accepted, revision), cancellation.Token));
        else
            (await Should.ThrowAsync<InvalidOperationException>(() => handler.HandleAsync(Command(accepted, revision)))).Message.ShouldBe("Injected failure after omission flush.");

        work.Flushes.ShouldBe(1);
        work.Commits.ShouldBe(0);
        db.ChangeTracker.Entries().ShouldBeEmpty();
        await AssertOriginalAsync(revision, !accepted);
    }

    [Fact]
    public async Task HandleAsync_FieldBecamePresent_RollsBackItsConditionalRevisionRotation()
    {
        Guid revision = await SeedAsync(false, "Populated");
        await using var db = _database.CreateContext();

        var result = await Handler(db, new EfUnitOfWork(db)).HandleAsync(Command(true, revision));

        result.FirstError.Code.ShouldBe("Enrichment.FieldPresent");
        await AssertOriginalAsync(revision, false);
    }

    [Fact]
    public async Task HandleAsync_StaleRevision_LeavesDecisionAndRevisionUnchanged()
    {
        Guid revision = await SeedAsync(false);
        await using var db = _database.CreateContext();

        var result = await Handler(db, new EfUnitOfWork(db)).HandleAsync(Command(true, Guid.NewGuid()));

        result.FirstError.Code.ShouldBe("Titles.PreconditionFailed");
        await AssertOriginalAsync(revision, false);
    }

    [Fact]
    public async Task HandleAsync_ConcurrentSameRevision_OneCommitsAndOneFailsPrecondition()
    {
        Guid revision = await SeedAsync(false);
        async Task<ErrorOr<Romd.Contracts.Management.Models.TitleDetail>> AcceptAsync()
        {
            await using var db = _database.CreateContext();
            return await Handler(db, new EfUnitOfWork(db)).HandleAsync(Command(true, revision));
        }

        var results = await Task.WhenAll(AcceptAsync(), AcceptAsync());

        results.Count(result => !result.IsError).ShouldBe(1);
        results.Single(result => result.IsError).FirstError.Code.ShouldBe("Titles.PreconditionFailed");
        await using var read = _database.CreateContext();
        (await read.Set<EnrichmentOmissionEntity>().CountAsync()).ShouldBe(1);
        results.Single(result => !result.IsError).Value.Revision.ShouldBe((await read.Titles.Select(row => row.Revision).SingleAsync()).ToString());
    }

    private async Task<Guid> SeedAsync(bool omission, string? description = null)
    {
        await using var db = _database.CreateContext();
        Guid revision = Guid.NewGuid();
        db.Platforms.Add(new() { Id = 1, Name = "System", ShortName = "system" });
        db.Titles.Add(new() { Id = 1, PlatformId = 1, Name = "Title", NormalizedName = "TITLE", EnrichmentStatus = "None", Revision = revision, Description = description });
        if (omission)
            db.Add(new EnrichmentOmissionEntity { TitleId = 1, Field = "Description", AcceptedAt = AcceptedAt });
        await db.SaveChangesAsync();
        return revision;
    }

    private async Task AssertOriginalAsync(Guid revision, bool omission)
    {
        await using var read = _database.CreateContext();
        (await read.Titles.Select(row => row.Revision).SingleAsync()).ShouldBe(revision);
        (await read.Set<EnrichmentOmissionEntity>().AnyAsync()).ShouldBe(omission);
    }

    private static SetEnrichmentOmissionCommand Command(bool accepted, Guid revision) => new(1, "Description", accepted, RevisionPrecondition.For(revision.ToString()));
    private static EnrichmentWorkbench Workbench(RomdDbContext db) => new(db, new FixedClock(), new ArtworkReader(db));
    private static SetEnrichmentOmissionCommandHandler Handler(RomdDbContext db, IUnitOfWork work, ITitleDetailReader? details = null)
    {
        var references = Substitute.For<IReferenceCatalogService>();
        references.GetSystemKeysAsync(Arg.Any<CancellationToken>()).Returns(new SystemKeys(new Dictionary<int, string> { [1] = "system" }));
        return new(references, Workbench(db), details ?? new TitleDetailReader(db), work, Microsoft.Extensions.Logging.Abstractions.NullLogger<SetEnrichmentOmissionCommandHandler>.Instance);
    }
    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => AcceptedAt;
    }

    private sealed class AfterFlush(IUnitOfWork inner, Func<Task> afterFlush) : IUnitOfWork
    {
        public int Flushes { get; private set; }
        public int Commits { get; private set; }
        public async Task FlushAsync(CancellationToken cancellationToken = default)
        {
            await inner.FlushAsync(cancellationToken);
            Flushes++;
            await afterFlush();
        }
        public TransactionConflict ConflictOf(Exception error) => inner.ConflictOf(error);
        public async Task<ITransaction> BeginTransactionAsync(CancellationToken cancellationToken = default) => new Transaction(await inner.BeginTransactionAsync(cancellationToken), this);
        private sealed class Transaction(ITransaction transaction, AfterFlush owner) : ITransaction
        {
            public Task CommitAsync(CancellationToken cancellationToken = default) { owner.Commits++; return transaction.CommitAsync(cancellationToken); }
            public Task RollbackAsync(CancellationToken cancellationToken = default) => transaction.RollbackAsync(cancellationToken);
            public ValueTask DisposeAsync() => transaction.DisposeAsync();
        }
    }
}
