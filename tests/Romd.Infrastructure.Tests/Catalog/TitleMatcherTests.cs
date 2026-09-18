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

public sealed class TitleMatcherTests : IDisposable
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
            Ownership = Romd.Domain.ReferenceData.ReferenceOwnership.Romd, BuiltInVersion = 1, BaseName = "Platform", BaseCompactLabel = "Platform", CanonicalKey = "platform", ShortName = "platform",
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
        var matcher = new TitleMatcher(new TitleRepository(db));

        var matches = await matcher.MatchOrCreateBatchAsync(
            PlatformId,
            ["Existing", "Alpha (USA)", "Beta", "Alpha (Europe)", "Existing"]);

        matches.Count.ShouldBe(5);
        matches.Select(match => match.TitleWasCreated)
            .ShouldBe([false, true, true, false, false]);
        matches.ShouldAllBe(match => match.TitleId > 0);
        matches[0].TitleId.ShouldBe(101);
        matches[1].TitleId.ShouldBe(matches[3].TitleId);
        matches[0].TitleId.ShouldBe(matches[4].TitleId);
        (await db.Titles.SingleAsync(title => title.Id == matches[1].TitleId)).Name.ShouldBe("Alpha");
        (await db.Titles.CountAsync()).ShouldBe(3);
    }

    [Fact]
    public async Task MatchOrCreateBatch_LaterBatchWithSameNormalizedName_ReusesPersistedIdAndReportsNotCreated()
    {
        await using var firstDb = CreateDb();
        var firstMatcher = new TitleMatcher(new TitleRepository(firstDb));
        var first = (await firstMatcher.MatchOrCreateBatchAsync(PlatformId, ["Alpha (USA)"]))
            .ShouldHaveSingleItem();

        await using var secondDb = CreateDb();
        var secondMatcher = new TitleMatcher(new TitleRepository(secondDb));
        var second = (await secondMatcher.MatchOrCreateBatchAsync(PlatformId, ["Alpha (Europe)"]))
            .ShouldHaveSingleItem();

        first.TitleId.ShouldBeGreaterThan(0);
        second.TitleId.ShouldBe(first.TitleId);
        second.TitleWasCreated.ShouldBeFalse();
        (await secondDb.Titles.SingleAsync(title => title.Id == second.TitleId)).Name.ShouldBe("Alpha");
    }

    [Fact]
    public async Task MatchOrCreateBatch_MaximumBatchSize_ReturnsEveryPosition()
    {
        await using var db = CreateDb();
        var matcher = new TitleMatcher(new TitleRepository(db));
        TitleMatchingContract.MaxBatchSize.ShouldBe(500);
        var names = Enumerable.Repeat("Existing", TitleMatchingContract.MaxBatchSize).ToList();

        var matches = await matcher.MatchOrCreateBatchAsync(PlatformId, names);

        matches.Count.ShouldBe(TitleMatchingContract.MaxBatchSize);
        matches.ShouldAllBe(match => match.TitleId == 101 && !match.TitleWasCreated);
        db.ChangeTracker.Entries().ShouldBeEmpty();
    }

    [Fact]
    public async Task MatchOrCreateBatch_ExceedsMaximumBatchSize_ThrowsProgrammerErrorBeforeQuerying()
    {
        await using var db = CreateDb();
        var matcher = new TitleMatcher(new TitleRepository(db));
        var names = Enumerable.Repeat("Existing", TitleMatchingContract.MaxBatchSize + 1).ToList();

        var exception = await Should.ThrowAsync<ArgumentOutOfRangeException>(
            () => matcher.MatchOrCreateBatchAsync(PlatformId, names));

        exception.ParamName.ShouldBe("gameNames");
        exception.Message.ShouldContain("500");
        db.ChangeTracker.Entries().ShouldBeEmpty();
    }

    public void Dispose() => _connection.Dispose();

    private RomdDbContext CreateDb() => new(_options);
}
