using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Romd.Application.Common.Pagination;
using Romd.Admin.Application.Export;
using Romd.Admin.Application.Search;
using Romd.Consumer.Application.Browse;
using Romd.Domain.Catalog;
using Romd.Domain.Hashing;
using Romd.Domain.Libraries;
using Romd.Infrastructure.Identity;
using Romd.Persistence.Identity;
using Romd.Persistence;
using Romd.Persistence.Entities;
using Romd.Persistence.Repositories;
using Romd.Persistence.Queries;
using Romd.Infrastructure.Source;
using Romd.Infrastructure.Tests.Helpers;
using Romd.PostgreSql.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Persistence;

// Large export fixtures publish more than the bounded lookup ceiling on the shared server.
[Collection(PostgreSqlScaleWorkloads.Name)]
public sealed class ExportRepositoryTests : IDisposable
{
    private const int MaxIdsPerLookup = BoundedIdQuery.MaxIdsPerBatch;
    private readonly PostgreSqlTestDatabase _connection;
    private readonly CommandParameterCaptureInterceptor _commands = new();
    private readonly DbContextOptions<RomdDbContext> _contextOptions;

    public ExportRepositoryTests()
    {
        _connection = PostgreSqlTestDatabase.Create();

        _contextOptions = new DbContextOptionsBuilder<RomdDbContext>()
            .UseNpgsql(_connection.ConnectionString)
            .AddInterceptors(_commands)
            .Options;

        using var db = CreateDb();
    }

    [Fact]
    public async Task ReleaseDownload_ExactReleaseAndCurrentAssignment_AreRequired()
    {
        using var db = CreateDb();
        SeedExportGraph(db);
        var user = RomdUser.Create("release-download", "download@localhost");
        user.LibraryId = 1;
        db.Users.Add(user);
        await db.PublishOriginalRomsAsync();
        await db.SaveChangesAsync();
        await RebuildCatalogAsync(db);
        var releaseId = await ResolveCatalogReleaseIdAsync(db, datGameId: 1);
        db.MaterializedLibraryReleases.Add(NewAccessibleRelease(catalogReleaseId: releaseId));
        await db.PublishOriginalRomsAsync();
        await db.SaveChangesAsync();
        var repository = new ReleaseDownloadRepository(db);
        var plan = await repository.GetAsync(releaseId, user.Id, false, CancellationToken.None);
        plan.ShouldNotBeNull();
        plan.Entries.ShouldHaveSingleItem().PlatformKeys.ShouldBe(["test"]);
        (await repository.GetAsync(int.MaxValue, user.Id, true, CancellationToken.None)).ShouldBeNull();
        await db.Users.Where(u => u.Id == user.Id).ExecuteUpdateAsync(s => s.SetProperty(u => u.LibraryId, (int?)null));
        (await repository.GetAsync(releaseId, user.Id, false, CancellationToken.None)).ShouldBeNull();
        (await repository.GetAsync(releaseId, user.Id, true, CancellationToken.None)).ShouldNotBeNull();
        await db.Users.Where(u => u.Id == user.Id).ExecuteUpdateAsync(s => s.SetProperty(u => u.IsSuspended, true));
        (await repository.GetAsync(releaseId, user.Id, true, CancellationToken.None)).ShouldBeNull();
    }

    [Fact]
    public async Task ReleaseDownloads_SeveralReleases_ReadOnceUnderEachReleasesAuthorization()
    {
        using var db = CreateDb();
        SeedExportGraph(db);
        var user = RomdUser.Create("release-downloads", "downloads@localhost");
        user.LibraryId = 1;
        db.Users.Add(user);
        await db.PublishOriginalRomsAsync();
        await db.SaveChangesAsync();
        await RebuildCatalogAsync(db);
        var exposed = await ResolveCatalogReleaseIdAsync(db, datGameId: 1);
        var unexposed = await ResolveCatalogReleaseIdAsync(db, datGameId: 2);
        db.MaterializedLibraryReleases.Add(NewAccessibleRelease(catalogReleaseId: exposed));
        await db.PublishOriginalRomsAsync();
        await db.SaveChangesAsync();
        var repository = new ReleaseDownloadRepository(db);
        _commands.Clear();

        var restricted = await repository.GetManyAsync([exposed, unexposed, int.MaxValue], user.Id, false, CancellationToken.None);

        _commands.Commands.ShouldHaveSingleItem();
        restricted.Keys.ShouldBe([exposed]);
        var single = (await repository.GetAsync(exposed, user.Id, false, CancellationToken.None)).ShouldNotBeNull();
        restricted[exposed].Name.ShouldBe(single.Name);
        restricted[exposed].Entries.Select(entry => entry with { PlatformKeys = null })
            .ShouldBe(single.Entries.Select(entry => entry with { PlatformKeys = null }));
        restricted[exposed].Entries.SelectMany(entry => entry.PlatformKeys!).ShouldBe(single.Entries.SelectMany(entry => entry.PlatformKeys!));
        restricted[exposed].MissingFiles.ShouldBe(single.MissingFiles);
        var unrestricted = await repository.GetManyAsync([exposed, unexposed], user.Id, true, CancellationToken.None);
        unrestricted.Keys.Order().ShouldBe(new[] { exposed, unexposed }.Order());
        unrestricted[unexposed].Entries.ShouldHaveSingleItem().Path.ShouldBe("excluded.rom");
        (await repository.GetManyAsync([], user.Id, true, CancellationToken.None)).ShouldBeEmpty();
    }

    [Fact]
    public async Task GetExportFilesAsync_LibraryId_ExportsOnlyMaterializedDatGame()
    {
        int catalogReleaseId;
        using (var db = CreateDb())
        {
            SeedExportGraph(db);
            await db.PublishOriginalRomsAsync();
            await db.SaveChangesAsync();
            await RebuildCatalogAsync(db);
            catalogReleaseId = await ResolveCatalogReleaseIdAsync(db, datGameId: 1);
            db.MaterializedLibraryReleases.Add(NewAccessibleRelease(catalogReleaseId: catalogReleaseId));
            await db.PublishOriginalRomsAsync();
            await db.SaveChangesAsync();
        }

        using (var db = CreateDb())
        {
            var repository = new ExportRepository(db, new ConsumerReleaseSelector());

            var files = await repository.GetExportFilesAsync(new ExportScope.Library(1, 0));

            files.Count.ShouldBe(1);
            files[0].OriginalFilename.ShouldBe("selected.rom");
        }
    }

    [Fact]
    public async Task GetExportFilesForTitleAsync_LibraryScope_UsesOwnedExposedMaterializedRelease()
    {
        int catalogReleaseId;
        using (var db = CreateDb())
        {
            SeedExportGraph(db);
            await db.PublishOriginalRomsAsync();
            await db.SaveChangesAsync();
            await RebuildCatalogAsync(db);
            catalogReleaseId = await ResolveCatalogReleaseIdAsync(db, datGameId: 1);
            db.MaterializedLibraryReleases.Add(NewAccessibleRelease(catalogReleaseId: catalogReleaseId));
            await db.PublishOriginalRomsAsync();
            await db.SaveChangesAsync();
        }

        using (var db = CreateDb())
        {
            var repository = new ExportRepository(db, new ConsumerReleaseSelector());

            var files = await repository.GetExportFilesForTitleAsync(
                1,
                new AuthorizedExportScope(
                    new ExportScope.Library(1, 0),
                    EffectiveLibraryUserId: null));

            files.ShouldHaveSingleItem().OriginalFilename.ShouldBe("selected.rom");
        }
    }

    [Fact]
    public async Task GetExportFilesForTitleAsync_UserAssignmentRevokedAfterScopeResolution_FailsClosed()
    {
        var userId = Guid.NewGuid();
        int catalogReleaseId;
        using (var db = CreateDb())
        {
            SeedExportGraph(db);
            var user = RomdUser.Create("export-race", "export-race@localhost");
            user.Id = userId;
            user.LibraryId = 1;
            db.Users.Add(user);
            await db.PublishOriginalRomsAsync();
            await db.SaveChangesAsync();
            await RebuildCatalogAsync(db);
            catalogReleaseId = await ResolveCatalogReleaseIdAsync(db, datGameId: 1);
            db.MaterializedLibraryReleases.Add(NewAccessibleRelease(catalogReleaseId: catalogReleaseId));
            await db.PublishOriginalRomsAsync();
            await db.SaveChangesAsync();
        }

        var authorization = new AuthorizedExportScope(
            new ExportScope.Library(1, 0),
            userId);
        using (var db = CreateDb())
        {
            var repository = new ExportRepository(db, new ConsumerReleaseSelector());
            (await repository.GetExportFilesForTitleAsync(1, authorization))
                .ShouldHaveSingleItem();

            await db.Users
                .Where(user => user.Id == userId)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(user => user.LibraryId, (int?)null));
        }

        using (var db = CreateDb())
        {
            var repository = new ExportRepository(db, new ConsumerReleaseSelector());

            var files = await repository.GetExportFilesForTitleAsync(1, authorization);

            files.ShouldBeEmpty();
        }
    }

    [Fact]
    public async Task GetExportFilesAsync_LibraryRematerializedAfterAuthorization_FailsClosed()
    {
        int catalogReleaseId;
        using (var db = CreateDb())
        {
            SeedExportGraph(db);
            db.Libraries.Local.Single().MaterializationGeneration = 4;
            await db.PublishOriginalRomsAsync();
            await db.SaveChangesAsync();
            await RebuildCatalogAsync(db);
            catalogReleaseId = await ResolveCatalogReleaseIdAsync(db, datGameId: 1);
            db.MaterializedLibraryReleases.Add(NewAccessibleRelease(catalogReleaseId: catalogReleaseId));
            await db.PublishOriginalRomsAsync();
            await db.SaveChangesAsync();
        }

        var staleScope = new ExportScope.Library(1, 4);
        using (var db = CreateDb())
        {
            var repository = new ExportRepository(db, new ConsumerReleaseSelector());
            (await repository.GetExportFilesAsync(staleScope)).ShouldHaveSingleItem();
            await db.Libraries.ExecuteUpdateAsync(setters => setters
                .SetProperty(library => library.MaterializationGeneration, 5));
        }

        using (var db = CreateDb())
        {
            var repository = new ExportRepository(db, new ConsumerReleaseSelector());

            (await repository.GetExportFilesAsync(staleScope)).ShouldBeEmpty();
        }
    }

    [Fact]
    public async Task GetExportFilesForTitleAsync_DormantSourceBeforeRefresh_ExportsPublishedSnapshot()
    {
        int catalogReleaseId;
        using (var db = CreateDb())
        {
            SeedExportGraph(db);
            await db.PublishOriginalRomsAsync();
            await db.SaveChangesAsync();
            await RebuildCatalogAsync(db);
            catalogReleaseId = await ResolveCatalogReleaseIdAsync(db, datGameId: 1);
            db.MaterializedLibraryReleases.Add(NewAccessibleRelease(catalogReleaseId: catalogReleaseId));
            await db.PublishOriginalRomsAsync();
            await db.SaveChangesAsync();

            await db.CatalogSources.ExecuteUpdateAsync(setters => setters
                .SetProperty(source => source.Status, "Disabled"));
            await db.Libraries.ExecuteUpdateAsync(setters => setters
                .SetProperty(library => library.NeedsMaterialization, true)
                .SetProperty(library => library.LastMaterializedAt, DateTimeOffset.UtcNow.AddMinutes(-5)));
        }

        using (var db = CreateDb())
        {
            var repository = new ExportRepository(db, new ConsumerReleaseSelector());

            var files = await repository.GetExportFilesForTitleAsync(
                1,
                new AuthorizedExportScope(
                    new ExportScope.Library(1, 0),
                    EffectiveLibraryUserId: null));

            // Publication is explicit: the published snapshot stays exportable until an admin refreshes.
            files.ShouldHaveSingleItem();
        }
    }

    [Fact]
    public async Task GetExportFilesForTitleAsync_ReleaseMovedToAnotherTitleBeforeRefresh_IsNotExportedUnderTheOldTitle()
    {
        using (var db = CreateDb())
        {
            SeedExportGraph(db);
            db.Titles.Add(new TitleEntity
            {
                Id = 2,
                PlatformId = 1,
                Name = "Destination Title",
                NormalizedName = "destination title",
                EnrichmentStatus = "None",
                CreatedAt = DateTimeOffset.UtcNow
            });
            await db.PublishOriginalRomsAsync();
            await db.SaveChangesAsync();
            await RebuildCatalogAsync(db);
            int catalogReleaseId = await ResolveCatalogReleaseIdAsync(db, datGameId: 1);
            db.MaterializedLibraryReleases.Add(NewAccessibleRelease(catalogReleaseId: catalogReleaseId));
            await db.PublishOriginalRomsAsync();
            await db.SaveChangesAsync();

            await db.CatalogReleases
                .Where(release => release.Id == catalogReleaseId)
                .ExecuteUpdateAsync(setters => setters.SetProperty(release => release.CatalogTitleId, 2));
        }

        using (var db = CreateDb())
        {
            var repository = new ExportRepository(db, new ConsumerReleaseSelector());

            var files = await repository.GetExportFilesForTitleAsync(
                1,
                new AuthorizedExportScope(
                    new ExportScope.Library(1, 0),
                    EffectiveLibraryUserId: null));

            files.ShouldBeEmpty();
        }
    }

    [Fact]
    public async Task GetExportFilesForTitleAsync_NeverPublishedLibrary_FailsClosed()
    {
        using (var db = CreateDb())
        {
            SeedExportGraph(db);
            await db.PublishOriginalRomsAsync();
            await db.SaveChangesAsync();
            await RebuildCatalogAsync(db);
            int catalogReleaseId = await ResolveCatalogReleaseIdAsync(db, datGameId: 1);
            db.MaterializedLibraryReleases.Add(NewAccessibleRelease(catalogReleaseId: catalogReleaseId));
            await db.PublishOriginalRomsAsync();
            await db.SaveChangesAsync();

            await db.Libraries.ExecuteUpdateAsync(setters => setters
                .SetProperty(library => library.NeedsMaterialization, true)
                .SetProperty(library => library.LastMaterializedAt, (DateTimeOffset?)null));
        }

        using (var db = CreateDb())
        {
            var repository = new ExportRepository(db, new ConsumerReleaseSelector());

            var files = await repository.GetExportFilesForTitleAsync(
                1,
                new AuthorizedExportScope(
                    new ExportScope.Library(1, 0),
                    EffectiveLibraryUserId: null));

            files.ShouldBeEmpty();
        }
    }

    [Fact]
    public async Task GetExportFilesForTitleAsync_AllCatalog_DormantOnlyBackingFailsClosed()
    {
        using (var db = CreateDb())
        {
            SeedExportGraph(db);
            await db.PublishOriginalRomsAsync();
            await db.SaveChangesAsync();
            await db.CatalogSources.ExecuteUpdateAsync(setters => setters
                .SetProperty(source => source.Status, "Disabled"));
        }

        using (var db = CreateDb())
        {
            var repository = new ExportRepository(db, new ConsumerReleaseSelector());

            var files = await repository.GetExportFilesForTitleAsync(
                1,
                new AuthorizedExportScope(
                    new ExportScope.AllCatalog(),
                    EffectiveLibraryUserId: null));

            files.ShouldBeEmpty();
        }
    }

    [Theory]
    [InlineData(false, true, LibraryConfigurationState.Valid, true)]
    [InlineData(true, true, LibraryConfigurationState.Valid, true)]
    [InlineData(true, false, LibraryConfigurationState.Valid, false)]
    [InlineData(false, true, LibraryConfigurationState.Invalid, false)]
    public async Task GetEffectiveLibraryScopeAsync_RequiresValidPublishedLibrary(
        bool needsMaterialization,
        bool published,
        LibraryConfigurationState configurationState,
        bool expected)
    {
        var userId = Guid.NewGuid();
        using (var db = CreateDb())
        {
            SeedExportGraph(db, configurationState.ToString());
            var library = db.Libraries.Local.Single();
            library.NeedsMaterialization = needsMaterialization;
            library.LastMaterializedAt = published ? DateTimeOffset.UtcNow.AddMinutes(-5) : null;
            library.MaterializationGeneration = 8;
            var user = RomdUser.Create("export-user", "export@localhost");
            user.Id = userId;
            user.LibraryId = 1;
            db.Users.Add(user);
            await db.PublishOriginalRomsAsync();
            await db.SaveChangesAsync();
        }

        using (var db = CreateDb())
        {
            var reader = new ExportAuthorizationReader(db);

            var scope = await reader.GetEffectiveLibraryScopeAsync(userId);

            if (expected)
            {
                scope.ShouldBe(new ExportScope.Library(1, 8));
            }
            else
            {
                scope.ShouldBeNull();
            }
        }
    }

    [Fact]
    public async Task GetCurrentLibraryScopeAsync_CurrentValidLibrary_ReturnsGeneration()
    {
        using (var db = CreateDb())
        {
            SeedExportGraph(db);
            db.Libraries.Local.Single().MaterializationGeneration = 13;
            await db.PublishOriginalRomsAsync();
            await db.SaveChangesAsync();
        }

        using (var db = CreateDb())
        {
            var reader = new ExportAuthorizationReader(db);

            var scope = await reader.GetCurrentLibraryScopeAsync(1);

            scope.ShouldBe(new ExportScope.Library(1, 13));
        }
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public async Task GetCurrentLibraryScopeAsync_PendingChanges_ServesOnlyPublishedLibrary(
        bool published,
        bool expected)
    {
        using (var db = CreateDb())
        {
            SeedExportGraph(db);
            var library = db.Libraries.Local.Single();
            library.NeedsMaterialization = true;
            library.LastMaterializedAt = published ? DateTimeOffset.UtcNow.AddMinutes(-5) : null;
            library.MaterializationGeneration = 13;
            await db.PublishOriginalRomsAsync();
            await db.SaveChangesAsync();
        }

        using (var db = CreateDb())
        {
            var reader = new ExportAuthorizationReader(db);

            var scope = await reader.GetCurrentLibraryScopeAsync(1);

            if (expected)
            {
                scope.ShouldBe(new ExportScope.Library(1, 13));
            }
            else
            {
                scope.ShouldBeNull();
            }
        }
    }

    [Fact]
    public async Task GetExportFilesAsync_NoLibraryId_ExportsAllMappedGames()
    {
        using (var db = CreateDb())
        {
            SeedExportGraph(db);
            await db.PublishOriginalRomsAsync();
            await db.SaveChangesAsync();
        }

        using (var db = CreateDb())
        {
            var repository = new ExportRepository(db, new ConsumerReleaseSelector());

            var files = await repository.GetExportFilesAsync(new ExportScope.AllCatalog());

            files.Select(f => f.OriginalFilename).ShouldBe(["selected.rom", "excluded.rom"], ignoreOrder: true);
        }
    }

    [Fact]
    public async Task GetExportFilesAsync_MoreThanLookupCeiling_UsesOneResolutionStatementAndReturnsCompleteStableResults()
    {
        const int releaseCount = MaxIdsPerLookup + 1;
        using (var db = CreateDb())
        {
            SeedLargeLibraryExportGraph(db, releaseCount);
            await db.PublishOriginalRomsAsync();
            await db.SaveChangesAsync();
        }

        _commands.Clear();
        using (var db = CreateDb())
        {
            var repository = new ExportRepository(db, new ConsumerReleaseSelector());

            var files = await repository.GetExportFilesAsync(new ExportScope.Library(1, 0));

            files.Count.ShouldBe(releaseCount);
            files.Select(file => file.TitleId).ShouldBe(Enumerable.Range(1, releaseCount));
            files.Select(file => file.OriginalFilename).Distinct().Count().ShouldBe(releaseCount);
        }

        var fileCommands = _commands.Commands
            .Where(command => command.CommandText.Contains("CatalogReleaseFiles", StringComparison.Ordinal))
            .ToList();
        var command = fileCommands.ShouldHaveSingleItem();
        command.ParameterCount.ShouldBe(2, string.Join(",", command.ParameterNames));
    }

    [Fact]
    public async Task GetExportFilesAsync_FilelessWinningCandidate_DoesNotFallBackToCandidateWithFiles()
    {
        using (var db = CreateDb())
        {
            SeedLargeLibraryExportGraph(db, releaseCount: 1);
            var now = DateTimeOffset.UtcNow;
            string sha1Text = 2.ToString("x").PadLeft(40, '0');
            db.CatalogReleases.Add(new CatalogReleaseEntity
            {
                Id = 2,
                PlatformId = 1,
                CatalogTitleId = 1,
                Fingerprint = $"sha1:{sha1Text}",
                PrimarySha1 = Sha1.Parse(sha1Text),
                Name = "Preferred fileless release",
                Revision = "Rev 9",
                SourceCount = 1,
                SizeBytes = 1024,
                FileCount = 1,
                CreatedAt = now
            });
            db.CatalogReleaseFiles.Add(new CatalogReleaseFileEntity
            {
                Id = 2,
                CatalogReleaseId = 2,
                FileFingerprint = $"sha1:{sha1Text}",
                Name = "missing.rom",
                Size = 1024,
                Sha1 = Sha1.Parse(sha1Text)
            });
            db.MaterializedLibraryReleases.Add(new MaterializedLibraryReleaseEntity
            {
                Id = 2,
                LibraryId = 1,
                TitleId = 1,
                CatalogReleaseId = 2,
                DatGameId = 2,
                DatFileId = 1,
                PlatformId = 1,
                IsEligible = true,
                IsComplete = true,
                IsOwned = true,
                IsPlayable = true,
                IsExposed = true,
                ExposureReason = "ExposedDefault"
            });
            await db.PublishOriginalRomsAsync();
            await db.SaveChangesAsync();
        }

        using (var db = CreateDb())
        {
            var repository = new ExportRepository(db, new ConsumerReleaseSelector());

            var files = await repository.GetExportFilesAsync(new ExportScope.Library(1, 0));

            files.ShouldBeEmpty();
        }
    }

    [Fact]
    public async Task SearchGamesAsync_LibraryId_ReturnsOnlyMaterializedDatGame()
    {
        using (var db = CreateDb())
        {
            SeedExportGraph(db);
            db.MaterializedLibraryReleases.Add(NewAccessibleRelease());
            await db.PublishOriginalRomsAsync();
            await db.SaveChangesAsync();
        }

        using (var db = CreateDb())
        {
            var repository = new SearchRepository(db);

            var result = (await repository.SearchGamesAsync(
                query: null,
                filters: new GameSearchFilters(BiosFilter: BiosFilter.Include),
                sortField: GameSortField.Name,
                cursor: null,
                limit: 10,
                libraryId: 1)).Value;

            result.Items.Select(g => g.Name).ShouldBe(["Shared Title (USA)"]);
        }
    }

    [Fact]
    public async Task GetGamesByDatIdAsync_LibraryId_ReturnsOnlyMaterializedDatGame()
    {
        using (var db = CreateDb())
        {
            SeedExportGraph(db);
            db.MaterializedLibraryReleases.Add(NewAccessibleRelease());
            await db.PublishOriginalRomsAsync();
            await db.SaveChangesAsync();
        }

        using (var db = CreateDb())
        {
            var repository = new DatRepository(db, TimeProvider.System);

            var result = await repository.GetGamesByDatIdAsync(
                datId: 1,
                cursor: null,
                limit: 10,
                biosFilter: BiosFilter.Include,
                libraryId: 1);

            result.Items.Select(g => g.Name).ShouldBe(["Shared Title (USA)"]);
        }
    }

    [Fact]
    public async Task IsAccessibleAsync_LibraryId_BlocksNonMaterializedDatGameForSameTitle()
    {
        using (var db = CreateDb())
        {
            SeedExportGraph(db);
            db.MaterializedLibraryReleases.Add(NewAccessibleRelease());
            await db.PublishOriginalRomsAsync();
            await db.SaveChangesAsync();
        }

        using (var db = CreateDb())
        {
            var repository = Romd.PostgreSql.TestSupport.MediaTestServices.CreateRomRepository(db);

            var selectedReleaseAccessible = await repository.IsAccessibleAsync(1, 1);
            var otherReleaseAccessible = await repository.IsAccessibleAsync(2, 1);

            selectedReleaseAccessible.ShouldBeTrue();
            otherReleaseAccessible.ShouldBeFalse();
        }
    }

    [Fact]
    public async Task IsAccessibleAsync_InvalidLibrary_BlocksMaterializedDatGame()
    {
        using (var db = CreateDb())
        {
            SeedExportGraph(db, configurationState: LibraryConfigurationState.Invalid.ToString());
            db.MaterializedLibraryReleases.Add(NewAccessibleRelease());
            await db.PublishOriginalRomsAsync();
            await db.SaveChangesAsync();
        }

        using (var db = CreateDb())
        {
            var repository = Romd.PostgreSql.TestSupport.MediaTestServices.CreateRomRepository(db);

            var accessible = await repository.IsAccessibleAsync(1, 1);

            accessible.ShouldBeFalse();
        }
    }

    [Fact]
    public async Task IsAccessibleAsync_ExposedButUnownedRelease_BlocksDownload()
    {
        using (var db = CreateDb())
        {
            SeedExportGraph(db);
            db.MaterializedLibraryReleases.Add(NewAccessibleRelease(datGameId: 2, isOwned: false));
            await db.PublishOriginalRomsAsync();
            await db.SaveChangesAsync();
        }

        using (var db = CreateDb())
        {
            var repository = Romd.PostgreSql.TestSupport.MediaTestServices.CreateRomRepository(db);

            var accessible = await repository.IsAccessibleAsync(2, 1);

            accessible.ShouldBeFalse();
        }
    }

    public void Dispose()
    {
        _connection.Dispose();
    }

    private RomdDbContext CreateDb() => new(_contextOptions);

    private static void SeedExportGraph(RomdDbContext db, string? configurationState = null)
    {
        var now = DateTimeOffset.UtcNow;

        db.Files.AddRange(
            NewFile(1, "01"),
            NewFile(2, "02"),
            NewFile(3, "03"));

        db.Platforms.Add(new PlatformEntity
        {
            Id = 1,
            Name = "Test Platform",
            Ownership = Romd.Domain.ReferenceData.ReferenceOwnership.Romd,
            BuiltInVersion = 1,
            BaseName = "Test Platform",
            BaseCompactLabel = "Test Platform",
            CanonicalKey = "test",
            ShortName = "test",
            CreatedAt = now
        });

        db.DatFiles.Add(new DatFileEntity
        {
            Source = new DatSourceEntity
            {
                CatalogSource = new CatalogSourceEntity { Id = 1, Kind = "Dat", Status = "Active" }
            },
            Id = 1,
            Name = "Test DAT",
            Description = "Test DAT",
            Type = "Unknown",
            PlatformId = 1,
            OriginalFilename = "test.dat",
            FileId = 1,
            GameCount = 2,
            RomCount = 2,
            CreatedAt = now
        });

        db.Titles.Add(new TitleEntity
        {
            Id = 1,
            PlatformId = 1,
            Name = "Shared Title",
            NormalizedName = "shared title",
            Genre = "Platformer",
            EnrichmentStatus = "None",
            CreatedAt = now
        });

        db.SourceEntries.AddRange(
            new SourceEntryEntity
            {
                Id = 1,
                CatalogSourceId = 1,
                EntryKey = "Shared Title (USA)",
                Name = "Shared Title (USA)",
                PlatformId = 1,
                CreatedAt = now
            },
            new SourceEntryEntity
            {
                Id = 2,
                CatalogSourceId = 1,
                EntryKey = "Shared Title (Japan)",
                Name = "Shared Title (Japan)",
                PlatformId = 1,
                CreatedAt = now
            });

        db.DatGames.AddRange(
            new DatGameEntity
            {
                Id = 1,
                DatFileId = 1,
                SourceEntryId = 1,
                Name = "Shared Title (USA)",
                CreatedAt = now
            },
            new DatGameEntity
            {
                Id = 2,
                DatFileId = 1,
                SourceEntryId = 2,
                Name = "Shared Title (Japan)",
                CreatedAt = now
            });

        db.TitleSourceLinks.AddRange(
            new TitleSourceLinkEntity
            {
                SourceEntryId = 1,
                TitleId = 1,
                CreatedAt = now
            },
            new TitleSourceLinkEntity
            {
                SourceEntryId = 2,
                TitleId = 1,
                CreatedAt = now
            });

        db.StageOriginalRoms(
            NewRomFile(1, 2, "selected.rom", "11"),
            NewRomFile(2, 3, "excluded.rom", "22"));

        // DAT rom hashes match the owned RomFiles they link to, mirroring real ingestion so the
        // SHA-1-based catalog availability join resolves the same files.
        db.DatRoms.AddRange(
            new DatRomEntity
            {
                Id = 1,
                DatGameId = 1,
                Name = "selected.rom",
                Size = 1024,
                Sha1 = Sha1.Parse("11" + new string('0', 38)),
                RomFileId = 1,
                CreatedAt = now
            },
            new DatRomEntity
            {
                Id = 2,
                DatGameId = 2,
                Name = "excluded.rom",
                Size = 1024,
                Sha1 = Sha1.Parse("22" + new string('0', 38)),
                RomFileId = 2,
                CreatedAt = now
            });

        db.Libraries.Add(new LibraryEntity
        {
            Id = 1,
            Name = "Curated",
            ConfigurationJson = "{}",
            ConfigurationState = configurationState ?? LibraryConfigurationState.Valid.ToString(),
            NeedsMaterialization = false,
            ItemCount = 1,
            CreatedAt = now
        });
    }

    private static void SeedLargeLibraryExportGraph(RomdDbContext db, int releaseCount)
    {
        var now = DateTimeOffset.UtcNow;
        db.Platforms.Add(new PlatformEntity
        {
            Id = 1,
            Name = "Test Platform",
            Ownership = Romd.Domain.ReferenceData.ReferenceOwnership.Romd,
            BuiltInVersion = 1,
            BaseName = "Test Platform",
            BaseCompactLabel = "Test Platform",
            CanonicalKey = "test",
            ShortName = "test",
            CreatedAt = now
        });
        db.Libraries.Add(new LibraryEntity
        {
            Id = 1,
            Name = "Curated",
            ConfigurationJson = "{}",
            ConfigurationState = LibraryConfigurationState.Valid.ToString(),
            NeedsMaterialization = false,
            ItemCount = releaseCount,
            CreatedAt = now
        });

        for (int id = 1; id <= releaseCount; id++)
        {
            string sha1Text = id.ToString("x").PadLeft(40, '0');
            string md5Text = id.ToString("x").PadLeft(32, '0');
            string crcText = id.ToString("x").PadLeft(8, '0');
            string sha256Text = id.ToString("x").PadLeft(64, '0');
            var sha1 = Sha1.Parse(sha1Text);

            db.Titles.Add(new TitleEntity
            {
                Id = id,
                PlatformId = 1,
                Name = $"Title {id:D4}",
                NormalizedName = $"title{id:D4}",
                EnrichmentStatus = EnrichmentStatus.None.ToString(),
                CreatedAt = now
            });
            db.CatalogReleases.Add(new CatalogReleaseEntity
            {
                Id = id,
                PlatformId = 1,
                CatalogTitleId = id,
                Fingerprint = $"sha1:{sha1Text}",
                PrimarySha1 = sha1,
                Name = $"Release {id:D4}",
                SourceCount = 1,
                SizeBytes = 1024,
                FileCount = 1,
                CreatedAt = now
            });
            db.CatalogReleaseFiles.Add(new CatalogReleaseFileEntity
            {
                Id = id,
                CatalogReleaseId = id,
                FileFingerprint = $"sha1:{sha1Text}",
                Name = $"file-{id:D4}.rom",
                Size = 1024,
                Sha1 = sha1
            });
            db.Files.Add(new FileEntityPersistence
            {
                Id = id,
                Sha256 = Sha256.Parse(sha256Text),
                Size = 1024,
                SizeOnDisk = 1024,
                CreatedAt = now
            });
            db.StageOriginalRom(new OriginalRomFixture
            {
                Id = id,
                FileId = id,
                OriginalFilename = $"file-{id:D4}.rom",
                Sha1 = sha1,
                Md5 = Md5.Parse(md5Text),
                Crc32 = Crc32.Parse(crcText),
                CreatedAt = now
            });
            db.MaterializedLibraryReleases.Add(new MaterializedLibraryReleaseEntity
            {
                Id = id,
                LibraryId = 1,
                TitleId = id,
                CatalogReleaseId = id,
                DatGameId = id,
                DatFileId = 1,
                PlatformId = 1,
                IsEligible = true,
                IsComplete = true,
                IsOwned = true,
                IsPlayable = true,
                IsExposed = true,
                ExposureReason = "ExposedDefault"
            });
        }
    }

    private static FileEntityPersistence NewFile(int id, string prefix) =>
        new()
        {
            Id = id,
            Sha256 = Sha256.Parse(prefix + new string('0', 62)),
            Size = 1024,
            SizeOnDisk = 1024,
            CreatedAt = DateTimeOffset.UtcNow
        };

    private static OriginalRomFixture NewRomFile(int id, int fileId, string filename, string prefix) =>
        new()
        {
            Id = id,
            FileId = fileId,
            OriginalFilename = filename,
            Sha1 = Sha1.Parse(prefix + new string('0', 38)),
            Md5 = Md5.Parse(prefix + new string('0', 30)),
            Crc32 = Crc32.Parse(prefix + new string('0', 6)),
            CreatedAt = DateTimeOffset.UtcNow
        };

    private async Task RebuildCatalogAsync(RomdDbContext db)
    {
        var service = CatalogProjectionTestFactory.Create(db);
        await service.RebuildPlatformAsync(1);
    }

    private static async Task<int> ResolveCatalogReleaseIdAsync(RomdDbContext db, int datGameId) =>
        await db.CatalogReleaseSources
            .AsNoTracking()
            .Where(source => db.DatGames.Any(game =>
                game.Id == datGameId && game.SourceEntryId == source.SourceEntryId))
            .Select(source => source.CatalogReleaseId)
            .SingleAsync();

    private static MaterializedLibraryReleaseEntity NewAccessibleRelease(
        int datGameId = 1,
        bool isOwned = true,
        int? catalogReleaseId = null) =>
        new()
        {
            LibraryId = 1,
            TitleId = 1,
            CatalogReleaseId = catalogReleaseId,
            DatGameId = datGameId,
            DatFileId = 1,
            PlatformId = 1,
            IsEligible = true,
            IsComplete = true,
            IsOwned = isOwned,
            IsPlayable = isOwned,
            IsBlocked = false,
            BlockReason = null,
            IsExposed = true,
            ExposureReason = "ExposedDefault"
        };

    [Fact]
    public async Task GetExportFiles_UnregisteredRelationalPlatform_IsLegalAndOmittedFromSystemKeys()
    {
        using var db = CreateDb();
        SeedExportGraph(db);
        var platform = db.Platforms.Local.Single(row => row.Id == 1);
        platform.CanonicalKey = null;
        platform.Ownership = null;
        platform.BuiltInVersion = null;
        platform.BaseName = null;
        platform.BaseCompactLabel = null;
        platform.BaseDescription = null;
        platform.BaseAssetHash = null;
        platform.Retired = false;
        platform.BaseMonochrome = false;
        await db.PublishOriginalRomsAsync();
        await db.SaveChangesAsync();

        (await db.Platforms.SingleAsync(row => row.Id == 1)).CanonicalKey.ShouldBeNull();
        (await db.Platforms.SingleAsync(row => row.Id == 1)).Ownership.ShouldBeNull();
        var keys = await new SystemSummaryReader(db).ReadKeysAsync(CancellationToken.None);
        Should.Throw<InvalidOperationException>(() => keys.Required(1)).Message.ShouldContain("Unregistered system identity 1");
        var repository = new ExportRepository(db, new ConsumerReleaseSelector());
        var files = await repository.GetExportFilesAsync(new ExportScope.AllCatalog());
        files.ShouldContain(file => file.PlatformId == 1 && file.RomFileId == 1 && file.OriginalFilename == "selected.rom");
        var titleFiles = await repository.GetExportFilesForTitleAsync(1,
            new AuthorizedExportScope(new ExportScope.AllCatalog(), null));
        titleFiles.ShouldContain(file => file.PlatformId == 1 && file.RomFileId == 1 && file.OriginalFilename == "selected.rom");
    }
}
