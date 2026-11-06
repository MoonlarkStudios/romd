using ErrorOr;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Admin.Application.Ingestion.Jobs.Commands.CancelJob;
using Romd.Admin.Application.Source.Rom.Reconciliation;
using Romd.Application.Common.Security;
using Romd.Domain.Catalog;
using Romd.Domain.Identity;
using Romd.Persistence;
using Romd.Persistence.Entities;
using Romd.Persistence.Repositories;
using Romd.PostgreSql.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Artwork;

public sealed partial class ArtworkCurationPersistenceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationCommand_CommittedPublicationAndNewerPendingIntent_PreservesAssetOutcomeAndPin(bool newerRequest)
    {
        await SeedAsync();
        var pending = (await AcceptAsyncCore(Request())).Value;
        var retained = await PublishAsync(pending, Content(1), complete: false);
        var newer = newerRequest
            ? (await AcceptAsyncCore(Request() with { ProviderAssetId = "newer" })).Value
            : null;
        await using var context = _database.CreateContext();
        var delivery = Substitute.For<IJobDeliveryCancellation>();

        (await CancellationHandler(context, delivery).HandleAsync(new(pending.Id))).IsError.ShouldBeFalse();

        await using var verify = _database.CreateContext();
        var job = await verify.Set<ArtworkImportJobEntity>().SingleAsync(row => row.Id == pending.Id);
        job.Phase.ShouldBe("Cancelled");
        job.RetainedAssetId.ShouldBe(retained);
        job.WasSuperseded.ShouldBeFalse();
        job.ExecutionFenceToken.ShouldBeNull();
        job.ExecutionLeaseExpiresAtUtc.ShouldBeNull();
        var selection = await verify.ArtworkSelections.SingleAsync();
        selection.PinnedAssetId.ShouldBe(retained);
        selection.Mode.ShouldBe(ArtworkSelectionMode.Pinned);
        selection.PendingRequestId.ShouldBe(newer?.Id);
        await delivery.Received(1).RequestStopAsync($"delivery-{pending.Id}", CancellationToken.None);
    }

    [Fact]
    public async Task CancellationCommand_TitleAndSelectionWriterHoldsNewerIntent_WaitsWithoutLockInversionAndPreservesWinner()
    {
        await SeedAsync();
        var prior = (await AcceptAsyncCore(Request())).Value;
        var retained = await PublishAsync(prior, Content(1));
        var pending = (await AcceptAsyncCore(Request() with { ProviderAssetId = "pending" })).Value;
        await using var writer = _database.CreateContext();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var boundary = await new EfUnitOfWork(writer).BeginTransactionAsync(timeout.Token);
        var currentRevision = (await Repository(writer).GetStatesAsync(1, timeout.Token)).Value
            .Single(state => state.Role == ArtworkRole.Poster).Revision;
        var newer = (await Repository(writer).StageRequestAsync(Request() with
        {
            ProviderAssetId = "newest",
            IfMatch = Role(currentRevision)
        }, timeout.Token)).Value;
        await writer.SaveChangesAsync(timeout.Token);
        await using var context = _database.CreateContext();
        int pid = await context.OpenBackendProcessIdAsync(timeout.Token);
        var delivery = Substitute.For<IJobDeliveryCancellation>();
        var cancelling = CancellationHandler(context, delivery).HandleAsync(new(pending.Id), timeout.Token);
        Exception? primary = null;
        try
        {
            (await _database.WaitsForALockAsync(pid, cancelling, timeout.Token)).ShouldBeTrue();
            await boundary.CommitAsync(timeout.Token);
            (await cancelling.WaitAsync(timeout.Token)).IsError.ShouldBeFalse();
            await using var verify = _database.CreateContext();
            (await verify.Jobs.SingleAsync(row => row.Id == pending.Id)).Phase.ShouldBe("Cancelled");
            (await verify.Jobs.SingleAsync(row => row.Id == newer.Id)).Phase.ShouldBe("Pending");
            var selection = await verify.ArtworkSelections.SingleAsync();
            selection.PendingRequestId.ShouldBe(newer.Id);
            selection.PinnedAssetId.ShouldBe(retained);
            selection.Mode.ShouldBe(ArtworkSelectionMode.Pinned);
        }
        catch (Exception error) { primary = error; throw; }
        finally
        {
            // The command accepted a durable request and ignores HTTP cancellation. Releasing the
            // title/selection writer first makes observation failures safe to join as well.
            await boundary.DisposeAsync();
            await PostgreSqlLockQueue.CancelAndJoinAsync(timeout, primary, cancelling);
        }
    }

    private static CancelJobCommandHandler CancellationHandler(RomdDbContext context, IJobDeliveryCancellation delivery)
    {
        var user = Substitute.For<ICurrentUser>();
        user.UserId.Returns(Guid.NewGuid());
        user.HasRole(Arg.Any<RomdRoleType>()).Returns(true);
        var repository = new JobRepository(context, TimeProvider.System);
        return new(repository, new JobCancellationStore(context, repository), Substitute.For<IMediaReconciliationRequests>(),
            new EfUnitOfWork(context), user, TimeProvider.System, delivery, NullLogger<CancelJobCommandHandler>.Instance);
    }
}
