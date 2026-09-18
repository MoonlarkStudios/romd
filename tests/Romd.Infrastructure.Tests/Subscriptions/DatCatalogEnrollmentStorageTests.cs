using Microsoft.EntityFrameworkCore;
using OpenIddict.EntityFrameworkCore;
using Romd.Persistence;
using Romd.Persistence.Entities;
using Romd.Persistence.ReferenceData;
using Romd.PostgreSql.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Subscriptions;

public sealed class DatCatalogEnrollmentStorageTests
{
    [Fact]
    public async Task PendingSubscription_BeforeFirstDat_RetainsSelectionAfterPlatformDeletion()
    {
        using var database = PostgreSqlTestDatabase.Create();
        await using var db = Context(database);
        await SharedReferenceDataSeeder.InitializeAsync(db, Romd.PostgreSql.TestSupport.TestReferenceBlobs.Instance);
        var platform = await db.Platforms.SingleAsync(x => x.ShortName == "psx");
        var row = new DatSubscriptionEntity { CatalogId = "redump/psx/discs", SystemId = "psx", ExpectedName = "Sony - PlayStation", PlatformId = platform.Id };
        db.DatSubscriptions.Add(row);
        await db.SaveChangesAsync();
        await db.SystemCompanies.Where(x => x.PlatformId == platform.Id).ExecuteDeleteAsync();
        await db.Platforms.Where(x => x.Id == platform.Id).ExecuteDeleteAsync();
        db.ChangeTracker.Clear();
        var saved = await db.DatSubscriptions.SingleAsync();
        saved.DatSourceId.ShouldBeNull();
        saved.PlatformId.ShouldBe(platform.Id);
        saved.SystemId.ShouldBe("psx");
        (await db.DatSources.CountAsync()).ShouldBe(0);
        (await db.DatFiles.CountAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task PendingSubscription_DuplicateCatalog_IsRejectedWithoutLosingOriginal()
    {
        using var database = PostgreSqlTestDatabase.Create();
        await using (var db = Context(database))
        {
            db.DatSubscriptions.Add(new() { CatalogId = "redump/psx/discs", SystemId = "psx", ExpectedName = "Sony - PlayStation", PlatformId = 1 });
            await db.SaveChangesAsync();
            db.DatSubscriptions.Add(new() { CatalogId = "redump/psx/discs", SystemId = "psx", ExpectedName = "Sony - PlayStation", PlatformId = 1 });
            await Should.ThrowAsync<DbUpdateException>(() => db.SaveChangesAsync());
        }
        await using var verify = Context(database);
        (await verify.DatSubscriptions.CountAsync()).ShouldBe(1);
    }
    private static RomdDbContext Context(PostgreSqlTestDatabase database) => new(new DbContextOptionsBuilder<RomdDbContext>()
        .UseNpgsql(database.ConnectionString).UseOpenIddict().Options);
}
