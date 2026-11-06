using Microsoft.EntityFrameworkCore;
using Romd.Admin.Application.Common.Persistence;
using Romd.Application.Common.Security;
using Romd.Infrastructure.Tests.Helpers;
using Romd.Persistence;
using Romd.Persistence.Entities;
using Romd.Persistence.Repositories;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Enrichment;

public sealed partial class MetadataPolicyTests
{
    private static readonly DateTimeOffset AuditTime = new(2026, 11, 4, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Save_ReplaceMultipleFields_FlushesDeletionsOnceAndPreservesReplacementAuditFacts()
    {
        int id = await SeedAsync();
        var clock = new ManualTimeProvider(AuditTime);
        await using var db = AuditContext(clock);
        await SeedHistoricalDefaultsAsync(db, id);
        int developerId = (await db.PlatformFieldDefaults.SingleAsync(row => row.FieldName == "Developer")).Id;
        var originalIds = await db.PlatformFieldDefaults.Where(row => row.FieldName != "Developer").Select(row => row.Id).ToListAsync();
        var saved = await new PlatformFieldDefaultRepository(db).GetByPlatformIdAsync(id);
        clock.SetUtcNow(AuditTime.AddMinutes(1));
        var work = new ObserveDefaultDeletionFlush(new EfUnitOfWork(db), db);

        var result = await Handler(db, id, work: work).HandleAsync(new(id, Revision(saved), new Dictionary<string, string?>
        {
            ["Description"] = "dat",
            ["Genre"] = "user"
        }));

        result.IsError.ShouldBeFalse();
        work.FlushCount.ShouldBe(1);
        work.SawAllRequestedDeletions.ShouldBeTrue();
        await using var verification = database.CreateContext();
        var rows = await verification.PlatformFieldDefaults.OrderBy(row => row.FieldName).ToListAsync();
        rows.Select(row => (row.FieldName, row.SourceId)).ShouldBe([
            ("Description", "dat"), ("Developer", "igdb"), ("Genre", "user")]);
        rows.Where(row => row.FieldName != "Developer").ShouldAllBe(row =>
            !originalIds.Contains(row.Id) && row.CreatedAt == AuditTime.AddMinutes(1));
        rows.Single(row => row.FieldName == "Developer").Id.ShouldBe(developerId);
        rows.Single(row => row.FieldName == "Developer").CreatedAt.ShouldBe(AuditTime);
        var audit = await verification.AdminAuditEvents.Where(row => row.TargetType == "System metadata").ToListAsync();
        audit.Count(row => row.Action == "Deleted").ShouldBe(3);
        audit.Count(row => row.Action == "Added").ShouldBe(6); // Four original rows, two replacements.
        (await verification.Jobs.CountAsync()).ShouldBe(1);
        (await verification.JobDispatches.CountAsync()).ShouldBe(1);
        (await verification.AdminRealtimeOutboxEvents.CountAsync()).ShouldBe(1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Save_InterruptedAfterDeletionFlush_RestoresDefaultsAuditAndWorkIntent(bool cancel)
    {
        int id = await SeedAsync();
        var clock = new ManualTimeProvider(AuditTime);
        await using var db = AuditContext(clock);
        await SeedHistoricalDefaultsAsync(db, id);
        var originalIds = await db.PlatformFieldDefaults.OrderBy(row => row.Id).Select(row => row.Id).ToListAsync();
        var saved = await new PlatformFieldDefaultRepository(db).GetByPlatformIdAsync(id);
        using var cancellation = new CancellationTokenSource();
        var work = new ObserveDefaultDeletionFlush(new EfUnitOfWork(db), db, cancellation, cancel);

        var operation = () => Handler(db, id, work: work).HandleAsync(new(id, Revision(saved),
            new Dictionary<string, string?> { ["Description"] = "dat", ["Genre"] = null }), cancellation.Token);
        if (cancel)
        {
            var error = await Should.ThrowAsync<OperationCanceledException>(operation);
            error.CancellationToken.ShouldBe(cancellation.Token);
        }
        else
            (await operation()).FirstError.Code.ShouldBe("MetadataPolicy.SaveFailed");

        work.FlushCount.ShouldBe(1);
        work.SawAllRequestedDeletions.ShouldBeTrue();
        db.ChangeTracker.Entries().ShouldBeEmpty();
        await db.SaveChangesAsync();
        await using var verification = database.CreateContext();
        (await verification.PlatformFieldDefaults.OrderBy(row => row.Id).Select(row => row.Id).ToListAsync()).ShouldBe(originalIds);
        (await verification.PlatformFieldDefaults.ToListAsync()).ShouldAllBe(row => row.CreatedAt == AuditTime);
        (await verification.AdminAuditEvents.CountAsync()).ShouldBe(4);
        (await verification.Jobs.CountAsync()).ShouldBe(0);
        (await verification.JobDispatches.CountAsync()).ShouldBe(0);
        (await verification.AdminRealtimeOutboxEvents.CountAsync()).ShouldBe(0);
    }

    private RomdDbContext AuditContext(ManualTimeProvider clock) => new(database.CreateOptions(options =>
        options.AddInterceptors(new AuditInterceptor(new PolicyAuditContext(), clock))));

    private static async Task SeedHistoricalDefaultsAsync(RomdDbContext db, int platformId)
    {
        db.PlatformFieldDefaults.AddRange(
            new PlatformFieldDefaultEntity { PlatformId = platformId, FieldName = "description", SourceId = "igdb" },
            new PlatformFieldDefaultEntity { PlatformId = platformId, FieldName = "Description", SourceId = "igdb" },
            new PlatformFieldDefaultEntity { PlatformId = platformId, FieldName = "Genre", SourceId = "igdb" },
            new PlatformFieldDefaultEntity { PlatformId = platformId, FieldName = "Developer", SourceId = "igdb" });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
    }

    private sealed class PolicyAuditContext : IAuditContext
    {
        public Guid ActorId => Guid.Empty;
        public bool IsSystem => true;
    }

    private sealed class ObserveDefaultDeletionFlush(
        IUnitOfWork inner, RomdDbContext db, CancellationTokenSource? interruption = null, bool cancel = false) : IUnitOfWork
    {
        public int FlushCount { get; private set; }
        public bool SawAllRequestedDeletions { get; private set; }
        public TransactionConflict ConflictOf(Exception error) => inner.ConflictOf(error);
        public Task<ITransaction> BeginTransactionAsync(CancellationToken cancellationToken = default) =>
            inner.BeginTransactionAsync(cancellationToken);

        public async Task FlushAsync(CancellationToken cancellationToken = default)
        {
            await inner.FlushAsync(cancellationToken);
            FlushCount++;
            SawAllRequestedDeletions = !await db.PlatformFieldDefaults.AnyAsync(
                row => row.FieldName.ToLower() == "description" || row.FieldName == "Genre", cancellationToken);
            if (interruption is null)
                return;
            if (cancel)
            {
                await interruption.CancelAsync();
                cancellationToken.ThrowIfCancellationRequested();
            }
            throw new IOException("Injected failure after default deletion flush.");
        }
    }
}
