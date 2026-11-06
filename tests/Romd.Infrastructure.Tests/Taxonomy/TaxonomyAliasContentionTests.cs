using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using Romd.Admin.Application.Taxonomy;
using Romd.Domain.Taxonomy;
using Romd.Persistence;
using Romd.Persistence.Entities;
using Romd.Persistence.Repositories;
using Romd.PostgreSql.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Taxonomy;

/// <summary>
///     An alias write queued behind concurrent catalog work waits at most its lock timeout, then answers a retryable
///     409 and changes nothing; once the work it waited for ends, the same write succeeds or meets that work's result.
/// </summary>
public sealed class TaxonomyAliasContentionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AddAlias_BehindAnIngestsUncommittedEqualAlias_Is409ThenFollowsTheIngestsOutcome(bool ingestCommits)
    {
        await using var database = PostgreSqlTestDatabase.Create();
        var (source, target) = await SeedRegionsAsync(database);
        await using var ingest = await PendingInsert.AcquireAsync(database,
            new RegionAliasEntity { RegionId = source.Id, NormalizedAlias = "atlantis" });
        await using var addDb = database.CreateContext();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));

        // The ingest's alias is not committed, so the admin's insert waits on the unique index for its outcome.
        var (add, waited) = await database.StartWaitingForALockAsync(addDb,
            ct => RegionService(addDb).AddAliasAsync("dat-target", "Atlantis", ct), timeout.Token);

        waited.ShouldBeTrue();
        (await add).FirstError.Code.ShouldBe("Taxonomy.AliasConflict");
        await using (var check = database.CreateContext())
            (await check.RegionAliases.AnyAsync(alias => alias.RegionId == target.Id)).ShouldBeFalse();

        await ingest.EndAsync(ingestCommits);
        var retried = await RegionService(addDb).AddAliasAsync("dat-target", "Atlantis", timeout.Token);

        await using var after = database.CreateContext();
        var owner = await after.RegionAliases.Where(alias => alias.NormalizedAlias == "atlantis").Select(alias => alias.RegionId).SingleAsync();
        if (ingestCommits)
        {
            retried.FirstError.Code.ShouldBe("Taxonomy.AliasAlreadyExists");
            owner.ShouldBe(source.Id);
        }
        else
        {
            retried.Value.Alias.ShouldBe("atlantis");
            owner.ShouldBe(target.Id);
        }
    }

    [Fact]
    public async Task AddLanguageAlias_BehindAnIngestsUncommittedEqualAlias_Is409ThenSucceedsOnceItRollsBack()
    {
        await using var database = PostgreSqlTestDatabase.Create();
        var (source, target) = await SeedLanguagesAsync(database);
        await using var ingest = await PendingInsert.AcquireAsync(database,
            new GameLanguageAliasEntity { GameLanguageId = source.Id, NormalizedAlias = "klingon" });
        await using var addDb = database.CreateContext();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));

        var (add, waited) = await database.StartWaitingForALockAsync(addDb,
            ct => LanguageService(addDb).AddAliasAsync("dat-target", "Klingon", ct), timeout.Token);

        waited.ShouldBeTrue();
        (await add).FirstError.Code.ShouldBe("Taxonomy.AliasConflict");
        await ingest.EndAsync(commit: false);
        (await LanguageService(addDb).AddAliasAsync("dat-target", "Klingon", timeout.Token)).Value.Alias.ShouldBe("klingon");
        await using var check = database.CreateContext();
        (await check.GameLanguageAliases.SingleAsync(alias => alias.NormalizedAlias == "klingon")).GameLanguageId.ShouldBe(target.Id);
    }

    [Fact]
    public async Task RemoveAlias_BehindAHeldRowPastTheLockTimeout_Is409AndKeepsTheAliasUntilRetried()
    {
        await using var database = PostgreSqlTestDatabase.Create();
        var (_, target) = await SeedRegionsAsync(database);
        int aliasId;
        await using (var db = database.CreateContext())
        {
            var alias = new RegionAliasEntity { RegionId = target.Id, NormalizedAlias = "atlantis" };
            db.RegionAliases.Add(alias);
            await db.SaveChangesAsync();
            aliasId = alias.Id;
        }

        // A merge holds the row FOR UPDATE while it waits for, or runs behind, other catalog work.
        await using var merge = await RowHold.AcquireAsync(database, "Regions", target.Id);
        await using var removeDb = database.CreateContext();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));

        var (remove, waited) = await database.StartWaitingForALockAsync(removeDb,
            ct => RegionService(removeDb).RemoveAliasAsync("dat-target", aliasId, ct), timeout.Token);

        waited.ShouldBeTrue();
        (await remove).FirstError.Code.ShouldBe("Taxonomy.AliasConflict");
        await using (var check = database.CreateContext())
            (await check.RegionAliases.AnyAsync(alias => alias.Id == aliasId)).ShouldBeTrue();
        await merge.ReleaseAsync();
        (await RegionService(removeDb).RemoveAliasAsync("dat-target", aliasId, timeout.Token)).IsError.ShouldBeFalse();
        await using var after = database.CreateContext();
        (await after.RegionAliases.AnyAsync(alias => alias.Id == aliasId)).ShouldBeFalse();
    }

    // Languages take their row locks with their own statements, so the region remove has a language twin.
    [Fact]
    public async Task RemoveLanguageAlias_BehindAHeldRowPastTheLockTimeout_Is409AndKeepsTheAlias()
    {
        await using var database = PostgreSqlTestDatabase.Create();
        var (_, target) = await SeedLanguagesAsync(database);
        int aliasId;
        await using (var db = database.CreateContext())
        {
            var alias = new GameLanguageAliasEntity { GameLanguageId = target.Id, NormalizedAlias = "klingon" };
            db.GameLanguageAliases.Add(alias);
            await db.SaveChangesAsync();
            aliasId = alias.Id;
        }

        await using var merge = await RowHold.AcquireAsync(database, "GameLanguages", target.Id);
        await using var removeDb = database.CreateContext();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));

        var (remove, waited) = await database.StartWaitingForALockAsync(removeDb,
            ct => LanguageService(removeDb).RemoveAliasAsync("dat-target", aliasId, ct), timeout.Token);

        waited.ShouldBeTrue();
        (await remove).FirstError.Code.ShouldBe("Taxonomy.AliasConflict");
        await using var check = database.CreateContext();
        (await check.GameLanguageAliases.AnyAsync(alias => alias.Id == aliasId)).ShouldBeTrue();
    }

    private static async Task<(RegionEntity Source, RegionEntity Target)> SeedRegionsAsync(PostgreSqlTestDatabase database)
    {
        var source = new RegionEntity { Name = "Source", CanonicalKey = UnregisteredTaxonomyFixtures.KeyFor("Source") };
        var target = new RegionEntity { Name = "Target", CanonicalKey = UnregisteredTaxonomyFixtures.KeyFor("Target") };
        await using var db = database.CreateContext();
        db.Regions.AddRange(source, target);
        await db.SaveChangesAsync();
        return (source, target);
    }

    private static async Task<(GameLanguageEntity Source, GameLanguageEntity Target)> SeedLanguagesAsync(PostgreSqlTestDatabase database)
    {
        var source = new GameLanguageEntity { Name = "Source", Code = "SRC", CanonicalKey = UnregisteredTaxonomyFixtures.KeyFor("Source") };
        var target = new GameLanguageEntity { Name = "Target", Code = "DST", CanonicalKey = UnregisteredTaxonomyFixtures.KeyFor("Target") };
        await using var db = database.CreateContext();
        db.GameLanguages.AddRange(source, target);
        await db.SaveChangesAsync();
        return (source, target);
    }

    private static TaxonomyService<Region> RegionService(RomdDbContext db) => new(new RegionRepository(db), new EfUnitOfWork(db), Microsoft.Extensions.Logging.Abstractions.NullLogger<TaxonomyService<Region>>.Instance);

    private static TaxonomyService<GameLanguage> LanguageService(RomdDbContext db) => new(new LanguageRepository(db), new EfUnitOfWork(db), Microsoft.Extensions.Logging.Abstractions.NullLogger<TaxonomyService<GameLanguage>>.Instance);

    /// <summary>A row an ingest has inserted but not yet committed or rolled back.</summary>
    private sealed class PendingInsert(RomdDbContext context, IDbContextTransaction transaction) : IAsyncDisposable
    {
        public static async Task<PendingInsert> AcquireAsync(PostgreSqlTestDatabase database, object row)
        {
            var context = database.CreateContext();
            var transaction = await context.Database.BeginTransactionAsync();
            context.Add(row);
            await context.SaveChangesAsync();
            return new PendingInsert(context, transaction);
        }

        public Task EndAsync(bool commit) => commit ? transaction.CommitAsync() : transaction.RollbackAsync();

        public async ValueTask DisposeAsync()
        {
            await transaction.DisposeAsync();
            await context.DisposeAsync();
        }
    }

    /// <summary>Holds one row <c>FOR UPDATE</c>, as a merge holds its source, until released.</summary>
    private sealed class RowHold(NpgsqlConnection connection, NpgsqlTransaction transaction) : IAsyncDisposable
    {
        public static async Task<RowHold> AcquireAsync(PostgreSqlTestDatabase database, string table, int id)
        {
            var connection = new NpgsqlConnection(database.ConnectionString);
            await connection.OpenAsync();
            var transaction = await connection.BeginTransactionAsync();
            await using var lockRow = new NpgsqlCommand(
                $"""SELECT 1 FROM romd."{table}" WHERE "Id" = {id} FOR UPDATE""", connection, transaction);
            await lockRow.ExecuteNonQueryAsync();
            return new RowHold(connection, transaction);
        }

        public Task ReleaseAsync() => transaction.CommitAsync();

        public async ValueTask DisposeAsync()
        {
            await transaction.DisposeAsync();
            await connection.DisposeAsync();
        }
    }
}
