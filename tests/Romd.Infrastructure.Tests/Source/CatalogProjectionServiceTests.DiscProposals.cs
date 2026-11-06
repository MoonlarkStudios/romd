using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Npgsql;
using Romd.Admin.Application.Catalog;
using Romd.Admin.Application.Common.Persistence;
using Romd.Domain.Catalog;
using Romd.Domain.Identity;
using Romd.Infrastructure.Source;
using Romd.Persistence;
using Romd.Persistence.Catalog;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Source;

public sealed partial class CatalogProjectionServiceTests
{
    [Theory]
    [InlineData(ProposalFlushOutcome.Commit)]
    [InlineData(ProposalFlushOutcome.Fault)]
    [InlineData(ProposalFlushOutcome.Cancel)]
    [InlineData(ProposalFlushOutcome.Conflict)]
    public async Task RebuildPlatformAsync_ProposalOwnerFlush_KeepsSavepointRecoveryAndManualDecisionIntact(ProposalFlushOutcome outcome)
    {
        await using var db = CreateDb();
        SeedTitle(db, 10);
        SeedMappedGame(db, 1, 1, 1, 10, NewSha1(0x11), gameName: "Disc Game (USA) (Disc 1)");
        SeedMappedGame(db, 1, 2, 2, 10, NewSha1(0x22), gameName: "Disc Game (USA) (Disc 2)");
        await db.SaveChangesAsync();
        (await CreateService(db).RebuildPlatformAsync(PlatformId)).ShouldBeTrue();
        var accepted = await db.DiscSets.AsTracking().SingleAsync();
        accepted.Decision = "accepted";
        accepted.CorrectionJson = accepted.ProposalJson;
        accepted.Reason = "Keep this reviewed membership";
        accepted.DecidedBy = SystemActor.UserId;
        accepted.DecidedAt = DateTimeOffset.UtcNow;
        accepted.Revision = Guid.NewGuid();
        await db.SaveChangesAsync();
        var original = await db.DiscSets.AsNoTracking().SingleAsync();
        SeedMappedGame(db, 1, 3, 3, 10, NewSha1(0x33), gameName: "Disc Game (USA) (Disc 3)");
        await db.SaveChangesAsync();
        using var cancellation = new CancellationTokenSource();
        var work = new ObserveProposalFlush(new EfUnitOfWork(db), db, original.Revision, outcome, cancellation);
        var payload = Substitute.For<ITitlePayloadAvailabilityProjection>();
        bool payloadRefreshed = false;
        payload.RefreshPlatformPayloadAssertionsAsync(PlatformId, Arg.Any<CancellationToken>()).Returns(_ =>
        {
            work.SawPersistedProposal.ShouldBeTrue();
            payloadRefreshed = true;
            return Task.CompletedTask;
        });
        var service = new CatalogProjectionService(db,
            new CatalogSourceSnapshotReader([new DatCatalogSourceSnapshotProvider(db)]), payload, new DiscSetService(db), work,
            TimeProvider.System, Substitute.For<ILogger<CatalogProjectionService>>());

        if (outcome == ProposalFlushOutcome.Cancel)
            (await Should.ThrowAsync<OperationCanceledException>(() => service.RebuildPlatformAsync(PlatformId, cancellation.Token)))
                .CancellationToken.ShouldBe(cancellation.Token);
        else
            (await service.RebuildPlatformAsync(PlatformId, cancellation.Token)).ShouldBe(outcome == ProposalFlushOutcome.Commit);

        work.SawPersistedProposal.ShouldBeTrue();
        work.FlushCount.ShouldBe(1);
        work.BeginCalled.ShouldBeFalse();
        payloadRefreshed.ShouldBe(outcome == ProposalFlushOutcome.Commit);
        db.ChangeTracker.Entries().ShouldBeEmpty();
        await db.SaveChangesAsync();
        await using var verification = CreateDb();
        var current = await verification.DiscSets.AsNoTracking().SingleAsync();
        current.Id.ShouldBe(original.Id);
        current.CorrectionJson.ShouldBe(original.CorrectionJson);
        current.Decision.ShouldBe(original.Decision);
        current.DecidedBy.ShouldBe(original.DecidedBy);
        current.DecidedAt.ShouldBe(original.DecidedAt);
        current.Reason.ShouldBe(original.Reason);
        var platform = await verification.Platforms.AsNoTracking().SingleAsync();
        if (outcome == ProposalFlushOutcome.Commit)
        {
            current.Revision.ShouldNotBe(original.Revision);
            DiscSetService.Decode(current.ProposalJson).Members.Count.ShouldBe(3);
            (await verification.CatalogReleases.CountAsync()).ShouldBe(3);
            platform.CatalogRebuildState.ShouldBe(CatalogRebuildState.Clean);
        }
        else
        {
            current.Revision.ShouldBe(original.Revision);
            current.ProposalJson.ShouldBe(original.ProposalJson);
            (await verification.CatalogReleases.CountAsync()).ShouldBe(2);
            platform.CatalogRebuildState.ShouldBe(outcome == ProposalFlushOutcome.Fault ? CatalogRebuildState.Failed : CatalogRebuildState.Dirty);
            if (outcome == ProposalFlushOutcome.Fault)
            {
                platform.CatalogRebuildError.ShouldNotBeNull().ShouldContain("proposal flush");
                platform.CatalogRebuildFailedAtUtc.ShouldNotBeNull();
            }
        }
    }

    public enum ProposalFlushOutcome { Commit, Fault, Cancel, Conflict }

    private sealed class ObserveProposalFlush(IUnitOfWork inner, RomdDbContext db, Guid originalRevision, ProposalFlushOutcome outcome,
        CancellationTokenSource cancellation) : IUnitOfWork
    {
        public bool SawPersistedProposal { get; private set; }
        public int FlushCount { get; private set; }
        public bool BeginCalled { get; private set; }
        public TransactionConflict ConflictOf(Exception error) => inner.ConflictOf(error);
        public Task<ITransaction> BeginTransactionAsync(CancellationToken cancellationToken = default)
        {
            BeginCalled = true;
            throw new InvalidOperationException("The rebuild retains its existing raw transaction and savepoint owner.");
        }
        public async Task FlushAsync(CancellationToken cancellationToken = default)
        {
            FlushCount++;
            db.Database.CurrentTransaction.ShouldNotBeNull();
            await inner.FlushAsync(cancellationToken);
            var persisted = await db.DiscSets.AsNoTracking().SingleAsync(cancellationToken);
            SawPersistedProposal = persisted.Revision != originalRevision && DiscSetService.Decode(persisted.ProposalJson).Members.Count == 3;
            SawPersistedProposal.ShouldBeTrue();
            (await db.Platforms.AsNoTracking().SingleAsync(cancellationToken)).CatalogRebuildState.ShouldBe(CatalogRebuildState.Dirty);
            if (outcome == ProposalFlushOutcome.Cancel)
            {
                await cancellation.CancelAsync();
                cancellationToken.ThrowIfCancellationRequested();
            }
            if (outcome == ProposalFlushOutcome.Fault)
                throw new InvalidOperationException("Injected failure after proposal flush.");
            if (outcome == ProposalFlushOutcome.Conflict)
                throw new PostgresException("Injected conflict after proposal flush.", "ERROR", "ERROR", PostgresErrorCodes.DeadlockDetected);
        }
    }
}
