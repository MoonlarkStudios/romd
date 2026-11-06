using Romd.Persistence.Libraries;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using NSubstitute;
using OpenIddict.EntityFrameworkCore;
using Romd.Admin.Application.Libraries;
using Romd.Admin.Application.Catalog;
using Romd.Admin.Application.Common.Persistence;
using Romd.Consumer.Application.Browse;
using Romd.Consumer.Application.Browse.ReadModels;
using Romd.Consumer.Application.Libraries;
using Romd.Domain.Catalog.Ratings;
using Romd.Domain.Hashing;
using Romd.Domain.Identity;
using Romd.Domain.Libraries;
using Romd.Infrastructure.Libraries;
using Romd.Persistence.Identity;
using Romd.Persistence;
using Romd.Persistence.Entities;
using Romd.Persistence.Repositories;
using Romd.Infrastructure.Realtime;
using Romd.Infrastructure.Source;
using Romd.Infrastructure.Tests.Helpers;
using Romd.PostgreSql.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Persistence;

public sealed class LibraryRepositoryTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Publication_WithoutCallerTransaction_RejectsBeforeChangingPersistedState(bool withdraw)
    {
        await using var database = await CreateDatabaseAsync();
        var seed = await SeedDivergentProjectionDataAsync(database.Context);
        database.Context.ChangeTracker.Clear();
        var repository = new LibraryRepository(database.Context);
        var before = (await repository.GetByIdAsync(seed.LibraryId))!;

        await Should.ThrowAsync<InvalidOperationException>(async () =>
        {
            if (withdraw)
                await repository.StageMaterializedProjectionsAndMarkConfigurationInvalidAsync(
                    seed.LibraryId, "invalid", before.MaterializationRevision);
            else
                using (await repository.StageMaterializedProjectionsAndActivateAsync(
                    seed.LibraryId, ReplacementProjection(seed.LibraryId), 1, before.MaterializationRevision))
                { }
        });

        var after = (await repository.GetByIdAsync(seed.LibraryId))!;
        after.MaterializationRevision.ShouldBe(before.MaterializationRevision);
        after.MaterializationGeneration.ShouldBe(before.MaterializationGeneration);
        after.ConfigurationState.ShouldBe(before.ConfigurationState);
        (await database.Context.MaterializedLibraryTitles.Select(row => row.TitleId).ToListAsync()).ShouldBe([1]);
        (await database.Context.MaterializedLibraryReleases.Select(row => row.DatGameId).ToListAsync()).ShouldBe([1]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Publication_StagingThrows_ReleasesOnlyOwnedTrackingAndLeavesRollbackToCaller(bool autoDetectChangesEnabled)
    {
        await using var database = await CreateDatabaseAsync();
        var context = database.Context;
        var seed = await SeedDivergentProjectionDataAsync(context);
        context.ChangeTracker.Clear();
        var repository = new LibraryRepository(context);
        var before = (await repository.GetByIdAsync(seed.LibraryId))!;
        var preexisting = await context.Platforms.AsTracking().SingleAsync(row => row.Id == 1);
        context.ChangeTracker.AutoDetectChangesEnabled = autoDetectChangesEnabled;
        var projection = ReplacementProjection(seed.LibraryId);
        var invalidProjection = projection with { Titles = [projection.Titles[0], projection.Titles[0]] };
        await using var transaction = await context.Database.BeginTransactionAsync();

        await Should.ThrowAsync<ArgumentException>(() => repository.StageMaterializedProjectionsAndActivateAsync(
            seed.LibraryId, invalidProjection, 1, before.MaterializationRevision));

        context.ChangeTracker.Entries<MaterializedLibraryTitleEntity>().ShouldBeEmpty();
        context.ChangeTracker.Entries<MaterializedLibraryReleaseEntity>().ShouldBeEmpty();
        context.ChangeTracker.Entries<PlatformEntity>().Single().Entity.ShouldBeSameAs(preexisting);
        context.ChangeTracker.AutoDetectChangesEnabled.ShouldBe(autoDetectChangesEnabled);
        (await repository.GetByIdAsync(seed.LibraryId))!.MaterializationRevision.ShouldNotBe(before.MaterializationRevision);
        await transaction.RollbackAsync();
        (await repository.GetByIdAsync(seed.LibraryId))!.MaterializationRevision.ShouldBe(before.MaterializationRevision);
        (await context.MaterializedLibraryTitles.Select(row => row.TitleId).ToListAsync()).ShouldBe([1]);
    }

    [Fact]
    public async Task Publication_CallerFlushAndCommit_PersistsChildrenBeforeReleasingOnlyOwnedTracking()
    {
        await using var database = await CreateDatabaseAsync();
        var context = database.Context;
        var seed = await SeedDivergentProjectionDataAsync(context);
        context.ChangeTracker.Clear();
        var repository = new LibraryRepository(context);
        var before = (await repository.GetByIdAsync(seed.LibraryId))!;
        var preexisting = await context.Platforms.AsTracking().SingleAsync(row => row.Id == 1);
        var unrelated = NewCollection(99, "Unrelated staged collection", DateTimeOffset.UnixEpoch, seed.UserId);
        context.Collections.Add(unrelated);
        var unitOfWork = new EfUnitOfWork(context);
        await using var transaction = await unitOfWork.BeginTransactionAsync();
        var publication = await repository.StageMaterializedProjectionsAndActivateAsync(
            seed.LibraryId, ReplacementProjection(seed.LibraryId), 1, before.MaterializationRevision);
        try
        {
            publication.Applied.ShouldBeTrue();
            context.ChangeTracker.Entries<MaterializedLibraryTitleEntity>().ShouldContain(entry => entry.State == EntityState.Added);
            (await context.MaterializedLibraryTitles.Select(row => row.TitleId).ToListAsync()).ShouldBe([1]);
            context.Entry(unrelated).State.ShouldBe(EntityState.Added);

            await unitOfWork.FlushAsync();
            (await context.MaterializedLibraryTitles.Select(row => row.TitleId).ToListAsync()).ShouldBe([2]);
            (await context.MaterializedLibraryReleases.Select(row => row.DatGameId).ToListAsync()).ShouldBe([2]);
            await using (var read = database.CreateReadContext())
            {
                (await read.MaterializedLibraryTitles.Select(row => row.TitleId).ToListAsync()).ShouldBe([1]);
                (await read.Collections.AnyAsync(row => row.Id == 99)).ShouldBeFalse();
            }
            await transaction.CommitAsync();
        }
        catch
        {
            await transaction.RollbackAsync();
            throw;
        }
        finally
        {
            publication.Dispose();
            publication.Dispose();
        }

        context.ChangeTracker.Entries<MaterializedLibraryTitleEntity>().ShouldBeEmpty();
        context.ChangeTracker.Entries<MaterializedLibraryReleaseEntity>().ShouldBeEmpty();
        context.Entry(preexisting).State.ShouldBe(EntityState.Unchanged);
        context.Entry(unrelated).State.ShouldBe(EntityState.Unchanged);
        await using var committed = database.CreateReadContext();
        (await committed.MaterializedLibraryTitles.Select(row => row.TitleId).ToListAsync()).ShouldBe([2]);
        (await committed.Collections.AnyAsync(row => row.Id == 99)).ShouldBeTrue();

        var preexistingProjection = await context.MaterializedLibraryTitles.AsTracking().SingleAsync();
        var revision = (await repository.GetByIdAsync(seed.LibraryId))!.MaterializationRevision;
        (await repository.PublishProjectionAsync(context, seed.LibraryId, ReplacementProjection(seed.LibraryId), 1, revision)).ShouldBeTrue();
        context.ChangeTracker.Entries<MaterializedLibraryTitleEntity>().Single().Entity.ShouldBeSameAs(preexistingProjection);
        context.Entry(preexistingProjection).State.ShouldBe(EntityState.Unchanged);
        context.ChangeTracker.Entries<MaterializedLibraryReleaseEntity>().ShouldBeEmpty();
        (await repository.GetByIdAsync(seed.LibraryId))!.MaterializationGeneration.ShouldBe(before.MaterializationGeneration + 2);
    }

    [Theory]
    [InlineData("outbox")]
    [InlineData("after-flush")]
    [InlineData("cancel")]
    public async Task MaterializeAsync_NonemptyPublicationFails_RollsBackChildrenHeaderAndEventAndCanRetry(string failure)
    {
        var interceptor = new PublicationOutboxFailureInterceptor();
        await using var database = await CreateDatabaseAsync(interceptor);
        var context = database.Context;
        var seed = await SeedDivergentProjectionDataAsync(context);
        context.ChangeTracker.Clear();
        var repository = new LibraryRepository(context);
        var before = (await repository.GetByIdAsync(seed.LibraryId))!;
        var provider = Substitute.For<IMaterializationDataProvider>();
        provider.GetCandidatesAsync(Arg.Any<LibraryConfiguration>(), Arg.Any<CancellationToken>())
            .Returns(new[] { new TitleCandidates(2, 2, "Action", [new ContentRating
                {
                    Board = RatingBoard.Esrb, Code = "E", Designation = RatingDesignation.Rated,
                    MinimumAge = 0, SourceId = "test"
                }],
                [new GameCandidate(2, null, 1, null, true, true, [1], [], [])], true) });
        var catalog = Substitute.For<ICatalogProjectionService>();
        catalog.GetPlatformIdsNeedingRebuildAsync(Arg.Any<CancellationToken>()).Returns(Array.Empty<int>());
        using var cancellation = new CancellationTokenSource();
        var unitOfWork = new PublicationFailureUnitOfWork(new EfUnitOfWork(context), failure, cancellation);
        var service = new LibraryMaterializationService(repository, provider, catalog,
            new AdminRealtimeOutbox(context, TimeProvider.System), unitOfWork,
            Substitute.For<ILogger<LibraryMaterializationService>>());
        interceptor.Enabled = failure == "outbox";

        if (failure == "outbox")
            await Should.ThrowAsync<DbUpdateException>(() => service.MaterializeAsync(seed.LibraryId, cancellation.Token));
        else if (failure == "cancel")
            (await Should.ThrowAsync<OperationCanceledException>(() => service.MaterializeAsync(seed.LibraryId, cancellation.Token)))
                .CancellationToken.ShouldBe(cancellation.Token);
        else
            (await Should.ThrowAsync<InvalidOperationException>(() => service.MaterializeAsync(seed.LibraryId, cancellation.Token)))
                .Message.ShouldBe("Injected failure after publication flush.");

        interceptor.Enabled = false;
        context.ChangeTracker.Entries().ShouldBeEmpty();
        await context.SaveChangesAsync();
        await using (var read = database.CreateReadContext())
        {
            var persisted = (await new LibraryRepository(read).GetByIdAsync(seed.LibraryId))!;
            persisted.MaterializationRevision.ShouldBe(before.MaterializationRevision);
            persisted.MaterializationGeneration.ShouldBe(before.MaterializationGeneration);
            persisted.ItemCount.ShouldBe(before.ItemCount);
            persisted.LastMaterializedAt.ShouldBe(before.LastMaterializedAt);
            (await read.MaterializedLibraryTitles.Select(row => row.TitleId).ToListAsync()).ShouldBe([1]);
            (await read.MaterializedLibraryReleases.Select(row => row.DatGameId).ToListAsync()).ShouldBe([1]);
            (await read.AdminRealtimeOutboxEvents.AnyAsync()).ShouldBeFalse();
        }

        unitOfWork.Enabled = false;
        (await service.MaterializeAsync(seed.LibraryId)).Outcome.ShouldBe(MaterializationOutcome.Materialized);
        context.ChangeTracker.Entries<MaterializedLibraryTitleEntity>().ShouldBeEmpty();
        context.ChangeTracker.Entries<MaterializedLibraryReleaseEntity>().ShouldBeEmpty();
        (await context.MaterializedLibraryTitles.Select(row => row.TitleId).ToListAsync()).ShouldBe([2]);
        (await context.AdminRealtimeOutboxEvents.CountAsync()).ShouldBe(1);
    }

    private static MaterializedLibraryProjection ReplacementProjection(int libraryId) => new(
        [new(libraryId, 2, 2, "Action", true, true, true, 1, 1, 1, LibraryTitleAvailability.Playable)],
        [new(libraryId, 2, null, 2, 1, 2, true, true, true, true, false, null, true, "ExposedDefault")]);

    [Fact]
    public async Task UpdateStagedAsync_DefaultSwitch_WaitsForCallerFlushAndCommit()
    {
        await using var database = await CreateDatabaseAsync();
        var repository = new LibraryRepository(database.Context);
        var first = await AddLibraryAsync(repository, database.Context, "First");
        var second = await AddLibraryAsync(repository, database.Context, "Second");

        first.MarkAsDefault(now: DateTimeOffset.UnixEpoch);
        await CommitLibraryUpdateAsync(repository, database.Context, first);
        database.Context.ChangeTracker.Clear();

        var updatedSecond = await repository.GetByIdAsync(second.Id);
        updatedSecond.ShouldNotBeNull();
        updatedSecond.MarkAsDefault(now: DateTimeOffset.UnixEpoch);

        await using (var transaction = await new EfUnitOfWork(database.Context).BeginTransactionAsync())
        {
            await repository.ClearDefaultAsync();
            await repository.UpdateStagedAsync(updatedSecond);

            (await repository.GetByIdAsync(second.Id))!.IsDefault.ShouldBeFalse();

            await transaction.CommitAsync();
        }

        var libraries = await repository.GetAllAsync();
        libraries.Single(l => l.Id == first.Id).IsDefault.ShouldBeFalse();
        libraries.Single(l => l.Id == second.Id).IsDefault.ShouldBeTrue();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UpdateStagedAsync_StaleAggregateAfterMaterialization_PreservesMonotonicGeneration(
        bool isDefault)
    {
        await using var database = await CreateDatabaseAsync();
        var repository = new LibraryRepository(database.Context);
        var seeded = await AddLibraryAsync(repository, database.Context, "Before");
        database.Context.ChangeTracker.Clear();
        var stale = await repository.GetByIdAsync(seeded.Id);
        stale.ShouldNotBeNull();
        stale.MaterializationGeneration.ShouldBe(0);

        bool firstActivation = await repository.PublishProjectionAsync(database.Context,
            seeded.Id,
            new MaterializedLibraryProjection([], []),
            itemCount: 4,
            stale.MaterializationRevision);
        firstActivation.ShouldBeTrue();

        stale.UpdateConfiguration("After", new LibraryConfiguration { ShowMissingGames = true }, now: DateTimeOffset.UnixEpoch);
        if (isDefault)
        {
            stale.MarkAsDefault(now: DateTimeOffset.UnixEpoch);
        }

        await using (var transaction = await new EfUnitOfWork(database.Context).BeginTransactionAsync())
        {
            await repository.UpdateStagedAsync(stale);
            var tracked = database.Context.ChangeTracker.Entries<LibraryEntity>().Single();
            tracked.Property(library => library.MaterializationGeneration).IsModified.ShouldBeFalse();
            tracked.Property(library => library.LastMaterializedAt).IsModified.ShouldBeFalse();
            tracked.Property(library => library.ItemCount).IsModified.ShouldBeFalse();
            await transaction.CommitAsync();
        }

        database.Context.ChangeTracker.Clear();
        var afterStaleUpdate = await database.Context.Libraries.SingleAsync(library => library.Id == seeded.Id);
        afterStaleUpdate.Name.ShouldBe("After");
        afterStaleUpdate.IsDefault.ShouldBe(isDefault);
        afterStaleUpdate.MaterializationGeneration.ShouldBe(1);
        afterStaleUpdate.LastMaterializedAt.ShouldNotBeNull();
        afterStaleUpdate.ItemCount.ShouldBe(4);

        bool secondActivation = await repository.PublishProjectionAsync(database.Context,
            seeded.Id,
            new MaterializedLibraryProjection([], []),
            itemCount: 5,
            afterStaleUpdate.MaterializationRevision);
        secondActivation.ShouldBeTrue();

        database.Context.ChangeTracker.Clear();
        var afterSecondActivation = await database.Context.Libraries.SingleAsync(library => library.Id == seeded.Id);
        afterSecondActivation.MaterializationGeneration.ShouldBe(2);
        afterSecondActivation.ItemCount.ShouldBe(5);
    }

    [Fact]
    public async Task ClearDefaultAsync_DefaultExists_ClearsDefault()
    {
        await using var database = await CreateDatabaseAsync();
        var repository = new LibraryRepository(database.Context);
        var library = await AddLibraryAsync(repository, database.Context, "First");

        library.MarkAsDefault(now: DateTimeOffset.UnixEpoch);
        await CommitLibraryUpdateAsync(repository, database.Context, library);
        await repository.ClearDefaultAsync();

        var defaultLibrary = await repository.GetDefaultAsync();
        defaultLibrary.ShouldBeNull();
    }

    [Fact]
    public async Task Libraries_PartialUniqueIndex_PreventsTwoDefaultLibraries()
    {
        await using var database = await CreateDatabaseAsync();
        var now = DateTimeOffset.UtcNow;

        database.Context.Libraries.AddRange(
            new LibraryEntity
            {
                Name = "First",
                ConfigurationJson = "{}",
                IsDefault = true,
                NeedsMaterialization = false,
                CreatedAt = now
            },
            new LibraryEntity
            {
                Name = "Second",
                ConfigurationJson = "{}",
                IsDefault = true,
                NeedsMaterialization = false,
                CreatedAt = now
            });

        await Should.ThrowAsync<DbUpdateException>(() => database.Context.SaveChangesAsync());
    }

    [Fact]
    public async Task GetByIdAsync_InvalidConfigurationJson_ReturnsInvalidLibrary()
    {
        await using var database = await CreateDatabaseAsync();
        var now = DateTimeOffset.UtcNow;
        var repository = new LibraryRepository(database.Context);

        database.Context.Libraries.Add(new LibraryEntity
        {
            Name = "Broken",
            ConfigurationJson = "{",
            NeedsMaterialization = true,
            CreatedAt = now
        });
        await database.Context.SaveChangesAsync();

        var library = await repository.GetByIdAsync(1);

        library.ShouldNotBeNull();
        library.ConfigurationState.ShouldBe(LibraryConfigurationState.Invalid);
        library.ConfigurationError.ShouldNotBeNull();
        library.Configuration.ShouldBe(LibraryConfiguration.InvalidFailClosedSentinel);
    }

    [Fact]
    public async Task GetByIdAsync_SemanticallyInvalidConfigurationJson_ReturnsInvalidLibrary()
    {
        await using var database = await CreateDatabaseAsync();
        var now = DateTimeOffset.UtcNow;
        var repository = new LibraryRepository(database.Context);

        database.Context.Libraries.Add(new LibraryEntity
        {
            Name = "Broken",
            ConfigurationJson = """{"ContentRatingPolicy":{"MaxMinimumAge":-1}}""",
            NeedsMaterialization = true,
            CreatedAt = now
        });
        await database.Context.SaveChangesAsync();

        var library = await repository.GetByIdAsync(1);

        library.ShouldNotBeNull();
        library.ConfigurationState.ShouldBe(LibraryConfigurationState.Invalid);
        library.ConfigurationError.ShouldBe("Maximum minimum age must be zero or greater.");
        library.Configuration.ShouldBe(LibraryConfiguration.InvalidFailClosedSentinel);
    }

    [Fact]
    public async Task GetByIdAsync_LegacySelectionFieldsJson_IgnoresUnknownFields()
    {
        await using var database = await CreateDatabaseAsync();
        var now = DateTimeOffset.UtcNow;
        var repository = new LibraryRepository(database.Context);

        database.Context.Libraries.Add(new LibraryEntity
        {
            Name = "Legacy Config",
            ConfigurationJson = """
                                {
                                  "AllowedPlatformIds": [1],
                                  "RegionPriority": [2],
                                  "LanguagePriority": [3],
                                  "PreferLatestRevision": false,
                                  "FallbackToAnyRegion": false
                                }
                                """,
            NeedsMaterialization = true,
            CreatedAt = now
        });
        await database.Context.SaveChangesAsync();

        var library = await repository.GetByIdAsync(1);

        library.ShouldNotBeNull();
        library.ConfigurationState.ShouldBe(LibraryConfigurationState.Valid);
        library.Configuration.AllowedPlatformIds.ShouldBe([1]);
    }

    [Fact]
    public void FromDomain_DoesNotSerializeSelectionFields()
    {
        var library = Library.CreateNew("Accessible", new LibraryConfiguration(), now: DateTimeOffset.UnixEpoch);

        var entity = LibraryEntity.FromDomain(library);

        entity.ConfigurationJson.ShouldNotContain("RegionPriority");
        entity.ConfigurationJson.ShouldNotContain("LanguagePriority");
        entity.ConfigurationJson.ShouldNotContain("PreferLatestRevision");
        entity.ConfigurationJson.ShouldNotContain("FallbackToAnyRegion");
    }

    [Fact]
    public async Task GetNeedingMaterializationAsync_InvalidPersistedState_DoesNotReturnLibrary()
    {
        await using var database = await CreateDatabaseAsync();
        var now = DateTimeOffset.UtcNow;
        var repository = new LibraryRepository(database.Context);

        database.Context.Libraries.Add(new LibraryEntity
        {
            Name = "Invalid",
            ConfigurationJson = "{}",
            ConfigurationState = LibraryConfigurationState.Invalid.ToString(),
            ConfigurationError = "Invalid test config.",
            NeedsMaterialization = true,
            CreatedAt = now
        });
        await database.Context.SaveChangesAsync();

        var libraries = await repository.GetNeedingMaterializationAsync();

        libraries.ShouldBeEmpty();
    }

    [Fact]
    public async Task MarkConfigurationInvalidAsync_PreservesRawJsonAndClearsMaterializationState()
    {
        await using var database = await CreateDatabaseAsync();
        var now = DateTimeOffset.UtcNow;
        var repository = new LibraryRepository(database.Context);

        database.Context.Libraries.Add(new LibraryEntity
        {
            Id = 1,
            Name = "Broken",
            ConfigurationJson = "{",
            NeedsMaterialization = true,
            ItemCount = 10,
            CreatedAt = now
        });
        await database.Context.SaveChangesAsync();

        await repository.MarkConfigurationInvalidAsync(1, "bad json");

        var entity = await database.Context.Libraries.SingleAsync(library => library.Id == 1);
        entity.ConfigurationJson.ShouldBe("{");
        entity.ConfigurationState.ShouldBe(LibraryConfigurationState.Invalid.ToString());
        entity.ConfigurationError.ShouldBe("bad json");
        entity.NeedsMaterialization.ShouldBeFalse();
        entity.ItemCount.ShouldBe(0);
    }

    [Fact]
    public async Task StageMaterializedProjectionsAndActivateAsync_UnchangedToken_ActivatesProjection()
    {
        await using var database = await CreateDatabaseAsync();
        var timestamp = DateTimeOffset.UtcNow.AddMinutes(-5);
        var token = Guid.NewGuid();
        var repository = new LibraryRepository(database.Context);

        database.Context.Libraries.Add(new LibraryEntity
        {
            Id = 1,
            Name = "Ready",
            ConfigurationJson = "{}",
            NeedsMaterialization = true,
            UpdatedAt = timestamp,
            MaterializationRevision = token,
            CreatedAt = timestamp
        });
        await database.Context.SaveChangesAsync();

        bool activated = await repository.PublishProjectionAsync(database.Context,
            1, new MaterializedLibraryProjection([], []), 12, token);

        activated.ShouldBeTrue();
        var entity = await database.Context.Libraries.SingleAsync(library => library.Id == 1);
        entity.NeedsMaterialization.ShouldBeFalse();
        entity.ItemCount.ShouldBe(12);
        entity.LastMaterializedAt.ShouldNotBeNull();
        entity.UpdatedAt.ShouldNotBe(timestamp);
        entity.MaterializationRevision.ShouldNotBe(token);
        entity.MaterializationGeneration.ShouldBe(1);

        bool repeatedActivation = await repository.PublishProjectionAsync(database.Context,
            1, new MaterializedLibraryProjection([], []), 99, token);
        repeatedActivation.ShouldBeFalse();
        (await repository.GetByIdAsync(1))!.ItemCount.ShouldBe(12);
    }

    [Fact]
    public async Task StageMaterializedProjectionsAndActivateAsync_NeverUpdatedLibrary_ActivatesProjection()
    {
        await using var database = await CreateDatabaseAsync();
        var createdAt = DateTimeOffset.UtcNow.AddMinutes(-5);
        var token = Guid.NewGuid();
        var repository = new LibraryRepository(database.Context);

        database.Context.Libraries.Add(new LibraryEntity
        {
            Id = 1,
            Name = "First Materialization",
            ConfigurationJson = "{}",
            NeedsMaterialization = true,
            UpdatedAt = null,
            MaterializationRevision = token,
            CreatedAt = createdAt
        });
        await database.Context.SaveChangesAsync();

        bool activated = await repository.PublishProjectionAsync(database.Context,
            1, new MaterializedLibraryProjection([], []), 12, token);

        activated.ShouldBeTrue();
        var entity = await database.Context.Libraries.SingleAsync(library => library.Id == 1);
        entity.NeedsMaterialization.ShouldBeFalse();
        entity.ItemCount.ShouldBe(12);
        entity.LastMaterializedAt.ShouldNotBeNull();
        entity.UpdatedAt.ShouldNotBeNull();
        entity.MaterializationGeneration.ShouldBe(1);
    }

    [Fact]
    public async Task StageMaterializedProjectionsAndActivateAsync_UpdatedAfterStart_MutatesNothing()
    {
        await using var database = await CreateDatabaseAsync();
        var timestamp = DateTimeOffset.UtcNow.AddMinutes(-5);
        var token = Guid.NewGuid();
        var reflaggedAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        var repository = new LibraryRepository(database.Context);

        database.Context.Libraries.Add(new LibraryEntity
        {
            Id = 1,
            Name = "Reflagged",
            ConfigurationJson = "{}",
            NeedsMaterialization = true,
            ItemCount = 3,
            UpdatedAt = reflaggedAt,
            CreatedAt = timestamp
        });
        await database.Context.SaveChangesAsync();
        var persistedUpdatedAt = await database.Context.Libraries
            .Where(library => library.Id == 1)
            .Select(library => library.UpdatedAt)
            .SingleAsync();

        bool activated = await repository.PublishProjectionAsync(database.Context,
            1, new MaterializedLibraryProjection([], []), 12, token);

        activated.ShouldBeFalse();
        var entity = await database.Context.Libraries.SingleAsync(library => library.Id == 1);
        entity.NeedsMaterialization.ShouldBeTrue();
        entity.ItemCount.ShouldBe(3);
        entity.LastMaterializedAt.ShouldBeNull();
        entity.UpdatedAt.ShouldBe(persistedUpdatedAt);
        entity.MaterializationGeneration.ShouldBe(0);
    }

    [Theory]
    [InlineData("all")]
    [InlineData("platform")]
    [InlineData("configuration")]
    [InlineData("default")]
    [InlineData("clear-default")]
    [InlineData("invalid")]
    [InlineData("clear-invalid")]
    public async Task StageMaterializedProjectionsAndActivateAsync_ChangedAtSameTimestamp_RejectsStaleRevision(
        string mutation)
    {
        await using var database = await CreateDatabaseAsync();
        var repository = new LibraryRepository(database.Context);
        database.Context.Libraries.Add(new LibraryEntity
        {
            Id = 1,
            Name = "Concurrent",
            ConfigurationJson = "{}",
            IsDefault = true,
            NeedsMaterialization = true,
            ItemCount = 3,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.Parse("2026-01-02T03:04:05Z")
        });
        await database.Context.SaveChangesAsync();
        database.Context.ChangeTracker.Clear();
        var original = (await repository.GetByIdAsync(1))!;
        var capturedRevision = original.MaterializationRevision;

        switch (mutation)
        {
            case "all":
                await repository.FlagAllForRematerializationAsync();
                break;
            case "platform":
                await repository.FlagForRematerializationByPlatformAsync(42);
                break;
            case "configuration":
                original.UpdateConfiguration("Changed", new LibraryConfiguration(), now: DateTimeOffset.UnixEpoch);
                await CommitLibraryUpdateAsync(repository, database.Context, original);
                break;
            case "default":
                original.MarkAsDefault(now: DateTimeOffset.UnixEpoch);
                await CommitLibraryUpdateAsync(repository, database.Context, original);
                break;
            case "clear-default":
                await repository.ClearDefaultAsync();
                break;
            case "invalid":
                await repository.MarkConfigurationInvalidAsync(1, "invalid");
                break;
            case "clear-invalid":
                await repository.WithdrawProjectionAsync(database.Context, 1, "invalid", capturedRevision);
                break;
        }

        // Reproduce timestamp equality deterministically without depending on clock speed or precision.
        await database.Context.Libraries.Where(library => library.Id == 1)
            .ExecuteUpdateAsync(setters => setters.SetProperty(library => library.UpdatedAt,
                DateTimeOffset.Parse("2026-01-02T03:04:05Z")));
        var beforeActivation = (await repository.GetByIdAsync(1))!;
        bool activated = await repository.PublishProjectionAsync(database.Context,
            1, new MaterializedLibraryProjection([], []), 12, capturedRevision);

        activated.ShouldBeFalse();
        var afterActivation = (await repository.GetByIdAsync(1))!;
        afterActivation.MaterializationRevision.ShouldBe(beforeActivation.MaterializationRevision);
        afterActivation.MaterializationRevision.ShouldNotBe(capturedRevision);
        afterActivation.UpdatedAt.ShouldBe(DateTimeOffset.Parse("2026-01-02T03:04:05Z"));
        afterActivation.NeedsMaterialization.ShouldBe(beforeActivation.NeedsMaterialization);
        afterActivation.ItemCount.ShouldBe(beforeActivation.ItemCount);
        afterActivation.MaterializationGeneration.ShouldBe(0);
        afterActivation.LastMaterializedAt.ShouldBeNull();
    }

    [Fact]
    public async Task FlagAllForRematerializationAsync_AlreadyFlaggedLibrary_AdvancesMaterializationToken()
    {
        await using var database = await CreateDatabaseAsync();
        var timestamp = DateTimeOffset.UtcNow.AddMinutes(-5);
        var token = Guid.NewGuid();
        var repository = new LibraryRepository(database.Context);

        database.Context.Libraries.Add(new LibraryEntity
        {
            Id = 1,
            Name = "Already Flagged",
            ConfigurationJson = "{}",
            NeedsMaterialization = true,
            UpdatedAt = timestamp,
            MaterializationRevision = token,
            CreatedAt = timestamp
        });
        await database.Context.SaveChangesAsync();

        await repository.FlagAllForRematerializationAsync();

        var entity = await database.Context.Libraries.SingleAsync(library => library.Id == 1);
        entity.NeedsMaterialization.ShouldBeTrue();
        entity.UpdatedAt.ShouldNotBeNull();
        entity.UpdatedAt.Value.ShouldBeGreaterThan(timestamp);
        entity.MaterializationRevision.ShouldNotBe(token);
    }

    [Fact]
    public async Task FlagForRematerializationByPlatformAsync_AlreadyFlaggedAffectedLibrary_AdvancesMaterializationToken()
    {
        await using var database = await CreateDatabaseAsync();
        var timestamp = DateTimeOffset.UtcNow.AddMinutes(-5);
        var token = Guid.NewGuid();
        var repository = new LibraryRepository(database.Context);

        database.Context.Libraries.Add(new LibraryEntity
        {
            Id = 1,
            Name = "Already Flagged",
            ConfigurationJson = "{}",
            NeedsMaterialization = true,
            UpdatedAt = timestamp,
            MaterializationRevision = token,
            CreatedAt = timestamp
        });
        await database.Context.SaveChangesAsync();

        await repository.FlagForRematerializationByPlatformAsync(42);

        var entity = await database.Context.Libraries.SingleAsync(library => library.Id == 1);
        entity.NeedsMaterialization.ShouldBeTrue();
        entity.UpdatedAt.ShouldNotBeNull();
        entity.UpdatedAt.Value.ShouldBeGreaterThan(timestamp);
        entity.MaterializationRevision.ShouldNotBe(token);
    }

    [Fact]
    public async Task ValidateConfigurationReferencesAsync_MissingPlatform_ReturnsError()
    {
        await using var database = await CreateDatabaseAsync();
        var repository = new LibraryRepository(database.Context);

        var error = await repository.ValidateConfigurationReferencesAsync(
            new LibraryConfiguration { AllowedPlatformIds = [999] });

        error.ShouldBe("Allowed platform ID 999 does not exist.");
    }

    [Fact]
    public async Task StageMaterializedProjectionsAndMarkConfigurationInvalidAsync_ExistingRows_ClearsProjectionAndMarksInvalid()
    {
        await using var database = await CreateDatabaseAsync();
        var repository = new LibraryRepository(database.Context);
        var seed = await SeedDivergentProjectionDataAsync(database.Context);

        var revision = (await repository.GetByIdAsync(seed.LibraryId))!.MaterializationRevision;
        bool invalidated = await repository.WithdrawProjectionAsync(database.Context,
            seed.LibraryId, "bad reference", revision);
        invalidated.ShouldBeTrue();

        database.Context.ChangeTracker.Clear();
        (await database.Context.MaterializedLibraryTitles.CountAsync()).ShouldBe(0);
        (await database.Context.MaterializedLibraryReleases.CountAsync()).ShouldBe(0);

        var library = await database.Context.Libraries.SingleAsync(l => l.Id == seed.LibraryId);
        library.ConfigurationState.ShouldBe(LibraryConfigurationState.Invalid.ToString());
        library.ConfigurationError.ShouldBe("bad reference");
        library.NeedsMaterialization.ShouldBeFalse();
        library.ItemCount.ShouldBe(0);
    }

    [Fact]
    public async Task StageMaterializedProjectionsAndMarkConfigurationInvalidAsync_WithdrawnSnapshot_RepairedRulesAwaitFirstPublication()
    {
        await using var database = await CreateDatabaseAsync();
        var repository = new LibraryRepository(database.Context);
        var seed = await SeedDivergentProjectionDataAsync(database.Context);
        await database.Context.Libraries
            .Where(library => library.Id == seed.LibraryId)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(library => library.LastMaterializedAt, DateTimeOffset.UtcNow.AddMinutes(-5)));

        var revision = (await repository.GetByIdAsync(seed.LibraryId))!.MaterializationRevision;
        (await repository.WithdrawProjectionAsync(database.Context,
            seed.LibraryId, "bad reference", revision)).ShouldBeTrue();
        var repaired = (await repository.GetByIdAsync(seed.LibraryId))!;
        repaired.UpdateConfiguration("Repaired", new LibraryConfiguration(), now: DateTimeOffset.UnixEpoch);
        await CommitLibraryUpdateAsync(repository, database.Context, repaired);

        // The wiped snapshot is no longer published, so repaired rules must not serve an empty library.
        var library = (await repository.GetByIdAsync(seed.LibraryId))!;
        library.LastMaterializedAt.ShouldBeNull();
        library.AwaitsFirstPublication.ShouldBeTrue();
        (await repository.GetCurrentContextAsync(new ConsumerLibraryScope(seed.UserId)))
            .ShouldBeOfType<ConsumerLibraryReadResult<ConsumerLibraryContextReadModel>.LibraryUnavailable>();
    }

    [Fact]
    public async Task StageMaterializedProjectionsAndMarkConfigurationInvalidAsync_RepairedConfiguration_PreservesProjection()
    {
        await using var database = await CreateDatabaseAsync();
        var repository = new LibraryRepository(database.Context);
        var seed = await SeedDivergentProjectionDataAsync(database.Context);
        var original = (await repository.GetByIdAsync(seed.LibraryId))!;
        var capturedRevision = original.MaterializationRevision;
        original.UpdateConfiguration("Repaired", new LibraryConfiguration(), now: DateTimeOffset.UnixEpoch);
        await CommitLibraryUpdateAsync(repository, database.Context, original);
        var repaired = (await repository.GetByIdAsync(seed.LibraryId))!;
        int titleCount = await database.Context.MaterializedLibraryTitles.CountAsync();
        int releaseCount = await database.Context.MaterializedLibraryReleases.CountAsync();
        titleCount.ShouldBeGreaterThan(0);
        releaseCount.ShouldBeGreaterThan(0);
        bool invalidated = await repository.WithdrawProjectionAsync(database.Context,
            seed.LibraryId, "stale validation failure", capturedRevision);
        invalidated.ShouldBeFalse();
        var after = (await repository.GetByIdAsync(seed.LibraryId))!;
        after.Name.ShouldBe("Repaired");
        after.HasValidConfiguration.ShouldBeTrue();
        after.NeedsMaterialization.ShouldBeTrue();
        after.MaterializationRevision.ShouldBe(repaired.MaterializationRevision);
        after.ItemCount.ShouldBe(repaired.ItemCount);
        (await database.Context.MaterializedLibraryTitles.CountAsync()).ShouldBe(titleCount);
        (await database.Context.MaterializedLibraryReleases.CountAsync()).ShouldBe(releaseCount);
    }

    [Fact]
    public async Task ReplaceMaterializedProjectionsAsync_ExistingRows_ReplacesAvailabilityProjection()
    {
        await using var database = await CreateDatabaseAsync();
        var now = DateTimeOffset.UtcNow;
        var userId = Guid.NewGuid();
        var repository = new LibraryRepository(database.Context);
        var library = await AddLibraryAsync(repository, database.Context, "Projection");

        database.Context.Platforms.Add(new PlatformEntity
        {
            Id = 1,
            Name = "Nintendo Entertainment System",
            Ownership = Romd.Domain.ReferenceData.ReferenceOwnership.Romd,
            BuiltInVersion = 1,
            BaseName = "Nintendo Entertainment System",
            BaseCompactLabel = "Nintendo Entertainment System",
            CanonicalKey = "nes",
            ShortName = "nes",
            Manufacturer = "Nintendo",
            CreatedAt = now,
            CreatedByUserId = userId
        });

        database.Context.Files.Add(new FileEntityPersistence
        {
            Id = 1,
            Sha256 = NewSha256(1),
            Size = 1,
            SizeOnDisk = 1,
            IsCompressed = false,
            CreatedAt = now,
            CreatedByUserId = userId
        });

        database.Context.DatFiles.Add(new DatFileEntity
        {
            Source = new DatSourceEntity
            {
                CatalogSource = new CatalogSourceEntity { Id = 1, Kind = "Dat", Status = "Active" }
            },
            Id = 1,
            Name = "No-Intro NES",
            Description = "NES DAT",
            Type = "NoIntro",
            PlatformId = 1,
            OriginalFilename = "nes.dat",
            FileId = 1,
            GameCount = 2,
            CreatedAt = now,
            CreatedByUserId = userId
        });

        database.Context.Titles.Add(new TitleEntity
        {
            Id = 1,
            PlatformId = 1,
            Name = "Mega Game",
            NormalizedName = "mega game",
            Genre = "Action",
            EnrichmentStatus = "None",
            CreatedAt = now,
            CreatedByUserId = userId
        });

        database.Context.SourceEntries.AddRange(
            NewSourceEntry(1, 1, "Mega Game (USA)", 1, now, userId),
            NewSourceEntry(2, 1, "Mega Game (Europe)", 1, now, userId));
        database.Context.DatGames.AddRange(
            new DatGameEntity
            {
                Id = 1,
                DatFileId = 1,
                SourceEntryId = 1,
                Name = "Mega Game (USA)",
                CreatedAt = now,
                CreatedByUserId = userId
            },
            new DatGameEntity
            {
                Id = 2,
                DatFileId = 1,
                SourceEntryId = 2,
                Name = "Mega Game (Europe)",
                CreatedAt = now,
                CreatedByUserId = userId
            });

        await database.Context.SaveChangesAsync();

        var projection = new MaterializedLibraryProjection(
            [
                new MaterializedLibraryTitle(
                    library.Id,
                    TitleId: 1,
                    PlatformId: 1,
                    Genre: "Action",
                    IsVisible: true,
                    IsOwned: true,
                    IsPlayable: true,
                    EligibleReleaseCount: 2,
                    PlayableReleaseCount: 1,
                    ExposedReleaseCount: 2,
                    LibraryTitleAvailability.Playable)
            ],
            [
                new MaterializedLibraryRelease(
                    library.Id,
                    TitleId: 1,
                    CatalogReleaseId: 1,
                    DatGameId: 1,
                    DatFileId: 1,
                    PlatformId: 1,
                    IsEligible: true,
                    IsComplete: true,
                    IsOwned: true,
                    IsPlayable: true,
                    IsBlocked: false,
                    BlockReason: null,
                    IsExposed: true,
                    ExposureReason: "ExposedDefault"),
                new MaterializedLibraryRelease(
                    library.Id,
                    TitleId: 1,
                    CatalogReleaseId: 2,
                    DatGameId: 2,
                    DatFileId: 1,
                    PlatformId: 1,
                    IsEligible: true,
                    IsComplete: false,
                    IsOwned: false,
                    IsPlayable: false,
                    IsBlocked: false,
                    BlockReason: null,
                    IsExposed: true,
                    ExposureReason: "ExposedDefault")
            ]);

        bool firstActivation = await repository.PublishProjectionAsync(database.Context,
            library.Id, projection, 1, library.MaterializationRevision);
        firstActivation.ShouldBeTrue();

        database.Context.ChangeTracker.Clear();
        var titleRow = await database.Context.MaterializedLibraryTitles.SingleAsync();
        var releaseRows = await database.Context.MaterializedLibraryReleases
            .OrderBy(release => release.DatGameId)
            .ToListAsync();

        titleRow.LibraryId.ShouldBe(library.Id);
        titleRow.TitleId.ShouldBe(1);
        titleRow.Availability.ShouldBe(LibraryTitleAvailability.Playable.ToString());
        titleRow.EligibleReleaseCount.ShouldBe(2);
        titleRow.PlayableReleaseCount.ShouldBe(1);
        releaseRows.Count.ShouldBe(2);
        releaseRows[0].IsPlayable.ShouldBeTrue();
        releaseRows[1].IsOwned.ShouldBeFalse();

        Guid nextToken = await database.Context.Libraries
            .Where(candidate => candidate.Id == library.Id)
            .Select(candidate => candidate.MaterializationRevision)
            .SingleAsync();
        bool secondActivation = await repository.PublishProjectionAsync(database.Context,
            library.Id,
            new MaterializedLibraryProjection([], []),
            0,
            nextToken);
        secondActivation.ShouldBeTrue();

        (await database.Context.Libraries
                .Where(candidate => candidate.Id == library.Id)
                .Select(candidate => candidate.MaterializationGeneration)
                .SingleAsync())
            .ShouldBe(2);

        database.Context.ChangeTracker.Clear();
        (await database.Context.MaterializedLibraryTitles.CountAsync()).ShouldBe(0);
        (await database.Context.MaterializedLibraryReleases.CountAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task ReplaceMaterializedProjectionsAsync_ExistingRows_AppliesDelta()
    {
        await using var database = await CreateDatabaseAsync();
        var now = DateTimeOffset.UtcNow;
        var userId = Guid.NewGuid();
        var repository = new LibraryRepository(database.Context);
        var library = await AddLibraryAsync(repository, database.Context, "Delta");
        var otherLibrary = await AddLibraryAsync(repository, database.Context, "Other");

        database.Context.Platforms.Add(NewPlatform(1, "Nintendo Entertainment System", "nes", now, userId));
        database.Context.Files.Add(NewFile(1, NewSha256(1), now, userId));
        database.Context.DatFiles.Add(NewDatFile(1, 1, 1, now, userId));
        database.Context.Titles.AddRange(
            NewTitle(1, 1, "Keep", "Action", now, userId),
            NewTitle(2, 1, "Remove", "Puzzle", now, userId),
            NewTitle(3, 1, "Add", "RPG", now, userId));
        database.Context.SourceEntries.AddRange(
            NewSourceEntry(1, 1, "Keep (USA)", 1, now, userId),
            NewSourceEntry(2, 1, "Remove (USA)", 1, now, userId),
            NewSourceEntry(3, 1, "Add (USA)", 1, now, userId));
        database.Context.DatGames.AddRange(
            NewDatGame(1, 1, "Keep (USA)", now, userId),
            NewDatGame(2, 1, "Remove (USA)", now, userId),
            NewDatGame(3, 1, "Add (USA)", now, userId));

        var retainedTitle = NewMaterializedTitle(
            library.Id,
            titleId: 1,
            platformId: 1,
            genre: "Action",
            isOwned: true,
            isPlayable: false);
        var removedTitle = NewMaterializedTitle(
            library.Id,
            titleId: 2,
            platformId: 1,
            genre: "Puzzle",
            isOwned: false,
            isPlayable: false);
        var sentinelTitle = NewMaterializedTitle(
            otherLibrary.Id,
            titleId: 1,
            platformId: 1,
            genre: "Action",
            isOwned: true,
            isPlayable: true);

        var retainedRelease = NewMaterializedRelease(
            library.Id,
            titleId: 1,
            datGameId: 1,
            datFileId: 1,
            platformId: 1,
            isOwned: true,
            isComplete: false,
            isPlayable: false);
        var removedRelease = NewMaterializedRelease(
            library.Id,
            titleId: 2,
            datGameId: 2,
            datFileId: 1,
            platformId: 1,
            isOwned: false,
            isComplete: false,
            isPlayable: false);
        var sentinelRelease = NewMaterializedRelease(
            otherLibrary.Id,
            titleId: 1,
            datGameId: 1,
            datFileId: 1,
            platformId: 1,
            isOwned: true,
            isComplete: true,
            isPlayable: true);

        database.Context.MaterializedLibraryTitles.AddRange(retainedTitle, removedTitle, sentinelTitle);
        database.Context.MaterializedLibraryReleases.AddRange(retainedRelease, removedRelease, sentinelRelease);
        await database.Context.SaveChangesAsync();

        int retainedTitleRowId = retainedTitle.Id;
        int removedTitleRowId = removedTitle.Id;
        int sentinelTitleRowId = sentinelTitle.Id;
        int retainedReleaseRowId = retainedRelease.Id;
        int removedReleaseRowId = removedRelease.Id;
        int sentinelReleaseRowId = sentinelRelease.Id;

        var projection = new MaterializedLibraryProjection(
            [
                new MaterializedLibraryTitle(
                    library.Id,
                    TitleId: 1,
                    PlatformId: 1,
                    Genre: "Platformer",
                    IsVisible: true,
                    IsOwned: true,
                    IsPlayable: true,
                    EligibleReleaseCount: 1,
                    PlayableReleaseCount: 1,
                    ExposedReleaseCount: 1,
                    LibraryTitleAvailability.Playable),
                new MaterializedLibraryTitle(
                    library.Id,
                    TitleId: 3,
                    PlatformId: 1,
                    Genre: "RPG",
                    IsVisible: true,
                    IsOwned: false,
                    IsPlayable: false,
                    EligibleReleaseCount: 1,
                    PlayableReleaseCount: 0,
                    ExposedReleaseCount: 1,
                    LibraryTitleAvailability.MetadataOnly)
            ],
            [
                new MaterializedLibraryRelease(
                    library.Id,
                    TitleId: 1,
                    CatalogReleaseId: 1,
                    DatGameId: 1,
                    DatFileId: 1,
                    PlatformId: 1,
                    IsEligible: true,
                    IsComplete: true,
                    IsOwned: true,
                    IsPlayable: true,
                    IsBlocked: false,
                    BlockReason: null,
                    IsExposed: true,
                    ExposureReason: "ExposedDefault"),
                new MaterializedLibraryRelease(
                    library.Id,
                    TitleId: 3,
                    CatalogReleaseId: 3,
                    DatGameId: 3,
                    DatFileId: 1,
                    PlatformId: 1,
                    IsEligible: true,
                    IsComplete: false,
                    IsOwned: false,
                    IsPlayable: false,
                    IsBlocked: false,
                    BlockReason: null,
                    IsExposed: true,
                    ExposureReason: "ExposedDefault")
            ]);

        bool activated = await repository.PublishProjectionAsync(database.Context,
            library.Id, projection, 1, library.MaterializationRevision);
        activated.ShouldBeTrue();

        database.Context.ChangeTracker.Clear();
        var titleRows = await database.Context.MaterializedLibraryTitles
            .Where(title => title.LibraryId == library.Id)
            .OrderBy(title => title.TitleId)
            .ToListAsync();
        var releaseRows = await database.Context.MaterializedLibraryReleases
            .Where(release => release.LibraryId == library.Id)
            .OrderBy(release => release.DatGameId)
            .ToListAsync();

        titleRows.Select(title => title.TitleId).ShouldBe([1, 3]);
        releaseRows.Select(release => release.DatGameId).ShouldBe([1, 3]);

        var updatedTitle = titleRows[0];
        updatedTitle.Id.ShouldBe(retainedTitleRowId);
        updatedTitle.Genre.ShouldBe("Platformer");
        updatedTitle.IsPlayable.ShouldBeTrue();
        updatedTitle.PlayableReleaseCount.ShouldBe(1);
        updatedTitle.Availability.ShouldBe(LibraryTitleAvailability.Playable.ToString());
        titleRows[1].Id.ShouldNotBe(removedTitleRowId);

        var updatedRelease = releaseRows[0];
        updatedRelease.Id.ShouldBe(retainedReleaseRowId);
        updatedRelease.IsComplete.ShouldBeTrue();
        updatedRelease.IsPlayable.ShouldBeTrue();
        releaseRows[1].Id.ShouldNotBe(removedReleaseRowId);

        (await database.Context.MaterializedLibraryTitles.AnyAsync(title => title.Id == removedTitleRowId))
            .ShouldBeFalse();
        (await database.Context.MaterializedLibraryReleases.AnyAsync(release => release.Id == removedReleaseRowId))
            .ShouldBeFalse();
        (await database.Context.MaterializedLibraryTitles.AnyAsync(title => title.Id == sentinelTitleRowId))
            .ShouldBeTrue();
        (await database.Context.MaterializedLibraryReleases.AnyAsync(release => release.Id == sentinelReleaseRowId))
            .ShouldBeTrue();
    }

    [Fact]
    public async Task GetTitleReleaseDiagnosticsAsync_MapsProjectionFields()
    {
        await using var database = await CreateDatabaseAsync();
        var repository = new LibraryRepository(database.Context);
        var now = DateTimeOffset.UtcNow;
        var userId = Guid.NewGuid();
        var library = await AddLibraryAsync(repository, database.Context, "Diagnostics");

        database.Context.Platforms.Add(NewPlatform(1, "Super Nintendo", "snes", now, userId));
        database.Context.Files.Add(NewFile(1, NewSha256(1), now, userId));
        database.Context.DatFiles.Add(NewDatFile(1, 1, 1, now, userId));
        database.Context.Titles.Add(NewTitle(1, 1, "Chrono Trigger", "RPG", now, userId));
        database.Context.SourceEntries.AddRange(
            NewSourceEntry(1, 1, "Chrono Trigger (USA)", 1, now, userId),
            NewSourceEntry(2, 1, "Chrono Trigger (Europe)", 1, now, userId));
        database.Context.DatGames.AddRange(
            NewDatGame(1, 1, "Chrono Trigger (USA)", now, userId),
            NewDatGame(2, 1, "Chrono Trigger (Europe)", now, userId));
        database.Context.CatalogReleases.Add(new CatalogReleaseEntity
        {
            Id = 1,
            PlatformId = 1,
            CatalogTitleId = 1,
            Name = "Chrono Trigger",
            Fingerprint = "diagnostic-test",
            CreatedAt = now,
            UpdatedAt = now
        });
        // Diagnostics derive the default VFS layout's path verdict from current catalog files.
        database.Context.CatalogReleaseFiles.AddRange(
            new CatalogReleaseFileEntity { CatalogReleaseId = 1, FileFingerprint = "track-lower", Name = "track.bin", Size = 1 },
            new CatalogReleaseFileEntity { CatalogReleaseId = 1, FileFingerprint = "track-upper", Name = "TRACK.BIN", Size = 1 });
        database.Context.MaterializedLibraryReleases.AddRange(
            new MaterializedLibraryReleaseEntity
            {
                LibraryId = library.Id,
                TitleId = 1,
                DatGameId = 1,
                CatalogReleaseId = 1,
                DatFileId = 1,
                PlatformId = 1,
                IsEligible = true,
                IsComplete = true,
                IsOwned = true,
                IsPlayable = true,
                IsBlocked = false,
                BlockReason = null,
                IsExposed = true,
                ExposureReason = "ExposedDefault"
            },
            new MaterializedLibraryReleaseEntity
            {
                LibraryId = library.Id,
                TitleId = 1,
                DatGameId = 2,
                DatFileId = 1,
                PlatformId = 1,
                IsEligible = false,
                IsComplete = true,
                IsOwned = true,
                IsPlayable = false,
                IsBlocked = true,
                BlockReason = "ExcludedDat",
                IsExposed = false,
                ExposureReason = "ExcludedDat"
            });
        await database.Context.SaveChangesAsync();

        var releases = await repository.GetTitleReleaseDiagnosticsAsync(library.Id, 1);

        releases.Count.ShouldBe(2);
        releases[0].ReleaseId.ShouldBe(1);
        releases[0].DatFileId.ShouldBe(1);
        releases[0].Name.ShouldBe("Chrono Trigger (USA)");
        releases[0].IsEligible.ShouldBeTrue();
        releases[0].IsBlocked.ShouldBeFalse();
        releases[0].BlockReason.ShouldBeNull();
        releases[0].IsExposed.ShouldBeTrue();
        releases[0].ExposureReason.ShouldBe("ExposedDefault");
        releases[0].VfsPathError.ShouldBe("Original paths conflict on case-insensitive or Unicode-normalizing filesystems.");
        releases[1].ReleaseId.ShouldBe(2);
        releases[1].IsEligible.ShouldBeFalse();
        releases[1].IsBlocked.ShouldBeTrue();
        releases[1].BlockReason.ShouldBe("ExcludedDat");
        releases[1].IsExposed.ShouldBeFalse();
        releases[1].ExposureReason.ShouldBe("ExcludedDat");
        releases[1].VfsPathError.ShouldBeNull();
    }

    [Fact]
    public async Task GetContentCountsAndFacetsAsync_ProjectionBackedResults_MatchExpectedSemantics()
    {
        await using var database = await CreateDatabaseAsync();
        var repository = new LibraryRepository(database.Context);
        await SeedAggregateParitySourceDataAsync(database.Context);
        await MaterializeValidLibrariesAsync(database.Context);
        await AssertAllMaterializedTitlesAreVisibleAsync(database.Context);
        await AssertUnknownRatingTitleIsHiddenAsync(database.Context);
        await AssertReleaseExposureProjectionAsync(database.Context);
        await AssertMetadataShelfProjectionAsync(database.Context);

        var libraryIds = await database.Context.Libraries
            .OrderBy(library => library.Id)
            .Select(library => library.Id)
            .ToListAsync();

        foreach (int libraryId in libraryIds)
        {
            var expectedCounts = await GetTitleContentCountsAsync(database.Context, libraryId);
            var expectedPlatformFacets = await GetTitlePlatformFacetsAsync(database.Context, libraryId);
            var expectedGenreFacets = await GetTitleGenreFacetsAsync(database.Context, libraryId);
            var expectedCollectionFacets = await GetTitleCollectionFacetsAsync(database.Context, libraryId);

            (await repository.GetContentCountsAsync(libraryId)).ShouldBe(expectedCounts);
            (await repository.GetPlatformFacetsAsync(libraryId)).ShouldBe(expectedPlatformFacets);
            (await repository.GetGenreFacetsAsync(libraryId)).ShouldBe(expectedGenreFacets);
            (await repository.GetCollectionFacetsAsync(libraryId)).ShouldBe(expectedCollectionFacets);
        }
    }

    [Fact]
    public async Task GetContentCountsAndFacetsAsync_TitleProjectionRows_ReadsTitleProjection()
    {
        await using var database = await CreateDatabaseAsync();
        var repository = new LibraryRepository(database.Context);
        var seed = await SeedDivergentProjectionDataAsync(database.Context);

        var counts = await repository.GetContentCountsAsync(seed.LibraryId);
        var platformFacets = await repository.GetPlatformFacetsAsync(seed.LibraryId);
        var genreFacets = await repository.GetGenreFacetsAsync(seed.LibraryId);
        var collectionFacets = await repository.GetCollectionFacetsAsync(seed.LibraryId);

        counts.ShouldBe(new LibraryContentCounts(1, 1, 1));
        platformFacets.ShouldBe([new PlatformFacet(seed.TitleProjectionPlatformId, "Super Nintendo", 1)]);
        genreFacets.ShouldBe([new GenreFacet("RPG", 1)]);
        collectionFacets.ShouldBe([new CollectionFacet(seed.TitleProjectionCollectionId, "Title Projection", 1, 1)]);
    }

    [Fact]
    public async Task GetCurrentContextAsync_TitleProjectionRows_ReadsTitleProjection()
    {
        await using var database = await CreateDatabaseAsync();
        var repository = new LibraryRepository(database.Context);
        var seed = await SeedDivergentProjectionDataAsync(database.Context);

        var result = await repository.GetCurrentContextAsync(new ConsumerLibraryScope(seed.UserId));
        var found = result.ShouldBeOfType<ConsumerLibraryReadResult<ConsumerLibraryContextReadModel>.Found>();
        found.LibraryId.ShouldBe(seed.LibraryId);
        var context = found.Value;
        context.Name.ShouldBe("Projection Source");
        context.Counts.OwnedTitleCount.ShouldBe(1);
        context.Counts.AvailableTitleCount.ShouldBe(1);
        context.Counts.CollectionCount.ShouldBe(1);
        context.Platforms.Single().SystemKey.ShouldBe("snes");
        context.Platforms.Single().PlatformName.ShouldBe("Super Nintendo");
        context.Platforms.Single().Count.ShouldBe(1);
        context.Genres.Single().Genre.ShouldBe("RPG");
        context.Genres.Single().Count.ShouldBe(1);
        context.FeaturedCollections.Single().CollectionId.ShouldBe(seed.TitleProjectionCollectionId);
        context.FeaturedCollections.Single().CollectionName.ShouldBe("Title Projection");
        context.FeaturedCollections.Single().MatchingCount.ShouldBe(1);
    }

    [Fact]
    public async Task GetCurrentContextAsync_InvalidLibrary_ReturnsNoContext()
    {
        await using var database = await CreateDatabaseAsync();
        var repository = new LibraryRepository(database.Context);
        var seed = await SeedDivergentProjectionDataAsync(database.Context);

        await database.Context.Libraries
            .Where(library => library.Id == seed.LibraryId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(library => library.ConfigurationState, LibraryConfigurationState.Invalid.ToString())
                .SetProperty(library => library.ConfigurationError, "invalid"));

        var context = await repository.GetCurrentContextAsync(new ConsumerLibraryScope(seed.UserId));

        context.ShouldBeOfType<ConsumerLibraryReadResult<ConsumerLibraryContextReadModel>.LibraryUnavailable>();
    }

    [Fact]
    public async Task GetCurrentContextAsync_MissingAssignmentOrUnknownUser_ReturnsNoContext()
    {
        await using var database = await CreateDatabaseAsync();
        var repository = new LibraryRepository(database.Context);
        var userId = Guid.NewGuid();
        database.Context.Users.Add(new RomdUser
        {
            Id = userId,
            UserName = "unassigned-user",
            NormalizedUserName = "UNASSIGNED-USER",
            Email = "unassigned@example.test",
            NormalizedEmail = "UNASSIGNED@EXAMPLE.TEST",
            LibraryId = null,
            CreatedAt = DateTimeOffset.UtcNow
        });
        await database.Context.SaveChangesAsync();

        var unassigned = await repository.GetCurrentContextAsync(new ConsumerLibraryScope(userId));
        var unknown = await repository.GetCurrentContextAsync(new ConsumerLibraryScope(Guid.NewGuid()));

        unassigned.ShouldBeOfType<ConsumerLibraryReadResult<ConsumerLibraryContextReadModel>.LibraryUnavailable>();
        unknown.ShouldBeOfType<ConsumerLibraryReadResult<ConsumerLibraryContextReadModel>.LibraryUnavailable>();
    }

    [Fact]
    public async Task GetCurrentContextAsync_NeverPublishedLibrary_ReturnsNoContext()
    {
        await using var database = await CreateDatabaseAsync();
        var repository = new LibraryRepository(database.Context);
        var seed = await SeedDivergentProjectionDataAsync(database.Context);

        await database.Context.Libraries
            .Where(library => library.Id == seed.LibraryId)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(library => library.NeedsMaterialization, true)
                .SetProperty(library => library.LastMaterializedAt, (DateTimeOffset?)null));

        var context = await repository.GetCurrentContextAsync(new ConsumerLibraryScope(seed.UserId));

        context.ShouldBeOfType<ConsumerLibraryReadResult<ConsumerLibraryContextReadModel>.LibraryUnavailable>();
    }

    [Fact]
    public async Task GetCurrentContextAsync_PublishedLibraryWithPendingChanges_ServesPublishedSnapshot()
    {
        await using var database = await CreateDatabaseAsync();
        var repository = new LibraryRepository(database.Context);
        var seed = await SeedDivergentProjectionDataAsync(database.Context);
        var scope = new ConsumerLibraryScope(seed.UserId);
        var published = await repository.GetCurrentContextAsync(scope);

        await database.Context.Libraries
            .Where(library => library.Id == seed.LibraryId)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(library => library.NeedsMaterialization, true)
                .SetProperty(library => library.LastMaterializedAt, DateTimeOffset.UtcNow.AddMinutes(-5)));

        var pending = await repository.GetCurrentContextAsync(scope);

        var found = pending.ShouldBeOfType<ConsumerLibraryReadResult<ConsumerLibraryContextReadModel>.Found>();
        found.Value.ShouldBeEquivalentTo(
            published.ShouldBeOfType<ConsumerLibraryReadResult<ConsumerLibraryContextReadModel>.Found>().Value);
    }

    [Fact]
    public async Task GetTitleAsync_ReleaseMovedToAnotherTitleBeforeRefresh_IsNotOfferedUnderTheOldTitle()
    {
        const int catalogReleaseId = 900;
        const int destinationTitleId = 3;
        await using var database = await CreateDatabaseAsync();
        var seed = await SeedDivergentProjectionDataAsync(database.Context);
        var now = DateTimeOffset.UtcNow;
        database.Context.Titles.Add(NewTitle(destinationTitleId, seed.TitleProjectionPlatformId, "Chrono Trigger Destination", "RPG", now, seed.UserId));
        database.Context.CatalogReleases.Add(new CatalogReleaseEntity
        {
            Id = catalogReleaseId,
            PlatformId = seed.TitleProjectionPlatformId,
            CatalogTitleId = seed.TitleProjectionTitleId,
            Fingerprint = "sha1:900",
            Name = "Chrono Trigger (USA)",
            CreatedAt = now,
            UpdatedAt = now
        });
        await database.Context.SaveChangesAsync();
        await database.Context.MaterializedLibraryReleases
            .Where(release => release.LibraryId == seed.LibraryId && release.DatGameId == seed.TitleProjectionDatGameId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(release => release.CatalogReleaseId, catalogReleaseId));
        var browse = new ConsumerBrowseRepository(database.Context, new ConsumerReleaseSelector());
        var scope = new ConsumerLibraryScope(seed.UserId);
        (await browse.GetTitleAsync(scope, seed.TitleProjectionTitleId, ConsumerReleasePreference.Default))
            .ShouldBeOfType<ConsumerLibraryReadResult<ConsumerTitleDetailData>.Found>()
            .Value.Releases.Select(release => release.Id).ShouldBe([catalogReleaseId]);

        await database.Context.CatalogReleases
            .Where(release => release.Id == catalogReleaseId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(release => release.CatalogTitleId, destinationTitleId));

        var detail = await browse.GetTitleAsync(scope, seed.TitleProjectionTitleId, ConsumerReleasePreference.Default);

        detail.ShouldBeOfType<ConsumerLibraryReadResult<ConsumerTitleDetailData>.Found>()
            .Value.Releases.ShouldBeEmpty();
    }

    [Fact]
    public async Task GetCurrentContextAsync_AssignmentChangesAfterResolution_ReturnsOneCoherentSnapshot()
    {
        using var database = PostgreSqlTestDatabase.Create();

        try
        {
            var setupOptions = new DbContextOptionsBuilder<RomdDbContext>()
                .UseNpgsql(database.ConnectionString)
                .UseOpenIddict()
                .Options;
            Guid userId = Guid.NewGuid();
            int firstLibraryId;
            int secondLibraryId;

            await using (var setup = new RomdDbContext(setupOptions))
            {
                await setup.Database.MigrateAsync();
                var now = DateTimeOffset.UtcNow;
                var firstLibrary = LibraryEntity.FromDomain(Library.CreateNew("First Library", new LibraryConfiguration(), now: DateTimeOffset.UnixEpoch));
                var secondLibrary = LibraryEntity.FromDomain(Library.CreateNew("Second Library", new LibraryConfiguration(), now: DateTimeOffset.UnixEpoch));
                firstLibrary.NeedsMaterialization = false;
                secondLibrary.NeedsMaterialization = false;
                setup.Libraries.AddRange(firstLibrary, secondLibrary);
                setup.Platforms.AddRange(
                    NewPlatform(101, "First Platform", "first", now, userId),
                    NewPlatform(202, "Second Platform", "second", now, userId));
                setup.Titles.AddRange(
                    NewTitle(1001, 101, "First Title", "First", now, userId),
                    NewTitle(2002, 202, "Second Title", "Second", now, userId));
                await setup.PublishOriginalRomsAsync();
                await setup.SaveChangesAsync();

                firstLibraryId = firstLibrary.Id;
                secondLibraryId = secondLibrary.Id;
                setup.Users.Add(new RomdUser
                {
                    Id = userId,
                    UserName = "coherence-user",
                    NormalizedUserName = "COHERENCE-USER",
                    Email = "coherence@example.test",
                    NormalizedEmail = "COHERENCE@EXAMPLE.TEST",
                    LibraryId = firstLibraryId,
                    CreatedAt = now
                });
                setup.MaterializedLibraryTitles.AddRange(
                    NewMaterializedTitle(firstLibraryId, 1001, 101, "First", isOwned: true, isPlayable: true),
                    NewMaterializedTitle(secondLibraryId, 2002, 202, "Second", isOwned: true, isPlayable: true));
                await setup.PublishOriginalRomsAsync();
                await setup.SaveChangesAsync();
            }

            var interceptor = new PauseAfterLiveAssignmentResolutionInterceptor();
            var readOptions = new DbContextOptionsBuilder<RomdDbContext>()
                .UseNpgsql(database.ConnectionString)
                .UseOpenIddict()
                .AddInterceptors(interceptor)
                .Options;
            await using var readContext = new RomdDbContext(readOptions);
            var repository = new LibraryRepository(readContext);
            Task<ConsumerLibraryReadResult<ConsumerLibraryContextReadModel>> readTask = repository.GetCurrentContextAsync(
                new ConsumerLibraryScope(userId));

            await interceptor.WaitUntilResolvedAsync();
            try
            {
                await using var writer = new RomdDbContext(setupOptions);
                int updated = await writer.Users
                    .Where(user => user.Id == userId)
                    .ExecuteUpdateAsync(setters => setters.SetProperty(
                        user => user.LibraryId,
                        secondLibraryId));
                updated.ShouldBe(1);
            }
            finally
            {
                interceptor.Resume();
            }

            var concurrentRead = await readTask.WaitAsync(TimeSpan.FromSeconds(5));
            var concurrentFound = concurrentRead
                .ShouldBeOfType<ConsumerLibraryReadResult<ConsumerLibraryContextReadModel>.Found>();
            concurrentFound.LibraryId.ShouldBe(firstLibraryId);
            concurrentFound.Value.Name.ShouldBe("First Library");
            concurrentFound.Value.Platforms.Select(platform => platform.PlatformName).ShouldBe(["First Platform"]);

            await using var nextContext = new RomdDbContext(setupOptions);
            var nextRead = await new LibraryRepository(nextContext)
                .GetCurrentContextAsync(new ConsumerLibraryScope(userId));
            var nextFound = nextRead.ShouldBeOfType<ConsumerLibraryReadResult<ConsumerLibraryContextReadModel>.Found>();
            nextFound.LibraryId.ShouldBe(secondLibraryId);
            nextFound.Value.Name.ShouldBe("Second Library");
            nextFound.Value.Platforms.Select(platform => platform.PlatformName).ShouldBe(["Second Platform"]);
        }
        finally
        {
        }
    }

    [Fact]
    public async Task GetContentCountsAsync_MultiReleaseAndVisibleOnlyRows_UsesAccessibleTitleCount()
    {
        await using var database = await CreateDatabaseAsync();
        var repository = new LibraryRepository(database.Context);
        var seed = await SeedTitleCardinalityDecisionDataAsync(database.Context);

        var counts = await repository.GetContentCountsAsync(seed.LibraryId);
        var expectedCounts = new LibraryContentCounts(1, 1, 1);

        counts.ShouldBe(expectedCounts);
    }

    private static async Task AttachSeedCollectionsAsync(RomdDbContext context)
    {
        var libraryIds = await context.Libraries.Select(l => l.Id).ToListAsync();
        var collectionIds = await context.Collections.Select(c => c.Id).ToListAsync();
        foreach (int libraryId in libraryIds)
            foreach (int collectionId in collectionIds)
                if (!await context.LibraryCollections.AnyAsync(a => a.LibraryId == libraryId && a.CollectionId == collectionId))
                    context.LibraryCollections.Add(new LibraryCollectionEntity { LibraryId = libraryId, CollectionId = collectionId, IsFeatured = true });
        await context.PublishOriginalRomsAsync();
        await context.SaveChangesAsync();
    }

    private static async Task<TestDatabase> CreateDatabaseAsync(params IInterceptor[] interceptors)
    {
        var connection = PostgreSqlTestDatabase.Create();

        var options = new DbContextOptionsBuilder<RomdDbContext>()
            .UseNpgsql(connection.ConnectionString)
            .UseOpenIddict()
            .AddInterceptors(interceptors)
            .Options;

        var context = new RomdDbContext(options);
        await context.Database.MigrateAsync();

        return new TestDatabase(connection, context);
    }

    private static async Task CommitLibraryUpdateAsync(
        LibraryRepository repository,
        RomdDbContext context,
        Library library)
    {
        await using var transaction = await new EfUnitOfWork(context).BeginTransactionAsync();
        await repository.UpdateStagedAsync(library);
        await transaction.CommitAsync();
    }

    private static async Task<Library> AddLibraryAsync(
        LibraryRepository repository,
        RomdDbContext context,
        string name)
    {
        await repository.AddStagedAsync(Library.CreateNew(name, new LibraryConfiguration(), now: DateTimeOffset.UnixEpoch));
        await context.PublishOriginalRomsAsync();
        await context.SaveChangesAsync();
        return await repository.GetByNameAsync(name)
            ?? throw new InvalidOperationException("Seeded library was not found.");
    }

    private static async Task SeedAggregateParitySourceDataAsync(RomdDbContext context)
    {
        var now = DateTimeOffset.UtcNow;
        var userId = Guid.NewGuid();

        context.Libraries.AddRange(
            LibraryEntity.FromDomain(Library.CreateNew(
                "Living Room",
                new LibraryConfiguration { IncludeUntrackedTitles = true, AllowedPlatformIds = [1] }, now: DateTimeOffset.UnixEpoch)),
            LibraryEntity.FromDomain(Library.CreateNew(
                "Metadata Shelf",
                new LibraryConfiguration { IncludeUntrackedTitles = true, AllowedPlatformIds = [2], ShowMissingGames = true }, now: DateTimeOffset.UnixEpoch)),
            new LibraryEntity
            {
                Name = "Invalid",
                ConfigurationJson = "{}",
                ConfigurationState = LibraryConfigurationState.Invalid.ToString(),
                ConfigurationError = "Invalid test configuration.",
                NeedsMaterialization = false,
                CreatedAt = now
            });

        context.Platforms.AddRange(
            NewPlatform(1, "Super Nintendo", "snes", now, userId),
            NewPlatform(2, "Nintendo Entertainment System", "nes", now, userId));

        context.Files.AddRange(
            NewFile(1, NewSha256(1), now, userId),
            NewFile(2, NewSha256(2), now, userId),
            NewFile(3, NewSha256(3), now, userId));
        context.StageOriginalRoms(
            NewRomFile(1, "chrono-trigger.sfc", 2, NewSha1(1), NewMd5(1), now, userId),
            NewRomFile(2, "partial-quest-a.sfc", 3, NewSha1(2), NewMd5(2), now, userId));
        context.DatFiles.AddRange(
            NewDatFile(1, 1, 1, now, userId),
            NewDatFile(2, 1, 2, now, userId));
        context.Titles.AddRange(
            NewTitle(1, 1, "Chrono Trigger", "RPG", now, userId),
            NewTitle(2, 1, "Partial Quest", "RPG", now, userId),
            NewTitle(3, 2, "Missing Quest", "Adventure", now, userId),
            NewTitle(4, 1, "Unknown Rating", "Puzzle", now, userId));
        context.TitleContentRatings.AddRange(
            NewTitleContentRating(1, now, userId),
            NewTitleContentRating(2, now, userId),
            NewTitleContentRating(3, now, userId));
        context.SourceEntries.AddRange(
            NewSourceEntry(1, 1, "Chrono Trigger (USA)", 1, now, userId),
            NewSourceEntry(2, 1, "Chrono Trigger (Europe)", 1, now, userId),
            NewSourceEntry(3, 1, "Partial Quest (USA)", 1, now, userId),
            NewSourceEntry(4, 2, "Missing Quest (USA)", 2, now, userId),
            NewSourceEntry(5, 1, "Unknown Rating (USA)", 1, now, userId));
        context.DatGames.AddRange(
            NewDatGame(1, 1, "Chrono Trigger (USA)", now, userId),
            NewDatGame(2, 1, "Chrono Trigger (Europe)", now, userId),
            NewDatGame(3, 1, "Partial Quest (USA)", now, userId),
            NewDatGame(4, 2, "Missing Quest (USA)", now, userId),
            NewDatGame(5, 1, "Unknown Rating (USA)", now, userId));
        context.TitleSourceLinks.AddRange(
            NewTitleSourceLink(1, 1, now, userId),
            NewTitleSourceLink(2, 1, now, userId),
            NewTitleSourceLink(3, 2, now, userId),
            NewTitleSourceLink(4, 3, now, userId),
            NewTitleSourceLink(5, 4, now, userId));
        context.DatRoms.AddRange(
            NewDatRom(1, 1, "chrono-trigger.sfc", romFileId: 1, now, userId),
            NewDatRom(2, 2, "chrono-trigger-europe.sfc", romFileId: null, now, userId),
            NewDatRom(3, 3, "partial-quest-a.sfc", romFileId: 2, now, userId),
            NewDatRom(4, 3, "partial-quest-b.sfc", romFileId: null, now, userId),
            NewDatRom(5, 4, "missing-quest.nes", romFileId: null, now, userId),
            NewDatRom(6, 5, "unknown-rating.sfc", romFileId: 1, now, userId));
        context.Collections.AddRange(
            NewCollection(1, "RPG Favorites", now, userId),
            NewCollection(2, "Missing Shelf", now, userId));
        context.CollectionItems.AddRange(
            NewCollectionItem(1, 1, now, userId),
            NewCollectionItem(1, 2, now, userId),
            NewCollectionItem(2, 3, now, userId));

        await context.PublishOriginalRomsAsync();

        await context.SaveChangesAsync();
    }

    private static async Task MaterializeValidLibrariesAsync(RomdDbContext context)
    {
        await AttachSeedCollectionsAsync(context);
        var repository = new LibraryRepository(context);
        var dataProvider = new MaterializationDataProvider(context);

        // Mirror the real lifecycle: rebuild the canonical catalog for every platform before
        // materializing, so candidates resolve their CatalogReleaseId from CatalogReleaseSource.
        var catalogProjection = CatalogProjectionTestFactory.Create(context);
        var platformIds = await context.Platforms.Select(p => p.Id).ToListAsync();
        foreach (int platformId in platformIds)
        {
            await catalogProjection.RebuildPlatformAsync(platformId);
        }

        var service = new LibraryMaterializationService(
            repository,
            dataProvider,
            catalogProjection,
            new AdminRealtimeOutbox(context, TimeProvider.System),
            new EfUnitOfWork(context),
            Substitute.For<ILogger<LibraryMaterializationService>>());
        var libraryIds = await context.Libraries
            .Where(library => library.ConfigurationState == LibraryConfigurationState.Valid.ToString())
            .OrderBy(library => library.Id)
            .Select(library => library.Id)
            .ToListAsync();

        foreach (int libraryId in libraryIds)
        {
            await service.MaterializeAsync(libraryId);
        }
    }

    private static async Task AssertAllMaterializedTitlesAreVisibleAsync(RomdDbContext context)
    {
        var invisibleTitleCount = await context.MaterializedLibraryTitles
            .CountAsync(title => !title.IsVisible);

        invisibleTitleCount.ShouldBe(0);
    }

    private static async Task AssertUnknownRatingTitleIsHiddenAsync(RomdDbContext context)
    {
        bool titleRowExists = await context.MaterializedLibraryTitles
            .AnyAsync(title => title.TitleId == 4);

        titleRowExists.ShouldBeFalse();
    }

    private static async Task AssertReleaseExposureProjectionAsync(RomdDbContext context)
    {
        var invalidExposedReleaseCount = await context.MaterializedLibraryReleases
            .CountAsync(release => release.IsExposed && (!release.IsEligible || release.IsBlocked));
        invalidExposedReleaseCount.ShouldBe(0);

        var materializedTitles = await context.MaterializedLibraryTitles
            .Select(title => new
            {
                title.LibraryId,
                title.TitleId,
                title.IsOwned,
                title.ExposedReleaseCount
            })
            .ToListAsync();
        var exposedCounts = await context.MaterializedLibraryReleases
            .Where(release => release.IsExposed)
            .GroupBy(release => new { release.LibraryId, release.TitleId })
            .Select(group => new
            {
                group.Key.LibraryId,
                group.Key.TitleId,
                Count = group.Count()
            })
            .ToListAsync();
        var exposedCountsByTitle = exposedCounts
            .ToDictionary(count => (count.LibraryId, count.TitleId), count => count.Count);

        foreach (var title in materializedTitles)
        {
            title.ExposedReleaseCount.ShouldBe(
                exposedCountsByTitle.GetValueOrDefault((title.LibraryId, title.TitleId)),
                $"Library {title.LibraryId} title {title.TitleId} exposed count must match release rows.");
        }

        var userIdByLibraryId = materializedTitles
            .Select(title => title.LibraryId)
            .Distinct()
            .ToDictionary(libraryId => libraryId, _ => Guid.NewGuid());
        context.Users.AddRange(userIdByLibraryId.Select(pair => new RomdUser
        {
            Id = pair.Value,
            UserName = $"projection-{pair.Key}",
            NormalizedUserName = $"PROJECTION-{pair.Key}",
            Email = $"projection-{pair.Key}@example.test",
            NormalizedEmail = $"PROJECTION-{pair.Key}@EXAMPLE.TEST",
            LibraryId = pair.Key,
            CreatedAt = DateTimeOffset.UtcNow
        }));
        await context.PublishOriginalRomsAsync();
        await context.SaveChangesAsync();

        var browseRepository = new ConsumerBrowseRepository(context, new ConsumerReleaseSelector());
        foreach (var title in materializedTitles.Where(title => title.IsOwned))
        {
            // Consumer browse exposes the stable CatalogReleaseId, so the expectation reads it
            // from the same materialized rows the browse repository projects.
            var expectedReleaseIds = await context.MaterializedLibraryReleases
                .Where(release =>
                    release.LibraryId == title.LibraryId &&
                    release.TitleId == title.TitleId &&
                    release.IsExposed &&
                    release.IsOwned &&
                    release.CatalogReleaseId != null &&
                    context.Libraries.Any(library =>
                        library.Id == release.LibraryId &&
                        library.ConfigurationState == LibraryConfigurationState.Valid.ToString()))
                .OrderBy(release => release.CatalogReleaseId)
                .Select(release => release.CatalogReleaseId!.Value)
                .ToListAsync();

            var detail = await browseRepository.GetTitleAsync(
                new ConsumerLibraryScope(userIdByLibraryId[title.LibraryId]),
                title.TitleId,
                ConsumerReleasePreference.Default);

            var found = detail.ShouldBeOfType<ConsumerLibraryReadResult<ConsumerTitleDetailData>.Found>();
            found.LibraryId.ShouldBe(title.LibraryId);
            found.Value.Releases
                .Select(release => release.Id)
                .Order()
                .ShouldBe(expectedReleaseIds);
        }
    }

    private static async Task AssertMetadataShelfProjectionAsync(RomdDbContext context)
    {
        int libraryId = await context.Libraries
            .Where(library => library.Name == "Metadata Shelf")
            .Select(library => library.Id)
            .SingleAsync();

        var accessibleRelease = await context.MaterializedLibraryReleases
            .Where(release => release.LibraryId == libraryId && release.IsExposed)
            .SingleAsync();
        var titleRow = await context.MaterializedLibraryTitles
            .Where(title => title.LibraryId == libraryId)
            .SingleAsync();

        accessibleRelease.TitleId.ShouldBe(3);
        accessibleRelease.IsOwned.ShouldBeFalse();
        accessibleRelease.IsComplete.ShouldBeFalse();
        titleRow.TitleId.ShouldBe(3);
        titleRow.IsOwned.ShouldBeFalse();
        titleRow.IsPlayable.ShouldBeFalse();
        titleRow.Availability.ShouldBe(LibraryTitleAvailability.MetadataOnly.ToString());
    }

    private static async Task<DivergentProjectionSeed> SeedDivergentProjectionDataAsync(RomdDbContext context)
    {
        var now = DateTimeOffset.UtcNow;
        var userId = Guid.NewGuid();
        var library = LibraryEntity.FromDomain(Library.CreateNew("Projection Source", new LibraryConfiguration(), now: DateTimeOffset.UnixEpoch));

        context.Libraries.Add(library);
        context.Platforms.AddRange(
            NewPlatform(1, "Super Nintendo", "snes", now, userId),
            NewPlatform(2, "Nintendo Entertainment System", "nes", now, userId));
        context.Files.Add(NewFile(1, NewSha256(1), now, userId));
        context.DatFiles.Add(NewDatFile(1, 1, 1, now, userId));
        context.Titles.AddRange(
            NewTitle(1, 1, "Chrono Trigger", "RPG", now, userId),
            NewTitle(2, 2, "Mega Man", "Action", now, userId));
        context.SourceEntries.AddRange(
            NewSourceEntry(1, 1, "Chrono Trigger (USA)", 1, now, userId),
            NewSourceEntry(2, 1, "Mega Man (USA)", 1, now, userId));
        context.DatGames.AddRange(
            NewDatGame(1, 1, "Chrono Trigger (USA)", now, userId),
            NewDatGame(2, 1, "Mega Man (USA)", now, userId));
        context.Collections.AddRange(
            NewCollection(1, "Title Projection", now, userId),
            NewCollection(2, "Legacy Projection", now, userId));
        context.CollectionItems.AddRange(
            NewCollectionItem(1, 1, now, userId),
            NewCollectionItem(2, 2, now, userId));

        await context.PublishOriginalRomsAsync();

        await context.SaveChangesAsync();

        await AttachSeedCollectionsAsync(context);

        context.Users.Add(new RomdUser
        {
            Id = userId,
            UserName = "projection-user",
            NormalizedUserName = "PROJECTION-USER",
            Email = "projection@example.test",
            NormalizedEmail = "PROJECTION@EXAMPLE.TEST",
            LibraryId = library.Id,
            CreatedAt = now
        });
        library.NeedsMaterialization = false;

        context.MaterializedLibraryTitles.Add(
            NewMaterializedTitle(library.Id, 1, 1, "RPG", isOwned: true, isPlayable: true));
        context.MaterializedLibraryReleases.Add(
            NewMaterializedRelease(
                library.Id,
                titleId: 1,
                datGameId: 1,
                datFileId: 1,
                platformId: 1,
                isOwned: true,
                isComplete: true,
                isPlayable: true));

        await context.PublishOriginalRomsAsync();

        await context.SaveChangesAsync();

        return new DivergentProjectionSeed(userId, library.Id, 1, 1, 1, 1, 1);
    }

    private static async Task<TitleCardinalityDecisionSeed> SeedTitleCardinalityDecisionDataAsync(
        RomdDbContext context)
    {
        var now = DateTimeOffset.UtcNow;
        var userId = Guid.NewGuid();
        var library = LibraryEntity.FromDomain(Library.CreateNew("Cardinality", new LibraryConfiguration(), now: DateTimeOffset.UnixEpoch));

        context.Libraries.Add(library);
        context.Platforms.Add(NewPlatform(1, "Super Nintendo", "snes", now, userId));
        context.Files.Add(NewFile(1, NewSha256(1), now, userId));
        context.DatFiles.Add(NewDatFile(1, 1, 1, now, userId));
        context.Titles.AddRange(
            NewTitle(1, 1, "Multi Release", "RPG", now, userId),
            NewTitle(2, 1, "Visible Only", "RPG", now, userId));
        context.SourceEntries.AddRange(
            NewSourceEntry(1, 1, "Multi Release (USA)", 1, now, userId),
            NewSourceEntry(2, 1, "Multi Release (Europe)", 1, now, userId));
        context.DatGames.AddRange(
            NewDatGame(1, 1, "Multi Release (USA)", now, userId),
            NewDatGame(2, 1, "Multi Release (Europe)", now, userId));
        context.Collections.Add(NewCollection(1, "Title Cardinality", now, userId));
        context.CollectionItems.AddRange(
            NewCollectionItem(1, 1, now, userId),
            NewCollectionItem(1, 2, now, userId));

        await context.PublishOriginalRomsAsync();

        await context.SaveChangesAsync();

        await AttachSeedCollectionsAsync(context);

        context.MaterializedLibraryTitles.AddRange(
            NewMaterializedTitle(library.Id, 1, 1, "RPG", isOwned: true, isPlayable: true),
            NewMaterializedTitle(library.Id, 2, 1, "RPG", isOwned: false, isPlayable: false, exposedReleaseCount: 0));
        context.MaterializedLibraryReleases.AddRange(
            NewMaterializedRelease(
                library.Id,
                titleId: 1,
                datGameId: 1,
                datFileId: 1,
                platformId: 1,
                isOwned: true,
                isComplete: true,
                isPlayable: true),
            NewMaterializedRelease(
                library.Id,
                titleId: 1,
                datGameId: 2,
                datFileId: 1,
                platformId: 1,
                isOwned: true,
                isComplete: true,
                isPlayable: true));

        await context.PublishOriginalRomsAsync();

        await context.SaveChangesAsync();

        return new TitleCardinalityDecisionSeed(library.Id, 1, 1);
    }

    private static async Task<LibraryContentCounts> GetTitleContentCountsAsync(
        RomdDbContext context,
        int libraryId)
    {
        int ownedTitleCount = await TitlesForValidLibrary(context, libraryId)
            .Where(title => title.IsOwned)
            .CountAsync();

        int availableTitleCount = await TitlesForValidLibrary(context, libraryId)
            .Where(title => title.ExposedReleaseCount > 0)
            .CountAsync();

        int collectionCount = await context.CollectionItems
            .Join(
                TitlesForValidLibrary(context, libraryId).Where(title => title.IsOwned),
                item => item.TitleId,
                materialized => materialized.TitleId,
                (item, _) => item.CollectionId)
            .Distinct()
            .CountAsync();

        return new LibraryContentCounts(ownedTitleCount, availableTitleCount, collectionCount);
    }

    private static async Task<IReadOnlyList<PlatformFacet>> GetTitlePlatformFacetsAsync(
        RomdDbContext context,
        int libraryId)
    {
        var facets = await TitlesForValidLibrary(context, libraryId)
            .Where(title => title.IsOwned)
            .GroupBy(title => title.PlatformId)
            .Select(group => new PlatformFacetCount(group.Key, group.Count()))
            .ToListAsync();

        return await ToPlatformFacetsAsync(context, facets);
    }

    private static async Task<IReadOnlyList<PlatformFacet>> ToPlatformFacetsAsync(
        RomdDbContext context,
        IReadOnlyList<PlatformFacetCount> facets)
    {
        var platformIds = facets.Select(facet => facet.PlatformId).ToList();
        var platforms = await context.Platforms
            .Where(platform => platformIds.Contains(platform.Id))
            .Select(platform => new { platform.Id, platform.Name })
            .ToDictionaryAsync(platform => platform.Id, platform => platform.Name);

        return facets
            .Select(facet => new PlatformFacet(
                facet.PlatformId,
                platforms.GetValueOrDefault(facet.PlatformId, "Unknown"),
                facet.Count))
            .OrderByDescending(facet => facet.Count)
            .ThenBy(facet => facet.PlatformName)
            .ToList();
    }

    private static async Task<IReadOnlyList<GenreFacet>> GetTitleGenreFacetsAsync(
        RomdDbContext context,
        int libraryId)
    {
        var facets = await TitlesForValidLibrary(context, libraryId)
            .Where(title => title.IsOwned && title.Genre != null)
            .GroupBy(title => title.Genre!)
            .Select(group => new { Genre = group.Key, Count = group.Count() })
            .ToListAsync();

        return facets
            .Select(facet => new GenreFacet(facet.Genre, facet.Count))
            .OrderByDescending(facet => facet.Count)
            .ThenBy(facet => facet.Genre)
            .ToList();
    }

    private static async Task<IReadOnlyList<CollectionFacet>> GetTitleCollectionFacetsAsync(
        RomdDbContext context,
        int libraryId)
    {
        var facets = await context.CollectionItems
            .Join(
                TitlesForValidLibrary(context, libraryId).Where(title => title.IsOwned),
                item => item.TitleId,
                materialized => materialized.TitleId,
                (item, _) => item.CollectionId)
            .GroupBy(collectionId => collectionId)
            .Select(group => new CollectionFacetCount(group.Key, group.Count()))
            .ToListAsync();

        return await ToCollectionFacetsAsync(context, facets);
    }

    private static async Task<IReadOnlyList<CollectionFacet>> ToCollectionFacetsAsync(
        RomdDbContext context,
        IReadOnlyList<CollectionFacetCount> facets)
    {
        var collectionIds = facets.Select(facet => facet.CollectionId).ToList();
        var collections = await context.Collections
            .Where(collection => collectionIds.Contains(collection.Id))
            .Select(collection => new { collection.Id, collection.Name })
            .ToDictionaryAsync(collection => collection.Id, collection => collection.Name);
        var totalCounts = await context.CollectionItems
            .Where(item => collectionIds.Contains(item.CollectionId))
            .GroupBy(item => item.CollectionId)
            .Select(group => new { CollectionId = group.Key, TotalCount = group.Count() })
            .ToDictionaryAsync(count => count.CollectionId, count => count.TotalCount);

        return facets
            .Select(facet => new CollectionFacet(
                facet.CollectionId,
                collections.GetValueOrDefault(facet.CollectionId, "Unknown"),
                facet.MatchingCount,
                totalCounts.GetValueOrDefault(facet.CollectionId, 0)))
            .OrderByDescending(facet => facet.MatchingCount)
            .ThenBy(facet => facet.CollectionName)
            .ToList();
    }

    private static IQueryable<MaterializedLibraryTitleEntity> TitlesForValidLibrary(
        RomdDbContext context,
        int libraryId) =>
        context.MaterializedLibraryTitles.Where(title =>
            title.LibraryId == libraryId &&
            context.Libraries.Any(library =>
                library.Id == title.LibraryId &&
                library.ConfigurationState == LibraryConfigurationState.Valid.ToString()));

    private static PlatformEntity NewPlatform(
        int id,
        string name,
        string shortName,
        DateTimeOffset now,
        Guid userId) =>
        new()
        {
            Id = id,
            Name = name,
            Ownership = Romd.Domain.ReferenceData.ReferenceOwnership.Romd,
            BuiltInVersion = 1,
            BaseName = name,
            BaseCompactLabel = name,
            CanonicalKey = shortName,
            ShortName = shortName,
            CreatedAt = now,
            CreatedByUserId = userId
        };

    private static FileEntityPersistence NewFile(int id, Sha256 sha256, DateTimeOffset now, Guid userId) =>
        new()
        {
            Id = id,
            Sha256 = sha256,
            Size = 1,
            SizeOnDisk = 1,
            IsCompressed = false,
            CreatedAt = now,
            CreatedByUserId = userId
        };

    private static OriginalRomFixture NewRomFile(
        int id,
        string originalFilename,
        int fileId,
        Sha1 sha1,
        Md5 md5,
        DateTimeOffset now,
        Guid userId) =>
        new()
        {
            Id = id,
            OriginalFilename = originalFilename,
            FileId = fileId,
            Sha1 = sha1,
            Md5 = md5,
            Crc32 = Crc32.FromUInt32((uint)id),
            CreatedAt = now,
            CreatedByUserId = userId
        };

    private static DatFileEntity NewDatFile(
        int id,
        int fileId,
        int platformId,
        DateTimeOffset now,
        Guid userId) =>
        new()
        {
            Source = new DatSourceEntity
            {
                CatalogSource = new CatalogSourceEntity { Id = id, Kind = "Dat", Status = "Active" }
            },
            Id = id,
            Name = $"DAT {id}",
            Description = $"Test DAT {id}",
            Type = "NoIntro",
            PlatformId = platformId,
            OriginalFilename = $"dat-{id}.dat",
            FileId = fileId,
            GameCount = 1,
            CreatedAt = now,
            CreatedByUserId = userId
        };

    private static TitleEntity NewTitle(
        int id,
        int platformId,
        string name,
        string genre,
        DateTimeOffset now,
        Guid userId) =>
        new()
        {
            Id = id,
            PlatformId = platformId,
            Name = name,
            NormalizedName = name.ToLowerInvariant(),
            Genre = genre,
            EnrichmentStatus = "None",
            CreatedAt = now,
            CreatedByUserId = userId
        };

    private static TitleContentRatingEntity NewTitleContentRating(int titleId, DateTimeOffset now, Guid userId) =>
        new()
        {
            TitleId = titleId,
            Board = (int)RatingBoard.Esrb,
            Code = "E",
            Designation = (int)RatingDesignation.Rated,
            MinimumAge = 0,
            SourceId = "test",
            CreatedAt = now,
            CreatedByUserId = userId
        };

    private static DatGameEntity NewDatGame(
        int id,
        int datFileId,
        string name,
        DateTimeOffset now,
        Guid userId) =>
        new()
        {
            Id = id,
            DatFileId = datFileId,
            SourceEntryId = id,
            Name = name,
            CreatedAt = now,
            CreatedByUserId = userId
        };

    private static SourceEntryEntity NewSourceEntry(
        int id,
        int catalogSourceId,
        string name,
        int? platformId,
        DateTimeOffset now,
        Guid userId) =>
        new()
        {
            Id = id,
            CatalogSourceId = catalogSourceId,
            EntryKey = name,
            Name = name,
            PlatformId = platformId,
            CreatedAt = now,
            CreatedByUserId = userId
        };

    private static TitleSourceLinkEntity NewTitleSourceLink(
        int sourceEntryId,
        int titleId,
        DateTimeOffset now,
        Guid userId) =>
        new()
        {
            SourceEntryId = sourceEntryId,
            TitleId = titleId,
            CreatedAt = now,
            CreatedByUserId = userId
        };

    private static DatRomEntity NewDatRom(
        int id,
        int datGameId,
        string name,
        int? romFileId,
        DateTimeOffset now,
        Guid userId) =>
        new()
        {
            Id = id,
            DatGameId = datGameId,
            Name = name,
            Size = 1,
            RomFileId = romFileId,
            CreatedAt = now,
            CreatedByUserId = userId
        };

    private static CollectionEntity NewCollection(int id, string name, DateTimeOffset now, Guid userId) =>
        new()
        {
            Id = id,
            Name = name,
            CreatedAt = now,
            CreatedByUserId = userId
        };

    private static CollectionItemEntity NewCollectionItem(
        int collectionId,
        int titleId,
        DateTimeOffset now,
        Guid userId) =>
        new()
        {
            CollectionId = collectionId,
            TitleId = titleId,
            SortOrder = titleId,
            AddedAt = now,
            CreatedAt = now,
            CreatedByUserId = userId
        };

    private static MaterializedLibraryTitleEntity NewMaterializedTitle(
        int libraryId,
        int titleId,
        int platformId,
        string genre,
        bool isOwned,
        bool isPlayable,
        int exposedReleaseCount = 1) =>
        new()
        {
            LibraryId = libraryId,
            TitleId = titleId,
            PlatformId = platformId,
            Genre = genre,
            IsVisible = true,
            IsOwned = isOwned,
            IsPlayable = isPlayable,
            EligibleReleaseCount = 1,
            PlayableReleaseCount = isPlayable ? 1 : 0,
            ExposedReleaseCount = exposedReleaseCount,
            Availability = isPlayable
                ? LibraryTitleAvailability.Playable.ToString()
                : isOwned ? LibraryTitleAvailability.Partial.ToString() : LibraryTitleAvailability.MetadataOnly.ToString()
        };

    private static MaterializedLibraryReleaseEntity NewMaterializedRelease(
        int libraryId,
        int titleId,
        int datGameId,
        int datFileId,
        int platformId,
        bool isOwned,
        bool isComplete,
        bool isPlayable) =>
        new()
        {
            LibraryId = libraryId,
            TitleId = titleId,
            DatGameId = datGameId,
            DatFileId = datFileId,
            PlatformId = platformId,
            IsEligible = true,
            IsComplete = isComplete,
            IsOwned = isOwned,
            IsPlayable = isPlayable,
            IsBlocked = false,
            BlockReason = null,
            IsExposed = true,
            ExposureReason = "ExposedDefault"
        };

    private static Sha256 NewSha256(byte firstByte)
    {
        var bytes = new byte[Sha256.ByteLength];
        bytes[0] = firstByte;
        return Sha256.FromBytes(bytes);
    }

    private static Sha1 NewSha1(byte firstByte)
    {
        var bytes = new byte[Sha1.ByteLength];
        bytes[0] = firstByte;
        return Sha1.FromSpan(bytes);
    }

    private static Md5 NewMd5(byte firstByte)
    {
        var bytes = new byte[Md5.ByteLength];
        bytes[0] = firstByte;
        return Md5.FromSpan(bytes);
    }

    private sealed class TestDatabase(PostgreSqlTestDatabase connection, RomdDbContext context) : IAsyncDisposable
    {
        public RomdDbContext Context { get; } = context;

        public RomdDbContext CreateReadContext() => new(new DbContextOptionsBuilder<RomdDbContext>()
            .UseNpgsql(connection.ConnectionString).UseOpenIddict().Options);

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            await connection.DisposeAsync();
        }
    }

    private sealed class PublicationOutboxFailureInterceptor : SaveChangesInterceptor
    {
        public bool Enabled { get; set; }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (Enabled && eventData.Context!.ChangeTracker.Entries<AdminRealtimeOutboxEventEntity>()
                    .Any(entry => entry.State == EntityState.Added))
                throw new DbUpdateException("Injected publication outbox failure.");
            return ValueTask.FromResult(result);
        }
    }

    private sealed class PublicationFailureUnitOfWork(
        IUnitOfWork inner,
        string failure,
        CancellationTokenSource cancellation) : IUnitOfWork
    {
        public bool Enabled { get; set; } = true;
        public Task<ITransaction> BeginTransactionAsync(CancellationToken cancellationToken = default) =>
            inner.BeginTransactionAsync(cancellationToken);
        public TransactionConflict ConflictOf(Exception error) => inner.ConflictOf(error);

        public async Task FlushAsync(CancellationToken cancellationToken = default)
        {
            await inner.FlushAsync(cancellationToken);
            if (!Enabled)
                return;
            if (failure == "after-flush")
                throw new InvalidOperationException("Injected failure after publication flush.");
            if (failure == "cancel")
            {
                cancellation.Cancel();
                cancellationToken.ThrowIfCancellationRequested();
            }
        }
    }

    private sealed record DivergentProjectionSeed(
        Guid UserId,
        int LibraryId,
        int TitleProjectionTitleId,
        int TitleProjectionDatGameId,
        int TitleProjectionDatFileId,
        int TitleProjectionPlatformId,
        int TitleProjectionCollectionId);

    private sealed record TitleCardinalityDecisionSeed(
        int LibraryId,
        int PlatformId,
        int CollectionId);

    private sealed record PlatformFacetCount(int PlatformId, int Count);

    private sealed record CollectionFacetCount(int CollectionId, int MatchingCount);

    private sealed class PauseAfterLiveAssignmentResolutionInterceptor : DbCommandInterceptor
    {
        private readonly TaskCompletionSource _resolved = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _resume = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _hasPaused;

        public Task WaitUntilResolvedAsync() => _resolved.Task.WaitAsync(TimeSpan.FromSeconds(5));

        public void Resume() => _resume.TrySetResult();

        public override async ValueTask<DbDataReader> ReaderExecutedAsync(
            DbCommand command,
            CommandExecutedEventData eventData,
            DbDataReader result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("AspNetUsers", StringComparison.Ordinal) &&
                command.CommandText.Contains("NeedsMaterialization", StringComparison.Ordinal) &&
                Interlocked.Exchange(ref _hasPaused, 1) == 0)
            {
                _resolved.TrySetResult();
                await _resume.Task.WaitAsync(cancellationToken);
            }

            return result;
        }
    }
}
