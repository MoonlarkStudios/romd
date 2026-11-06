using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Romd.Admin.Application.Catalog;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Source.Dat.Commands.AssignPlatform;
using Romd.Domain.Catalog;
using Romd.Infrastructure.Catalog;
using Romd.Infrastructure.Tests.Helpers;
using Romd.Persistence;
using Romd.Persistence.Catalog;
using Romd.Persistence.Repositories;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests;

public sealed partial class AdminMutationOutboxAtomicityTests
{
    private static readonly DateTimeOffset BiosCreatedAt = DateTimeOffset.UnixEpoch.AddTicks(1234567);

    [Theory]
    [InlineData(BiosFailure.None)]
    [InlineData(BiosFailure.AfterIdentityFlush)]
    [InlineData(BiosFailure.CancelAfterIdentityFlush)]
    [InlineData(BiosFailure.BeforeCommit)]
    public async Task AssignPlatform_BiosGrouping_CommitsOrRollsBackBiosMappingsAndRoutingTogether(BiosFailure failure)
    {
        await using var database = await TestDatabase.CreateAsync();
        await SeedCatalogMutationGraphAsync(database.Context, routed: false);
        const string biosName = "[BIOS] PSX (USA)";
        await database.Context.DatGames.Where(row => row.Id == 40).ExecuteUpdateAsync(setters => setters
            .SetProperty(row => row.IsBios, true).SetProperty(row => row.Name, biosName));
        await database.Context.SourceEntries.Where(row => row.Id == 30).ExecuteUpdateAsync(setters => setters
            .SetProperty(row => row.EntryKey, biosName).SetProperty(row => row.Name, biosName));
        await database.Context.TitleSourceLinks.ExecuteDeleteAsync();
        using var cancellation = new CancellationTokenSource();
        var work = new ObserveBiosTransaction(database.UnitOfWork, database.Context, cancellation, failure);
        var repository = new DatRepository(database.Context, TimeProvider.System);
        var handler = new AssignPlatformCommandHandler(repository,
            new TitleDerivationService(database.Context, new TitleMatcher(new TitleRepository(database.Context), TimeProvider.System), database.UnitOfWork),
            new PlatformRepository(database.Context),
            new BiosGrouper(new BiosRepository(database.Context), work, new ManualTimeProvider(BiosCreatedAt)),
            work, new LibraryRepository(database.Context),
            new RebuildSuppressingCatalogProjection(RealProjection(database)),
            NullLogger<AssignPlatformCommandHandler>.Instance, new SourceLifecycleStore(database.Context));

        if (failure == BiosFailure.CancelAfterIdentityFlush)
        {
            var error = await Should.ThrowAsync<OperationCanceledException>(() =>
                handler.HandleAsync(AssignPlatformCommand.Create(7, 10).Value, cancellation.Token));
            error.CancellationToken.ShouldBe(cancellation.Token);
        }
        else
            (await handler.HandleAsync(AssignPlatformCommand.Create(7, 10).Value)).IsError.ShouldBe(failure != BiosFailure.None);

        work.GeneratedBiosId.ShouldBeGreaterThan(0);
        if (failure is BiosFailure.None or BiosFailure.BeforeCommit)
        {
            work.SawFlushedMappingBeforeCommit.ShouldBeTrue();
            work.SawDirtyPlatformBeforeCommit.ShouldBeTrue();
        }
        bool committed = failure == BiosFailure.None;
        if (!committed)
            database.Context.ChangeTracker.Entries().ShouldBeEmpty();
        await database.Context.SaveChangesAsync();
        await using var read = database.CreateReadContext();
        (await read.Bios.CountAsync()).ShouldBe(committed ? 1 : 0);
        (await read.BiosGameMappings.CountAsync()).ShouldBe(committed ? 1 : 0);
        if (committed)
        {
            var bios = await read.Bios.SingleAsync();
            bios.Id.ShouldBe(work.GeneratedBiosId);
            bios.NormalizedName.ShouldBe("psxusa");
            bios.Name.ShouldBe(biosName);
            bios.CreatedAt.ShouldBe(DateTimeOffset.FromUnixTimeMilliseconds(BiosCreatedAt.ToUnixTimeMilliseconds()));
            (await read.BiosGameMappings.SingleAsync()).BiosId.ShouldBe(bios.Id);
        }
        (await read.DatFiles.SingleAsync(row => row.Id == 7)).PlatformId.ShouldBe(committed ? 10 : null);
        (await read.SourceEntries.SingleAsync(row => row.Id == 30)).PlatformId.ShouldBe(committed ? 10 : null);
        (await read.Platforms.SingleAsync(row => row.Id == 10)).CatalogRebuildState
            .ShouldBe(committed ? CatalogRebuildState.Dirty : CatalogRebuildState.Clean);
        (await read.Libraries.SingleAsync(row => row.Id == 3)).NeedsMaterialization.ShouldBeFalse();
        (await read.Jobs.CountAsync()).ShouldBe(0);
        (await read.JobDispatches.CountAsync()).ShouldBe(0);
        (await read.AdminRealtimeOutboxEvents.CountAsync()).ShouldBe(0);
    }

    public enum BiosFailure { None, AfterIdentityFlush, CancelAfterIdentityFlush, BeforeCommit }

    private sealed class ObserveBiosTransaction(
        IUnitOfWork inner, RomdDbContext context, CancellationTokenSource cancellation, BiosFailure failure) : IUnitOfWork
    {
        public int GeneratedBiosId { get; private set; }
        public bool SawFlushedMappingBeforeCommit { get; private set; }
        public bool SawDirtyPlatformBeforeCommit { get; private set; }
        public TransactionConflict ConflictOf(Exception error) => inner.ConflictOf(error);
        public async Task<ITransaction> BeginTransactionAsync(CancellationToken cancellationToken = default) =>
            new Transaction(this, await inner.BeginTransactionAsync(cancellationToken));

        public async Task FlushAsync(CancellationToken cancellationToken = default)
        {
            await inner.FlushAsync(cancellationToken);
            GeneratedBiosId = await context.Bios.Select(row => row.Id).SingleAsync(cancellationToken);
            if (failure == BiosFailure.CancelAfterIdentityFlush)
            {
                await cancellation.CancelAsync();
                cancellationToken.ThrowIfCancellationRequested();
            }
            if (failure == BiosFailure.AfterIdentityFlush)
                throw new InvalidOperationException("Injected failure after BIOS identity flush.");
        }

        private async Task BeforeCommitAsync(CancellationToken ct)
        {
            await inner.FlushAsync(ct);
            SawFlushedMappingBeforeCommit = await context.BiosGameMappings.AnyAsync(ct);
            SawDirtyPlatformBeforeCommit = await context.Platforms.AnyAsync(
                row => row.Id == 10 && row.CatalogRebuildState == CatalogRebuildState.Dirty, ct);
            if (failure == BiosFailure.BeforeCommit)
                throw new InvalidOperationException("Injected failure after BIOS mapping flush.");
        }

        private sealed class Transaction(ObserveBiosTransaction owner, ITransaction innerTransaction) : ITransaction
        {
            public async Task CommitAsync(CancellationToken cancellationToken = default)
            {
                await owner.BeforeCommitAsync(cancellationToken);
                await innerTransaction.CommitAsync(cancellationToken);
            }
            public Task RollbackAsync(CancellationToken cancellationToken = default) => innerTransaction.RollbackAsync(cancellationToken);
            public ValueTask DisposeAsync() => innerTransaction.DisposeAsync();
        }
    }
}
