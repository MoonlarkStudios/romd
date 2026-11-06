using Romd.Admin.Application.Titles.Commands.QueueBulkEnrichmentJob;
using Romd.Admin.Application.Export.Commands.QueueLibraryExport;
using Romd.Admin.Application.Export.Queries.ResolveExportScope;
using Romd.Application.Common.Cqrs;
using Romd.Application.Common.Security;
using Romd.Infrastructure.Tests.Helpers;
using ErrorOr;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Romd.Admin.Application.Export;
using Romd.Admin.Application.Source.Platform;
using Romd.Domain.Catalog;
using Romd.Infrastructure.Jobs;
using Romd.Persistence;
using Romd.Persistence.Entities;
using Romd.Persistence.Repositories;
using Romd.PostgreSql.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Jobs;

public sealed class JobAcceptanceSchedulerTests
{
    [Fact]
    public async Task CompletionAcceptance_ReturnsIdentityWithoutProjectingSummary()
    {
        using var database = PostgreSqlTestDatabase.Create();
        await using var db = database.CreateContext();
        var actor = Guid.NewGuid();
        db.Users.Add(new Romd.Persistence.Identity.RomdUser { Id = actor, UserName = "curator", CreatedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();
        db.Platforms.Add(new PlatformEntity { Id = 1, Name = "Test", Ownership = Romd.Domain.ReferenceData.ReferenceOwnership.Romd, BuiltInVersion = 1, BaseName = "Test", BaseCompactLabel = "Test", CanonicalKey = "test", ShortName = "test", CreatedAt = DateTimeOffset.UtcNow });
        db.Titles.Add(new TitleEntity { Id = 1, PlatformId = 1, Name = "Test", NormalizedName = "test", EnrichmentStatus = "None", CreatedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();
        var adapter = Substitute.For<Romd.Admin.Application.MetadataProviders.IProviderIdentityAdapter>();
        var store = new Romd.Persistence.Artwork.ArtworkCompletionStore(db, clock: TimeProvider.System, identityAdapters: [adapter]);
        var id = Guid.NewGuid();
        var request = new Romd.Contracts.Management.Artwork.ArtworkCompletionRequest(id, [Romd.Application.Common.Ids.IdCoder.Encode(1)]);
        var result = await store.AcceptCompletionAsync(db, request, actor, default);
        result.IsError.ShouldBeFalse();
        result.Value.ShouldBe(id);
        (await store.AcceptCompletionAsync(db, request, actor, default)).Value.ShouldBe(id);
        (await store.AcceptCompletionAsync(db, request with { RequestId = Guid.NewGuid() }, actor, default)).Value.ShouldBe(id);
        (await db.Jobs.CountAsync()).ShouldBe(1);
        await adapter.DidNotReceiveWithAnyArgs().GetAvailabilityAsync(default);
    }

    [Fact]
    public async Task BulkEnrichment_ReplayReturnsExistingJobAndRetainsCreator()
    {
        using var database = PostgreSqlTestDatabase.Create();
        await using var db = database.CreateContext();
        db.Platforms.Add(new PlatformEntity { Id = 1, Name = "Test", Ownership = Romd.Domain.ReferenceData.ReferenceOwnership.Romd, BuiltInVersion = 1, BaseName = "Test", BaseCompactLabel = "Test", CanonicalKey = "test", ShortName = "test", CreatedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();
        var creator = Guid.NewGuid();
        db.Users.Add(new Romd.Persistence.Identity.RomdUser { Id = creator, UserName = "curator", CreatedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();
        var handler = new QueueBulkEnrichmentJobCommandHandler(new BulkEnrichmentJobRepository(db, TimeProvider.System),
            Substitute.For<IPlatformRepository>(), new EfUnitOfWork(db), TimeProvider.System,
            NullLogger<QueueBulkEnrichmentJobCommandHandler>.Instance);
        var first = (await handler.HandleAsync(new(1, EnrichmentScope.All, creator))).Value;
        var replay = (await handler.HandleAsync(new(1, EnrichmentScope.All, Guid.NewGuid()))).Value;
        replay.ShouldBe(first);
        var job = await db.Jobs.OfType<BulkEnrichmentJobEntity>().SingleAsync();
        job.Id.ShouldBe(first);
        job.CreatedByUserId.ShouldBe(creator);
        (await db.JobDispatches.CountAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task Export_DuplicateAcceptanceReturnsConflictInsteadOfEmptyIdentity()
    {
        using var database = PostgreSqlTestDatabase.Create();
        await using var db = database.CreateContext();
        var repository = new ExportJobRepository(db, TimeProvider.System);
        var scope = Substitute.For<IQueryHandler<ResolveExportScopeQuery, AuthorizedExportScope>>();
        scope.HandleAsync(Arg.Any<ResolveExportScopeQuery>(), Arg.Any<CancellationToken>())
            .Returns(new AuthorizedExportScope(new ExportScope.AllCatalog(), null));
        var scheduler = new QueueLibraryExportCommandHandler(scope, Substitute.For<ICurrentUser>(), repository,
            new EfUnitOfWork(db), TimeProvider.System, NullLogger<QueueLibraryExportCommandHandler>.Instance);
        await scheduler.HandleAsync(new QueueLibraryExportCommand(null));
        var duplicate = await scheduler.HandleAsync(new QueueLibraryExportCommand(null));
        duplicate.FirstError.Code.ShouldBe("Export.AlreadyInProgress");
        duplicate.FirstError.Type.ShouldBe(ErrorType.Conflict);
        (await db.Jobs.CountAsync()).ShouldBe(1);
    }

}
