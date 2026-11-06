using Microsoft.EntityFrameworkCore;
using Romd.Domain.Catalog;
using Romd.Domain.Catalog.Ratings;
using Romd.Persistence;
using Romd.Persistence.Entities;
using Romd.Persistence.Repositories;
using Romd.PostgreSql.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Persistence;

/// <summary>
///     Repository sync semantics for materialized per-board rating rows: replace-set
///     keyed by (TitleId, Board), guarded so that a title loaded without its rating
///     collection can never wipe persisted rows.
/// </summary>
public class TitleContentRatingPersistenceTests : IDisposable
{
    private readonly PostgreSqlTestDatabase _connection;
    private readonly DbContextOptions<RomdDbContext> _contextOptions;

    public TitleContentRatingPersistenceTests()
    {
        _connection = PostgreSqlTestDatabase.Create();

        _contextOptions = new DbContextOptionsBuilder<RomdDbContext>()
            .UseNpgsql(_connection.ConnectionString)
            .Options;

        using var db = new RomdDbContext(_contextOptions);

        db.Platforms.Add(new PlatformEntity
        {
            Id = 1,
            Name = "Super Nintendo",
            Ownership = Romd.Domain.ReferenceData.ReferenceOwnership.Romd,
            BuiltInVersion = 1,
            BaseName = "Super Nintendo",
            BaseCompactLabel = "Super Nintendo",
            CanonicalKey = "snes",
            ShortName = "snes",
            Manufacturer = "Nintendo",
            CreatedAt = DateTimeOffset.UtcNow
        });
        db.SaveChanges();
    }

    public void Dispose()
    {
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    private RomdDbContext CreateDb() => new(_contextOptions);

    private static async Task ApplyEnrichmentAsync(RomdDbContext db, TitleRepository repo, Title title)
    {
        await using var transaction = await db.Database.BeginTransactionAsync();
        await repo.ApplyEnrichmentStagedAsync(title);
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
    }

    private static TitleMetadataPayload PayloadWithClaims(params ContentRatingClaim[] claims) =>
        new() { ContentRatings = claims };

    private static ContentRatingClaim Claim(RatingBoard board, string rawCode) =>
        new() { Board = board, RawCode = rawCode };

    private async Task<Title> CreateEnrichedTitleAsync(params ContentRatingClaim[] claims)
    {
        int titleId;
        using (var db = CreateDb())
        {
            var repo = new TitleRepository(db);
            var work = new EfUnitOfWork(db);
            await using var transaction = await work.BeginTransactionAsync();
            var creation = await repo.AddStagedAsync(Title.CreateNew(1, "Super Mario World", "super mario world", createdAt: DateTimeOffset.UnixEpoch));
            await work.FlushAsync();
            titleId = creation.ReadIdAfterFlush();
            await transaction.CommitAsync();
        }

        using (var db = CreateDb())
        {
            var repo = new TitleRepository(db);
            var title = (await repo.GetAggregateAsync(titleId))!;
            title.StoreProviderLayer("igdb", MetadataSourceType.Provider, PayloadWithClaims(claims), updatedAt: DateTimeOffset.UnixEpoch);
            title.Rematerialize(["igdb"]);
            await ApplyEnrichmentAsync(db, repo, title);
        }

        using (var db = CreateDb())
        {
            var repo = new TitleRepository(db);
            return (await repo.GetAggregateAsync(titleId))!;
        }
    }

    [Fact]
    public async Task ApplyEnrichmentStagedAsync_MaterializedRatings_PersistsOneRowPerBoard()
    {
        var title = await CreateEnrichedTitleAsync(
            Claim(RatingBoard.Esrb, "E10"),
            Claim(RatingBoard.Pegi, "12"));

        title.ContentRatingsMaterialized.ShouldBeTrue();
        title.ContentRatings.Count.ShouldBe(2);
        title.ConservativeMinimumAge.ShouldBe(12);

        var esrb = title.ContentRatings.Single(r => r.Board == RatingBoard.Esrb);
        esrb.Code.ShouldBe("E10+");
        esrb.MinimumAge.ShouldBe(10);
        esrb.SourceId.ShouldBe("igdb");
    }

    [Fact]
    public async Task ApplyEnrichmentStagedAsync_RatingChanged_UpdatesRowInPlaceWithoutDuplicates()
    {
        var title = await CreateEnrichedTitleAsync(Claim(RatingBoard.Esrb, "T"));
        int titleId = title.Id;

        using (var db = CreateDb())
        {
            var repo = new TitleRepository(db);
            var loaded = (await repo.GetAggregateAsync(titleId))!;
            loaded.StoreProviderLayer("igdb", MetadataSourceType.Provider,
                PayloadWithClaims(Claim(RatingBoard.Esrb, "M")), updatedAt: DateTimeOffset.UnixEpoch);
            loaded.Rematerialize(["igdb"]);
            await ApplyEnrichmentAsync(db, repo, loaded);
        }

        using (var db = CreateDb())
        {
            var rows = await db.TitleContentRatings.Where(r => r.TitleId == titleId).ToListAsync();
            rows.Count.ShouldBe(1);
            rows[0].Code.ShouldBe("M");
            rows[0].MinimumAge.ShouldBe(17);
        }
    }

    [Fact]
    public async Task ApplyEnrichmentStagedAsync_BoardNoLongerClaimed_RemovesStaleRow()
    {
        var title = await CreateEnrichedTitleAsync(
            Claim(RatingBoard.Esrb, "T"),
            Claim(RatingBoard.Pegi, "12"));
        int titleId = title.Id;

        using (var db = CreateDb())
        {
            var repo = new TitleRepository(db);
            var loaded = (await repo.GetAggregateAsync(titleId))!;
            loaded.StoreProviderLayer("igdb", MetadataSourceType.Provider,
                PayloadWithClaims(Claim(RatingBoard.Esrb, "T")), updatedAt: DateTimeOffset.UnixEpoch);
            loaded.Rematerialize(["igdb"]);
            await ApplyEnrichmentAsync(db, repo, loaded);
        }

        using (var db = CreateDb())
        {
            var rows = await db.TitleContentRatings.Where(r => r.TitleId == titleId).ToListAsync();
            rows.Count.ShouldBe(1);
            rows[0].Board.ShouldBe((int)RatingBoard.Esrb);
        }
    }

    [Fact]
    public async Task ApplyEnrichmentStagedAsync_TitleLoadedWithoutRatings_DoesNotWipePersistedRows()
    {
        var title = await CreateEnrichedTitleAsync(Claim(RatingBoard.Esrb, "E10"));
        int titleId = title.Id;

        using (var db = CreateDb())
        {
            var repo = new TitleRepository(db);
            // Scalar-only snapshot: no metadata layers, no rating rows, no rematerialization
            var loaded = (await db.Titles.AsNoTracking().SingleAsync(t => t.Id == titleId)).ToDomain();
            loaded.ContentRatingsMaterialized.ShouldBeFalse();
            loaded.MarkEnrichmentCompleted(occurredAt: DateTimeOffset.UnixEpoch);
            await ApplyEnrichmentAsync(db, repo, loaded);
        }

        using (var db = CreateDb())
        {
            var rows = await db.TitleContentRatings.Where(r => r.TitleId == titleId).ToListAsync();
            rows.Count.ShouldBe(1);
            rows[0].Code.ShouldBe("E10+");
        }
    }

    [Fact]
    public async Task GetWithMetadataLayersAsync_RoundTripsRatingsWithDescriptors()
    {
        var claim = new ContentRatingClaim
        {
            Board = RatingBoard.Esrb,
            RawCode = "E10",
            ExternalRatingId = "ext-1",
            Descriptors = ["Fantasy Violence"],
            Synopsis = "Mild cartoon antics."
        };

        var title = await CreateEnrichedTitleAsync(claim);

        var rating = title.ContentRatings.Single();
        rating.Code.ShouldBe("E10+");
        rating.Designation.ShouldBe(RatingDesignation.Rated);
        rating.Descriptors.ShouldBe(["Fantasy Violence"]);
        rating.Synopsis.ShouldBe("Mild cartoon antics.");
        rating.ExternalRatingId.ShouldBe("ext-1");
        rating.SourceId.ShouldBe("igdb");
    }

    [Fact]
    public async Task DeleteAbsorbedSourceAsync_CascadesRatingRows()
    {
        var title = await CreateEnrichedTitleAsync(Claim(RatingBoard.Esrb, "T"));
        int titleId = title.Id;

        using (var db = CreateDb())
        {
            var repo = new TitleRepository(db);
            await repo.DeleteAbsorbedSourceAsync(titleId);
        }

        using (var db = CreateDb())
        {
            (await db.TitleContentRatings.CountAsync(r => r.TitleId == titleId)).ShouldBe(0);
        }
    }
}
