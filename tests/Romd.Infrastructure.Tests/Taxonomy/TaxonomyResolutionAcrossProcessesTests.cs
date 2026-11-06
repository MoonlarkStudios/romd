using Microsoft.EntityFrameworkCore;
using Romd.Admin.Application.Taxonomy;
using Romd.Domain.Taxonomy;
using Romd.Persistence;
using Romd.Persistence.Entities;
using Romd.Persistence.ReferenceData;
using Romd.Persistence.Repositories;
using Romd.PostgreSql.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Taxonomy;

/// <summary>
///     The admin and worker hosts are separate processes. A worker ingest resolves each token from what is committed,
///     never from what the worker kept from an earlier ingest: the admin host's merges and alias edits, and the
///     worker's own rolled-back ingests, reach its next ingest.
/// </summary>
public sealed class TaxonomyResolutionAcrossProcessesTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task Resolve_TokenWhoseRowTheAdminHostMergedAway_ResolvesToTheMergeTarget()
    {
        await using var database = PostgreSqlTestDatabase.Create();
        await using (var db = database.CreateContext())
            await SharedReferenceDataSeeder.InitializeAsync(db, TestReferenceBlobs.For(db), clock: TimeProvider.System);
        var worker = new HostProcess();
        var admin = new HostProcess();
        int created = await IngestAsync(database, worker, "Atlantis");

        await using (var db = database.CreateContext())
        {
            string minted = await db.Regions.Where(row => row.Id == created).Select(row => row.CanonicalKey).SingleAsync();
            (await admin.RegionService(db).MergeAsync(minted, "asia", default)).IsError.ShouldBeFalse();
        }

        await using var check = database.CreateContext();
        int asia = await check.Regions.Where(row => row.CanonicalKey == "asia").Select(row => row.Id).SingleAsync();
        (await IngestAsync(database, worker, "ATLANTIS")).ShouldBe(asia);
    }

    [Fact]
    public async Task Resolve_TokenARolledBackIngestCreated_CreatesItsRowAgain()
    {
        await using var database = PostgreSqlTestDatabase.Create();
        var worker = new HostProcess();
        await IngestAsync(database, worker, "Atlantis", commit: false);

        int created = await IngestAsync(database, worker, "Atlantis");

        await using var check = database.CreateContext();
        (await check.Regions.Where(row => row.Id == created).Select(row => row.CanonicalKey).SingleOrDefaultAsync())
            .ShouldBe("dat-atlantis");
    }

    [Fact]
    public async Task Resolve_TokenTheAdminHostAliased_ResolvesToTheAliasedRow()
    {
        await using var database = PostgreSqlTestDatabase.Create();
        var worker = new HostProcess();
        var admin = new HostProcess();
        int lemuria = await IngestAsync(database, worker, "Lemuria");

        await using (var db = database.CreateContext())
            (await admin.RegionService(db).AddAliasAsync("dat-lemuria", "Mu", default)).IsError.ShouldBeFalse();

        (await IngestAsync(database, worker, "Mu")).ShouldBe(lemuria);
        await using var check = database.CreateContext();
        (await check.Regions.CountAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task Resolve_TokenTheAdminHostUnaliased_NoLongerResolvesToItsFormerRow()
    {
        await using var database = PostgreSqlTestDatabase.Create();
        var lemuria = new RegionEntity { Name = "Lemuria", CanonicalKey = UnregisteredTaxonomyFixtures.KeyFor("Lemuria") };
        RegionAliasEntity alias;
        await using (var db = database.CreateContext())
        {
            db.Regions.Add(lemuria);
            await db.SaveChangesAsync();
            alias = new RegionAliasEntity { RegionId = lemuria.Id, NormalizedAlias = "mu" };
            db.RegionAliases.Add(alias);
            await db.SaveChangesAsync();
        }

        var worker = new HostProcess();
        var admin = new HostProcess();
        (await IngestAsync(database, worker, "Mu")).ShouldBe(lemuria.Id);

        await using (var db = database.CreateContext())
            (await admin.RegionService(db).RemoveAliasAsync("dat-lemuria", alias.Id, default)).IsError.ShouldBeFalse();

        int resolved = await IngestAsync(database, worker, "Mu");
        resolved.ShouldNotBe(lemuria.Id);
        await using var check = database.CreateContext();
        (await check.Regions.Where(row => row.Id == resolved).Select(row => row.CanonicalKey).SingleAsync()).ShouldBe("dat-mu");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Resolve_TokenAnotherIngestInTheProcessIsCreating_WaitsAndResolvesToWhatCommitted(bool firstCommits)
    {
        await using var database = PostgreSqlTestDatabase.Create();
        var worker = new HostProcess();
        await using var firstDb = database.CreateContext();
        await using var secondDb = database.CreateContext();
        await using var firstIngest = await firstDb.Database.BeginTransactionAsync();
        await using var secondIngest = await secondDb.Database.BeginTransactionAsync();
        int first = (await worker.Regions(firstDb).ResolveAsync("Atlantis")).ShouldHaveSingleItem();

        // The first ingest's row is not committed, so the second cannot use it yet.
        using var timeout = new CancellationTokenSource(Timeout);
        var (pending, waited) = await database.StartWaitingForALockAsync(secondDb,
            ct => worker.Regions(secondDb).ResolveAsync("ATLANTIS", ct), timeout.Token);
        waited.ShouldBeTrue();
        if (firstCommits)
            await firstIngest.CommitAsync();
        else
            await firstIngest.RollbackAsync();
        int second = (await pending.WaitAsync(Timeout)).ShouldHaveSingleItem();
        await secondIngest.CommitAsync();

        (second == first).ShouldBe(firstCommits);
        await using var check = database.CreateContext();
        (await check.Regions.Select(row => row.Id).ToListAsync()).ShouldBe([second]);
    }

    // One DAT ingest's resolution of a token, in its own transaction, which ends as the ingest does.
    private static async Task<int> IngestAsync(PostgreSqlTestDatabase database, HostProcess worker, string token, bool commit = true)
    {
        await using var db = database.CreateContext();
        await using var ingest = await db.Database.BeginTransactionAsync();
        int id = (await worker.Regions(db).ResolveAsync(token).WaitAsync(Timeout)).ShouldHaveSingleItem();
        if (commit)
            await ingest.CommitAsync();
        else
            await ingest.RollbackAsync();
        return id;
    }

    /// <summary>
    ///     One host process. Like the host's container, it composes a resolver for each ingest and a service for each
    ///     admin request, and keeps nothing between them.
    /// </summary>
    private sealed class HostProcess
    {
        public TaxonomyResolver<Region> Regions(RomdDbContext db) =>
            new(new RegionRepository(db), new EfUnitOfWork(db), token => Region.CreateNew(token, sortOrder: 999, isAutoCreated: true));

        public TaxonomyService<Region> RegionService(RomdDbContext db) =>
            new(new RegionRepository(db), new EfUnitOfWork(db), Microsoft.Extensions.Logging.Abstractions.NullLogger<TaxonomyService<Region>>.Instance);
    }
}
