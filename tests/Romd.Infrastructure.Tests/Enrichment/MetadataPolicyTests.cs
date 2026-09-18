using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Romd.Admin.Application.Configuration;
using Romd.Admin.Application.Source.Platform;
using Romd.Admin.Application.Source.Platform.Commands.SetMetadataPolicy;
using Romd.Admin.Application.Titles.Enrichment;
using Romd.Persistence;
using Romd.Persistence.Entities;
using Romd.Persistence.Repositories;
using Romd.PostgreSql.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Enrichment;

public sealed class MetadataPolicyTests
{
    [Fact]
    public async Task SavedHistoricalCasing_ReadsCorrectly_AndAutoClearsAllSpellings()
    {
        await using var database = PostgreSqlTestDatabase.Create();
        await using var db = database.CreateContext();
        var platform = new PlatformEntity { Name = "Test system", Ownership = Romd.Domain.ReferenceData.ReferenceOwnership.Romd, BuiltInVersion = 1, BaseName = "Test system", BaseCompactLabel = "Test system", CanonicalKey = "test", ShortName = "test" };
        db.Platforms.Add(platform); await db.SaveChangesAsync();
        db.PlatformFieldDefaults.AddRange(
            new PlatformFieldDefaultEntity { PlatformId = platform.Id, FieldName = "description", SourceId = "igdb" },
            new PlatformFieldDefaultEntity { PlatformId = platform.Id, FieldName = "Description", SourceId = "igdb" });
        await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        var repository = new PlatformFieldDefaultRepository(db);
        var saved = await repository.GetByPlatformIdAsync(platform.Id);
        saved.Count.ShouldBe(1);
        saved["Description"].ShouldBe("igdb");
        var scheduler = Substitute.For<IRematerializationScheduler>();
        var handler = new SetMetadataPolicyCommandHandler(new PlatformRepository(db), repository, scheduler,
            Options.Create(new EnrichmentOptions()), new EfUnitOfWork(db), NullLogger<SetMetadataPolicyCommandHandler>.Instance);
        var result = await handler.HandleAsync(new(platform.Id, MetadataPolicy.Revision(saved),
            new Dictionary<string, string?> { ["Description"] = null }));
        result.IsError.ShouldBeFalse();
        (await repository.GetByPlatformIdAsync(platform.Id)).ShouldBeEmpty();
        await scheduler.Received(1).EnqueuePlatformAsync(platform.Id, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StaleRevision_DoesNotOverwriteSavedPolicyOrQueueWork()
    {
        await using var database = PostgreSqlTestDatabase.Create();
        await using var db = database.CreateContext();
        var platform = new PlatformEntity { Name = "Test system", Ownership = Romd.Domain.ReferenceData.ReferenceOwnership.Romd, BuiltInVersion = 1, BaseName = "Test system", BaseCompactLabel = "Test system", CanonicalKey = "test", ShortName = "test" };
        db.Platforms.Add(platform); await db.SaveChangesAsync();
        var repository = new PlatformFieldDefaultRepository(db);
        var emptyRevision = MetadataPolicy.Revision(await repository.GetByPlatformIdAsync(platform.Id));
        await repository.SetAsync(platform.Id, "Description", "igdb");
        var scheduler = Substitute.For<IRematerializationScheduler>();
        var handler = new SetMetadataPolicyCommandHandler(new PlatformRepository(db), repository, scheduler,
            Options.Create(new EnrichmentOptions()), new EfUnitOfWork(db), NullLogger<SetMetadataPolicyCommandHandler>.Instance);
        var result = await handler.HandleAsync(new(platform.Id, emptyRevision,
            new Dictionary<string, string?> { ["Description"] = null }));
        result.FirstError.Code.ShouldBe("MetadataPolicy.Conflict");
        (await repository.GetByPlatformIdAsync(platform.Id))["Description"].ShouldBe("igdb");
        await scheduler.DidNotReceiveWithAnyArgs().EnqueuePlatformAsync(default, default);
    }
}
