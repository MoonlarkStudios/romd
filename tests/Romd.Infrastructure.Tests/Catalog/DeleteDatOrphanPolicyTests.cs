using ErrorOr;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Catalog.Sources;
using Romd.Persistence.Catalog;
using Romd.Admin.Application.Common.Realtime;
using Romd.Admin.Application.Libraries;
using Romd.Admin.Application.Source.Dat;
using Romd.Admin.Application.Source.Dat.Commands.DeleteDat;
using Romd.Admin.Application.Titles;
using Romd.Domain.Catalog;
using Romd.Domain.Hashing;
using Romd.Domain.Source.Dat;
using Romd.Persistence;
using Romd.Persistence.Entities;
using Romd.Persistence.Repositories;
using Romd.Infrastructure.Realtime;
using Romd.PostgreSql.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Catalog;

/// <summary>
///     Real-PostgreSQL proofs for the transactional orphan policy on DAT deletion (#152 Phase 4):
///     when deleting a source's last version removes a title's last link, the title is
///     hard-deleted — or retained as UserOnly when user state exists — inside the same
///     commit as the deletion; titles still backed elsewhere and entries still referenced
///     by a remaining version are untouched. The handler is composed through the production
///     DI wiring so these tests pin the composed behavior, not a hand-built constructor.
/// </summary>
public sealed class DeleteDatOrphanPolicyTests : IDisposable
{
    private const int PlatformId = 10;
    private const int TitleId = 201;

    private readonly PostgreSqlTestDatabase _connection;
    private readonly DbContextOptions<RomdDbContext> _contextOptions;

    public DeleteDatOrphanPolicyTests()
    {
        _connection = PostgreSqlTestDatabase.Create();

        _contextOptions = new DbContextOptionsBuilder<RomdDbContext>()
            .UseNpgsql(_connection.ConnectionString)
            .Options;

        using var db = CreateDb();
        db.Platforms.Add(new PlatformEntity
        {
            Id = PlatformId,
            Name = "Sony PlayStation",
            Ownership = Romd.Domain.ReferenceData.ReferenceOwnership.Romd, BuiltInVersion = 1, BaseName = "Sony PlayStation", BaseCompactLabel = "Sony PlayStation", CanonicalKey = "psx", ShortName = "psx",
            CreatedAt = DateTimeOffset.UtcNow
        });
        db.SaveChanges();
        db.ChangeTracker.Clear();
    }

    public void Dispose() => _connection.Dispose();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HandleAsync_LastVersionDeleted_HardDeletesUntrackedOrphanTitleInSameCommit(bool failCommit)
    {
        await using var db = CreateDb();
        SeedSourceWithVersion(db, catalogSourceId: 1, datSourceId: 1, datId: 7, fileId: 17);
        SeedEntryWithGame(db, entryId: 100, catalogSourceId: 1, gameId: 70, datId: 7, key: "Game X");
        SeedLinkedTitle(db, TitleId, entryId: 100);
        db.DatSubscriptions.Add(new DatSubscriptionEntity { DatSourceId = 1, CandidateFileId = 17 });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var result = await ExecuteDeleteAsync(db, datId: 7, failCommit);

        result.IsError.ShouldBe(failCommit);
        await using var read = CreateDb();
        (await read.DatSubscriptions.AnyAsync(s => s.DatSourceId == 1)).ShouldBe(failCommit);
        (await read.DatFiles.AnyAsync(d => d.Id == 7)).ShouldBe(failCommit);
        (await read.CatalogSources.AnyAsync(s => s.Id == 1)).ShouldBe(failCommit);
        (await read.SourceEntries.AnyAsync(e => e.Id == 100)).ShouldBe(failCommit);
        (await read.TitleSourceLinks.AnyAsync(l => l.TitleId == TitleId)).ShouldBe(failCommit);
        // The orphan policy runs inside the deletion transaction: on success the title that
        // lost its last link is hard-deleted; on commit failure it survives with everything
        // else — the policy can never outrun or outlive the deletion it reacts to.
        (await read.Titles.AnyAsync(t => t.Id == TitleId)).ShouldBe(failCommit);
        if (failCommit)
        {
            (await read.Titles.SingleAsync(t => t.Id == TitleId))
                .CatalogState.ShouldBe(TitleCatalogState.Active);
        }
    }

    [Fact]
    public async Task HandleAsync_LastVersionDeleted_HardDeleteAlsoRemovesStaleProjectedRelease()
    {
        await using var db = CreateDb();
        SeedSourceWithVersion(db, catalogSourceId: 1, datSourceId: 1, datId: 7, fileId: 17);
        SeedEntryWithGame(db, entryId: 100, catalogSourceId: 1, gameId: 70, datId: 7, key: "Game X");
        SeedLinkedTitle(db, TitleId, entryId: 100);
        db.CatalogReleases.Add(new CatalogReleaseEntity
        {
            Id = 900,
            PlatformId = PlatformId,
            CatalogTitleId = TitleId,
            Fingerprint = "sha1:900",
            Name = "Release 900",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var result = await ExecuteDeleteAsync(db, datId: 7);

        // CatalogReleases.CatalogTitleId is a Restrict FK, so the orphan hard-delete must
        // remove the title's stale projection rows itself — otherwise the FK violation
        // rolls back the whole DAT deletion.
        result.IsError.ShouldBeFalse();
        await using var read = CreateDb();
        (await read.DatFiles.AnyAsync(d => d.Id == 7)).ShouldBeFalse();
        (await read.Titles.AnyAsync(t => t.Id == TitleId)).ShouldBeFalse();
        (await read.CatalogReleases.AnyAsync(r => r.CatalogTitleId == TitleId)).ShouldBeFalse();
    }

    [Fact]
    public async Task HandleAsync_SharedReleaseAssertedByAnotherSource_IsRepointedNotDeleted()
    {
        await using var db = CreateDb();
        SeedSourceWithVersion(db, catalogSourceId: 1, datSourceId: 1, datId: 7, fileId: 17);
        SeedEntryWithGame(db, entryId: 100, catalogSourceId: 1, gameId: 70, datId: 7, key: "Game X");
        SeedSourceWithVersion(db, catalogSourceId: 2, datSourceId: 2, datId: 8, fileId: 18);
        SeedEntryWithGame(db, entryId: 200, catalogSourceId: 2, gameId: 80, datId: 8, key: "Game X");
        // Both sources asserted the same fingerprint with conflicting titles; the
        // projection pointed the shared release at source 1's title.
        SeedLinkedTitle(db, TitleId, entryId: 100);
        SeedLinkedTitle(db, 202, entryId: 200);
        db.CatalogReleases.Add(new CatalogReleaseEntity
        {
            Id = 900,
            PlatformId = PlatformId,
            CatalogTitleId = TitleId,
            Fingerprint = "sha1:900",
            Name = "Release 900",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });
        db.CatalogReleaseSources.AddRange(
            new CatalogReleaseSourceEntity
            {
                Id = 70, CatalogReleaseId = 900, SourceEntryId = 100, ProviderClaimKey = "700", AssertedTitleId = TitleId
            },
            new CatalogReleaseSourceEntity
            {
                Id = 71, CatalogReleaseId = 900, SourceEntryId = 200, ProviderClaimKey = "800", AssertedTitleId = 202
            });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var result = await ExecuteDeleteAsync(db, datId: 7);

        result.IsError.ShouldBeFalse();
        await using var read = CreateDb();
        (await read.Titles.AnyAsync(t => t.Id == TitleId)).ShouldBeFalse();
        // The release is public identity shared across sources: deleting source 1's last
        // version re-points it at the surviving title instead of deleting it.
        var release = await read.CatalogReleases.SingleAsync(r => r.Id == 900);
        release.CatalogTitleId.ShouldBe(202);
        (await read.Titles.SingleAsync(t => t.Id == 202))
            .CatalogState.ShouldBe(TitleCatalogState.Active);
        // Source 1's entry cascade removed its provenance row; the survivor's row remains.
        var remaining = (await read.CatalogReleaseSources
                .Where(s => s.CatalogReleaseId == 900).ToListAsync())
            .ShouldHaveSingleItem();
        remaining.SourceEntryId.ShouldBe(200);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HandleAsync_OwnedUntrackedTitle_KeepsIdentityAndStoredRom(bool disabled)
    {
        await using var db = CreateDb();
        SeedSourceWithVersion(db, 1, 1, 7, 17);
        SeedEntryWithGame(db, 100, 1, 70, 7, "Owned Game");
        SeedLinkedTitle(db, TitleId, 100);
        var romId = 90;
        db.RomFiles.Add(new RomFileEntity { Id = romId, FileId = SeedFile(db, 91), OriginalFilename = "owned.rom",
            Sha1 = Sha1.FromSpan(new byte[Sha1.ByteLength]), Md5 = Md5.FromSpan(new byte[Md5.ByteLength]), Crc32 = Crc32.FromUInt32(1) });
        db.DatRoms.Add(new DatRomEntity { Id = 80, DatGameId = 70, Name = "owned.rom", Size = 1, RomFileId = romId });
        if (disabled) db.CatalogSources.Local.Single().Status = nameof(CatalogSourceStatus.Disabled);
        await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        (await ExecuteDeleteAsync(db, 7)).IsError.ShouldBeFalse();
        await using var read = CreateDb();
        var retained = await read.Titles.SingleOrDefaultAsync(t => t.Id == TitleId);
        retained.ShouldNotBeNull(); retained.CatalogState.ShouldBe(TitleCatalogState.UserOnly);
        (await read.TrackedTitles.AnyAsync(t => t.TitleId == TitleId)).ShouldBeFalse();
        (await read.RomFiles.AnyAsync(r => r.Id == romId && r.FileId == 91)).ShouldBeTrue();
        (await read.Files.AnyAsync(f => f.Id == 91)).ShouldBeTrue();
    }

    [Fact]
    public async Task HandleAsync_LastVersionDeleted_TrackedOrphanTitleIsRetainedAsUserOnly()
    {
        await using var db = CreateDb();
        SeedSourceWithVersion(db, catalogSourceId: 1, datSourceId: 1, datId: 7, fileId: 17);
        SeedEntryWithGame(db, entryId: 100, catalogSourceId: 1, gameId: 70, datId: 7, key: "Game X");
        SeedLinkedTitle(db, TitleId, entryId: 100);
        db.TrackedTitles.Add(new TrackedTitleEntity
        {
            TitleId = TitleId,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var result = await ExecuteDeleteAsync(db, datId: 7);

        result.IsError.ShouldBeFalse();
        await using var read = CreateDb();
        (await read.TitleSourceLinks.AnyAsync(l => l.TitleId == TitleId)).ShouldBeFalse();
        var title = await read.Titles.SingleAsync(t => t.Id == TitleId);
        title.CatalogState.ShouldBe(TitleCatalogState.UserOnly);
        (await read.TrackedTitles.AnyAsync(t => t.TitleId == TitleId)).ShouldBeTrue();
    }

    [Fact]
    public async Task HandleAsync_TitleBackedByAnotherSourcesEntry_IsLeftUntouched()
    {
        await using var db = CreateDb();
        SeedSourceWithVersion(db, catalogSourceId: 1, datSourceId: 1, datId: 7, fileId: 17);
        SeedEntryWithGame(db, entryId: 100, catalogSourceId: 1, gameId: 70, datId: 7, key: "Game X");
        SeedSourceWithVersion(db, catalogSourceId: 2, datSourceId: 2, datId: 8, fileId: 18);
        SeedEntryWithGame(db, entryId: 200, catalogSourceId: 2, gameId: 80, datId: 8, key: "Game X");
        SeedLinkedTitle(db, TitleId, entryId: 100);
        db.TitleSourceLinks.Add(new TitleSourceLinkEntity { SourceEntryId = 200, TitleId = TitleId });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var result = await ExecuteDeleteAsync(db, datId: 7);

        result.IsError.ShouldBeFalse();
        await using var read = CreateDb();
        // The deleted source's claim is gone, but the other source's backing keeps the
        // title out of the orphan policy entirely.
        (await read.SourceEntries.AnyAsync(e => e.Id == 100)).ShouldBeFalse();
        (await read.SourceEntries.AnyAsync(e => e.Id == 200)).ShouldBeTrue();
        var link = (await read.TitleSourceLinks.Where(l => l.TitleId == TitleId).ToListAsync())
            .ShouldHaveSingleItem();
        link.SourceEntryId.ShouldBe(200);
        (await read.Titles.SingleAsync(t => t.Id == TitleId))
            .CatalogState.ShouldBe(TitleCatalogState.Active);
    }

    [Fact]
    public async Task HandleAsync_AnotherVersionStillReferencesEntry_DoesNotOrphanTitle()
    {
        await using var db = CreateDb();
        SeedSourceWithVersion(db, catalogSourceId: 1, datSourceId: 1, datId: 7, fileId: 17);
        SeedVersion(db, datId: 8, datSourceId: 1, fileId: 18, DatFileLifecycle.Superseded);
        SeedEntryWithGame(db, entryId: 100, catalogSourceId: 1, gameId: 70, datId: 7, key: "Game X");
        db.DatGames.Add(NewGame(gameId: 80, datId: 8, entryId: 100));
        SeedLinkedTitle(db, TitleId, entryId: 100);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var result = await ExecuteDeleteAsync(db, datId: 7);

        result.IsError.ShouldBeFalse();
        await using var read = CreateDb();
        // The superseded version's game still asserts the entry, so nothing is pruned,
        // no link is lost, and the title never becomes an orphan candidate.
        (await read.DatFiles.AnyAsync(d => d.Id == 7)).ShouldBeFalse();
        (await read.DatFiles.AnyAsync(d => d.Id == 8)).ShouldBeTrue();
        (await read.DatSources.AnyAsync(s => s.Id == 1)).ShouldBeTrue();
        (await read.SourceEntries.AnyAsync(e => e.Id == 100)).ShouldBeTrue();
        (await read.TitleSourceLinks.AnyAsync(l => l.TitleId == TitleId)).ShouldBeTrue();
        (await read.Titles.SingleAsync(t => t.Id == TitleId))
            .CatalogState.ShouldBe(TitleCatalogState.Active);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SourceDelete_ReviewedLayeredImpact_RemovesAllVersionsAndRetainsIdentitiesAtomically(bool failCommit)
    {
        await using var db = CreateDb();
        SeedSourceWithVersion(db, 1, 1, 7, 17);
        SeedSourceWithVersion(db, 2, 2, 8, 18);
        SeedVersion(db, 9, 1, 19, DatFileLifecycle.Superseded);
        for (var i = 0; i < 4; i++)
        {
            SeedEntryWithGame(db, 100 + i, 1, 70 + i, 7, $"Game {i}");
            SeedLinkedTitle(db, 201 + i, 100 + i);
        }
        SeedEntryWithGame(db, 200, 2, 80, 8, "Shared");
        db.TitleSourceLinks.Add(new TitleSourceLinkEntity { SourceEntryId = 200, TitleId = 202 });
        db.DatGames.Add(NewGame(90, 9, 100));
        db.Titles.Local.Single(t => t.Id == 204).FieldSourceOverridesJson = "{\"Name\":\"manual\"}";
        db.RomFiles.Add(new RomFileEntity { Id = 90, FileId = SeedFile(db, 91), OriginalFilename = "owned.rom",
            Sha1 = Sha1.FromSpan(new byte[Sha1.ByteLength]), Md5 = Md5.FromSpan(new byte[Md5.ByteLength]), Crc32 = Crc32.FromUInt32(1) });
        db.DatRoms.Add(new DatRomEntity { Id = 80, DatGameId = 70, Name = "owned.rom", Size = 1, RomFileId = 90 });
        db.DatSubscriptions.Add(new DatSubscriptionEntity { DatSourceId = 1, CandidateFileId = 17 });
        await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        await using var provider = BuildHandlerProvider(db, failCommit);
        var service = ActivatorUtilities.CreateInstance<DatSourceManagement>(provider);
        var preview = (await service.PreviewAsync(7, "Delete", default)).Value;
        preview.Versions.ShouldBe(2); preview.StillCovered.ShouldBe(1);
        preview.OwnedWithoutDefinition.ShouldBe(1); preview.PersonalWithoutDefinition.ShouldBe(1);
        preview.CatalogOnlyWithoutDefinition.ShouldBe(1); preview.Busy.ShouldBeFalse();
        if (failCommit)
            await Should.ThrowAsync<InvalidOperationException>(() => service.ApplyAsync(7, "Delete", preview.ReviewToken, default));
        else (await service.ApplyAsync(7, "Delete", preview.ReviewToken, default)).IsError.ShouldBeFalse();
        await using var read = CreateDb();
        (await read.DatFiles.CountAsync(d => d.DatSourceId == 1)).ShouldBe(failCommit ? 2 : 0);
        (await read.DatSubscriptions.AnyAsync(d => d.DatSourceId == 1)).ShouldBe(failCommit);
        (await read.Titles.AnyAsync(t => t.Id == 203)).ShouldBe(failCommit);
        foreach (var id in new[] { 201, 202, 204 }) (await read.Titles.AnyAsync(t => t.Id == id)).ShouldBeTrue();
        (await read.Titles.SingleAsync(t => t.Id == 201)).RetainWithoutCatalog.ShouldBe(!failCommit);
        (await read.RomFiles.AnyAsync(r => r.Id == 90)).ShouldBeTrue();
        (await read.Files.AnyAsync(f => f.Id == 91)).ShouldBeTrue();
        (await read.Titles.SingleAsync(t => t.Id == 204)).FieldSourceOverridesJson.ShouldBe("{\"Name\":\"manual\"}");
    }

    [Fact]
    public async Task SourceLifecycle_ChangedImpactRequiresReview_DisableAndRestoreKeepSourceIdentity()
    {
        await using var db = CreateDb();
        SeedSourceWithVersion(db, 1, 1, 7, 17);
        SeedEntryWithGame(db, 100, 1, 70, 7, "Game"); SeedLinkedTitle(db, 201, 100);
        await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        await using var provider = BuildHandlerProvider(db, false);
        var service = ActivatorUtilities.CreateInstance<DatSourceManagement>(provider);
        var stale = (await service.PreviewAsync(7, "Disabled", default)).Value;
        await db.Titles.Where(t => t.Id == 201).ExecuteUpdateAsync(u => u.SetProperty(t => t.FieldSourceOverridesJson, "{\"Name\":\"manual\"}"));
        (await service.ApplyAsync(7, "Disabled", stale.ReviewToken, default)).FirstError.Code.ShouldBe("SourceLifecycle.Stale");
        var current = (await service.PreviewAsync(7, "Disabled", default)).Value;
        (await service.ApplyAsync(7, "Disabled", current.ReviewToken, default)).IsError.ShouldBeFalse();
        (await db.CatalogSources.SingleAsync()).Status.ShouldBe("Disabled");
        (await db.TitleSourceLinks.SingleAsync()).TitleId.ShouldBe(201);
        var restore = (await service.PreviewAsync(7, "Active", default)).Value;
        (await service.ApplyAsync(7, "Active", restore.ReviewToken, default)).IsError.ShouldBeFalse();
        (await db.CatalogSources.SingleAsync()).Status.ShouldBe("Active");
        (await db.DatFiles.SingleAsync()).Id.ShouldBe(7);
    }

    [Fact]
    public async Task SourceEntries_SearchAndCursor_ResolveTitleAndCoverageWithoutLoadingWholeDat()
    {
        await using var db = CreateDb();
        SeedSourceWithVersion(db, 1, 1, 7, 17);
        for (var i = 0; i < 3; i++)
        {
            SeedEntryWithGame(db, 100 + i, 1, 70 + i, 7, $"Game {i}");
            if (i < 2) SeedLinkedTitle(db, 201 + i, 100 + i);
        }
        await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        var reader = new SourceEntryReader(db);
        var first = (await reader.ReadAsync(7, null, 1, null, null, default)).Value;
        first.Items.Count.ShouldBe(1); first.NextCursor.ShouldBe(70);
        first.Items[0].EntryId.ShouldBe(100); first.Items[0].TitleId.ShouldBe(201); first.Items[0].ActiveSources.ShouldBe(1);
        var next = (await reader.ReadAsync(7, first.NextCursor, 2, null, null, default)).Value;
        next.Items.Select(e => e.GameId).ShouldBe(new[] { 71, 72 }); next.NextCursor.ShouldBeNull();
        next.Items[1].TitleId.ShouldBeNull();
        var filtered = (await reader.ReadAsync(7, null, 50, "TITLE 202", 202, default)).Value;
        filtered.Items.Single().EntryId.ShouldBe(101);
        var reference = (await new Romd.Infrastructure.Catalog.TitleSourceReferenceReader(db).GetReferencesAsync(201)).Single();
        reference.DatId.ShouldBe(7); reference.PlatformId.ShouldBe(PlatformId); reference.HasActiveDefinition.ShouldBeTrue();
    }

    [Fact]
    public async Task SourceDelete_QueuedReplacement_RequiresJobToFinish()
    {
        await using var db = CreateDb();
        SeedSourceWithVersion(db, 1, 1, 7, 17);
        await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        var job = Romd.Domain.Jobs.ReplaceDatJob.Create(7, "candidate.dat", PlatformId);
        await new ReplaceDatJobRepository(db, TimeProvider.System).AddAsync(job);
        await using var provider = BuildHandlerProvider(db, false);
        var service = ActivatorUtilities.CreateInstance<DatSourceManagement>(provider);
        var preview = (await service.PreviewAsync(7, "Delete", default)).Value;
        preview.Busy.ShouldBeTrue();
        (await service.ApplyAsync(7, "Delete", preview.ReviewToken, default)).FirstError.Code.ShouldBe("SourceLifecycle.Busy");
        (await db.DatFiles.CountAsync()).ShouldBe(1);
    }

    [Theory]
    [InlineData("Delete", false)]
    [InlineData("Disabled", false)]
    [InlineData("Delete", true)]
    [InlineData("Disabled", true)]
    public async Task SourceLifecycle_JustImportedSubscription_IsRemovedOrBoundBeforeChecksResume(string action, bool running)
    {
        await using var db = CreateDb();
        SeedSourceWithVersion(db, 1, 1, 7, 17);
        // Enrollment resolves completed uploads by the exact document until its next check.
        await db.SaveChangesAsync();
        var job = Romd.Domain.Jobs.UploadJob.Create("subscribed.dat", PlatformId);
        await new UploadJobRepository(db, TimeProvider.System).AddAsync(job);
        if (!running) await db.Jobs.Where(j => j.Id == job.Id).ExecuteUpdateAsync(u => u.SetProperty(j => j.Phase, "Completed"));
        db.DatSubscriptions.Add(new DatSubscriptionEntity { PlatformId = PlatformId, CandidateFileId = 17,
            JobId = job.Id, State = "Applying" });
        await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        await using var provider = BuildHandlerProvider(db, false);
        var service = ActivatorUtilities.CreateInstance<DatSourceManagement>(provider);
        var preview = (await service.PreviewAsync(7, action, default)).Value;
        preview.Busy.ShouldBe(running);
        if (running)
        {
            (await service.ApplyAsync(7, action, preview.ReviewToken, default)).FirstError.Code.ShouldBe("SourceLifecycle.Busy");
            (await db.DatFiles.CountAsync()).ShouldBe(1);
            return;
        }
        (await service.ApplyAsync(7, action, preview.ReviewToken, default)).IsError.ShouldBeFalse();
        await using var read = CreateDb();
        if (action == "Delete") (await read.DatSubscriptions.AnyAsync()).ShouldBeFalse();
        else
        {
            (await read.DatSubscriptions.SingleAsync()).DatSourceId.ShouldBe(1);
            (await read.CatalogSources.SingleAsync()).Status.ShouldBe("Disabled");
        }
    }

    [Fact]
    public async Task SourceDelete_ConcurrentPersonalEdit_CompletesBeforeRevalidationAndRequiresNewReview()
    {
        await using var db = CreateDb();
        SeedSourceWithVersion(db, 1, 1, 7, 17);
        SeedEntryWithGame(db, 100, 1, 70, 7, "Game"); SeedLinkedTitle(db, 201, 100);
        await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        await db.Database.OpenConnectionAsync();
        var pid = ((Npgsql.NpgsqlConnection)db.Database.GetDbConnection()).ProcessID;
        await using var provider = BuildHandlerProvider(db, false);
        var service = ActivatorUtilities.CreateInstance<DatSourceManagement>(provider);
        var preview = (await service.PreviewAsync(7, "Delete", default)).Value;
        await using var editor = CreateDb();
        await using var edit = await editor.Database.BeginTransactionAsync();
        await editor.Titles.Where(t => t.Id == 201).ExecuteUpdateAsync(u => u.SetProperty(t => t.FieldSourceOverridesJson, "{\"Name\":\"manual\"}"));
        var removal = service.ApplyAsync(7, "Delete", preview.ReviewToken, default);
        await using var observer = CreateDb();
        var blocked = false;
        for (var attempt = 0; attempt < 100; attempt++)
        {
            blocked = await observer.Database.SqlQuery<bool>($"SELECT EXISTS (SELECT 1 FROM pg_stat_activity WHERE pid = {pid} AND wait_event_type = 'Lock') AS \"Value\"").SingleAsync();
            if (blocked) break;
            await Task.Delay(20);
        }
        // Release the lock even on assertion failure so the request can finish cleanly.
        await edit.CommitAsync();
        var result = await removal.WaitAsync(TimeSpan.FromSeconds(10));
        blocked.ShouldBeTrue();
        result.FirstError.Code.ShouldBe("SourceLifecycle.Stale");
        (await observer.Titles.SingleAsync(t => t.Id == 201)).FieldSourceOverridesJson.ShouldBe("{\"Name\":\"manual\"}");
        (await observer.DatFiles.CountAsync()).ShouldBe(1);
    }

    private RomdDbContext CreateDb() => new(_contextOptions);

    private static async Task<ErrorOr<Deleted>> ExecuteDeleteAsync(
        RomdDbContext db,
        int datId,
        bool failCommit = false)
    {
        await using var provider = BuildHandlerProvider(db, failCommit);
        var handler = ActivatorUtilities.CreateInstance<DeleteDatCommandHandler>(provider);
        return await handler.HandleAsync(DeleteDatCommand.Create(datId).Value);
    }

    /// <summary>
    ///     Composes the handler's dependencies from the production admin curation
    ///     registration plus real repositories over the test database, so the handler's
    ///     constructor shape is owned by DI rather than restated here.
    /// </summary>
    private static ServiceProvider BuildHandlerProvider(RomdDbContext context, bool failCommit)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(context);
        services.AddSingleton<IDatRepository>(new DatRepository(context, TimeProvider.System));
        services.AddSingleton<ITitleRepository>(new TitleRepository(context));
        services.AddSingleton<ILibraryRepository>(new LibraryRepository(context));
        services.AddSingleton<IAdminEventOutbox>(new AdminRealtimeOutbox(context, TimeProvider.System));
        services.AddSingleton<IUnitOfWork>(failCommit
            ? new FailOnCommitUnitOfWork(context)
            : new EfUnitOfWork(context));
        services.AddRomdAdminCurationServices();
        return services.BuildServiceProvider();
    }

    private static void SeedSourceWithVersion(
        RomdDbContext db,
        int catalogSourceId,
        int datSourceId,
        int datId,
        int fileId)
    {
        db.DatFiles.Add(new DatFileEntity
        {
            Source = new DatSourceEntity
            {
                Id = datSourceId,
                CatalogSource = new CatalogSourceEntity
                {
                    Id = catalogSourceId,
                    Kind = "Dat",
                    Status = nameof(CatalogSourceStatus.Active)
                }
            },
            Id = datId,
            Name = $"DAT {datId}",
            Description = $"DAT {datId}",
            Type = nameof(DatType.NoIntro),
            PlatformId = PlatformId,
            OriginalFilename = $"dat-{datId}.dat",
            FileId = SeedFile(db, fileId),
            CreatedAt = DateTimeOffset.UtcNow
        });
    }

    private static void SeedVersion(
        RomdDbContext db,
        int datId,
        int datSourceId,
        int fileId,
        DatFileLifecycle lifecycle)
    {
        db.DatFiles.Add(new DatFileEntity
        {
            DatSourceId = datSourceId,
            Id = datId,
            Name = $"DAT {datId}",
            Description = $"DAT {datId}",
            Type = nameof(DatType.NoIntro),
            PlatformId = PlatformId,
            OriginalFilename = $"dat-{datId}.dat",
            FileId = SeedFile(db, fileId),
            Lifecycle = lifecycle.ToString(),
            CreatedAt = DateTimeOffset.UtcNow
        });
    }

    private static int SeedFile(RomdDbContext db, int fileId)
    {
        var bytes = new byte[Sha256.ByteLength];
        bytes[0] = (byte)fileId;
        db.Files.Add(new FileEntityPersistence
        {
            Id = fileId,
            Sha256 = Sha256.FromBytes(bytes),
            Size = 1,
            SizeOnDisk = 1,
            CreatedAt = DateTimeOffset.UtcNow
        });
        return fileId;
    }

    private static void SeedEntryWithGame(
        RomdDbContext db,
        int entryId,
        int catalogSourceId,
        int gameId,
        int datId,
        string key)
    {
        db.SourceEntries.Add(new SourceEntryEntity
        {
            Id = entryId,
            CatalogSourceId = catalogSourceId,
            EntryKey = key,
            Name = key,
            PlatformId = PlatformId,
            CreatedAt = DateTimeOffset.UtcNow
        });
        db.DatGames.Add(NewGame(gameId, datId, entryId));
    }

    private static DatGameEntity NewGame(int gameId, int datId, int entryId) => new()
    {
        Id = gameId,
        DatFileId = datId,
        SourceEntryId = entryId,
        Name = $"Game {gameId}",
        CreatedAt = DateTimeOffset.UtcNow
    };

    private static void SeedLinkedTitle(RomdDbContext db, int titleId, int entryId)
    {
        db.Titles.Add(new TitleEntity
        {
            Id = titleId,
            PlatformId = PlatformId,
            Name = $"Title {titleId}",
            NormalizedName = $"title{titleId}",
            EnrichmentStatus = "None",
            CreatedAt = DateTimeOffset.UtcNow
        });
        db.TitleSourceLinks.Add(new TitleSourceLinkEntity { SourceEntryId = entryId, TitleId = titleId });
    }

    /// <summary>
    ///     Flushes staged writes into SQLite, then rolls the real transaction back and
    ///     throws — proving the orphan policy shares the deletion's transaction.
    /// </summary>
    private sealed class FailOnCommitUnitOfWork(RomdDbContext context) : IUnitOfWork
    {
        private readonly EfUnitOfWork _inner = new(context);

        public Task FlushAsync(CancellationToken cancellationToken = default) =>
            _inner.FlushAsync(cancellationToken);

        public async Task<ITransaction> BeginTransactionAsync(CancellationToken cancellationToken = default) =>
            new FailOnCommitTransaction(this, await _inner.BeginTransactionAsync(cancellationToken));

        private sealed class FailOnCommitTransaction(
            FailOnCommitUnitOfWork owner,
            ITransaction inner) : ITransaction
        {
            public async Task CommitAsync(CancellationToken cancellationToken = default)
            {
                await owner.FlushAsync(cancellationToken);
                await inner.RollbackAsync(CancellationToken.None);
                throw new InvalidOperationException("Injected commit failure.");
            }

            public Task RollbackAsync(CancellationToken cancellationToken = default) =>
                inner.RollbackAsync(cancellationToken);

            public ValueTask DisposeAsync() => inner.DisposeAsync();
        }
    }
}
