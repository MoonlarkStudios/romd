using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Romd.Admin.Application.Taxonomy;
using Romd.Domain.Taxonomy;
using Romd.Persistence;
using Romd.Persistence.Entities;
using Romd.Persistence.Repositories;
using Romd.PostgreSql.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Taxonomy;

public sealed class TaxonomyMergeTests
{
    [Fact]
    public async Task Merge_RegisteredReferenceIdentity_CannotBeReassigned()
    {
        await using var database = PostgreSqlTestDatabase.Create();
        await using var db = database.CreateContext();
        var source = new RegionEntity { Name = "Registered", Ownership = Romd.Domain.ReferenceData.ReferenceOwnership.Romd, BuiltInVersion = 1, BaseName = "Registered", CanonicalKey = "registered" };
        var target = new RegionEntity { Name = "Other" };
        db.Regions.AddRange(source, target);
        await db.SaveChangesAsync();
        var service = new TaxonomyService<Region>(new RegionRepository(db), Substitute.For<ITaxonomyResolver<Region>>(), new EfUnitOfWork(db));
        (await service.MergeAsync(source.Id, target.Id, default)).FirstError.Code.ShouldBe("Taxonomy.ReferenceIdentity");
        (await db.Regions.AnyAsync(row => row.Id == source.Id && row.CanonicalKey == "registered")).ShouldBeTrue();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Merge_PreservesCanonicalReleaseMemberships_DeduplicatesAndRecordsEvidence(bool regions)
    {
        await using var database = PostgreSqlTestDatabase.Create();
        await using var db = database.CreateContext();
        var platform = new PlatformEntity { Name = "Merge test", Ownership = Romd.Domain.ReferenceData.ReferenceOwnership.Romd, BuiltInVersion = 1, BaseName = "Merge test", BaseCompactLabel = "Merge test", CanonicalKey = "merge", ShortName = "merge" };
        db.Platforms.Add(platform); await db.SaveChangesAsync();
        var title = new TitleEntity { PlatformId = platform.Id, Name = "Test", NormalizedName = "test", EnrichmentStatus = "Pending" };
        db.Titles.Add(title); await db.SaveChangesAsync();
        var releases = Enumerable.Range(0, 2).Select(index => new CatalogReleaseEntity
        {
            PlatformId = platform.Id, CatalogTitleId = title.Id, Name = $"Release {index}", Fingerprint = $"test-{index}"
        }).ToArray();
        db.CatalogReleases.AddRange(releases); await db.SaveChangesAsync();
        if (regions)
        {
            var source = new RegionEntity { Name = "Source" };
            var target = new RegionEntity { Name = "Target" };
            db.Regions.AddRange(source, target); await db.SaveChangesAsync();
            db.CatalogReleaseRegions.AddRange(releases.Select(release => new CatalogReleaseRegionEntity { CatalogReleaseId = release.Id, RegionId = source.Id }));
            db.CatalogReleaseRegions.Add(new CatalogReleaseRegionEntity { CatalogReleaseId = releases[0].Id, RegionId = target.Id });
            await db.SaveChangesAsync(); db.ChangeTracker.Clear();
            var service = new TaxonomyService<Region>(new RegionRepository(db), Substitute.For<ITaxonomyResolver<Region>>(), new EfUnitOfWork(db));
            (await service.MergeAsync(source.Id, target.Id, default)).IsError.ShouldBeFalse();
            (await db.CatalogReleaseRegions.CountAsync(item => item.RegionId == target.Id)).ShouldBe(2);
            (await db.Regions.AnyAsync(item => item.Id == source.Id)).ShouldBeFalse();
        }
        else
        {
            var source = new GameLanguageEntity { Name = "Source", Code = "SRC" };
            var target = new GameLanguageEntity { Name = "Target", Code = "DST" };
            db.GameLanguages.AddRange(source, target); await db.SaveChangesAsync();
            db.CatalogReleaseLanguages.AddRange(releases.Select(release => new CatalogReleaseLanguageEntity { CatalogReleaseId = release.Id, GameLanguageId = source.Id }));
            db.CatalogReleaseLanguages.Add(new CatalogReleaseLanguageEntity { CatalogReleaseId = releases[0].Id, GameLanguageId = target.Id });
            await db.SaveChangesAsync(); db.ChangeTracker.Clear();
            var service = new TaxonomyService<GameLanguage>(new LanguageRepository(db), Substitute.For<ITaxonomyResolver<GameLanguage>>(), new EfUnitOfWork(db));
            (await service.MergeAsync(source.Id, target.Id, default)).IsError.ShouldBeFalse();
            (await db.CatalogReleaseLanguages.CountAsync(item => item.GameLanguageId == target.Id)).ShouldBe(2);
            (await db.GameLanguages.AnyAsync(item => item.Id == source.Id)).ShouldBeFalse();
        }
        (await db.AdminAuditEvents.CountAsync(item => item.Action == "Merged")).ShouldBe(1);
    }
}
