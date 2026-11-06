using ErrorOr;
using Romd.Application.Common.ReferenceCatalog;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Admin.Application.Source.Dat;
using Romd.Admin.Application.Source.Platform;
using Romd.Admin.Application.Source.Platform.Commands.ImportSystemDat;
using Romd.Admin.Application.Storage.Files;
using Romd.Application.Common.Configuration;
using Romd.Application.Common.Security;
using Romd.Dat.Parsing;
using Romd.Dat.Parsing.Formats.Logiqx;
using Romd.Domain.Jobs;
using Romd.Infrastructure.Dats;
using Romd.Infrastructure.Jobs;
using Romd.Persistence;
using Romd.Persistence.Entities;
using Romd.Persistence.ReferenceData;
using Romd.Persistence.Repositories;
using Romd.Persistence.Systems;
using Romd.PostgreSql.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Systems;

public sealed partial class SystemSetupTests
{
    [Fact]
    public async Task List_CountsTrackedTitlesIndependentlyOfLocalFilesAndOtherSystems()
    {
        using var database = PostgreSqlTestDatabase.Create();
        await using var db = database.CreateContext();
        await SharedReferenceDataSeeder.InitializeAsync(db, Romd.PostgreSql.TestSupport.TestReferenceBlobs.For(db), clock: TimeProvider.System);
        var platforms = await db.Platforms.OrderBy(p => p.Id).Take(2).ToListAsync();
        var tracked = new TitleEntity { PlatformId = platforms[0].Id, Name = "Tracked", NormalizedName = "tracked", EnrichmentStatus = "None" };
        var local = new TitleEntity { PlatformId = platforms[0].Id, Name = "Local", NormalizedName = "local", EnrichmentStatus = "None", HasLocalPayload = true };
        var other = new TitleEntity { PlatformId = platforms[1].Id, Name = "Other", NormalizedName = "other", EnrichmentStatus = "None" };
        db.Titles.AddRange(tracked, local, other);
        await db.SaveChangesAsync();
        db.TrackedTitles.AddRange(new TrackedTitleEntity { TitleId = tracked.Id }, new TrackedTitleEntity { TitleId = other.Id });
        await db.SaveChangesAsync();
        var rows = await List(Service(db));
        rows.Single(r => r.Id == platforms[0].Id).TrackedTitles.ShouldBe(1);
        rows.Single(r => r.Id == platforms[0].Id).OwnedTitles.ShouldBe(1);
        rows.Single(r => r.Id == platforms[1].Id).TrackedTitles.ShouldBe(1);
        rows.Single(r => r.Id == platforms[1].Id).OwnedTitles.ShouldBe(0);
    }

    private static SystemSetupService Service(RomdDbContext db)
    {
        var parser = new DatParser([new LogiqxDatFormat(NullLogger<LogiqxDatFormat>.Instance)]);
        return new(db, new DatReplacementReview(new DatRepository(db, TimeProvider.System), Substitute.For<IFileStorageService>(),
            parser, parser),
            new PlatformHeaderResolver(new PlatformRepository(db), new PlatformAliasRepository(db)));
    }

    private static ImportSystemDatCommandHandler Importer(RomdDbContext db, string root, IUnitOfWork? work = null)
    {
        var parser = new DatParser([new LogiqxDatFormat(NullLogger<LogiqxDatFormat>.Instance)]);
        var review = new DatReplacementReview(new DatRepository(db, TimeProvider.System), Substitute.For<IFileStorageService>(),
            parser, parser);
        var options = Substitute.For<IRomdOptions>();
        options.DataDirectory.Returns(root);
        return new(review, new SystemDatImportStore(db), new JobRepository(db, TimeProvider.System),
            new JobWorkspaceExclusion(options), new UploadWorkspacePreparation(options), new UploadJobRepository(db, TimeProvider.System),
            work ?? new EfUnitOfWork(db), Substitute.For<ICurrentUser>(), TimeProvider.System,
            NullLogger<ImportSystemDatCommandHandler>.Instance);
    }

    private sealed class ImportWorkspace : IDisposable
    {
        public string Root { get; } = Directory.CreateTempSubdirectory("romd-system-import-fixture-").FullName;
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }

    private sealed class ImportWork(IUnitOfWork inner) : IUnitOfWork
    {
        public Func<CancellationToken, Task>? AfterFlush { get; init; }
        public Task<ITransaction> BeginTransactionAsync(CancellationToken ct = default) => inner.BeginTransactionAsync(ct);
        public TransactionConflict ConflictOf(Exception error) => inner.ConflictOf(error);
        public async Task FlushAsync(CancellationToken ct = default)
        {
            await inner.FlushAsync(ct);
            if (AfterFlush is { } afterFlush)
                await afterFlush(ct);
        }
    }

    private static async Task<IReadOnlyList<ManagedSystem>> List(SystemSetupService service)
    {
        List<ManagedSystem> rows = [];
        string? cursor = null;
        do
        {
            var page = await service.ListAsync(ReferencePageRequest.Create("test", cursor: cursor).Value, default);
            rows.AddRange(page.Items);
            cursor = page.NextCursor;
            page.HasNextPage.ShouldBe(cursor is not null);
        } while (cursor is not null);
        return rows;
    }
    private static MemoryStream Dat(string name = "Sony - PlayStation") => new(Encoding.UTF8.GetBytes(
        $"<datafile><header><name>{name}</name><version>1</version></header><game name='Test'><rom name='test.bin' size='1' crc='11111111'/></game></datafile>"));

    [Fact]
    public async Task FreshRegistry_IsNotEnabled_ExplicitChoiceSurvivesSeedingAndLocalData()
    {
        using var database = PostgreSqlTestDatabase.Create();
        await using var db = database.CreateContext();
        await SharedReferenceDataSeeder.InitializeAsync(db, Romd.PostgreSql.TestSupport.TestReferenceBlobs.For(db), clock: TimeProvider.System);
        var service = Service(db);
        var systems = await List(service);
        systems.Count.ShouldBe(56);
        systems.ShouldAllBe(x => !x.Enabled);
        var psx = systems.Single(x => x.SystemId == "psx");
        (await service.ListAsync(ReferencePageRequest.Create("test", query: "PS1").Value, default)).Items.ShouldContain(x => x.Id == psx.Id);
        (await service.SetEnabledAsync(psx.Id, true, default)).IsError.ShouldBeFalse();
        (await List(service)).Single(x => x.Id == psx.Id).State.ShouldBe("NeedsCatalog");
        (await service.SetEnabledAsync(psx.Id, false, default)).IsError.ShouldBeFalse();
        db.DatSubscriptions.Add(new DatSubscriptionEntity { CatalogId = "redump/psx/discs", PlatformId = psx.Id, State = "ReadyToImport" });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        await SharedReferenceDataSeeder.InitializeAsync(db, Romd.PostgreSql.TestSupport.TestReferenceBlobs.For(db), clock: TimeProvider.System);
        (await List(service)).Single(x => x.Id == psx.Id).Enabled.ShouldBeFalse();
        (await db.DatSubscriptions.CountAsync()).ShouldBe(1);
        (await service.SetEnabledAsync(psx.Id, true, default)).IsError.ShouldBeFalse();
        (await List(service)).Single(x => x.Id == psx.Id).Enabled.ShouldBeTrue();
    }

    [Fact]
    public async Task SetEnabledAsync_DisablingConfiguredSystems_RetainsDatAndOwnedTitle()
    {
        using var database = PostgreSqlTestDatabase.Create();
        await using var db = database.CreateContext();
        await SharedReferenceDataSeeder.InitializeAsync(db, Romd.PostgreSql.TestSupport.TestReferenceBlobs.For(db), clock: TimeProvider.System);
        int psx = await db.Platforms.Where(x => x.ShortName == "psx").Select(x => x.Id).SingleAsync();
        int snes = await db.Platforms.Where(x => x.ShortName == "snes").Select(x => x.Id).SingleAsync();
        var file = new FileEntityPersistence { Sha256 = Romd.Domain.Hashing.Sha256.FromSpan(new byte[32]), Size = 1, SizeOnDisk = 1 };
        db.Files.Add(file);
        await db.SaveChangesAsync();
        var dat = Romd.Domain.Source.Dat.DatFile.CreateNew("Catalog", "Catalog", Romd.Domain.Source.Dat.DatType.Redump, "catalog.dat", file.Id, now: DateTimeOffset.UnixEpoch, psx);
        await new DatRepository(db, TimeProvider.System).AddStagedAsync(dat, Romd.Domain.Source.Dat.DatSource.CreateNew(now: DateTimeOffset.UnixEpoch));
        db.Titles.Add(new TitleEntity { PlatformId = snes, Name = "Owned", NormalizedName = "owned", EnrichmentStatus = "None", HasLocalPayload = true });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var service = Service(db);
        (await service.SetEnabledAsync(psx, true, default)).IsError.ShouldBeFalse();
        (await service.SetEnabledAsync(snes, true, default)).IsError.ShouldBeFalse();
        var rows = await List(service);
        rows.Single(x => x.Id == psx).Enabled.ShouldBeTrue();
        rows.Single(x => x.Id == snes).Enabled.ShouldBeTrue();
        rows.Single(x => x.Id == snes).OwnedTitles.ShouldBe(1);
        (await service.SetEnabledAsync(psx, false, default)).IsError.ShouldBeFalse();
        (await service.SetEnabledAsync(snes, false, default)).IsError.ShouldBeFalse();
        (await db.DatFiles.CountAsync()).ShouldBe(1);
        (await db.Titles.CountAsync()).ShouldBe(1);
        await using (var sourceChange = await db.Database.BeginTransactionAsync())
        {
            await db.CatalogSources.ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, "Quarantined"));
            // Source facts and their dirty marker belong to the same committed mutation.
            await Romd.Infrastructure.Tests.Helpers.CatalogProjectionTestFactory.Create(db).MarkPlatformDirtyAsync(psx);
            await sourceChange.CommitAsync();
        }
        // Processing takes precedence until the requested rebuild completes.
        (await List(service)).Single(x => x.Id == psx).State.ShouldBe("Processing");
        (await Romd.Infrastructure.Tests.Helpers.CatalogProjectionTestFactory.Create(db).RebuildPlatformAsync(psx)).ShouldBeTrue();
        (await List(service)).Single(x => x.Id == psx).State.ShouldBe("NeedsAttention");
        (await List(service)).Where(x => x.Id == psx || x.Id == snes).ShouldAllBe(x => !x.Enabled);
    }

    [Fact]
    public async Task LayeredSources_NameOverlapIsNotReplacement_CheckFailureDoesNotHideUsableCatalog()
    {
        using var database = PostgreSqlTestDatabase.Create();
        await using var db = database.CreateContext();
        await SharedReferenceDataSeeder.InitializeAsync(db, Romd.PostgreSql.TestSupport.TestReferenceBlobs.For(db), clock: TimeProvider.System);
        int psx = await db.Platforms.Where(x => x.ShortName == "psx").Select(x => x.Id).SingleAsync();
        var file = new FileEntityPersistence { Sha256 = Romd.Domain.Hashing.Sha256.Parse(new string('1', 64)), Size = 1, SizeOnDisk = 1 };
        db.Files.Add(file);
        await db.SaveChangesAsync();
        var dat = Romd.Domain.Source.Dat.DatFile.CreateNew("Sony - PlayStation", "Catalog", Romd.Domain.Source.Dat.DatType.Redump, "catalog.dat", file.Id, now: DateTimeOffset.UnixEpoch, psx);
        await new DatRepository(db, TimeProvider.System).AddStagedAsync(dat, Romd.Domain.Source.Dat.DatSource.CreateNew(now: DateTimeOffset.UnixEpoch));
        db.DatSubscriptions.Add(new DatSubscriptionEntity { CatalogId = "redump/psx/discs", PlatformId = psx, State = "CheckFailed" });
        await db.SaveChangesAsync();
        (await List(Service(db))).Single(x => x.Id == psx).State.ShouldBe("Ready");
        var service = Service(db);
        using var workspace = new ImportWorkspace();
        var handler = Importer(db, workspace.Root);
        using var previewInput = Dat();
        var preview = (await service.PreviewAsync(previewInput, default)).Value;
        using var input = Dat();
        var accepted = (await handler.HandleAsync(new(psx, input, preview.Document.Preview.CandidateSha256))).Value;
        accepted.ShouldNotBe(Guid.Empty);
        (await db.Jobs.SingleAsync()).Id.ShouldBe(accepted);
        (await db.JobDispatches.SingleAsync()).JobId.ShouldBe(accepted);
        (await db.DatFiles.SingleAsync()).FileId.ShouldBe(file.Id); // existing source/version unchanged
        await db.Files.Where(x => x.Id == file.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.Sha256,
            Romd.Domain.Hashing.Sha256.Parse(preview.Document.Preview.CandidateSha256)));
        using var duplicate = Dat();
        (await handler.HandleAsync(new(psx, duplicate, preview.Document.Preview.CandidateSha256))).FirstError.Code.ShouldBe("SystemSetup.Unchanged");
    }

    [Fact]
    public async Task Preview_SuggestsAliasMatch_WithoutEnablingOrCreatingAJob()
    {
        using var database = PostgreSqlTestDatabase.Create();
        await using var db = database.CreateContext();
        await SharedReferenceDataSeeder.InitializeAsync(db, Romd.PostgreSql.TestSupport.TestReferenceBlobs.For(db), clock: TimeProvider.System);
        using var input = Dat("PS1");
        var result = await Service(db).PreviewAsync(input, default);
        result.IsError.ShouldBeFalse();
        result.Value.Document.Preview.CandidateEntries.ShouldBe(1);
        result.Value.SuggestedPlatformId.ShouldBe((await db.Platforms.SingleAsync(x => x.ShortName == "psx")).Id);
        (await db.Jobs.CountAsync()).ShouldBe(0);
        (await db.Platforms.AnyAsync(x => x.IsEnabled == true)).ShouldBeFalse();
    }

    [Fact]
    public async Task Preview_AmbiguousOrUnknownHeader_NeedsExplicitSelection()
    {
        using var database = PostgreSqlTestDatabase.Create();
        await using var db = database.CreateContext();
        await SharedReferenceDataSeeder.InitializeAsync(db, Romd.PostgreSql.TestSupport.TestReferenceBlobs.For(db), clock: TimeProvider.System);
        using var unknown = Dat("Unrecognized catalog");
        (await Service(db).PreviewAsync(unknown, default)).Value.SuggestedPlatformId.ShouldBeNull();
        await db.Platforms.Where(x => x.ShortName == "psx" || x.ShortName == "snes")
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Name, "Shared name"));
        using var ambiguous = Dat("Shared name");
        (await Service(db).PreviewAsync(ambiguous, default)).Value.SuggestedPlatformId.ShouldBeNull();
    }

    [Fact]
    public async Task Approval_RevalidatesExactDocument_BeforeEnablingOrAccepting()
    {
        using var database = PostgreSqlTestDatabase.Create();
        await using var db = database.CreateContext();
        await SharedReferenceDataSeeder.InitializeAsync(db, Romd.PostgreSql.TestSupport.TestReferenceBlobs.For(db), clock: TimeProvider.System);
        int id = await db.Platforms.Select(x => x.Id).FirstAsync();
        using var input = Dat();
        using var workspace = new ImportWorkspace();
        (await Importer(db, workspace.Root).HandleAsync(new(id, input, new string('0', 64)))).FirstError.Code.ShouldBe("SystemSetup.Stale");
        (await db.Platforms.SingleAsync(x => x.Id == id)).IsEnabled.ShouldBeNull();
        (await db.Jobs.CountAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task Approval_FailureAfterJobSave_RollsBackEnablementJobAndDispatch()
    {
        using var database = PostgreSqlTestDatabase.Create();
        await using var db = database.CreateContext();
        await SharedReferenceDataSeeder.InitializeAsync(db, Romd.PostgreSql.TestSupport.TestReferenceBlobs.For(db), clock: TimeProvider.System);
        int id = await db.Platforms.Select(x => x.Id).FirstAsync();
        using var workspace = new ImportWorkspace();
        var work = new ImportWork(new EfUnitOfWork(db))
        {
            AfterFlush = _ => Task.FromException(new IOException("Injected failure after save"))
        };
        var handler = Importer(db, workspace.Root, work);
        var service = Service(db);
        using var previewInput = Dat();
        var preview = (await service.PreviewAsync(previewInput, default)).Value;
        using var input = Dat();
        await Should.ThrowAsync<IOException>(() => handler.HandleAsync(new(id, input, preview.Document.Preview.CandidateSha256)));
        db.ChangeTracker.Clear();
        (await db.Platforms.SingleAsync(x => x.Id == id)).IsEnabled.ShouldBeNull();
        (await db.Jobs.CountAsync()).ShouldBe(0);
        (await db.JobDispatches.CountAsync()).ShouldBe(0);
    }
}
