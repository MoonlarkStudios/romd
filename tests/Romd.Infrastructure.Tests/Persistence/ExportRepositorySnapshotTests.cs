using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Romd.Admin.Application.Export;
using Romd.Consumer.Application.Browse;
using Romd.Domain.Catalog;
using Romd.Domain.Hashing;
using Romd.Domain.Libraries;
using Romd.Persistence;
using Romd.Persistence.Entities;
using Romd.Persistence.Repositories;
using Romd.PostgreSql.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Persistence;

public sealed class ExportRepositorySnapshotTests : IAsyncLifetime
{
    private readonly PostgreSqlTestDatabase _database = PostgreSqlTestDatabase.Create();

    public async Task InitializeAsync()
    {
        await using var db = CreateDb();
        Seed(db);
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task GetExportFilesAsync_ConcurrentRematerialization_ReturnsOneCompleteGeneration()
    {
        var pause = new PauseBeforeFileResolutionInterceptor();
        await using var readDb = CreateDb(pause);
        var repository = new ExportRepository(readDb, new ConsumerReleaseSelector());

        var readTask = repository.GetExportFilesAsync(new ExportScope.Library(1, 0));
        await pause.QueryReached.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await using (var writeDb = CreateDb())
        {
            int updated = await writeDb.MaterializedLibraryReleases
                .Where(release => release.LibraryId == 1 && release.TitleId == 1)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(release => release.CatalogReleaseId, 2)
                    .SetProperty(release => release.DatGameId, 2));
            updated.ShouldBe(1);
        }

        pause.Resume.TrySetResult();
        var files = await readTask;

        files.ShouldHaveSingleItem().OriginalFilename.ShouldBe("new.rom");
        pause.FileResolutionCommandCount.ShouldBe(1);
    }

    public Task DisposeAsync()
    {
        _database.Dispose();
        return Task.CompletedTask;
    }

    private RomdDbContext CreateDb(IInterceptor? interceptor = null)
    {
        var builder = new DbContextOptionsBuilder<RomdDbContext>()
            .UseNpgsql(_database.ConnectionString);
        if (interceptor is not null)
        {
            builder.AddInterceptors(interceptor);
        }

        return new RomdDbContext(builder.Options);
    }

    private static void Seed(RomdDbContext db)
    {
        var now = DateTimeOffset.UtcNow;
        db.Platforms.Add(new PlatformEntity { Id = 1, Name = "Test", Ownership = Romd.Domain.ReferenceData.ReferenceOwnership.Romd, BuiltInVersion = 1, BaseName = "Test", BaseCompactLabel = "Test", CanonicalKey = "test", ShortName = "test", CreatedAt = now });
        db.Libraries.Add(new LibraryEntity
        {
            Id = 1,
            Name = "Library",
            ConfigurationJson = "{}",
            ConfigurationState = LibraryConfigurationState.Valid.ToString(),
            CreatedAt = now
        });
        db.Titles.Add(new TitleEntity
        {
            Id = 1,
            PlatformId = 1,
            Name = "Title",
            NormalizedName = "title",
            EnrichmentStatus = EnrichmentStatus.None.ToString(),
            CreatedAt = now
        });

        for (int id = 1; id <= 2; id++)
        {
            string hex = id.ToString("x");
            string sha1Text = hex.PadLeft(40, '0');
            var sha1 = Sha1.Parse(sha1Text);
            db.CatalogReleases.Add(new CatalogReleaseEntity
            {
                Id = id,
                PlatformId = 1,
                CatalogTitleId = 1,
                Fingerprint = $"sha1:{sha1Text}",
                PrimarySha1 = sha1,
                Name = $"Release {id}",
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
                Name = id == 1 ? "old.rom" : "new.rom",
                Size = 1024,
                Sha1 = sha1
            });
            db.Files.Add(new FileEntityPersistence
            {
                Id = id,
                Sha256 = Sha256.Parse(hex.PadLeft(64, '0')),
                Size = 1024,
                SizeOnDisk = 1024,
                CreatedAt = now
            });
            db.RomFiles.Add(new RomFileEntity
            {
                Id = id,
                FileId = id,
                OriginalFilename = id == 1 ? "old.rom" : "new.rom",
                Sha1 = sha1,
                Md5 = Md5.Parse(hex.PadLeft(32, '0')),
                Crc32 = Crc32.Parse(hex.PadLeft(8, '0')),
                CreatedAt = now
            });
        }

        db.MaterializedLibraryReleases.Add(new MaterializedLibraryReleaseEntity
        {
            Id = 1,
            LibraryId = 1,
            TitleId = 1,
            CatalogReleaseId = 1,
            DatGameId = 1,
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

    private sealed class PauseBeforeFileResolutionInterceptor : DbCommandInterceptor
    {
        public TaskCompletionSource QueryReached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Resume { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int FileResolutionCommandCount { get; private set; }

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("CatalogReleaseFiles", StringComparison.Ordinal))
            {
                FileResolutionCommandCount++;
                if (FileResolutionCommandCount == 1)
                {
                    QueryReached.TrySetResult();
                    await Resume.Task.WaitAsync(cancellationToken);
                }
            }

            return await base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }
}
