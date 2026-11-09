using Romd.Admin.Application.Ingestion.Jobs.Commands.CancelJob;
using Romd.Infrastructure.Jobs;
using Romd.Application.Common.Concurrency;
using Romd.Admin.Application.Ingestion.Jobs.Commands.SetJobArchived;
using System.Reflection;
using Hangfire;
using Hangfire.States;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Romd.Admin.Application.Artwork;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Admin.Application.Source.Rom;
using Romd.Application.Common.Security;
using Romd.Domain.Catalog;
using Romd.Domain.Identity;
using Romd.Domain.Jobs;
using Romd.Host.Endpoints;
using Romd.Persistence;
using Romd.Persistence.Entities;
using Romd.Persistence.Repositories;
using Romd.PostgreSql.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Hosting.IntegrationTests.Endpoints;

/// <summary>Exercises endpoint bodies with real PostgreSQL transactions and existing authorization semantics.</summary>
public sealed class ArtworkImportJobEndpointsTests : IDisposable
{
    private static readonly DateTimeOffset CancelledAt = new DateTimeOffset(2030, 1, 2, 3, 4, 5, TimeSpan.Zero).AddTicks(1234560);
    private readonly PostgreSqlTestDatabase _database = PostgreSqlTestDatabase.Create();

    [Fact]
    public async Task Cancel_Admin_CommitsJobAndPendingCleanupBeforeTransportDeletion()
    {
        var job = await SeedAsync();
        await using var db = _database.CreateContext();
        var transport = Substitute.For<IBackgroundJobClient>();
        transport.ChangeState("delivery-1", Arg.Any<DeletedState>(), null).Returns(_ =>
        {
            using var observer = _database.CreateContext();
            observer.Jobs.Single().Phase.ShouldBe("Cancelled");
            observer.ArtworkSelections.Single().PendingRequestId.ShouldBeNull();
            return true;
        });

        var result = await InvokeAsync("Cancel", job.Id, new JobRepository(db, TimeProvider.System),
            new EfUnitOfWork(db), User(RomdRoleType.Admin), transport,
            new JobCancellationStore(db, new JobRepository(db, TimeProvider.System)));

        result.ShouldBeOfType<NoContent>();
        transport.Received(1).ChangeState("delivery-1", Arg.Any<DeletedState>(), null);
        await using var verify = _database.CreateContext();
        var cancelled = await verify.Jobs.SingleAsync();
        cancelled.Phase.ShouldBe("Cancelled");
        cancelled.CompletedAt.ShouldBe(DateTimeOffset.FromUnixTimeMilliseconds(CancelledAt.ToUnixTimeMilliseconds()));
        (await verify.ArtworkSelections.SingleAsync()).PendingRequestId.ShouldBeNull();
    }

    [Fact]
    public async Task Archive_AdminFailedArtworkJob_UsesRequiredTransaction()
    {
        var job = await SeedAsync();
        job.Fail("Invalid image.", failedAt: DateTimeOffset.UtcNow);
        await using (var db = _database.CreateContext())
        {
            await using var transaction = await new EfUnitOfWork(db).BeginTransactionAsync();
            await new ArtworkImportJobRepository(db, TimeProvider.System).UpdateStagedAsync(job);
            await transaction.CommitAsync();
        }
        await using var endpointContext = _database.CreateContext();

        var result = await InvokeAsync("Update", job.Id, new JobRepository(endpointContext, TimeProvider.System),
            new EfUnitOfWork(endpointContext), User(RomdRoleType.Admin));

        result.ShouldBeOfType<Ok<Romd.Contracts.Management.Models.Job>>().Value!.IsArchived.ShouldBeTrue();
        await using var verify = _database.CreateContext();
        var saved = await verify.Jobs.SingleAsync();
        saved.IsArchived.ShouldBeTrue();
        saved.Phase.ShouldBe("Failed");
        (await verify.ArtworkSelections.SingleAsync()).PendingRequestId.ShouldBeNull();
    }

    [Theory]
    [InlineData("Cancel")]
    [InlineData("Update")]
    public async Task Mutate_UnrelatedUser_DoesNotRevealOrChangeArtworkJob(string endpoint)
    {
        var job = await SeedAsync();
        await using var db = _database.CreateContext();

        var result = await InvokeAsync(endpoint, job.Id, new JobRepository(db, TimeProvider.System),
            new EfUnitOfWork(db), User(RomdRoleType.User),
            cancellations: new JobCancellationStore(db, new JobRepository(db, TimeProvider.System)));

        result.ShouldBeAssignableTo<IStatusCodeHttpResult>()!.StatusCode.ShouldBe(StatusCodes.Status404NotFound);
        (await db.Jobs.SingleAsync()).Phase.ShouldBe("Pending");
        (await db.Jobs.SingleAsync()).IsArchived.ShouldBeFalse();
        (await db.ArtworkSelections.SingleAsync()).PendingRequestId.ShouldBe(job.Id);
    }

    [Fact]
    public async Task Cancel_CheckpointThrows_RollsBackPendingCleanupAndDoesNotDeleteDelivery()
    {
        var job = await SeedAsync();
        await using var db = _database.CreateContext();
        var repository = new JobRepository(db, TimeProvider.System);
        var store = new JobCancellationStore(db, repository);
        var cancellations = Substitute.For<IJobCancellationStore>();
        cancellations.LockVisibleForCancellationStagedAsync(job.Id, Arg.Any<JobVisibilityScope>(), CancellationToken.None)
            .Returns(call => store.LockVisibleForCancellationStagedAsync(job.Id, call.Arg<JobVisibilityScope>(), CancellationToken.None));
        cancellations.ApplyCancellationStagedAsync(Arg.Any<Job>(), CancellationToken.None).Returns(async call =>
        {
            await store.ApplyCancellationStagedAsync(call.Arg<Job>(), CancellationToken.None);
            throw new InvalidOperationException("Cancellation interrupted before commit.");
        });
        var transport = Substitute.For<IBackgroundJobClient>();

        await Should.ThrowAsync<InvalidOperationException>(() => InvokeAsync("Cancel", job.Id, repository,
            new EfUnitOfWork(db), User(RomdRoleType.Admin), transport, cancellations));

        transport.DidNotReceiveWithAnyArgs().ChangeState(default!, default!, default);
        await using var verify = _database.CreateContext();
        (await verify.Jobs.SingleAsync()).Phase.ShouldBe("Pending");
        (await verify.ArtworkSelections.SingleAsync()).PendingRequestId.ShouldBe(job.Id);
    }

    [Fact]
    public async Task Archive_RepresentationReadFails_RollsBackArchiveAndPreservesTerminalCleanup()
    {
        var job = await SeedAsync();
        job.Fail("Invalid image.", failedAt: DateTimeOffset.UtcNow);
        await using (var db = _database.CreateContext())
        {
            await using var tx = await new EfUnitOfWork(db).BeginTransactionAsync();
            await new ArtworkImportJobRepository(db, TimeProvider.System).UpdateStagedAsync(job);
            await tx.CommitAsync();
        }
        await using var context = _database.CreateContext();
        var catalog = Romd.Hosting.IntegrationTests.Infrastructure.TestReferenceCatalog.Create();
        catalog.GetSystemKeysAsync(Arg.Any<CancellationToken>()).Returns<Task<Romd.Application.Common.Systems.SystemKeys>>(_ => throw new IOException("Injected representation failure."));
        var handler = new SetJobArchivedCommandHandler(new JobRepository(context, TimeProvider.System), new EfUnitOfWork(context), User(RomdRoleType.Admin), catalog, Microsoft.Extensions.Logging.Abstractions.NullLogger<SetJobArchivedCommandHandler>.Instance);
        await Should.ThrowAsync<IOException>(() => handler.HandleAsync(new(job.Id, true)));
        await using var verify = _database.CreateContext();
        (await verify.Jobs.SingleAsync()).IsArchived.ShouldBeFalse();
        (await verify.Jobs.SingleAsync()).Phase.ShouldBe("Failed");
        (await verify.ArtworkSelections.SingleAsync()).PendingRequestId.ShouldBeNull();
    }

    public void Dispose() => _database.Dispose();

    private async Task<ArtworkImportJob> SeedAsync()
    {
        await using var db = _database.CreateContext();
        var now = DateTimeOffset.UtcNow;
        db.Platforms.Add(new PlatformEntity
        {
            Id = 1,
            Name = "Test",
            Ownership = Romd.Domain.ReferenceData.ReferenceOwnership.Romd,
            BuiltInVersion = 1,
            BaseName = "Test",
            BaseCompactLabel = "Test",
            CanonicalKey = "test",
            ShortName = "test",
            Manufacturer = "Test",
            CreatedAt = now,
            CreatedByUserId = Guid.Empty
        });
        db.Titles.Add(new TitleEntity
        {
            Id = 1,
            PlatformId = 1,
            Name = "Title",
            NormalizedName = "title",
            EnrichmentStatus = "None",
            FieldProvenanceJson = "{}",
            FieldSourceOverridesJson = "{}",
            ScreenshotPrefsJson = "{}",
            CreatedAt = now,
            CreatedByUserId = Guid.Empty
        });
        await db.SaveChangesAsync();
        var service = new ArtworkCurationService(new ArtworkCurationRepository(db, TimeProvider.System), new EfUnitOfWork(db), Microsoft.Extensions.Logging.Abstractions.NullLogger<ArtworkCurationService>.Instance);
        var result = await service.RequestAsync(new ArtworkImportRequest(Guid.NewGuid(), 1, ArtworkRole.Poster,
            "steamgriddb", "game", "asset", "https://cdn2.steamgriddb.com/grid/asset.png", "Artist", null,
            RevisionPrecondition.For(ArtworkValidators.Role(0))));
        result.IsError.ShouldBeFalse();
        result.Value.SetHangfireJobId("delivery-1");
        await using var transaction = await new EfUnitOfWork(db).BeginTransactionAsync();
        await new ArtworkImportJobRepository(db, TimeProvider.System).UpdateStagedAsync(result.Value);
        await transaction.CommitAsync();
        return result.Value;
    }

    private static ICurrentUser User(RomdRoleType role)
    {
        var user = Substitute.For<ICurrentUser>();
        user.UserId.Returns(Guid.NewGuid());
        user.HasRole(Arg.Any<RomdRoleType>()).Returns(call => role >= call.Arg<RomdRoleType>());
        return user;
    }

    private static async Task<IResult> InvokeAsync(string endpoint, Guid id, IJobRepository repository,
        IUnitOfWork unitOfWork, ICurrentUser user, IBackgroundJobClient? transport = null, IJobCancellationStore? cancellations = null)
    {
        var method = typeof(JobEndpoints).GetMethod(endpoint, BindingFlags.NonPublic | BindingFlags.Static)!;
        var clock = Substitute.For<TimeProvider>();
        clock.GetUtcNow().Returns(CancelledAt);
        object?[] arguments = endpoint == "Cancel"
            ? [id, new CancelJobCommandHandler(repository, cancellations!,
                Substitute.For<Romd.Admin.Application.Source.Rom.Reconciliation.IMediaReconciliationRequests>(), unitOfWork, user,
                clock, new HangfireJobDeliveryCancellation(transport ?? Substitute.For<IBackgroundJobClient>()),
                NullLogger<CancelJobCommandHandler>.Instance), CancellationToken.None]
            : [id, new Romd.Contracts.Management.Models.UpdateJobRequest(true), new SetJobArchivedCommandHandler(repository, unitOfWork, user, Romd.Hosting.IntegrationTests.Infrastructure.TestReferenceCatalog.Create(), Microsoft.Extensions.Logging.Abstractions.NullLogger<SetJobArchivedCommandHandler>.Instance), CancellationToken.None];
        return await (Task<IResult>)method.Invoke(null, arguments)!;
    }
}
