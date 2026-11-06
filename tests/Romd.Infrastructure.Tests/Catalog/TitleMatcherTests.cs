using Microsoft.EntityFrameworkCore;
using Romd.Admin.Application.Titles.Matching;
using Romd.Infrastructure.Catalog;
using Romd.Persistence;
using Romd.Persistence.Entities;
using Romd.Persistence.Repositories;
using Romd.PostgreSql.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Catalog;

public sealed partial class TitleMatcherTests : IDisposable
{
    private const int PlatformId = 3;
    private readonly PostgreSqlTestDatabase _connection;
    private readonly DbContextOptions<RomdDbContext> _options;

    public TitleMatcherTests()
    {
        _connection = PostgreSqlTestDatabase.Create();
        _options = new DbContextOptionsBuilder<RomdDbContext>().UseNpgsql(_connection.ConnectionString).Options;

        using var db = CreateDb();
        db.Platforms.Add(new PlatformEntity
        {
            Id = PlatformId,
            Name = "Platform",
            Ownership = Romd.Domain.ReferenceData.ReferenceOwnership.Romd,
            BuiltInVersion = 1,
            BaseName = "Platform",
            BaseCompactLabel = "Platform",
            CanonicalKey = "platform",
            ShortName = "platform",
            CreatedAt = DateTimeOffset.UtcNow
        });
        db.Titles.Add(new TitleEntity
        {
            Id = 101,
            PlatformId = PlatformId,
            Name = "Existing",
            NormalizedName = "existing",
            EnrichmentStatus = "None",
            CreatedAt = DateTimeOffset.UtcNow
        });
        db.SaveChanges();
    }

    [Fact]
    public async Task MatchOrCreateBatch_MixedExistingNewAndDuplicates_ReturnsOrderedFlushedMatches()
    {
        await using var db = CreateDb();
        // Persistence retains its Unix-millisecond contract; Domain tests cover finer timestamp facts.
        var createdAt = new DateTimeOffset(2026, 11, 4, 12, 13, 14, TimeSpan.Zero).AddMilliseconds(123);
        var matcher = new TitleMatcher(new TitleRepository(db), clock: new FixedClock(createdAt));

        var staged = await matcher.MatchOrStageBatchAsync(
            PlatformId,
            ["Existing", "Alpha (USA)", "Beta", "Alpha (Europe)", "Existing"]);
        staged.RequiresFlush.ShouldBeTrue();
        Should.Throw<InvalidOperationException>(() => staged.ReadAfterFlush());
        await new EfUnitOfWork(db).FlushAsync();
        var matches = staged.ReadAfterFlush();

        matches.Count.ShouldBe(5);
        matches.Select(match => match.TitleWasCreated)
            .ShouldBe([false, true, true, false, false]);
        matches.ShouldAllBe(match => match.TitleId > 0);
        matches[0].TitleId.ShouldBe(101);
        matches[1].TitleId.ShouldBe(matches[3].TitleId);
        matches[0].TitleId.ShouldBe(matches[4].TitleId);
        (await db.Titles.SingleAsync(title => title.Id == matches[1].TitleId)).Name.ShouldBe("Alpha");
        (await db.Titles.CountAsync()).ShouldBe(3);
        (await db.Titles.SingleAsync(title => title.Id == matches[1].TitleId)).CreatedAt.ShouldBe(createdAt);
    }

    [Fact]
    public async Task MatchOrCreateBatch_LaterBatchWithSameNormalizedName_ReusesPersistedIdAndReportsNotCreated()
    {
        await using var firstDb = CreateDb();
        var firstMatcher = new TitleMatcher(new TitleRepository(firstDb), clock: TimeProvider.System);
        var firstStaged = await firstMatcher.MatchOrStageBatchAsync(PlatformId, ["Alpha (USA)"]);
        await new EfUnitOfWork(firstDb).FlushAsync();
        var first = firstStaged.ReadAfterFlush().ShouldHaveSingleItem();

        await using var secondDb = CreateDb();
        var secondMatcher = new TitleMatcher(new TitleRepository(secondDb), clock: TimeProvider.System);
        var secondStaged = await secondMatcher.MatchOrStageBatchAsync(PlatformId, ["Alpha (Europe)"]);
        secondStaged.RequiresFlush.ShouldBeFalse();
        var second = secondStaged.ReadAfterFlush().ShouldHaveSingleItem();

        first.TitleId.ShouldBeGreaterThan(0);
        second.TitleId.ShouldBe(first.TitleId);
        second.TitleWasCreated.ShouldBeFalse();
        (await secondDb.Titles.SingleAsync(title => title.Id == second.TitleId)).Name.ShouldBe("Alpha");
    }

    [Fact]
    public async Task MatchOrCreateBatch_MaximumBatchSize_ReturnsEveryPosition()
    {
        await using var db = CreateDb();
        var matcher = new TitleMatcher(new TitleRepository(db), clock: TimeProvider.System);
        TitleMatchingContract.MaxBatchSize.ShouldBe(500);
        var names = Enumerable.Repeat("Existing", TitleMatchingContract.MaxBatchSize).ToList();

        var staged = await matcher.MatchOrStageBatchAsync(PlatformId, names);
        staged.RequiresFlush.ShouldBeFalse();
        var matches = staged.ReadAfterFlush();

        matches.Count.ShouldBe(TitleMatchingContract.MaxBatchSize);
        matches.ShouldAllBe(match => match.TitleId == 101 && !match.TitleWasCreated);
        db.ChangeTracker.Entries().ShouldBeEmpty();
    }

    [Fact]
    public async Task MatchOrCreateBatch_ExceedsMaximumBatchSize_ThrowsProgrammerErrorBeforeQuerying()
    {
        await using var db = CreateDb();
        var matcher = new TitleMatcher(new TitleRepository(db), clock: TimeProvider.System);
        var names = Enumerable.Repeat("Existing", TitleMatchingContract.MaxBatchSize + 1).ToList();

        var exception = await Should.ThrowAsync<ArgumentOutOfRangeException>(
            () => matcher.MatchOrStageBatchAsync(PlatformId, names));

        exception.ParamName.ShouldBe("gameNames");
        exception.Message.ShouldContain("500");
        db.ChangeTracker.Entries().ShouldBeEmpty();
    }

    public void Dispose() => _connection.Dispose();

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private RomdDbContext CreateDb() => new(_options);
}
