using Microsoft.EntityFrameworkCore;
using Romd.Consumer.Application.Activity;
using Romd.Domain.Activity;
using Romd.Domain.Libraries;
using Romd.Persistence;
using Romd.Persistence.Entities;
using Romd.Persistence.Identity;
using Romd.Persistence.Repositories;
using Romd.PostgreSql.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Persistence;

public sealed class PlayActivityRepositoryTests : IAsyncDisposable
{
    private static readonly Guid UserId = Guid.Parse("11111111-1111-4111-8111-111111111111");
    private static readonly Guid OtherUserId = Guid.Parse("aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee");
    private readonly PostgreSqlTestDatabase _database = PostgreSqlTestDatabase.Create();
    private readonly int _libraryId;

    public PlayActivityRepositoryTests()
    {
        using var context = _database.CreateContext();
        var library = LibraryEntity.FromDomain(Library.CreateNew("Play Library", new LibraryConfiguration()));
        library.NeedsMaterialization = false;
        context.Libraries.Add(library);
        context.SaveChanges();
        _libraryId = library.Id;

        var now = DateTimeOffset.Parse("2026-09-14T10:00:00Z");
        context.Users.AddRange(NewUser(UserId, "player", library.Id), NewUser(OtherUserId, "other", library.Id));
        context.Platforms.Add(new PlatformEntity
        {
            Id = 91,
            Name = "Test Platform",
            Ownership = Romd.Domain.ReferenceData.ReferenceOwnership.Romd, BuiltInVersion = 1, BaseName = "Test Platform", BaseCompactLabel = "Test Platform", CanonicalKey = "test-platform", ShortName = "test-platform",
            CreatedAt = now
        });
        context.Titles.AddRange(NewTitle(101, "Alpha", now), NewTitle(102, "Beta", now));
        context.SaveChanges();
        context.MaterializedLibraryTitles.AddRange(NewLibraryTitle(101), NewLibraryTitle(102));
        context.MaterializedLibraryReleases.AddRange(NewLibraryRelease(101, 201), NewLibraryRelease(102, 202));
        context.SaveChanges();
    }

    [Fact]
    public async Task Upsert_IsIdempotentMonotonicAndRejectsConflictingSnapshots()
    {
        await using var context = _database.CreateContext();
        var repository = Repository(context);
        var sessionId = Guid.Parse("22222222-2222-4222-8222-222222222222");
        var startedAt = DateTimeOffset.Parse("2026-09-14T10:00:00Z");
        var open = Snapshot(101, 201, startedAt);

        var created = await UpsertAsync(context, repository, UserId, sessionId, open);
        var retry = await UpsertAsync(context, repository, UserId, sessionId, open);
        var ended = await UpsertAsync(
            context,
            repository,
            UserId,
            sessionId,
            open with { EndedAt = startedAt.AddMinutes(5), ActiveDurationSeconds = 240 });
        var lateOpen = await UpsertAsync(context, repository, UserId, sessionId, open);
        var conflict = await UpsertAsync(
            context,
            repository,
            UserId,
            sessionId,
            open with { EndedAt = startedAt.AddMinutes(6), ActiveDurationSeconds = 240 });

        created.Status.ShouldBe(PlaySessionUpsertStatus.Created);
        retry.Status.ShouldBe(PlaySessionUpsertStatus.Unchanged);
        ended.Status.ShouldBe(PlaySessionUpsertStatus.Updated);
        lateOpen.Status.ShouldBe(PlaySessionUpsertStatus.Unchanged);
        conflict.Status.ShouldBe(PlaySessionUpsertStatus.Conflict);
        (await context.PlaySessions.CountAsync()).ShouldBe(1);
        var stored = await context.PlaySessions.AsNoTracking().SingleAsync();
        stored.EndedAt.ShouldBe(startedAt.AddMinutes(5));
        stored.ActiveDurationSeconds.ShouldBe(240);
    }

    [Fact]
    public async Task Pagination_UsesStableNewestFirstKeysetAndRecentlyPlayedAggregatesTitles()
    {
        await using var context = _database.CreateContext();
        var repository = Repository(context);
        var baseTime = DateTimeOffset.Parse("2026-09-14T10:00:00Z");
        var ids = new[]
        {
            Guid.Parse("00000000-0000-4000-8000-000000000001"),
            Guid.Parse("00000000-0000-4000-8000-000000000002"),
            Guid.Parse("00000000-0000-4000-8000-000000000003")
        };
        await UpsertAsync(context, repository, UserId, ids[0], Snapshot(101, 201, baseTime));
        await UpsertAsync(context, repository, UserId, ids[1], Snapshot(101, 201, baseTime.AddMinutes(1)));
        await UpsertAsync(context, repository, UserId, ids[2], Snapshot(102, 202, baseTime.AddMinutes(2)));

        var first = (await repository.ListAccessibleAsync(UserId, null, 2))!;
        var second = (await repository.ListAccessibleAsync(UserId, first.NextCursor, 2))!;
        var recent = (await repository.ListRecentlyPlayedAsync(UserId, 10))!;

        first.Items.Select(item => item.SessionId).ShouldBe([ids[2], ids[1]]);
        first.HasNextPage.ShouldBeTrue();
        second.Items.Select(item => item.SessionId).ShouldBe([ids[0]]);
        second.HasNextPage.ShouldBeFalse();
        recent.Select(item => item.TitleId).ShouldBe([102, 101]);
        recent.Single(item => item.TitleId == 101).PlayCount.ShouldBe(2);
        recent.Single(item => item.TitleId == 102).PlayCount.ShouldBe(1);
    }

    [Fact]
    public async Task HardDelete_UsesOwnershipAndStillWorksAfterAccessRevocation()
    {
        await using var context = _database.CreateContext();
        var repository = Repository(context);
        var sessionId = Guid.Parse("33333333-3333-4333-8333-333333333333");
        await UpsertAsync(
            context,
            repository,
            UserId,
            sessionId,
            Snapshot(101, 201, DateTimeOffset.Parse("2026-09-14T10:00:00Z")));

        await repository.DeleteAsync(OtherUserId, sessionId);
        (await context.PlaySessions.CountAsync()).ShouldBe(1);
        await context.MaterializedLibraryReleases
            .Where(item => item.LibraryId == _libraryId && item.CatalogReleaseId == 201)
            .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.IsExposed, false));
        (await repository.GetAccessibleAsync(UserId, sessionId)).ShouldBeNull();

        await repository.DeleteAsync(UserId, sessionId);

        (await context.PlaySessions.CountAsync()).ShouldBe(0);
        await repository.DeleteAsync(UserId, sessionId);
        (await context.PlaySessions.CountAsync()).ShouldBe(0);
    }

    public ValueTask DisposeAsync() => _database.DisposeAsync();

    private static PlayActivityRepository Repository(RomdDbContext context) =>
        new(context, TimeProvider.System, new ArtworkReader(context));

    private static async Task<PlaySessionUpsertResult> UpsertAsync(
        RomdDbContext context,
        PlayActivityRepository repository,
        Guid userId,
        Guid sessionId,
        PlaySessionSnapshot snapshot)
    {
        await using var transaction = await new PlayActivityUnitOfWork(context).BeginAsync(userId);
        var result = await repository.UpsertAsync(userId, sessionId, snapshot);
        if (result.Status != PlaySessionUpsertStatus.Conflict)
        {
            await transaction.CommitAsync();
        }

        return result;
    }

    private static PlaySessionSnapshot Snapshot(
        int titleId,
        int releaseId,
        DateTimeOffset startedAt) => new("org.example.client", titleId, releaseId, startedAt, null, null);

    private static RomdUser NewUser(Guid id, string name, int libraryId) => new()
    {
        Id = id,
        UserName = name,
        NormalizedUserName = name.ToUpperInvariant(),
        Email = $"{name}@example.test",
        NormalizedEmail = $"{name.ToUpperInvariant()}@EXAMPLE.TEST",
        LibraryId = libraryId,
        CreatedAt = DateTimeOffset.Parse("2026-09-14T10:00:00Z")
    };

    private static TitleEntity NewTitle(int id, string name, DateTimeOffset now) => new()
    {
        Id = id,
        PlatformId = 91,
        Name = name,
        NormalizedName = name.ToLowerInvariant(),
        EnrichmentStatus = "None",
        CreatedAt = now
    };

    private MaterializedLibraryTitleEntity NewLibraryTitle(int titleId) => new()
    {
        LibraryId = _libraryId,
        TitleId = titleId,
        PlatformId = 91,
        IsVisible = true,
        IsOwned = true,
        IsPlayable = true,
        EligibleReleaseCount = 1,
        PlayableReleaseCount = 1,
        ExposedReleaseCount = 1,
        Availability = LibraryTitleAvailability.Playable.ToString()
    };

    private MaterializedLibraryReleaseEntity NewLibraryRelease(int titleId, int releaseId) => new()
    {
        LibraryId = _libraryId,
        TitleId = titleId,
        CatalogReleaseId = releaseId,
        DatGameId = releaseId,
        DatFileId = releaseId,
        PlatformId = 91,
        IsEligible = true,
        IsComplete = true,
        IsOwned = true,
        IsPlayable = true,
        IsExposed = true,
        ExposureReason = "Test"
    };
}
