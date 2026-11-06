using NSubstitute;
using Romd.Admin.Application.Export.Commands.QueueLibraryExport;
using Romd.Admin.Application.Export.Queries.ResolveExportScope;
using Romd.Application.Common.Cqrs;
using Romd.Application.Common.Security;
using Romd.Infrastructure.Tests.Helpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Romd.Admin.Application.Export;
using Romd.Domain.Jobs;
using Romd.Domain.Source.Rom;
using Romd.Infrastructure.Jobs;
using Romd.Persistence;
using Romd.Persistence.Entities;
using Romd.Persistence.Repositories;
using Romd.PostgreSql.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Jobs;

public sealed class ExportJobFormatPersistenceTests : IDisposable
{
    private readonly PostgreSqlTestDatabase _database = PostgreSqlTestDatabase.Create();

    [Theory]
    [InlineData(RomDeliveryFormat.Original, "original")]
    [InlineData(RomDeliveryFormat.Rvz, "rvz")]
    [InlineData(RomDeliveryFormat.Chd, "chd")]
    public async Task QueueLibraryExport_PersistsRequestedFormatAcrossContexts(RomDeliveryFormat format, string stored)
    {
        Guid id;
        using (var db = CreateDb())
        {
            var repository = new ExportJobRepository(db, TimeProvider.System);
            var scope = Substitute.For<IQueryHandler<ResolveExportScopeQuery, AuthorizedExportScope>>();
            scope.HandleAsync(Arg.Any<ResolveExportScopeQuery>(), Arg.Any<CancellationToken>())
                .Returns(new AuthorizedExportScope(new ExportScope.AllCatalog(), null));
            var scheduler = new QueueLibraryExportCommandHandler(scope, Substitute.For<ICurrentUser>(), repository,
                new EfUnitOfWork(db), TimeProvider.System, NullLogger<QueueLibraryExportCommandHandler>.Instance);
            id = (await scheduler.HandleAsync(new QueueLibraryExportCommand(null, format))).Value;
            id.ShouldNotBe(Guid.Empty);
        }
        using (var db = CreateDb())
        {
            var repository = new ExportJobRepository(db, TimeProvider.System);
            var job = (await repository.GetByIdAsync(id)).ShouldNotBeNull();
            job.Format.ShouldBe(format);
            job.ScopeKind.ShouldBe(ExportScopeKind.AllCatalog);
            (await db.Jobs.OfType<ExportJobEntity>().SingleAsync()).ExportFormat.ShouldBe(stored);
            job.Start("delivery", startedAt: DateTimeOffset.UtcNow);
            job.BeginPackaging();
            await repository.CommitCheckpointAsync(db, job);
        }
        using (var db = CreateDb())
        {
            var job = (await new ExportJobRepository(db, TimeProvider.System).GetByIdAsync(id)).ShouldNotBeNull();
            job.Format.ShouldBe(format);
            job.PhaseEnum.ShouldBe(ExportPhase.Packaging);
        }
    }

    [Fact]
    public void ToDomain_LegacyNullFormat_DefaultsOriginalWithoutRewritingEntity()
    {
        var entity = ExportJobEntity.FromDomain(ExportJob.CreateAllCatalog(createdAt: DateTimeOffset.UtcNow));
        entity.ExportFormat = null;
        entity.ToDomain().Format.ShouldBe(RomDeliveryFormat.Original);
        entity.ExportFormat.ShouldBeNull();
    }

    [Theory]
    [InlineData("Original")]
    [InlineData("RVZ")]
    [InlineData(" rvz")]
    [InlineData("1")]
    [InlineData("")]
    [InlineData("unknown")]
    public void ToDomain_CorruptFormat_RejectsInsteadOfDefaulting(string value)
    {
        var entity = ExportJobEntity.FromDomain(ExportJob.CreateAllCatalog(createdAt: DateTimeOffset.UtcNow));
        entity.ExportFormat = value;
        Should.Throw<InvalidDataException>(() => entity.ToDomain());
    }

    private RomdDbContext CreateDb() => new(new DbContextOptionsBuilder<RomdDbContext>()
        .UseNpgsql(_database.ConnectionString).Options);

    public void Dispose() => _database.Dispose();
    [Fact]
    public void ChdFormat_EntityRoundTripPreservesFormatAndAuthorization()
    {
        var job = ExportJob.CreateLibrary(7, 11, createdAt: DateTimeOffset.UtcNow, format: RomDeliveryFormat.Chd);
        var entity = ExportJobEntity.FromDomain(job);
        entity.ExportFormat.ShouldBe("chd");
        var restored = entity.ToDomain();
        restored.Format.ShouldBe(RomDeliveryFormat.Chd);
        restored.ScopeKind.ShouldBe(ExportScopeKind.Library);
        restored.LibraryId.ShouldBe(7);
        restored.AuthorizedMaterializationGeneration.ShouldBe(11);
    }

}
