using System.Data.Common;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Romd.Admin.Application.Titles.ReadModels;
using Romd.Application.Common.Ids;
using Romd.Application.Common.Pagination;
using Romd.Domain.Hashing;
using Romd.Domain.ReferenceData;
using Romd.Domain.Source.Dat;
using Romd.Persistence;
using Romd.Persistence.Entities;
using Romd.Persistence.Queries;
using Romd.PostgreSql.TestSupport;
using Shouldly;
using Xunit;
using Xunit.Abstractions;

namespace Romd.Infrastructure.Tests.Persistence;

public sealed class TitleDetailReleasePagingTests(ITestOutputHelper output)
{
    private const int TitleId = 1;
    private const int OtherTitleId = 2;
    private const int EmptyTitleId = 3;
    private const int PendingGameId = 1;
    private const int FirstRepresentativeId = 2;
    private const int SecondRepresentativeId = 3;
    private const int FirstCatalogReleaseId = 21;
    private const int SecondCatalogReleaseId = 20;
    private const int ReleaseCount = 105;
    private const int ActiveDatId = 1;
    private const int PendingDatId = 2;

    [Fact]
    public async Task GetReleasePageAsync_LargeTitle_BatchesProvenanceAndPreservesContinuations()
    {
        await using var database = PostgreSqlTestDatabase.Create();
        int[] firstSources = [FirstRepresentativeId, .. Enumerable.Range(0, 56).Select(i => 1000 + 2 * i)];
        int[] secondSources = [SecondRepresentativeId, .. Enumerable.Range(0, 50).Select(i => 1001 + 2 * i)];
        await SeedAsync(database, firstSources, secondSources);
        var commands = new ReadCounter();
        await using var db = new RomdDbContext(database.CreateOptions(options => options.AddInterceptors(commands)));
        var reader = new TitleDetailReader(db);
        string version = await db.Database.SqlQuery<string>($"SELECT current_setting('server_version') AS \"Value\"").SingleAsync();
        output.WriteLine($"PostgreSQL {version}");
        version.ShouldStartWith("18.");

        // The two large groups each fetch 50 sources and one lookahead. Every other group fetches one.
        var first = await ReadReleasePageAsync(null, 1, 1);
        var fifty = await ReadReleasePageAsync(null, 50, 150);
        var hundred = await ReadReleasePageAsync(null, 100, 200);
        var last = await ReadReleasePageAsync(hundred.NextCursor, 100, 5);
        var detail = await CountQueriesAsync("Title detail", 16, 150, () => reader.GetPublicDetailAsync(TitleId));
        var empty = await CountQueriesAsync("Empty title release page", 2, 0,
            () => reader.GetReleasePageAsync(EmptyTitleId, null, 50, default));

        first.Items.Select(release => release.Id).ShouldBe([PendingGameId]);
        fifty.Items.Select(release => release.Id).ShouldBe(Enumerable.Range(1, 50));
        hundred.Items.Select(release => release.Id).ShouldBe(Enumerable.Range(1, 100));
        last.Items.Select(release => release.Id).ShouldBe(Enumerable.Range(101, ReleaseCount - 100));
        foreach (var page in new[] { first, fifty, hundred })
        {
            page.HasNextPage.ShouldBeTrue();
            page.NextCursor.ShouldBe(KeysetCursor.Encode(new IntegerPosition(page.Items[^1].Id),
                KeysetCursor.Fingerprint("admin.title.releases", TitleId, "representativeId:asc")));
        }
        last.HasNextPage.ShouldBeFalse();
        last.NextCursor.ShouldBeNull();
        empty.IsError.ShouldBeFalse();
        empty.Value.Page.Items.ShouldBeEmpty();
        empty.Value.Page.HasNextPage.ShouldBeFalse();
        empty.Value.Page.NextCursor.ShouldBeNull();
        detail.ShouldNotBeNull();
        detail.Releases.Select(release => release.Id).ShouldBe(Enumerable.Range(1, 50));
        detail.ReleasesHasNextPage.ShouldBeTrue();
        detail.ReleasesNextCursor.ShouldBe(fifty.NextCursor);

        var pending = first.Items.ShouldHaveSingleItem();
        pending.CatalogReleaseId.ShouldBeNull();
        pending.Sources.ShouldBe([new TitleReleaseSourceData
        {
            DatGameId = PendingGameId, DatFileId = PendingDatId, DatName = "Pending DAT", GameName = "Game 1"
        }]);
        pending.SourcesHasNextPage.ShouldBeFalse();
        pending.SourcesNextCursor.ShouldBeNull();
        var pendingSources = await reader.GetReleaseSourcesPageAsync(TitleId, IdCoder.Encode(pending.Id), null, 100, default);
        pendingSources.IsError.ShouldBeFalse();
        pendingSources.Value.Page.Items.ShouldBe(pending.Sources);
        pendingSources.Value.Page.HasNextPage.ShouldBeFalse();
        pendingSources.Value.Page.NextCursor.ShouldBeNull();

        var firstRelease = fifty.Items[1];
        var secondRelease = fifty.Items[2];
        firstRelease.CatalogReleaseId.ShouldBe(FirstCatalogReleaseId);
        secondRelease.CatalogReleaseId.ShouldBe(SecondCatalogReleaseId);
        firstRelease.Files.ShouldHaveSingleItem().Name.ShouldBe("representative.rom");
        detail.Releases[1].Sources.ShouldBe(firstRelease.Sources);
        detail.Releases[1].SourcesNextCursor.ShouldBe(firstRelease.SourcesNextCursor);
        await AssertSourcesAsync(firstRelease, firstSources);
        await AssertSourcesAsync(secondRelease, secondSources);

        var wrongRelease = await reader.GetReleaseSourcesPageAsync(TitleId, IdCoder.Encode(secondRelease.Id), firstRelease.SourcesNextCursor, 3, default);
        wrongRelease.FirstError.Code.ShouldBe("Titles.InvalidCursor");

        async Task<PagedList<TitleReleaseData>> ReadReleasePageAsync(string? cursor, int limit, int expectedSourceRows)
        {
            var result = await CountQueriesAsync($"Release page limit {limit}, cursor supplied {cursor is not null}",
                4, expectedSourceRows, () => reader.GetReleasePageAsync(TitleId, cursor, limit, default));
            result.IsError.ShouldBeFalse();
            return result.Value.Page;
        }

        async Task<T> CountQueriesAsync<T>(string label, int expectedQueries, int expectedSourceRows, Func<Task<T>> read)
        {
            int before = commands.Commands;
            int sourceReadsBefore = commands.SourceRows.Count;
            T result = await read();
            int count = commands.Commands - before;
            int[] sourceReads = commands.SourceRows.Skip(sourceReadsBefore).ToArray();
            output.WriteLine($"{label}: {count} SQL commands, {sourceReads.Sum()} provenance rows");
            count.ShouldBe(expectedQueries, label);
            sourceReads.ShouldBe(expectedSourceRows == 0 ? [] : [expectedSourceRows]);
            return result;
        }

        async Task AssertSourcesAsync(TitleReleaseData release, int[] expectedIds)
        {
            var expected = expectedIds.Select(id => new TitleReleaseSourceData
            {
                DatGameId = id,
                DatFileId = ActiveDatId,
                DatName = "Active DAT",
                GameName = $"Game {id}"
            }).ToArray();
            string releaseId = IdCoder.Encode(release.Id);
            string fingerprint = KeysetCursor.Fingerprint("admin.title.release-sources", TitleId, release.Id, "datGameId:asc");
            release.Sources.ShouldBe(expected.Take(50));
            release.SourcesHasNextPage.ShouldBeTrue();
            release.SourcesNextCursor.ShouldBe(KeysetCursor.Encode(new IntegerPosition(expectedIds[49]), fingerprint));

            var all = await reader.GetReleaseSourcesPageAsync(TitleId, releaseId, null, 100, default);
            all.IsError.ShouldBeFalse();
            all.Value.Page.Items.ShouldBe(expected);
            all.Value.Page.HasNextPage.ShouldBeFalse();
            all.Value.Page.NextCursor.ShouldBeNull();

            int offset = 50;
            var cursor = release.SourcesNextCursor;
            while (cursor is not null)
            {
                const int limit = 3;
                var next = await reader.GetReleaseSourcesPageAsync(TitleId, releaseId, cursor, limit, default);
                next.IsError.ShouldBeFalse();
                var page = next.Value.Page;
                page.Items.ShouldBe(expected.Skip(offset).Take(limit));
                offset += page.Items.Count;
                page.HasNextPage.ShouldBe(offset < expected.Length);
                page.NextCursor.ShouldBe(page.HasNextPage
                    ? KeysetCursor.Encode(new IntegerPosition(expectedIds[offset - 1]), fingerprint)
                    : null);
                cursor = page.NextCursor;
            }
            offset.ShouldBe(expected.Length);
        }
    }

    private static async Task SeedAsync(PostgreSqlTestDatabase database, int[] firstSources, int[] secondSources)
    {
        await using var db = database.CreateContext();
        const int platformId = 1;
        db.Platforms.Add(new PlatformEntity
        {
            Id = platformId,
            Name = "Test platform",
            ShortName = "test",
            CanonicalKey = "test",
            BaseName = "Test platform",
            BaseCompactLabel = "Test",
            Ownership = ReferenceOwnership.Romd,
            BuiltInVersion = 1
        });
        foreach (int id in new[] { TitleId, OtherTitleId, EmptyTitleId })
            db.Titles.Add(new TitleEntity { Id = id, PlatformId = platformId, Name = $"Title {id}", NormalizedName = $"title{id}", EnrichmentStatus = "None" });
        foreach (int id in new[] { ActiveDatId, PendingDatId })
            db.Files.Add(new FileEntityPersistence { Id = id, Sha256 = Sha256.Parse(id.ToString("x64", CultureInfo.InvariantCulture)), Size = 1, SizeOnDisk = 1 });

        db.DatFiles.AddRange(
            new DatFileEntity
            {
                Id = ActiveDatId,
                Name = "Active DAT",
                Description = "Active DAT",
                PlatformId = platformId,
                Type = "NoIntro",
                OriginalFilename = "active.dat",
                FileId = ActiveDatId,
                Source = new DatSourceEntity { Id = 1, CatalogSource = new CatalogSourceEntity { Id = 1, Kind = "Dat", Status = "Active" } }
            },
            new DatFileEntity
            {
                Id = PendingDatId,
                Name = "Pending DAT",
                Description = "Pending DAT",
                PlatformId = platformId,
                Type = "NoIntro",
                OriginalFilename = "pending.dat",
                FileId = PendingDatId,
                DatSourceId = 1,
                Lifecycle = nameof(DatFileLifecycle.PendingActivation)
            });

        AddRelease(FirstCatalogReleaseId, firstSources.Length + 1);
        AddRelease(SecondCatalogReleaseId, secondSources.Length);
        foreach (int id in firstSources)
            AddGame(id, TitleId, FirstCatalogReleaseId);
        foreach (int id in secondSources)
            AddGame(id, TitleId, SecondCatalogReleaseId);
        foreach (int id in Enumerable.Range(4, ReleaseCount - 3))
        {
            int releaseId = 100 + id;
            AddRelease(releaseId, 1);
            AddGame(id, TitleId, releaseId);
        }
        AddGame(500, OtherTitleId, FirstCatalogReleaseId);

        // Two DAT versions share this entry, and only the active game asserts the release.
        db.DatGames.Add(new DatGameEntity { Id = PendingGameId, DatFileId = PendingDatId, SourceEntryId = FirstRepresentativeId, Name = "Game 1" });
        db.DatRoms.AddRange(
            new DatRomEntity { DatGameId = FirstRepresentativeId, Name = "representative.rom", Size = 1 },
            new DatRomEntity { DatGameId = firstSources[1], Name = "other.rom", Size = 1 });
        await db.SaveChangesAsync();

        void AddRelease(int id, int sourceCount) => db.CatalogReleases.Add(new CatalogReleaseEntity
        {
            Id = id,
            PlatformId = platformId,
            CatalogTitleId = TitleId,
            Fingerprint = $"release:{id}",
            Name = $"Release {id}",
            SourceCount = sourceCount
        });

        void AddGame(int id, int titleId, int releaseId)
        {
            string name = $"Game {id}";
            db.SourceEntries.Add(new SourceEntryEntity { Id = id, CatalogSourceId = 1, EntryKey = name, Name = name, PlatformId = platformId });
            db.TitleSourceLinks.Add(new TitleSourceLinkEntity { SourceEntryId = id, TitleId = titleId });
            db.DatGames.Add(new DatGameEntity { Id = id, DatFileId = ActiveDatId, SourceEntryId = id, Name = name });
            db.CatalogReleaseSources.Add(new CatalogReleaseSourceEntity
            {
                CatalogReleaseId = releaseId,
                SourceEntryId = id,
                ProviderClaimKey = id.ToString(CultureInfo.InvariantCulture),
                AssertedTitleId = titleId
            });
        }
    }

    private sealed class ReadCounter : DbCommandInterceptor
    {
        public int Commands { get; private set; }
        public List<int> SourceRows { get; } = [];

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Commands++;
            return ValueTask.FromResult(result);
        }

        public override InterceptionResult DataReaderDisposing(DbCommand command, DataReaderDisposingEventData eventData,
            InterceptionResult result)
        {
            if (command.CommandText.Contains("-- Admin title release provenance", StringComparison.Ordinal))
                SourceRows.Add(eventData.ReadCount - 1); // Includes the final read that found no row.
            return result;
        }
    }
}
