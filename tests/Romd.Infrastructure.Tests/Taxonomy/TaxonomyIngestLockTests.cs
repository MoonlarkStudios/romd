using ErrorOr;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NSubstitute;
using Romd.Admin.Application.Taxonomy;
using Romd.Application.Common.Security;
using Romd.Domain.Taxonomy;
using Romd.Infrastructure.Tests.Helpers;
using Romd.Persistence;
using Romd.Persistence.Entities;
using Romd.Persistence.Repositories;
using Romd.PostgreSql.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Taxonomy;

/// <summary>
///     An ingest's lookup of a token locks the row the token names <c>FOR KEY SHARE</c> before the ingest links anything
///     to it, so a merge of that row waits for the ingest, and a lookup of a row a merge holds waits for the merge.
/// </summary>
public sealed class TaxonomyIngestLockTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Merge_OfARowAnIngestHasOnlyLookedUp_WaitsForTheIngest(bool languages)
    {
        await using var database = PostgreSqlTestDatabase.Create();
        var (source, _) = await SeedSourceAndTargetAsync(database, languages);
        await using var ingestDb = database.CreateContext();
        await using var ingest = await ingestDb.Database.BeginTransactionAsync();
        // The ingest has resolved the token but inserted no link, so only its lookup can hold the row.
        (await ResolveAsync(ingestDb, languages, "Atlantis", default)).ShouldHaveSingleItem().ShouldBe(source);
        await using var mergeDb = database.CreateContext();
        using var timeout = new CancellationTokenSource(Timeout);

        var (merge, mergeWaited) = await database.StartWaitingForALockAsync(mergeDb,
            ct => MergeAsync(mergeDb, languages, ct), timeout.Token);
        await ingest.CommitAsync();

        mergeWaited.ShouldBeTrue();
        (await merge.WaitAsync(Timeout)).IsError.ShouldBeFalse();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Resolve_ATokenWhoseRowAMergeHolds_WaitsAndResolvesToTheMergeTarget(bool languages)
    {
        await using var database = PostgreSqlTestDatabase.Create();
        var (source, target) = await SeedSourceAndTargetAsync(database, languages);
        // The merge queues behind this hold, so it holds the source while the ingest's lookup queues behind it.
        await using var hold = await RowHold.AcquireAsync(database, languages, source);
        await using var mergeDb = database.CreateContext();
        await using var ingestDb = database.CreateContext();
        await using var ingest = await ingestDb.Database.BeginTransactionAsync();
        using var timeout = new CancellationTokenSource(Timeout);

        var (merge, mergeWaited) = await database.StartWaitingForALockAsync(mergeDb,
            ct => MergeAsync(mergeDb, languages, ct), timeout.Token);
        var (resolve, resolveWaited) = await database.StartWaitingForALockAsync(ingestDb,
            ct => ResolveAsync(ingestDb, languages, "Atlantis", ct), timeout.Token);
        await hold.ReleaseAsync();

        mergeWaited.ShouldBeTrue();
        resolveWaited.ShouldBeTrue();
        (await merge.WaitAsync(Timeout)).IsError.ShouldBeFalse();
        (await resolve.WaitAsync(Timeout)).ShouldHaveSingleItem().ShouldBe(target);
        await ingest.CommitAsync();
        await using var check = database.CreateContext();
        (await KeysAsync(check, languages)).ShouldBe(["dat-target"]);
    }

    [Fact]
    public async Task Resolve_ATokenAMergeMovesWhileTheReadUnderTheMintingLockWaits_ReadsAgainAndResolvesToTheTarget()
    {
        await using var database = PostgreSqlTestDatabase.Create();
        var (source, target) = await SeedSourceAndTargetAsync(database, languages: false, sourceAlias: null);
        using var timeout = new CancellationTokenSource(Timeout);
        // Another ingest mints a token and holds the minting lock until it commits.
        await using var mintingDb = database.CreateContext();
        await using var minting = await mintingDb.Database.BeginTransactionAsync();
        (await ResolveAsync(mintingDb, languages: false, "Lemuria", timeout.Token)).ShouldHaveSingleItem();
        // This ingest finds no alias, so it queues for the minting lock to create the token's row.
        var log = new CommandLog();
        await using var ingestDb = new RomdDbContext(database.CreateOptions(options => options.AddInterceptors(log)));
        await using var ingest = await ingestDb.Database.BeginTransactionAsync();
        int ingestProcess = await ingestDb.OpenBackendProcessIdAsync(timeout.Token);
        var (resolve, waitedToMint) = await database.StartWaitingForALockAsync(ingestDb,
            ct => ResolveAsync(ingestDb, languages: false, "Atlantis", ct), timeout.Token);
        Exception? failure = null;
        try
        {
            // Meanwhile the token gains an alias, and a merge of its row starts, queued behind a hold of the row.
            await using (var adminDb = database.CreateContext())
                (await new TaxonomyService<Region>(new RegionRepository(adminDb), new EfUnitOfWork(adminDb), Microsoft.Extensions.Logging.Abstractions.NullLogger<TaxonomyService<Region>>.Instance)
                    .AddAliasAsync("dat-source", "Atlantis", timeout.Token)).IsError.ShouldBeFalse();
            await using var hold = await RowHold.AcquireAsync(database, languages: false, source);
            await using var mergeDb = database.CreateContext();
            Task<ErrorOr<Deleted>>? merge = null;
            Exception? mergeFailure = null;
            try
            {
                var started = await database.StartWaitingForALockAsync(mergeDb,
                    ct => MergeAsync(mergeDb, languages: false, ct), timeout.Token);
                merge = started.Pending;
                bool mergeWaited = started.Waited;

                // The ingest takes the minting lock and reads the token again, waiting behind the merge, which moves the alias.
                await minting.CommitAsync();
                (await database.WaitsForALockAsync(ingestProcess, resolve, timeout.Token)).ShouldBeTrue();
                await hold.ReleaseAsync();

                waitedToMint.ShouldBeTrue();
                mergeWaited.ShouldBeTrue();
                (await merge.WaitAsync(Timeout)).IsError.ShouldBeFalse();
                (await resolve.WaitAsync(Timeout)).ShouldHaveSingleItem().ShouldBe(target);
                // It read the moved alias again rather than minting a row and being refused.
                log.Commands.ShouldNotContain(command => command.Contains("INSERT INTO romd.\"Regions\""));
                await ingest.CommitAsync();
                await using var check = database.CreateContext();
                (await KeysAsync(check, languages: false)).ShouldBe(["dat-target", "dat-lemuria"]);
            }
            catch (Exception error)
            {
                mergeFailure = error;
                throw;
            }
            finally
            {
                await PostgreSqlLockQueue.CancelAndJoinAsync(timeout, mergeFailure, resolve, merge ?? Task.CompletedTask);
            }
        }
        catch (Exception error)
        {
            failure = error;
            throw;
        }
        finally
        {
            await PostgreSqlLockQueue.CancelAndJoinAsync(timeout, failure, resolve);
        }
    }

    [Fact]
    public async Task Resolve_AnAliasAnotherWriterCommitsWhileTheIngestCreatesTheRow_AdoptsTheCommittedRow()
    {
        await using var database = PostgreSqlTestDatabase.Create();
        var (source, _) = await SeedSourceAndTargetAsync(database, languages: false, sourceAlias: null);
        // Another writer has inserted the alias but not committed it, so the ingest's reads cannot see it.
        await using var writerDb = database.CreateContext();
        await using var writer = await writerDb.Database.BeginTransactionAsync();
        writerDb.RegionAliases.Add(new RegionAliasEntity { RegionId = source, NormalizedAlias = "atlantis" });
        await writerDb.SaveChangesAsync();
        // The worker audits what an ingest mints, as the admin host audits its own writes.
        await using var ingestDb = AuditedContext(database);
        await using var ingest = await ingestDb.Database.BeginTransactionAsync();
        using var timeout = new CancellationTokenSource(Timeout);

        // The ingest mints a row and waits on the alias's unique index for the other writer.
        var (resolve, waited) = await database.StartWaitingForALockAsync(ingestDb,
            ct => ResolveAsync(ingestDb, languages: false, "Atlantis", ct), timeout.Token);
        await writer.CommitAsync();

        waited.ShouldBeTrue();
        (await resolve.WaitAsync(Timeout)).ShouldHaveSingleItem().ShouldBe(source);
        // The ingest's commit flushes whatever its context still stages.
        await ingestDb.SaveChangesAsync();
        await ingest.CommitAsync();
        await using var check = database.CreateContext();
        (await KeysAsync(check, languages: false)).ShouldBe(["dat-source", "dat-target"]);
        // The refused attempt leaves no row, and no audit of a row or alias that never stayed.
        (await check.AdminAuditEvents.Select(item => item.TargetId).ToListAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task Resolve_ARefusedMint_KeepsTheIngestsOtherPendingChanges()
    {
        await using var database = PostgreSqlTestDatabase.Create();
        var (source, _) = await SeedSourceAndTargetAsync(database, languages: false, sourceAlias: null);
        // Another writer has inserted the alias but not committed it, so the ingest mints a row and is refused.
        await using var writerDb = database.CreateContext();
        await using var writer = await writerDb.Database.BeginTransactionAsync();
        writerDb.RegionAliases.Add(new RegionAliasEntity { RegionId = source, NormalizedAlias = "atlantis" });
        await writerDb.SaveChangesAsync();
        await using var ingestDb = database.CreateContext();
        await using var ingest = await ingestDb.Database.BeginTransactionAsync();
        // The ingest has staged work of its own that it has not saved yet.
        ingestDb.GameLanguages.Add(new GameLanguageEntity { Name = "Staged", Code = "stg", CanonicalKey = UnregisteredTaxonomyFixtures.KeyFor("Staged") });
        using var timeout = new CancellationTokenSource(Timeout);

        var (resolve, waited) = await database.StartWaitingForALockAsync(ingestDb,
            ct => ResolveAsync(ingestDb, languages: false, "Atlantis", ct), timeout.Token);
        await writer.CommitAsync();

        waited.ShouldBeTrue();
        (await resolve.WaitAsync(Timeout)).ShouldHaveSingleItem().ShouldBe(source);
        await ingestDb.SaveChangesAsync();
        await ingest.CommitAsync();
        await using var check = database.CreateContext();
        (await check.GameLanguages.Select(row => row.CanonicalKey).ToListAsync()).ShouldBe(["dat-staged"]);
    }

    // A context that audits its writes, as the worker's and the admin host's do.
    private static RomdDbContext AuditedContext(PostgreSqlTestDatabase database)
    {
        var actor = Substitute.For<IAuditContext>();
        actor.ActorId.Returns(Guid.NewGuid());
        return new RomdDbContext(database.CreateOptions(options =>
            options.AddInterceptors(new AuditInterceptor(actor, TimeProvider.System))));
    }

    // A source, with the alias when one is named, and a target; the source is added first, so a merge locks it first.
    private static async Task<(int Source, int Target)> SeedSourceAndTargetAsync(PostgreSqlTestDatabase database, bool languages,
        string? sourceAlias = "atlantis")
    {
        await using var db = database.CreateContext();
        if (languages)
        {
            var source = new GameLanguageEntity { Name = "Source", Code = "SRC", CanonicalKey = UnregisteredTaxonomyFixtures.KeyFor("Source") };
            var target = new GameLanguageEntity { Name = "Target", Code = "DST", CanonicalKey = UnregisteredTaxonomyFixtures.KeyFor("Target") };
            db.GameLanguages.AddRange(source, target);
            await db.SaveChangesAsync();
            if (sourceAlias is not null)
                db.GameLanguageAliases.Add(new GameLanguageAliasEntity { GameLanguageId = source.Id, NormalizedAlias = sourceAlias });
            await db.SaveChangesAsync();
            return (source.Id, target.Id);
        }

        var region = new RegionEntity { Name = "Source", CanonicalKey = UnregisteredTaxonomyFixtures.KeyFor("Source") };
        var other = new RegionEntity { Name = "Target", CanonicalKey = UnregisteredTaxonomyFixtures.KeyFor("Target") };
        db.Regions.AddRange(region, other);
        await db.SaveChangesAsync();
        if (sourceAlias is not null)
            db.RegionAliases.Add(new RegionAliasEntity { RegionId = region.Id, NormalizedAlias = sourceAlias });
        await db.SaveChangesAsync();
        return (region.Id, other.Id);
    }

    private static Task<IReadOnlyList<int>> ResolveAsync(RomdDbContext db, bool languages, string token, CancellationToken ct) =>
        languages
            ? new TaxonomyResolver<GameLanguage>(new LanguageRepository(db), new EfUnitOfWork(db),
                name => GameLanguage.CreateNew(name, name.ToLowerInvariant(), sortOrder: 999, isAutoCreated: true)).ResolveAsync(token, ct)
            : new TaxonomyResolver<Region>(new RegionRepository(db), new EfUnitOfWork(db),
                name => Region.CreateNew(name, sortOrder: 999, isAutoCreated: true)).ResolveAsync(token, ct);

    private static Task<ErrorOr<Deleted>> MergeAsync(RomdDbContext db, bool languages, CancellationToken ct) =>
        languages
            ? new TaxonomyService<GameLanguage>(new LanguageRepository(db), new EfUnitOfWork(db), Microsoft.Extensions.Logging.Abstractions.NullLogger<TaxonomyService<GameLanguage>>.Instance).MergeAsync("dat-source", "dat-target", ct)
            : new TaxonomyService<Region>(new RegionRepository(db), new EfUnitOfWork(db), Microsoft.Extensions.Logging.Abstractions.NullLogger<TaxonomyService<Region>>.Instance).MergeAsync("dat-source", "dat-target", ct);

    private static Task<List<string>> KeysAsync(RomdDbContext db, bool languages) =>
        languages
            ? db.GameLanguages.OrderBy(row => row.Id).Select(row => row.CanonicalKey).ToListAsync()
            : db.Regions.OrderBy(row => row.Id).Select(row => row.CanonicalKey).ToListAsync();

    /// <summary>Holds a region or language row as a merge holds its source, until released.</summary>
    private sealed class RowHold(NpgsqlConnection connection, NpgsqlTransaction transaction) : IAsyncDisposable
    {
        public static async Task<RowHold> AcquireAsync(PostgreSqlTestDatabase database, bool languages, int id)
        {
            var connection = new NpgsqlConnection(database.ConnectionString);
            await connection.OpenAsync();
            var transaction = await connection.BeginTransactionAsync();
            string table = languages ? "GameLanguages" : "Regions";
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
