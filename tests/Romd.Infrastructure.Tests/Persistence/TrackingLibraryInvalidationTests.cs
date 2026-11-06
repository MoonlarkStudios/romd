using Romd.Infrastructure.Tests.Helpers;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Romd.Domain.Libraries;
using Romd.Persistence;
using Romd.Persistence.Entities;
using Romd.Persistence.Repositories;
using Romd.PostgreSql.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Persistence;

public sealed class TrackingLibraryInvalidationTests : IDisposable
{
    private readonly PostgreSqlTestDatabase database = PostgreSqlTestDatabase.Create();

    [Fact]
    public async Task TrackBatch_ScopeAndNoOps_MarksUpdatesWithoutRevokingAccessOrScheduling()
    {
        await using var db = database.CreateContext();
        await SeedAsync(db);
        var before = await db.Libraries.ToDictionaryAsync(l => l.Id, l => l.MaterializationRevision);
        var repository = new TrackedTitleRepository(db, TimeProvider.System);

        (await repository.TrackByTitleIdsAsync([1, 2, 1])).ShouldBe(2);
        var after = await db.Libraries.OrderBy(l => l.Id).ToListAsync();
        after.Where(l => l.ToDomain().NeedsRefresh).Select(l => l.Id).ShouldBe([1, 3]);
        after.Where(l => !l.ToDomain().NeedsRefresh).ShouldAllBe(l => l.MaterializationRevision == before[l.Id]);
        (await db.Jobs.OfType<MaterializationJobEntity>().CountAsync()).ShouldBe(0);
        after.ShouldAllBe(l => !l.NeedsMaterialization);
        var revision = after[0].MaterializationRevision;

        (await repository.TrackByTitleIdsAsync([1, 2])).ShouldBe(0);
        db.CatalogReleases.Add(new CatalogReleaseEntity
        {
            Id = 1,
            CatalogTitleId = 1,
            PlatformId = 1,
            Fingerprint = "pin-test",
            Name = "Pinned release"
        });
        await db.SaveChangesAsync();
        await repository.TrackAsync(1, 1);
        (await repository.GetAsync(1))!.PinnedCatalogReleaseId.ShouldBe(1);
        (await db.Libraries.SingleAsync(l => l.Id == 1)).MaterializationRevision.ShouldBe(revision);

        (await repository.UntrackByTitleIdsAsync([1])).ShouldBe(1);
        (await db.Libraries.SingleAsync(l => l.Id == 1)).MaterializationRevision.ShouldNotBe(revision);
        (await db.Jobs.OfType<MaterializationJobEntity>().CountAsync()).ShouldBe(0);
        var afterUntrack = (await db.Libraries.SingleAsync(l => l.Id == 1)).MaterializationRevision;
        (await repository.UntrackByTitleIdsAsync([1])).ShouldBe(0);
        (await db.Libraries.SingleAsync(l => l.Id == 1)).MaterializationRevision.ShouldBe(afterUntrack);
    }

    [Fact]
    public async Task Track_InCallerTransaction_RollbackRestoresMembershipFlagsAndJobs()
    {
        await using var db = database.CreateContext();
        await SeedAsync(db);
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            await new TrackedTitleRepository(db, TimeProvider.System).TrackAsync(1);
            (await db.Jobs.CountAsync()).ShouldBe(0);
            await transaction.RollbackAsync();
        }
        (await db.TrackedTitles.CountAsync()).ShouldBe(0);
        (await db.Libraries.AnyAsync(l => l.NeedsMaterialization)).ShouldBeFalse();
        (await db.Jobs.CountAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task Track_JobAcceptanceUnavailable_StillMarksUpdatesWithoutCreatingJobs()
    {
        await using var db = database.CreateContext();
        await SeedAsync(db);
        await db.Database.ExecuteSqlRawAsync("""
            ALTER TABLE romd."Jobs" ADD CONSTRAINT "RejectTestJob" CHECK ("JobType" <> 'materialization');
            """);
        await new TrackedTitleRepository(db, TimeProvider.System).TrackAsync(1);
        (await db.TrackedTitles.CountAsync()).ShouldBe(1);
        (await db.Libraries.SingleAsync(l => l.Id == 1)).ToDomain().NeedsRefresh.ShouldBeTrue();
        (await db.Libraries.AnyAsync(l => l.NeedsMaterialization)).ShouldBeFalse();
        (await db.Jobs.CountAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task Untrack_DuringRebuild_RejectsOldProjectionActivation()
    {
        await using var db = database.CreateContext();
        await SeedAsync(db);
        var tracking = new TrackedTitleRepository(db, TimeProvider.System);
        await tracking.TrackAsync(1);
        var token = (await db.Libraries.SingleAsync(l => l.Id == 1)).MaterializationRevision;
        await tracking.UntrackAsync(1);
        (await new LibraryRepository(db).PublishProjectionAsync(db,
            1, new MaterializedLibraryProjection([], []), 0, token)).ShouldBeFalse();
        (await db.Libraries.SingleAsync(l => l.Id == 1)).NeedsMaterialization.ShouldBeTrue();
    }

    [Fact]
    public async Task TrackPlatform_OptInAndHandpickedScope_RespectsCurrentPolicy()
    {
        await using var db = database.CreateContext();
        await SeedAsync(db);
        var tracking = new TrackedTitleRepository(db, TimeProvider.System);
        (await tracking.TrackByPlatformAsync(2)).ShouldBe(1);
        var affected = await db.Libraries.Where(l => l.UpdatedAt > l.LastMaterializedAt).Select(l => l.Id).ToListAsync();
        affected.ShouldBe([4]);
        (await tracking.UntrackByPlatformAsync(2)).ShouldBe(1);
        (await db.Jobs.CountAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task Track_ConcurrentConfigurationUpdate_UsesCommittedPolicy()
    {
        await using var db = database.CreateContext();
        await SeedAsync(db);
        await using var writer = database.CreateContext();
        await using var transaction = await writer.Database.BeginTransactionAsync();
        // Hold the library row while tracking reaches invalidation. PostgreSQL must recheck
        // the policy after the lock wait instead of applying a stale C# configuration read.
        await writer.Libraries.Where(l => l.Id == 1).ExecuteUpdateAsync(s => s.SetProperty(
            l => l.ConfigurationJson, JsonSerializer.Serialize(new LibraryConfiguration { IncludeUntrackedTitles = true })));
        var track = new TrackedTitleRepository(db, TimeProvider.System).TrackAsync(1);
        await transaction.CommitAsync();
        await track;
        (await db.Libraries.SingleAsync(l => l.Id == 1)).ToDomain().NeedsRefresh.ShouldBeFalse();
        (await db.Jobs.OfType<MaterializationJobEntity>().AnyAsync(j => j.LibraryId == 1)).ShouldBeFalse();
    }

    [Fact]
    public async Task UploadUpdates_KeepPublishedAccess_ExplicitRefreshPublishes_RevocationsKeepPublishedSnapshot()
    {
        await using var db = database.CreateContext();
        await SeedAsync(db);
        var userId = Guid.NewGuid();
        db.Users.Add(new Romd.Persistence.Identity.RomdUser
        {
            Id = userId,
            UserName = "reader",
            NormalizedUserName = "READER",
            LibraryId = 1,
            CreatedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();
        var libraries = new LibraryRepository(db);
        var exports = new ExportAuthorizationReader(db);
        var tracking = new TrackedTitleRepository(db, TimeProvider.System);
        var scope = new Romd.Consumer.Application.Libraries.ConsumerLibraryScope(userId);
        var published = (await libraries.GetByIdAsync(1))!;

        for (int titleId = 1; titleId <= 2; titleId++)
        {
            await libraries.MarkUpdatesAvailableByPlatformAsync(1);
            await tracking.TrackByTitleIdsAsync([titleId]);
            var pending = (await libraries.GetByIdAsync(1))!;
            pending.NeedsRefresh.ShouldBeTrue();
            pending.NeedsMaterialization.ShouldBeFalse();
            pending.LastMaterializedAt.ShouldBe(published.LastMaterializedAt);
            pending.MaterializationGeneration.ShouldBe(published.MaterializationGeneration);
            (await libraries.GetCurrentContextAsync(scope)).ShouldBeOfType<
                Romd.Consumer.Application.Libraries.ConsumerLibraryReadResult<
                    Romd.Consumer.Application.Libraries.ConsumerLibraryContextReadModel>.Found>();
            (await exports.GetEffectiveLibraryScopeAsync(userId)).ShouldNotBeNull();
            (await db.Jobs.AnyAsync()).ShouldBeFalse();
        }

        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            var requested = (await libraries.FlagForRematerializationAsync(1))!;
            requested.NeedsMaterialization.ShouldBeFalse();
            requested.NeedsRefresh.ShouldBeTrue();
            await transaction.CommitAsync();
        }
        var pendingToken = (await libraries.GetByIdAsync(1))!.MaterializationRevision;
        // A concurrent upload must reject the old snapshot rather than falsely publish it.
        await libraries.MarkUpdatesAvailableByPlatformAsync(1);
        (await libraries.PublishProjectionAsync(db,
            1, new MaterializedLibraryProjection([], []), 0, pendingToken)).ShouldBeFalse();
        (await libraries.GetByIdAsync(1))!.NeedsRefresh.ShouldBeTrue();
        var currentToken = (await libraries.GetByIdAsync(1))!.MaterializationRevision;
        (await libraries.PublishProjectionAsync(db,
            1, new MaterializedLibraryProjection([], []), 0, currentToken)).ShouldBeTrue();
        (await libraries.GetByIdAsync(1))!.NeedsRefresh.ShouldBeFalse();

        await tracking.UntrackByTitleIdsAsync([1]);
        // Further additive uploads must never clear a pending access revocation.
        await libraries.MarkUpdatesAvailableByPlatformAsync(1);
        await tracking.TrackByTitleIdsAsync([1]);
        (await libraries.GetByIdAsync(1))!.NeedsMaterialization.ShouldBeTrue();
        // A pending revocation waits for the explicit refresh; consumers keep the published snapshot meanwhile.
        (await libraries.GetCurrentContextAsync(scope)).ShouldBeOfType<
            Romd.Consumer.Application.Libraries.ConsumerLibraryReadResult<
                Romd.Consumer.Application.Libraries.ConsumerLibraryContextReadModel>.Found>();
        (await exports.GetEffectiveLibraryScopeAsync(userId)).ShouldNotBeNull();
        (await db.Jobs.AnyAsync()).ShouldBeFalse();
    }

    private static async Task SeedAsync(RomdDbContext db)
    {
        var now = DateTimeOffset.UtcNow;
        for (int id = 1; id <= 2; id++)
            db.Platforms.Add(new PlatformEntity
            {
                Id = id,
                Name = $"Platform {id}",
                ShortName = $"p{id}",
                Ownership = Romd.Domain.ReferenceData.ReferenceOwnership.Romd,
                CanonicalKey = $"p{id}",
                BaseName = $"Platform {id}",
                BaseCompactLabel = $"P{id}",
                BuiltInVersion = 1,
                CreatedAt = now
            });
        for (int id = 1; id <= 3; id++)
            db.Titles.Add(new TitleEntity
            {
                Id = id,
                Name = $"Title {id}",
                NormalizedName = $"title {id}",
                PlatformId = id == 3 ? 2 : 1,
                EnrichmentStatus = "None",
                CreatedAt = now
            });
        var policies = new[] {
            new LibraryConfiguration { AllowedPlatformIds = [1] },
            new LibraryConfiguration { IncludeUntrackedTitles = true },
            // Include-only ignores platform scope, just like the domain builder.
            new LibraryConfiguration { TitleSelectionMode = LibraryTitleSelectionMode.IncludeOnly,
                IncludeTitleIds = [1], AllowedPlatformIds = [2] },
            new LibraryConfiguration { AllowedPlatformIds = [2] },
            new LibraryConfiguration { ExcludeTitleIds = [1, 2, 3] }
        };
        for (int i = 0; i < policies.Length; i++)
            db.Libraries.Add(new LibraryEntity
            {
                Id = i + 1,
                Name = $"Library {i + 1}",
                ConfigurationJson = JsonSerializer.Serialize(policies[i]),
                CreatedAt = now,
                LastMaterializedAt = now,
                UpdatedAt = now,
                NeedsMaterialization = false
            });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
    }

    public void Dispose() => database.Dispose();
}
