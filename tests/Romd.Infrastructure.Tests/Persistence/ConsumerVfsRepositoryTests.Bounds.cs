using System.Data.Common;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using Romd.Application.Common.Ids;
using Romd.Consumer.Application.Libraries;
using Romd.Consumer.Application.Vfs;
using Romd.Persistence;
using Romd.Persistence.Entities;
using Romd.Persistence.Repositories;
using Romd.Persistence.MediaFacts;
using Romd.PostgreSql.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Persistence;

public sealed partial class ConsumerVfsRepositoryTests
{
    [Theory]
    [InlineData(10_000, 10_000, 1)]
    [InlineData(8_000, 8_000, 8_000)]
    public async Task ListReleasesAsync_FileFactBudget_StopsBeforeNextReleaseAndResumesWithoutLoss(
        int firstCount, int secondCount, int thirdCount)
    {
        await SeedPagedReleasesAsync(firstCount, secondCount, thirdCount, 1);
        var cost = new VfsQueryCost();
        await using var db = ObservedDb(cost);
        var repository = new ConsumerVfsRepository(db, DeliveryPolicy, MediaTestServices.CreateContentCatalog(db), new ConsumerMediaAdmission(ConsumerMediaFixture.ManagedBuild, null));

        var first = (await repository.ListReleasesAsync(new(UserId), PlatformId, limit: 4))
            .ShouldBeOfType<ConsumerLibraryReadResult<ConsumerVfsReleasePage>.Found>().Value;

        first.Items.Select(release => release.Id).ShouldBe([1, 2]);
        first.ReleasePaths[1].Count.ShouldBe(firstCount);
        first.ReleasePaths[2].Count.ShouldBe(secondCount);
        first.NextReleaseId.ShouldBe(2);
        // EF records the final unsuccessful Read as well as the returned rows.
        cost.PathFactReads.ShouldHaveSingleItem().ShouldBe(firstCount + secondCount + 1);
        cost.PathFactReleaseIds.ShouldHaveSingleItem().ShouldBe([1, 2]);

        cost.Reset();
        var second = (await repository.ListReleasesAsync(new(UserId), PlatformId,
            afterReleaseId: first.NextReleaseId.GetValueOrDefault(), limit: 4))
            .ShouldBeOfType<ConsumerLibraryReadResult<ConsumerVfsReleasePage>.Found>().Value;
        second.Items.Select(release => release.Id).ShouldBe([3, 4]);
        second.ReleasePaths[3].Count.ShouldBe(thirdCount);
        second.ReleasePaths[4].Count.ShouldBe(1);
        second.NextReleaseId.ShouldBeNull();
        cost.PathFactReads.ShouldHaveSingleItem().ShouldBe(thirdCount + 2);
    }

    [Fact]
    public async Task ListReleasesAsync_OversizedRelease_DoesNotFetchItsPathsAndAdvancesToValidRelease()
    {
        await SeedPagedReleasesAsync(10_001, 1);
        var cost = new VfsQueryCost();
        await using var db = ObservedDb(cost);
        var repository = new ConsumerVfsRepository(db, DeliveryPolicy, MediaTestServices.CreateContentCatalog(db), new ConsumerMediaAdmission(ConsumerMediaFixture.ManagedBuild, null));

        var first = (await repository.ListReleasesAsync(new(UserId), PlatformId, limit: 1))
            .ShouldBeOfType<ConsumerLibraryReadResult<ConsumerVfsReleasePage>.Found>().Value;
        first.Items.Select(release => release.Id).ShouldBe([1]);
        first.ReleasePaths[1].ShouldBeEmpty();
        first.NextReleaseId.ShouldBe(1);
        cost.PathFactReads.Sum().ShouldBeLessThanOrEqualTo(1);
        cost.PathFactReleaseIds.ShouldAllBe(ids => ids.Length == 0);

        cost.Reset();
        var second = (await repository.ListReleasesAsync(new(UserId), PlatformId,
            afterReleaseId: first.NextReleaseId.GetValueOrDefault(), limit: 1))
            .ShouldBeOfType<ConsumerLibraryReadResult<ConsumerVfsReleasePage>.Found>().Value;
        second.Items.Select(release => release.Id).ShouldBe([2]);
        second.ReleasePaths[2].ShouldBe(["file-1.bin"]);
        second.NextReleaseId.ShouldBeNull();
        cost.PathFactReads.ShouldHaveSingleItem().ShouldBe(2);
        cost.PathFactReleaseIds.ShouldHaveSingleItem().ShouldBe([2]);
    }

    [Fact]
    public async Task ListReleasesAsync_EnormousRelease_StopsCountingAtFirstUnprojectableSize()
    {
        await SeedPagedReleasesAsync(50_000, 1);
        await using (var seed = CreateDb())
            await seed.Database.ExecuteSqlRawAsync("ANALYZE romd.\"CatalogReleaseFiles\"");
        var cost = new VfsQueryCost();
        await using var db = ObservedDb(cost);
        var page = (await new ConsumerVfsRepository(db, DeliveryPolicy, MediaTestServices.CreateContentCatalog(db), new ConsumerMediaAdmission(ConsumerMediaFixture.ManagedBuild, null)).ListReleasesAsync(new(UserId), PlatformId, limit: 1))
            .ShouldBeOfType<ConsumerLibraryReadResult<ConsumerVfsReleasePage>.Found>().Value;
        page.ReleasePaths[1].ShouldBeEmpty();
        page.NextReleaseId.ShouldBe(1);
        cost.PathFactReads.Sum().ShouldBeLessThanOrEqualTo(1);

        var counted = cost.FileCountQueries.ShouldHaveSingleItem();
        await using var connection = new NpgsqlConnection(_connection.ConnectionString);
        await connection.OpenAsync();
        await using var explain = new NpgsqlCommand("EXPLAIN (ANALYZE, BUFFERS, FORMAT JSON) " + counted.Sql, connection);
        explain.Parameters.AddRange(counted.Parameters);
        string plan = (await explain.ExecuteScalarAsync()).ShouldBeOfType<string>();
        Console.WriteLine("VFS capped count SQL:\n" + counted.Sql + "\nVFS capped count plan:\n" + plan);
        using var document = JsonDocument.Parse(plan);
        var limit = Plans(document.RootElement[0].GetProperty("Plan"))
            .Where(node => node.GetProperty("Node Type").GetString() == "Limit").ShouldHaveSingleItem();
        limit.GetProperty("Actual Rows").GetDouble().ShouldBe(10_001);
        limit.GetProperty("Actual Loops").GetInt32().ShouldBe(1);
        var scan = Plans(limit).Where(node => node.TryGetProperty("Relation Name", out var relation)
            && relation.GetString() == "CatalogReleaseFiles").ShouldHaveSingleItem();
        scan.GetProperty("Node Type").GetString().ShouldBeOneOf("Index Scan", "Index Only Scan");
        scan.GetProperty("Index Name").GetString().ShouldBe("IX_CatalogReleaseFiles_CatalogReleaseId_Name");
        // A full scan followed by a sort can satisfy the Limit while doing unbounded work below it.
        double filtered = scan.TryGetProperty("Rows Removed by Filter", out var filter) ? filter.GetDouble() : 0;
        double rechecked = scan.TryGetProperty("Rows Removed by Index Recheck", out var recheck) ? recheck.GetDouble() : 0;
        double inspected = (scan.GetProperty("Actual Rows").GetDouble() + filtered + rechecked)
            * scan.GetProperty("Actual Loops").GetDouble();
        inspected.ShouldBeLessThanOrEqualTo(10_001);
    }

    [Fact]
    public async Task ReadPlatformAsync_EntirePageHasNoFiles_OmitsItAndReachesNextPage()
    {
        await SeedPagedReleasesAsync([.. Enumerable.Repeat(0, 200), 1]);
        var cost = new VfsQueryCost();
        await using var db = ObservedDb(cost);
        var repository = new ConsumerVfsRepository(db, DeliveryPolicy, MediaTestServices.CreateContentCatalog(db), new ConsumerMediaAdmission(ConsumerMediaFixture.ManagedBuild, null));
        var first = (await repository.ListReleasesAsync(new(UserId), PlatformId, limit: 200))
            .ShouldBeOfType<ConsumerLibraryReadResult<ConsumerVfsReleasePage>.Found>().Value;
        first.Items.Count.ShouldBe(200);
        first.ReleasePaths.Values.ShouldAllBe(paths => paths.Count == 0);
        first.NextReleaseId.ShouldBe(200);

        var platformPath = IdCoder.Encode(PlatformId) + " Sony PlayStation";
        var read = await new DefaultLibraryLayout(repository).ReadAsync(new(UserId), platformPath, true, 1);
        read.IsError.ShouldBeFalse();
        read.Value.Children.ShouldHaveSingleItem().Path.ShouldBe(platformPath + "/" + IdCoder.Encode(201) + " Release 201");
        cost.PathFactReads.Sum().ShouldBeLessThanOrEqualTo(4);
    }

    [Fact]
    public async Task ReadPlatformAsync_CancelledWhileReadingOmittedPage_StopsAndReleasesSnapshot()
    {
        await SeedPagedReleasesAsync([.. Enumerable.Repeat(0, 200), 1]);
        using var cancellation = new CancellationTokenSource();
        var cost = new VfsQueryCost { AfterPathFacts = cancellation.Cancel };
        await using (var db = ObservedDb(cost))
        {
            var platformPath = IdCoder.Encode(PlatformId) + " Sony PlayStation";
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new DefaultLibraryLayout(new ConsumerVfsRepository(db, DeliveryPolicy, MediaTestServices.CreateContentCatalog(db), new ConsumerMediaAdmission(ConsumerMediaFixture.ManagedBuild, null)))
                .ReadAsync(new(UserId), platformPath, true, 1, cancellation.Token));
            db.Database.CurrentTransaction.ShouldBeNull();
        }
        cost.PathFactReads.Count.ShouldBe(1);
        await using var recovered = CreateDb();
        (await new ConsumerVfsRepository(recovered, DeliveryPolicy, MediaTestServices.CreateContentCatalog(recovered), new ConsumerMediaAdmission(ConsumerMediaFixture.ManagedBuild, null)).ListReleasesAsync(new(UserId), PlatformId, afterReleaseId: 200))
            .ShouldBeOfType<ConsumerLibraryReadResult<ConsumerVfsReleasePage>.Found>().Value.Items.ShouldHaveSingleItem().Id.ShouldBe(201);
    }

    private async Task SeedPagedReleasesAsync(params int[] fileCounts)
    {
        SeedReleaseFiles(("seed.bin", true));
        await using var db = CreateDb();
        await db.CatalogReleaseFiles.ExecuteDeleteAsync();
        for (int id = 2; id <= fileCounts.Length; id++)
        {
            db.CatalogReleases.Add(new CatalogReleaseEntity
            {
                Id = id,
                PlatformId = PlatformId,
                CatalogTitleId = TitleId,
                Fingerprint = $"release-{id}",
                Name = $"Release {id}",
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            });
            db.MaterializedLibraryReleases.Add(new MaterializedLibraryReleaseEntity
            {
                LibraryId = LibraryId,
                TitleId = TitleId,
                CatalogReleaseId = id,
                DatGameId = id,
                DatFileId = 1,
                PlatformId = PlatformId,
                IsOwned = true,
                IsExposed = true
            });
        }
        await db.SaveChangesAsync();
        for (int index = 0; index < fileCounts.Length; index++)
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO romd."CatalogReleaseFiles"
                    ("CatalogReleaseId", "FileFingerprint", "Name", "Size", "Sha1", "IsDisk")
                SELECT {index + 1}, 'file-' || n, 'file-' || n || '.bin', 64, {NewSha1(1).ToArray()}, false
                FROM generate_series(1, {fileCounts[index]}) AS n
                """);
        }
    }

    private RomdDbContext ObservedDb(params IInterceptor[] interceptors) =>
        new(new DbContextOptionsBuilder<RomdDbContext>(_contextOptions).AddInterceptors(interceptors).Options);

    private static IEnumerable<JsonElement> Plans(JsonElement node)
    {
        yield return node;
        if (node.TryGetProperty("Plans", out var children))
            foreach (var child in children.EnumerateArray())
                foreach (var descendant in Plans(child))
                    yield return descendant;
    }

    private sealed record CapturedQuery(string Sql, NpgsqlParameter[] Parameters);

    private sealed class VfsQueryCost : DbCommandInterceptor
    {
        public int Commands { get; private set; }
        public List<int> GraphBatchSizes { get; } = [];
        public List<int> PathFactReads { get; } = [];
        public List<int[]> PathFactReleaseIds { get; } = [];
        public List<CapturedQuery> FileCountQueries { get; } = [];
        public Action? AfterPathFacts { get; init; }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Commands++;
            if (command.CommandText.Contains("FROM romd.\"Representations\" r JOIN romd.\"Files\" f", StringComparison.Ordinal))
                GraphBatchSizes.Add(command.Parameters.Cast<DbParameter>().Select(parameter => parameter.Value).OfType<int[]>().Single().Length);
            if (command.CommandText.Contains("-- VFS release file counts", StringComparison.Ordinal))
                FileCountQueries.Add(new(command.CommandText, command.Parameters.Cast<NpgsqlParameter>()
                    .Select(parameter => parameter.Clone()).ToArray()));
            if (command.CommandText.Contains("-- VFS path facts", StringComparison.Ordinal))
                PathFactReleaseIds.Add(command.Parameters.Cast<DbParameter>().Select(parameter => parameter.Value)
                    .OfType<int[]>().SingleOrDefault() ?? []);
            return ValueTask.FromResult(result);
        }

        public override InterceptionResult DataReaderDisposing(DbCommand command, DataReaderDisposingEventData eventData,
            InterceptionResult result)
        {
            if (command.CommandText.Contains("-- VFS path facts", StringComparison.Ordinal))
            {
                PathFactReads.Add(eventData.ReadCount);
                AfterPathFacts?.Invoke();
            }
            return result;
        }

        public void Reset()
        {
            Commands = 0;
            GraphBatchSizes.Clear();
            PathFactReads.Clear();
            PathFactReleaseIds.Clear();
            FileCountQueries.Clear();
        }
    }
}
