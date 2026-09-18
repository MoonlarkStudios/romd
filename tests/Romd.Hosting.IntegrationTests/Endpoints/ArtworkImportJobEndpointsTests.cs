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
            new EfUnitOfWork(db), User(RomdRoleType.Admin), transport);

        result.ShouldBeOfType<NoContent>();
        transport.Received(1).ChangeState("delivery-1", Arg.Any<DeletedState>(), null);
        await using var verify = _database.CreateContext();
        (await verify.Jobs.SingleAsync()).Phase.ShouldBe("Cancelled");
        (await verify.ArtworkSelections.SingleAsync()).PendingRequestId.ShouldBeNull();
    }

    [Fact]
    public async Task Archive_AdminFailedArtworkJob_UsesRequiredTransaction()
    {
        var job = await SeedAsync();
        job.Fail("Invalid image.");
        await using (var db = _database.CreateContext())
        {
            await using var transaction = await new EfUnitOfWork(db).BeginTransactionAsync();
            await new ArtworkImportJobRepository(db, TimeProvider.System).UpdateAsync(job);
            await transaction.CommitAsync();
        }
        await using var endpointContext = _database.CreateContext();

        var result = await InvokeAsync("Archive", job.Id, new JobRepository(endpointContext, TimeProvider.System),
            new EfUnitOfWork(endpointContext), User(RomdRoleType.Admin));

        result.ShouldBeOfType<NoContent>();
        await using var verify = _database.CreateContext();
        var saved = await verify.Jobs.SingleAsync();
        saved.IsArchived.ShouldBeTrue();
        saved.Phase.ShouldBe("Failed");
        (await verify.ArtworkSelections.SingleAsync()).PendingRequestId.ShouldBeNull();
    }

    [Theory]
    [InlineData("Cancel")]
    [InlineData("Archive")]
    public async Task Mutate_UnrelatedUser_DoesNotRevealOrChangeArtworkJob(string endpoint)
    {
        var job = await SeedAsync();
        await using var db = _database.CreateContext();

        var result = await InvokeAsync(endpoint, job.Id, new JobRepository(db, TimeProvider.System),
            new EfUnitOfWork(db), User(RomdRoleType.User));

        result.ShouldBeOfType<NotFound>();
        (await db.Jobs.SingleAsync()).Phase.ShouldBe("Pending");
        (await db.Jobs.SingleAsync()).IsArchived.ShouldBeFalse();
        (await db.ArtworkSelections.SingleAsync()).PendingRequestId.ShouldBe(job.Id);
    }

    [Fact]
    public async Task Cancel_CheckpointThrows_RollsBackPendingCleanupAndDoesNotDeleteDelivery()
    {
        var job = await SeedAsync();
        await using var db = _database.CreateContext();
        var repository = Substitute.For<IJobRepository>();
        repository.GetByIdAsync(job.Id, Arg.Any<CancellationToken>()).Returns(job);
        repository.UpdateAsync(job, CancellationToken.None).Returns(async _ =>
        {
            await new ArtworkImportJobRepository(db, TimeProvider.System).UpdateAsync(job);
            throw new InvalidOperationException("Checkpoint interrupted before commit.");
        });
        var transport = Substitute.For<IBackgroundJobClient>();

        await Should.ThrowAsync<InvalidOperationException>(() => InvokeAsync("Cancel", job.Id, repository,
            new EfUnitOfWork(db), User(RomdRoleType.Admin), transport));

        transport.DidNotReceiveWithAnyArgs().ChangeState(default!, default!, default);
        await using var verify = _database.CreateContext();
        (await verify.Jobs.SingleAsync()).Phase.ShouldBe("Pending");
        (await verify.ArtworkSelections.SingleAsync()).PendingRequestId.ShouldBe(job.Id);
    }

    public void Dispose() => _database.Dispose();

    private async Task<ArtworkImportJob> SeedAsync()
    {
        await using var db = _database.CreateContext();
        var now = DateTimeOffset.UtcNow;
        db.Platforms.Add(new PlatformEntity
        {
            Id = 1, Name = "Test", Ownership = Romd.Domain.ReferenceData.ReferenceOwnership.Romd, BuiltInVersion = 1, BaseName = "Test", BaseCompactLabel = "Test", CanonicalKey = "test", ShortName = "test", Manufacturer = "Test",
            CreatedAt = now, CreatedByUserId = Guid.Empty
        });
        db.Titles.Add(new TitleEntity
        {
            Id = 1, PlatformId = 1, Name = "Title", NormalizedName = "title", EnrichmentStatus = "None",
            FieldProvenanceJson = "{}", FieldSourceOverridesJson = "{}", ScreenshotPrefsJson = "{}",
            CreatedAt = now, CreatedByUserId = Guid.Empty
        });
        await db.SaveChangesAsync();
        var service = new ArtworkCurationService(new ArtworkCurationRepository(db, TimeProvider.System), new EfUnitOfWork(db));
        var result = await service.RequestAsync(new ArtworkImportRequest(Guid.NewGuid(), 1, ArtworkRole.Poster,
            "steamgriddb", "game", "asset", "https://cdn2.steamgriddb.com/grid/asset.png", "Artist", null));
        result.IsError.ShouldBeFalse();
        result.Value.SetHangfireJobId("delivery-1");
        await new ArtworkImportJobRepository(db, TimeProvider.System).UpdateAsync(result.Value);
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
        IUnitOfWork unitOfWork, ICurrentUser user, IBackgroundJobClient? transport = null)
    {
        var method = typeof(JobEndpoints).GetMethod(endpoint, BindingFlags.NonPublic | BindingFlags.Static)!;
        object?[] arguments = endpoint == "Cancel"
            ? [id, repository, unitOfWork, user, transport ?? Substitute.For<IBackgroundJobClient>(),
                NullLoggerFactory.Instance, CancellationToken.None]
            : [id, repository, unitOfWork, user, CancellationToken.None];
        return await (Task<IResult>)method.Invoke(null, arguments)!;
    }
}
