using ErrorOr;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Romd.Application.Common.Ids;
using Romd.Application.Common.Security;
using Romd.Consumer.Application;
using Romd.Consumer.Application.Activity;
using Romd.Consumer.Application.Activity.Commands.UpsertPlaySession;
using Romd.Contracts.Consumer.Activity;
using Romd.Persistence;
using Romd.Persistence.Entities;
using Romd.Persistence.Repositories;
using Romd.PostgreSql.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Persistence;

public sealed partial class PlayActivityRepositoryTests
{
    [Fact]
    public async Task Upsert_MergedTitleChain_ResolvesOfflineAndCurrentIdsWithReleaseAuthorization()
    {
        await using var writer = _database.CreateContext();
        var open = ActivityCommand();
        await SeedGroupingCatalogAsync(writer, open.Request.StartedAt);
        var created = (await SubmitAsync(open)).Value;

        await MergeAndPublishAsync(writer, 101, 102, default);
        await MergeAndPublishAsync(writer, 102, 103, default);

        UpsertPlaySessionCommand closed = open with
        {
            Request = open.Request with { EndedAt = open.Request.StartedAt.AddMinutes(3), ActiveDurationSeconds = 120 }
        };
        var updated = (await SubmitAsync(closed)).Value;
        updated.SessionId.ShouldBe(created.SessionId);
        updated.TitleId.ShouldBe(IdCoder.Encode(103));
        updated.ReleaseId.ShouldBe(created.ReleaseId);
        updated.ClientId.ShouldBe(created.ClientId);
        updated.CreatedAt.ShouldBe(StoredTime(created.CreatedAt));
        updated.StartedAt.ShouldBe(created.StartedAt);
        updated.EndedAt.ShouldBe(closed.Request.EndedAt);
        updated.ActiveDurationSeconds.ShouldBe(120);

        foreach (int titleId in new[] { 101, 102, 103 })
        {
            var request = closed.Request with { TitleId = IdCoder.Encode(titleId) };
            var replay = (await SubmitAsync(closed with { Request = request })).Value;
            replay.ShouldBe(updated with { UpdatedAt = StoredTime(updated.UpdatedAt) });
        }

        var wrongRelease = closed.Request with { TitleId = IdCoder.Encode(103), ReleaseId = IdCoder.Encode(202) };
        (await SubmitAsync(closed with { Request = wrongRelease })).FirstError.Code
            .ShouldBe(ConsumerErrors.PlaySessionConflict().Code);
        await AssertDeniedAsync(titleId: 104, releaseId: 201);
        await AssertDeniedAsync(titleId: 101, releaseId: 204);
        await AssertDeniedAsync(titleId: 101, releaseId: 999);

        var originalRelease = writer.MaterializedLibraryReleases.Where(release => release.CatalogReleaseId == 201);
        await originalRelease.ExecuteUpdateAsync(set => set.SetProperty(release => release.IsExposed, false));
        await AssertDeniedAsync(titleId: 101, releaseId: 201);
        await originalRelease.ExecuteUpdateAsync(set => set
            .SetProperty(release => release.IsExposed, true)
            .SetProperty(release => release.IsOwned, false));
        await AssertDeniedAsync(titleId: 101, releaseId: 201);

        var stored = await writer.PlaySessions.SingleAsync();
        stored.SessionId.ShouldBe(open.SessionId);
        stored.TitleId.ShouldBe(103);
        stored.ReleaseId.ShouldBe(201);

        async Task AssertDeniedAsync(int titleId, int releaseId)
        {
            var request = open.Request with { TitleId = IdCoder.Encode(titleId), ReleaseId = IdCoder.Encode(releaseId) };
            var result = await SubmitAsync(new(Guid.NewGuid(), request));
            result.FirstError.Code.ShouldBe(ConsumerErrors.TitleNotFound().Code);
        }
    }

    [Fact]
    public async Task Upsert_MergeInProgress_WaitsBeforeResolvingTheOldTitleForANewOfflineSession()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var command = ActivityCommand();
        await using var writer = _database.CreateContext();
        await SeedGroupingCatalogAsync(writer, command.Request.StartedAt);
        await using var grouping = await writer.Database.BeginTransactionAsync(deadline.Token);
        await StageGroupingAndProjectionAsync(writer, 101, 102, deadline.Token);

        await using var consumer = _database.CreateContext();
        var (pending, waited) = await _database.StartWaitingForALockAsync(consumer,
            ct => RealActivityHandler(consumer).HandleAsync(command, ct), deadline.Token);
        Exception? primary = null;
        try
        {
            waited.ShouldBeTrue("The play write must wait before resolving the previous title ID.");
            await grouping.CommitAsync(deadline.Token);

            var result = await pending;
            result.Value.TitleId.ShouldBe(IdCoder.Encode(102));
            var stored = await writer.PlaySessions.SingleAsync(deadline.Token);
            stored.SessionId.ShouldBe(command.SessionId);
            stored.TitleId.ShouldBe(102);
        }
        catch (Exception error)
        {
            primary = error;
            throw;
        }
        finally
        {
            await PostgreSqlLockQueue.CancelAndJoinAsync(deadline, primary, pending);
        }
    }

    [Fact]
    public async Task Upsert_WriteInProgress_MergeTransfersTheSessionAfterTheWriteCommits()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var command = ActivityCommand();
        await using var consumer = _database.CreateContext();
        await SeedGroupingCatalogAsync(consumer, command.Request.StartedAt);
        await using var write = await new PlayActivityUnitOfWork(consumer).BeginAsync(UserId, deadline.Token);
        var staged = await Repository(consumer).UpsertAsync(UserId, command.SessionId,
            Snapshot(101, 201, command.Request.StartedAt), deadline.Token);
        staged.Status.ShouldBe(PlaySessionUpsertStatus.Created);

        await using var writer = _database.CreateContext();
        var (pending, waited) = await _database.StartWaitingForALockAsync(writer, async ct =>
        {
            await MergeAndPublishAsync(writer, 101, 102, ct);
            return true;
        }, deadline.Token);
        Exception? primary = null;
        try
        {
            waited.ShouldBeTrue("Grouping must wait for the in-flight play write before moving history.");
            await write.CommitAsync(deadline.Token);
            await pending;

            var result = await SubmitAsync(command);
            result.Value.SessionId.ShouldBe(command.SessionId);
            result.Value.TitleId.ShouldBe(IdCoder.Encode(102));
            (await writer.PlaySessions.SingleAsync(deadline.Token)).TitleId.ShouldBe(102);
        }
        catch (Exception error)
        {
            primary = error;
            throw;
        }
        finally
        {
            await PostgreSqlLockQueue.CancelAndJoinAsync(deadline, primary, pending);
        }
    }

    private async Task<ErrorOr<PlaySession>> SubmitAsync(UpsertPlaySessionCommand command)
    {
        await using var context = _database.CreateContext();
        return await RealActivityHandler(context).HandleAsync(command);
    }

    private static UpsertPlaySessionCommandHandler RealActivityHandler(RomdDbContext context)
    {
        var user = Substitute.For<ICurrentUser>();
        user.IsAuthenticated.Returns(true);
        user.UserId.Returns(UserId);
        return new(user, new ConsumerReleaseAccessRepository(context), Repository(context), new PlayActivityUnitOfWork(context),
            NullLogger<UpsertPlaySessionCommandHandler>.Instance);
    }

    private async Task SeedGroupingCatalogAsync(RomdDbContext context, DateTimeOffset now)
    {
        context.Titles.AddRange(NewTitle(103, "Gamma", now), NewTitle(104, "Delta", now));
        await context.SaveChangesAsync();
        context.CatalogReleases.AddRange(NewCatalogRelease(101, 201), NewCatalogRelease(102, 202), NewCatalogRelease(104, 204));
        context.MaterializedLibraryReleases.Add(NewLibraryRelease(104, 204));
        await context.SaveChangesAsync();
    }

    private static CatalogReleaseEntity NewCatalogRelease(int titleId, int releaseId) => new()
    {
        Id = releaseId,
        PlatformId = 91,
        CatalogTitleId = titleId,
        Fingerprint = $"release-{releaseId}",
        Name = $"Release {releaseId}"
    };

    private static async Task MergeAndPublishAsync(RomdDbContext db, int source, int target, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await StageGroupingAndProjectionAsync(db, source, target, ct);
        await transaction.CommitAsync(ct);
    }

    private static async Task StageGroupingAndProjectionAsync(RomdDbContext db, int source, int target, CancellationToken ct)
    {
        var repository = new TitleRepository(db);
        await repository.ReassignTitleReferencesAsync(source, target, ct);
        await repository.DeleteAbsorbedSourceAsync(source, ct);
        await db.SaveChangesAsync(ct);
        // Simulate publication of the newly grouped release identities.
        int[] moved = await db.CatalogReleases.Where(release => release.CatalogTitleId == target)
            .Select(release => release.Id).ToArrayAsync(ct);
        var published = await db.MaterializedLibraryReleases.Select(row => row.CatalogReleaseId).ToArrayAsync(ct);
        int libraryId = await db.Libraries.Select(library => library.Id).SingleAsync(ct);
        foreach (int releaseId in moved.Where(id => !published.Contains(id)))
        {
            db.MaterializedLibraryReleases.Add(new MaterializedLibraryReleaseEntity
            {
                LibraryId = libraryId,
                TitleId = target,
                CatalogReleaseId = releaseId,
                DatGameId = releaseId,
                PlatformId = 91,
                IsOwned = true,
                IsExposed = true
            });
        }
        await db.SaveChangesAsync(ct);
    }
}
