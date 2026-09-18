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
using Romd.Infrastructure.Source;
using Romd.Infrastructure.Tests.Helpers;
using Romd.PostgreSql.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Persistence;

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
    public async Task GetExportFilesAsync_LibraryId_ExportsOnlyMaterializedDatGame()
    {
        int catalogReleaseId;
        using (var db = CreateDb())
        {
            SeedExportGraph(db);
            await db.SaveChangesAsync();
            await RebuildCatalogAsync(db);
            catalogReleaseId = await ResolveCatalogReleaseIdAsync(db, datGameId: 1);
            db.MaterializedLibraryReleases.Add(NewAccessibleRelease(catalogReleaseId: catalogReleaseId));
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
            await db.SaveChangesAsync();
            await RebuildCatalogAsync(db);
            catalogReleaseId = await ResolveCatalogReleaseIdAsync(db, datGameId: 1);
            db.MaterializedLibraryReleases.Add(NewAccessibleRelease(catalogReleaseId: catalogReleaseId));
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
            await db.SaveChangesAsync();
            await RebuildCatalogAsync(db);
            catalogReleaseId = await ResolveCatalogReleaseIdAsync(db, datGameId: 1);
            db.MaterializedLibraryReleases.Add(NewAccessibleRelease(catalogReleaseId: catalogReleaseId));
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
            await db.SaveChangesAsync();
            await RebuildCatalogAsync(db);
            catalogReleaseId = await ResolveCatalogReleaseIdAsync(db, datGameId: 1);
            db.MaterializedLibraryReleases.Add(NewAccessibleRelease(catalogReleaseId: catalogReleaseId));
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
    public async Task GetExportFilesForTitleAsync_DormantSourceWithStaleProjection_FailsClosed()
    {
        int catalogReleaseId;
        using (var db = CreateDb())
        {
            SeedExportGraph(db);
            await db.SaveChangesAsync();
            await RebuildCatalogAsync(db);
            catalogReleaseId = await ResolveCatalogReleaseIdAsync(db, datGameId: 1);
            db.MaterializedLibraryReleases.Add(NewAccessibleRelease(catalogReleaseId: catalogReleaseId));
            await db.SaveChangesAsync();

            await db.CatalogSources.ExecuteUpdateAsync(setters => setters
                .SetProperty(source => source.Status, "Disabled"));
            await db.Libraries.ExecuteUpdateAsync(setters => setters
                .SetProperty(library => library.NeedsMaterialization, true));
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
    [InlineData(false, LibraryConfigurationState.Valid, true)]
    [InlineData(true, LibraryConfigurationState.Valid, false)]
    [InlineData(false, LibraryConfigurationState.Invalid, false)]
    public async Task GetEffectiveLibraryScopeAsync_RequiresValidCurrentMaterialization(
        bool needsMaterialization,
        LibraryConfigurationState configurationState,
        bool expected)
    {
        var userId = Guid.NewGuid();
        using (var db = CreateDb())
        {
            SeedExportGraph(db, configurationState.ToString());
            var library = db.Libraries.Local.Single();
            library.NeedsMaterialization = needsMaterialization;
            library.MaterializationGeneration = 8;
            var user = RomdUser.Create("export-user", "export@localhost");
            user.Id = userId;
            user.LibraryId = 1;
            db.Users.Add(user);
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
            await db.SaveChangesAsync();
        }

        using (var db = CreateDb())
        {
            var reader = new ExportAuthorizationReader(db);

            var scope = await reader.GetCurrentLibraryScopeAsync(1);

            scope.ShouldBe(new ExportScope.Library(1, 13));
        }
    }

    [Fact]
    public async Task GetExportFilesAsync_NoLibraryId_ExportsAllMappedGames()
    {
        using (var db = CreateDb())
        {
            SeedExportGraph(db);
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
            await db.SaveChangesAsync();
        }

        using (var db = CreateDb())
        {
            var repository = new SearchRepository(db);

            var result = await repository.SearchGamesAsync(
                query: null,
                filters: new GameSearchFilters(BiosFilter: BiosFilter.Include),
                sortField: GameSortField.Name,
                cursor: null,
                limit: 10,
                libraryId: 1);

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
            await db.SaveChangesAsync();
        }

        using (var db = CreateDb())
        {
            var repository = new RomRepository(db);

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
            await db.SaveChangesAsync();
        }

        using (var db = CreateDb())
        {
            var repository = new RomRepository(db);

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
            await db.SaveChangesAsync();
        }

        using (var db = CreateDb())
        {
            var repository = new RomRepository(db);

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
            Ownership = Romd.Domain.ReferenceData.ReferenceOwnership.Romd, BuiltInVersion = 1, BaseName = "Test Platform", BaseCompactLabel = "Test Platform", CanonicalKey = "test", ShortName = "test",
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

        db.RomFiles.AddRange(
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
            Ownership = Romd.Domain.ReferenceData.ReferenceOwnership.Romd, BuiltInVersion = 1, BaseName = "Test Platform", BaseCompactLabel = "Test Platform", CanonicalKey = "test", ShortName = "test",
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
            db.RomFiles.Add(new RomFileEntity
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

    private static RomFileEntity NewRomFile(int id, int fileId, string filename, string prefix) =>
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
}
