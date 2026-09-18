using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Admin.Application.Source.Dat;
using Romd.Admin.Application.Source.Platform;
using Romd.Admin.Application.Storage.Files;
using Romd.Application.Common.Security;
using Romd.Dat.Parsing;
using Romd.Dat.Parsing.Formats.Logiqx;
using Romd.Domain.Jobs;
using Romd.Infrastructure.Dats;
using Romd.Persistence;
using Romd.Persistence.Entities;
using Romd.Persistence.ReferenceData;
using Romd.Persistence.Repositories;
using Romd.Persistence.Systems;
using Romd.PostgreSql.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Systems;

public sealed class SystemSetupTests
{
    [Fact]
    public async Task List_CountsTrackedTitlesIndependentlyOfLocalFilesAndOtherSystems()
    {
        using var database = PostgreSqlTestDatabase.Create();
        await using var db = database.CreateContext();
        await SharedReferenceDataSeeder.InitializeAsync(db, Romd.PostgreSql.TestSupport.TestReferenceBlobs.Instance);
        var platforms = await db.Platforms.OrderBy(p => p.Id).Take(2).ToListAsync();
        var tracked = new TitleEntity { PlatformId = platforms[0].Id, Name = "Tracked", NormalizedName = "tracked", EnrichmentStatus = "None" };
        var local = new TitleEntity { PlatformId = platforms[0].Id, Name = "Local", NormalizedName = "local", EnrichmentStatus = "None", HasLocalPayload = true };
        var other = new TitleEntity { PlatformId = platforms[1].Id, Name = "Other", NormalizedName = "other", EnrichmentStatus = "None" };
        db.Titles.AddRange(tracked, local, other);
        await db.SaveChangesAsync();
        db.TrackedTitles.AddRange(new TrackedTitleEntity { TitleId = tracked.Id }, new TrackedTitleEntity { TitleId = other.Id });
        await db.SaveChangesAsync();
        var rows = await Service(db).ListAsync(default);
        rows.Single(r => r.Id == platforms[0].Id).TrackedTitles.ShouldBe(1);
        rows.Single(r => r.Id == platforms[0].Id).OwnedTitles.ShouldBe(1);
        rows.Single(r => r.Id == platforms[1].Id).TrackedTitles.ShouldBe(1);
        rows.Single(r => r.Id == platforms[1].Id).OwnedTitles.ShouldBe(0);
    }

    private static SystemSetupService Service(RomdDbContext db, IUploadJobCreator? uploads = null)
    {
        var parser = new DatParser([new LogiqxDatFormat(NullLogger<LogiqxDatFormat>.Instance)]);
        return new(db, new DatReplacementReview(new DatRepository(db, TimeProvider.System), Substitute.For<IFileStorageService>(),
            parser, parser, Substitute.For<IReplaceDatJobCreator>()),
            new PlatformHeaderResolver(new PlatformRepository(db), new PlatformAliasRepository(db)),
            uploads ?? Substitute.For<IUploadJobCreator>(), Substitute.For<ICurrentUser>());
    }
    private static MemoryStream Dat(string name = "Sony - PlayStation") => new(Encoding.UTF8.GetBytes(
        $"<datafile><header><name>{name}</name><version>1</version></header><game name='Test'><rom name='test.bin' size='1' crc='11111111'/></game></datafile>"));

    [Fact]
    public async Task FreshRegistry_IsNotEnabled_ExplicitChoiceSurvivesSeedingAndLocalData()
    {
        using var database = PostgreSqlTestDatabase.Create(); await using var db = database.CreateContext();
        await SharedReferenceDataSeeder.InitializeAsync(db, Romd.PostgreSql.TestSupport.TestReferenceBlobs.Instance);
        var service = Service(db);
        var systems = await service.ListAsync(default);
        systems.Count.ShouldBe(56); systems.ShouldAllBe(x => !x.Enabled);
        var psx = systems.Single(x => x.SystemId == "psx"); psx.Aliases.ShouldContain("PS1");
        (await service.SetEnabledAsync(psx.Id, true, default)).IsError.ShouldBeFalse();
        (await service.ListAsync(default)).Single(x => x.Id == psx.Id).State.ShouldBe("NeedsCatalog");
        (await service.SetEnabledAsync(psx.Id, false, default)).IsError.ShouldBeFalse();
        db.DatSubscriptions.Add(new DatSubscriptionEntity { CatalogId = "redump/psx/discs", PlatformId = psx.Id, State = "ReadyToImport" });
        await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        await SharedReferenceDataSeeder.InitializeAsync(db, Romd.PostgreSql.TestSupport.TestReferenceBlobs.Instance);
        (await service.ListAsync(default)).Single(x => x.Id == psx.Id).Enabled.ShouldBeFalse();
        (await db.DatSubscriptions.CountAsync()).ShouldBe(1);
        (await service.SetEnabledAsync(psx.Id, true, default)).IsError.ShouldBeFalse();
        (await service.ListAsync(default)).Single(x => x.Id == psx.Id).Enabled.ShouldBeTrue();
    }

    [Fact]
    public async Task Migration_PreservesPreviouslyConfiguredSystems_WithoutEnablingRegistryOnlyRows()
    {
        using var database = PostgreSqlTestDatabase.Create(); await using var db = database.CreateContext();
        await SharedReferenceDataSeeder.InitializeAsync(db, Romd.PostgreSql.TestSupport.TestReferenceBlobs.Instance);
        // Production's historical seed rows carry the reserved system actor.
        await db.Platforms.ExecuteUpdateAsync(s => s.SetProperty(x => x.CreatedByUserId,
            Guid.Parse("00000000-0000-0000-0000-000000000001")));
        int custom = await db.Platforms.Where(x => x.ShortName == "snes").Select(x => x.Id).SingleAsync();
        await db.Platforms.Where(x => x.Id == custom).ExecuteUpdateAsync(s => s.SetProperty(x => x.CreatedByUserId, Guid.NewGuid()));
        int psx = await db.Platforms.Where(x => x.ShortName == "psx").Select(x => x.Id).SingleAsync();
        db.DatSubscriptions.Add(new DatSubscriptionEntity { CatalogId = "redump/psx/discs", PlatformId = psx, State = "ReadyToImport" });
        await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        await db.GetService<IMigrator>().MigrateAsync("20260906162538_DatCatalogEnrollment");
        await db.Database.MigrateAsync();
        var rows = await Service(db).ListAsync(default);
        rows.Single(x => x.Id == psx).Enabled.ShouldBeTrue();
        rows.Count(x => x.Enabled).ShouldBe(2);
        rows.Single(x => x.Id == custom).Enabled.ShouldBeTrue();
        (await db.Platforms.SingleAsync(x => x.Id == psx)).IsEnabled.ShouldBe(true);
    }

    [Fact]
    public async Task Migration_ExistingDatAndOwnedTitleRemainEnabled_DisableRetainsBoth()
    {
        using var database = PostgreSqlTestDatabase.Create(); await using var db = database.CreateContext();
        await SharedReferenceDataSeeder.InitializeAsync(db, Romd.PostgreSql.TestSupport.TestReferenceBlobs.Instance);
        int psx = await db.Platforms.Where(x => x.ShortName == "psx").Select(x => x.Id).SingleAsync();
        int snes = await db.Platforms.Where(x => x.ShortName == "snes").Select(x => x.Id).SingleAsync();
        var file = new FileEntityPersistence { Sha256 = Romd.Domain.Hashing.Sha256.FromSpan(new byte[32]), Size = 1, SizeOnDisk = 1 };
        db.Files.Add(file); await db.SaveChangesAsync();
        var dat = Romd.Domain.Source.Dat.DatFile.CreateNew("Catalog", "Catalog", Romd.Domain.Source.Dat.DatType.Redump, "catalog.dat", file.Id, psx);
        await new DatRepository(db, TimeProvider.System).AddStagedAsync(dat, Romd.Domain.Source.Dat.DatSource.CreateNew());
        db.Titles.Add(new TitleEntity { PlatformId = snes, Name = "Owned", NormalizedName = "owned", EnrichmentStatus = "None", HasLocalPayload = true });
        await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        await db.GetService<IMigrator>().MigrateAsync("20260906162538_DatCatalogEnrollment");
        await db.Database.MigrateAsync();
        var service = Service(db); var rows = await service.ListAsync(default);
        rows.Single(x => x.Id == psx).Enabled.ShouldBeTrue(); rows.Single(x => x.Id == snes).Enabled.ShouldBeTrue();
        rows.Single(x => x.Id == snes).OwnedTitles.ShouldBe(1);
        (await service.SetEnabledAsync(psx, false, default)).IsError.ShouldBeFalse();
        (await service.SetEnabledAsync(snes, false, default)).IsError.ShouldBeFalse();
        (await db.DatFiles.CountAsync()).ShouldBe(1); (await db.Titles.CountAsync()).ShouldBe(1);
        await db.CatalogSources.ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, "Quarantined"));
        (await service.ListAsync(default)).Single(x => x.Id == psx).State.ShouldBe("NeedsAttention");
        (await service.ListAsync(default)).Where(x => x.Id == psx || x.Id == snes).ShouldAllBe(x => !x.Enabled);
    }

    [Fact]
    public async Task LayeredSources_NameOverlapIsNotReplacement_CheckFailureDoesNotHideUsableCatalog()
    {
        using var database = PostgreSqlTestDatabase.Create(); await using var db = database.CreateContext();
        await SharedReferenceDataSeeder.InitializeAsync(db, Romd.PostgreSql.TestSupport.TestReferenceBlobs.Instance);
        int psx = await db.Platforms.Where(x => x.ShortName == "psx").Select(x => x.Id).SingleAsync();
        var file = new FileEntityPersistence { Sha256 = Romd.Domain.Hashing.Sha256.Parse(new string('1', 64)), Size = 1, SizeOnDisk = 1 };
        db.Files.Add(file); await db.SaveChangesAsync();
        var dat = Romd.Domain.Source.Dat.DatFile.CreateNew("Sony - PlayStation", "Catalog", Romd.Domain.Source.Dat.DatType.Redump, "catalog.dat", file.Id, psx);
        await new DatRepository(db, TimeProvider.System).AddStagedAsync(dat, Romd.Domain.Source.Dat.DatSource.CreateNew());
        db.DatSubscriptions.Add(new DatSubscriptionEntity { CatalogId = "redump/psx/discs", PlatformId = psx, State = "CheckFailed" });
        await db.SaveChangesAsync();
        (await Service(db).ListAsync(default)).Single(x => x.Id == psx).State.ShouldBe("Ready");
        var uploads = Substitute.For<IUploadJobCreator>();
        var accepted = new UploadJobCreationResult(Guid.NewGuid(), "stable", "/jobs/stable");
        uploads.CreateAsync(Arg.Any<Stream>(), Arg.Any<string>(), Arg.Any<UploadJobOptions>(), Arg.Any<CancellationToken>()).Returns(accepted);
        var service = Service(db, uploads);
        using var previewInput = Dat();
        var preview = (await service.PreviewAsync(previewInput, default)).Value;
        using var input = Dat();
        (await service.ImportAsync(psx, input, preview.Document.Preview.CandidateSha256, default)).Value.ShouldBe(accepted);
        (await db.DatFiles.SingleAsync()).FileId.ShouldBe(file.Id); // existing source/version unchanged
        await db.Files.Where(x => x.Id == file.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.Sha256,
            Romd.Domain.Hashing.Sha256.Parse(preview.Document.Preview.CandidateSha256)));
        using var duplicate = Dat();
        (await service.ImportAsync(psx, duplicate, preview.Document.Preview.CandidateSha256, default)).FirstError.Code.ShouldBe("SystemSetup.Unchanged");
    }

    [Fact]
    public async Task Preview_SuggestsAliasMatch_WithoutEnablingOrCreatingAJob()
    {
        using var database = PostgreSqlTestDatabase.Create(); await using var db = database.CreateContext();
        await SharedReferenceDataSeeder.InitializeAsync(db, Romd.PostgreSql.TestSupport.TestReferenceBlobs.Instance);
        using var input = Dat("PS1");
        var result = await Service(db).PreviewAsync(input, default);
        result.IsError.ShouldBeFalse(); result.Value.Document.Preview.CandidateEntries.ShouldBe(1);
        result.Value.SuggestedPlatformId.ShouldBe((await db.Platforms.SingleAsync(x => x.ShortName == "psx")).Id);
        (await db.Jobs.CountAsync()).ShouldBe(0); (await db.Platforms.AnyAsync(x => x.IsEnabled == true)).ShouldBeFalse();
    }

    [Fact]
    public async Task Preview_AmbiguousOrUnknownHeader_NeedsExplicitSelection()
    {
        using var database = PostgreSqlTestDatabase.Create(); await using var db = database.CreateContext();
        await SharedReferenceDataSeeder.InitializeAsync(db, Romd.PostgreSql.TestSupport.TestReferenceBlobs.Instance);
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
        using var database = PostgreSqlTestDatabase.Create(); await using var db = database.CreateContext();
        await SharedReferenceDataSeeder.InitializeAsync(db, Romd.PostgreSql.TestSupport.TestReferenceBlobs.Instance);
        int id = await db.Platforms.Select(x => x.Id).FirstAsync();
        using var input = Dat();
        (await Service(db).ImportAsync(id, input, new string('0',64), default)).FirstError.Code.ShouldBe("SystemSetup.Stale");
        (await db.Platforms.SingleAsync(x => x.Id == id)).IsEnabled.ShouldBeNull();
        (await db.Jobs.CountAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task Approval_FailureAfterJobSave_RollsBackEnablementJobAndDispatch()
    {
        using var database = PostgreSqlTestDatabase.Create(); await using var db = database.CreateContext();
        await SharedReferenceDataSeeder.InitializeAsync(db, Romd.PostgreSql.TestSupport.TestReferenceBlobs.Instance);
        int id = await db.Platforms.Select(x => x.Id).FirstAsync();
        var uploads = Substitute.For<IUploadJobCreator>();
        uploads.CreateAsync(Arg.Any<Stream>(), Arg.Any<string>(), Arg.Any<UploadJobOptions>(), Arg.Any<CancellationToken>())
            .Returns< Task<UploadJobCreationResult>>(async _ =>
            {
                await new UploadJobRepository(db, TimeProvider.System).AddAsync(UploadJob.Create("reviewed.dat", id));
                throw new IOException("Injected failure after save");
            });
        var service = Service(db, uploads);
        using var previewInput = Dat();
        var preview = (await service.PreviewAsync(previewInput, default)).Value;
        using var input = Dat();
        await Should.ThrowAsync<IOException>(() => service.ImportAsync(id, input, preview.Document.Preview.CandidateSha256, default));
        db.ChangeTracker.Clear();
        (await db.Platforms.SingleAsync(x => x.Id == id)).IsEnabled.ShouldBeNull();
        (await db.Jobs.CountAsync()).ShouldBe(0); (await db.JobDispatches.CountAsync()).ShouldBe(0);
    }
}
